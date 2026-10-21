using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

public sealed partial class DemoSession
{
    readonly RemoteOptions? remoteOptions;
    RemoteConnection? remote;
    RemoteWelcome? welcome;
    IPEndPoint? diagnosticDestination;
    NetworkSnapshot? remoteNetwork;
    readonly object remoteTimingGate = new();
    readonly Dictionary<long, FrameDiagnostic> remoteDiagnostics = new();
    readonly Dictionary<long, long> remotePresented = new();
    readonly Queue<long> remotePresentationTicks = new();
    long remoteHistorySweep;
    long lastRemoteStatusTick;
    public bool IsRemote => remoteOptions != null;
    public double RemoteStatusWaitSeconds => remoteOptions == null || Volatile.Read(ref lastRemoteStatusTick) == 0
        ? 0 : Math.Max(0, (Stopwatch.GetTimestamp() - Volatile.Read(ref lastRemoteStatusTick)) / (double)Stopwatch.Frequency);
    public AppConfiguration Configuration => config;
    public int RemoteSourceWidth => welcome?.SourceWidth ?? config.Width;
    public int RemoteSourceHeight => welcome?.SourceHeight ?? config.Height;
    string RemoteTimingDescription => packetDiagnosticsEnabled
        ? $"跨机估算，时钟采样不确定度 ±{remote!.Clock.UncertaintyMs:F2} ms（不含漂移）"
        : "诊断关闭：跨机捕获到呈现耗时不可测；画面继续接收";

    public DemoSession(AppConfiguration config, RemoteOptions options)
    {
        this.config = config; remoteOptions = options;
        capture = () => throw new InvalidOperationException("Controller must not capture or encode its desktop.");
    }

    async Task StartRemoteAsync()
    {
        receiver = new(dualStack: true); receiver.VideoReceived += ReceiveVideo;
        receiver.DiagnosticReceived += ReceiveDiagnostic;
        receiver.Failed += error => ReportError("UDP 接收", error);
        remote = await RemoteConnection.ConnectAsync(remoteOptions!, stop.Token);
        var reply = await remote.HelloAsync(remoteOptions!.Token, stop.Token, TimeSpan.FromSeconds(15));
        welcome = reply.Welcome ?? throw new InvalidDataException("Host did not return stream configuration.");
        Volatile.Write(ref lastRemoteStatusTick, Stopwatch.GetTimestamp());
        config = config with { Width = welcome.Width, Height = welcome.Height, FramesPerSecond = welcome.FramesPerSecond,
            InitialPreset = welcome.InitialPreset, InitialBitrateKbps = welcome.InitialBitrateKbps, Presets = welcome.Presets, TransmissionScale = welcome.TransmissionScale, MaximumBitrateMbps = welcome.MaximumBitrateMbps };
        config = await Task.Run(() => AutoCodecProbe.SelectReceiver(config, welcome.ProbeSamples), stop.Token);
        await receiver.RegisterRemoteAsync(new(remote.Endpoint.Address, remoteOptions.Port), Convert.FromHexString(welcome.Session), stop.Token);
        for (var i = 0; i < 4; i++) await remote.ExchangeAsync(new("status"), stop.Token);
        await SetPacketDiagnosticsAsync(true);
        var applied = await ApplyAsync(config.InitialPreset, config.InitialBitrateKbps);
        if (!applied.Success) throw new IOException(applied.Message);
        statusTask = Task.Run(StatusLoopAsync);
    }

    async Task<ControlResult> RemoteControlAsync(ControlCommand command)
    {
        var reply = await remote!.ExchangeAsync(new("control", Command: command), stop.Token);
        var result = reply.Control ?? throw new InvalidDataException("Missing control acknowledgement.");
        if (result.Success)
        {
            Volatile.Write(ref appliedGeneration, result.Generation);
            if (command.Kind == "apply") { activePreset = command.PresetId; appliedLimit = command.LimitKbps; }
            if (command.Kind == "diagnostics") Volatile.Write(ref packetDiagnosticsEnabled, command.DiagnosticsEnabled);
        }
        return result;
    }

    async Task<NetworkSnapshot> ReadRemoteStatusAsync()
    {
        await controlGate.WaitAsync(stop.Token);
        try
        {
        var reply = await remote!.ExchangeAsync(new("status"), stop.Token);
        Volatile.Write(ref lastRemoteStatusTick, Stopwatch.GetTimestamp());
        if (reply.Generation > AppliedGeneration)
        {
            if (reply.Width is < 64 or > 8192 || reply.Height is < 64 or > 8192 ||
                (reply.Width & 1) != 0 || (reply.Height & 1) != 0 ||
                !config.Presets.TryGetValue(reply.Preset, out var preset))
                throw new InvalidDataException("Host reported an invalid stream resolution or preset.");
            VideoDecoder? decoder = new(preset.DecoderArguments);
            lock (decoderGate)
            {
                if (decoders.TryGetValue(reply.Generation, out var existing) &&
                    presetNames.TryGetValue(reply.Generation, out var existingPreset) && existingPreset != reply.Preset)
                {
                    existing.Dispose();
                    decoders.Remove(reply.Generation);
                    presetNames.Remove(reply.Generation);
                }
                if (!decoders.ContainsKey(reply.Generation))
                {
                    decoders[reply.Generation] = decoder;
                    presetNames[reply.Generation] = reply.Preset;
                    decoder = null;
                }
            }
            decoder?.Dispose();
            config = config with { Width = reply.Width, Height = reply.Height };
            if (welcome != null) welcome = welcome with { SourceWidth = reply.Width, SourceHeight = reply.Height };
            Volatile.Write(ref appliedGeneration, reply.Generation);
            var previous = Volatile.Read(ref requestedGeneration);
            while (previous < reply.Generation)
            {
                var actual = Interlocked.CompareExchange(ref requestedGeneration, reply.Generation, previous);
                if (actual == previous) break;
                previous = actual;
            }
            Console.Error.WriteLine($"[video] Remote source changed to {reply.Width}x{reply.Height}, generation {reply.Generation}; requesting clean frame.");
            RequestRecovery();
        }
        activePreset = reply.Preset; appliedLimit = reply.LimitKbps; packetDiagnosticsEnabled = reply.Diagnostics;
        Volatile.Write(ref hostBusy, reply.HostBusy);
        message = string.IsNullOrWhiteSpace(reply.Message) ? "远端发送；预设和码率完全手动" : reply.Message;
        return remoteNetwork = reply.Network ?? throw new InvalidDataException("Missing sender network statistics.");
        }
        finally { controlGate.Release(); }
    }

    public async Task<RemoteInputClient> ConnectRemoteInputAsync(CancellationToken token)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            var (reply, _) = await RemoteAuth.HandshakeAsync(client.GetStream(), remoteOptions.Token, new RemoteRequest("input", Session: welcome.Session, InputProtocol: 2), token);
            if (!reply.Success) throw new IOException(reply.Message);
            if (reply.InputSession is not { Length: 32 } || reply.InputSession.Any(c => !Uri.IsHexDigit(c)))
                throw new IOException("Remote host does not support isolated UDP input sessions; update both endpoints.");
            return new(client, new(remote.Endpoint.Address, remoteOptions.Port), Convert.FromHexString(reply.InputSession),
                (packet, endpoint) => receiver!.SendInput(packet, endpoint), RemoteAuth.InputKey(remote.SessionKey));
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<ClipboardSyncSession> ConnectRemoteClipboardAsync(IClipboardAccess clipboard, CancellationToken token, string? cacheRoot = null)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            var (reply, _) = await RemoteAuth.HandshakeAsync(client.GetStream(), remoteOptions.Token, new RemoteRequest("clipboard", Session: welcome.Session), token);
            if (!reply.Success) throw new IOException(reply.Message);
            return new(client, clipboard, cacheRoot, token);
        }
        catch { client.Dispose(); throw; }
    }

    internal void StartSender(IPEndPoint video, IPEndPoint diagnostics)
    {
        diagnosticDestination = diagnostics;
        sender = new(video, config.Simulation); sender.SetEncoderBitrateKbps(config.InitialBitrateKbps);
        sender.Failed += error => ReportError("UDP 发送/反馈", error);
        sender.FrameSending += (generation, id, timing) =>
        {
            if (frameClocks.TryGetValue(id, out var clock) && clock.Generation == generation) Volatile.Write(ref clock.Sending, timing);
        };
        encodeTask = Task.Run(EncodeLoop);
    }

    internal void StartSender(Socket socket, IPEndPoint destination, RemoteUdpSession udpSession)
    {
        diagnosticDestination = destination;
        sender = new(socket, destination, config.Simulation); sender.SetEncoderBitrateKbps(config.InitialBitrateKbps);
        sender.Failed += error => ReportError("UDP 发送/反馈", error);
        sender.FrameSending += (generation, id, timing) =>
        {
            if (frameClocks.TryGetValue(id, out var clock) && clock.Generation == generation) Volatile.Write(ref clock.Sending, timing);
        };
        udpSession.AttachSender(sender);
        encodeTask = Task.Run(EncodeLoop);
    }

    public async Task<AnnotationClient> ConnectRemoteAnnotationAsync(CancellationToken token)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            var (reply, _) = await RemoteAuth.HandshakeAsync(client.GetStream(), remoteOptions.Token, new RemoteRequest("annotate", Session: welcome.Session), token);
            if (!reply.Success) throw new IOException(reply.Message);
            return new(client, token);
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<CursorClient> ConnectRemoteCursorAsync(Action<CursorUpdate> received, Action<Exception> failed, CancellationToken token)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            var (reply, _) = await RemoteAuth.HandshakeAsync(client.GetStream(), remoteOptions.Token, new RemoteRequest("cursor", Session: welcome.Session), token);
            if (!reply.Success) throw new IOException(reply.Message);
            return new(client, received, failed, token);
        }
        catch { client.Dispose(); throw; }
    }

    internal async Task<ControlResult> SubmitControlAsync(ControlCommand command, CancellationToken token)
    {
        if (command.Kind is not ("apply" or "keyframe" or "diagnostics")) return new(false, "Unknown control command.", AppliedGeneration);
        var pending = new PendingControl(command, new(TaskCreationOptions.RunContinuationsAsynchronously));
        await commands.Writer.WriteAsync(pending, token);
        return await pending.Completion.Task.WaitAsync(token);
    }

    internal RemoteReply SenderStatus() => new(true, Message: message, Network: sender?.Snapshot, Preset: activePreset,
        LimitKbps: appliedLimit, Diagnostics: packetDiagnosticsEnabled,
        Generation: AppliedGeneration, Width: config.Width, Height: config.Height);

    void ApplyRemoteDiagnostic(FrameTimeline clock, FrameDiagnostic diagnostic)
    {
        clock.CaptureTick = remote!.Clock.ToLocal(diagnostic.CaptureStarted, diagnostic.Frequency);
        clock.CapturedTick = remote.Clock.ToLocal(diagnostic.CaptureCompleted, diagnostic.Frequency);
        clock.EncodedTick = remote.Clock.ToLocal(diagnostic.EncodeCompleted, diagnostic.Frequency);
        if (diagnostic.Sending is { } sending)
            clock.Sending = sending with { FirstSendTick = remote.Clock.ToLocal(sending.FirstSendTick, diagnostic.Frequency),
                LastSendTick = remote.Clock.ToLocal(sending.LastSendTick, diagnostic.Frequency) };
        clock.HasRemoteDiagnostic = true;
    }

    void PrepareRemoteFrame(ReceivedVideo video, long tick)
    {
        lock (remoteTimingGate)
        {
            var clock = new FrameTimeline(0, 0, 0, video.Generation) { ReceivedTick = tick, ReassembledTick = video.ReassembledTick };
            if (remoteDiagnostics.Remove(video.FrameId, out var diagnostic) && diagnostic.Generation == video.Generation)
                ApplyRemoteDiagnostic(clock, diagnostic);
            frameClocks[video.FrameId] = clock;
            PruneRemoteHistory(video.FrameId);
        }
    }

    void ReceiveRemoteDiagnostic(FrameDiagnostic diagnostic)
    {
        long completed = 0;
        lock (remoteTimingGate)
        {
            if (frameClocks.TryGetValue(diagnostic.FrameId, out var clock) && clock.Generation == diagnostic.Generation)
                ApplyRemoteDiagnostic(clock, diagnostic);
            else remoteDiagnostics[diagnostic.FrameId] = diagnostic;
            PruneRemoteHistory(diagnostic.FrameId);
            remotePresented.TryGetValue(diagnostic.FrameId, out completed);
        }
        if (completed > 0) ReportPresented(diagnostic.FrameId, completed);
    }

    bool PrepareRemotePresentation(long id, long tick)
    {
        lock (remoteTimingGate)
        {
            if (remotePresented.TryAdd(id, tick)) remotePresentationTicks.Enqueue(tick);
            PruneRemoteHistory(id);
            if (!frameClocks.TryGetValue(id, out var clock) || !clock.HasRemoteDiagnostic) return false;
            remotePresented.Remove(id);
            return true;
        }
    }

    int RemotePresentedFps()
    {
        lock (remoteTimingGate)
        {
            while (remotePresentationTicks.TryPeek(out var tick) && Stopwatch.GetTimestamp() - tick > Stopwatch.Frequency) remotePresentationTicks.Dequeue();
            return remotePresentationTicks.Count;
        }
    }

    void PruneRemoteHistory(long frameId)
    {
        if (frameId < remoteHistorySweep) return;
        remoteHistorySweep = frameId + 64;
        var oldest = frameId - 4096;
        // Missing frames cannot trigger exact-id eviction; periodically sweep the whole expired range.
        foreach (var id in frameClocks.Keys) if (id <= oldest) frameClocks.TryRemove(id, out _);
        foreach (var id in remoteDiagnostics.Keys.Where(id => id <= oldest).ToArray()) remoteDiagnostics.Remove(id);
        foreach (var id in remotePresented.Keys.Where(id => id <= oldest).ToArray()) remotePresented.Remove(id);
    }
}
