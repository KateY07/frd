using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Simple;

namespace Frd;

public sealed record RemoteOptions(string Host, int Port, string Token);
sealed record RemoteRequest(string Kind, string Token = "", string Session = "", ControlCommand? Command = null);
sealed record RemoteWelcome(string Session, int Width, int Height, int SourceWidth, int SourceHeight,
    int FramesPerSecond, string InitialPreset, int InitialBitrateKbps, Dictionary<string, CodecPreset> Presets, double TransmissionScale = 1,
    double MaximumBitrateMbps = 100, CodecProbeSample[]? ProbeSamples = null);
sealed record RemoteReply(bool Success, string Message = "", RemoteWelcome? Welcome = null, ControlResult? Control = null,
    NetworkSnapshot? Network = null, string Preset = "", int LimitKbps = 0, bool Diagnostics = false,
    long ReceiveTick = 0, long SendTick = 0, long Frequency = 0,
    int Generation = 0, int Width = 0, int Height = 0);

static class RemoteWire
{
    public static async Task WriteAsync<T>(NetworkStream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > (typeof(T) == typeof(RemoteReply) ? 2 * 1024 * 1024 : 65536))
            throw new InvalidDataException("Remote control message exceeds its size limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }

    public static async Task<T> ReadAsync<T>(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > (typeof(T) == typeof(RemoteReply) ? 2 * 1024 * 1024 : 65536))
            throw new InvalidDataException("Invalid remote control message length.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty remote message.");
    }
}

sealed class RemoteConnection : IDisposable
{
    readonly TcpClient client;
    readonly SemaphoreSlim gate = new(1, 1);
    public ClockEstimate Clock { get; } = new();
    public IPEndPoint Endpoint => (IPEndPoint)client.Client.RemoteEndPoint!;
    RemoteConnection(TcpClient client) => this.client = client;

    public static async Task<RemoteConnection> ConnectAsync(RemoteOptions options, CancellationToken token)
    {
        return new(await FrdNetwork.ConnectAsync(options.Host, options.Port, token));
    }

    public async Task<RemoteReply> ExchangeAsync(RemoteRequest request, CancellationToken token, TimeSpan? timeoutDuration = null)
    {
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (timeoutDuration is { } duration) timeout.CancelAfter(duration);
            var started = Stopwatch.GetTimestamp();
            await RemoteWire.WriteAsync(client.GetStream(), request, timeout.Token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), timeout.Token);
            Clock.Observe(started, Stopwatch.GetTimestamp(), reply.ReceiveTick, reply.SendTick, reply.Frequency);
            if (!reply.Success) throw new IOException(reply.Message);
            return reply;
        }
        catch { client.Dispose(); throw; }
        finally { gate.Release(); }
    }
    public void Dispose() => client.Dispose();
}

public sealed class ClockEstimate
{
    readonly object gate = new();
    readonly Queue<(long Local, double Offset, double Error)> samples = new();
    double offsetSeconds, uncertaintyMs = double.PositiveInfinity;
    public double UncertaintyMs { get { lock (gate) return uncertaintyMs; } }
    public void Observe(long t1, long t4, long t2, long t3, long frequency)
    {
        if (frequency <= 0 || t3 < t2 || t4 < t1) return;
        var localDuration = (t4 - t1) / (double)Stopwatch.Frequency;
        var remoteDuration = (t3 - t2) / (double)frequency;
        var uncertainty = (localDuration - remoteDuration) * 500;
        if (uncertainty < 0 || uncertainty > 2000) return;
        var offset = (t1 / (double)Stopwatch.Frequency - t2 / (double)frequency +
            t4 / (double)Stopwatch.Frequency - t3 / (double)frequency) / 2;
        lock (gate)
        {
            samples.Enqueue((t4, offset, uncertainty));
            while (samples.TryPeek(out var sample) && (t4 - sample.Local > Stopwatch.Frequency * 10 || samples.Count > 64)) samples.Dequeue();
            var best = samples.MinBy(sample => sample.Error);
            offsetSeconds = best.Offset; uncertaintyMs = best.Error;
        }
    }
    public long ToLocal(long tick, long frequency)
    {
        lock (gate) return checked((long)((tick / (double)frequency + offsetSeconds) * Stopwatch.Frequency));
    }
}

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
        var reply = await remote.ExchangeAsync(new("hello", remoteOptions!.Token),
            stop.Token, TimeSpan.FromSeconds(15));
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
            await RemoteWire.WriteAsync(client.GetStream(), new RemoteRequest("input", remoteOptions.Token, Session: welcome.Session), token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), token);
            if (!reply.Success) throw new IOException(reply.Message);
            return new(client, new(remote.Endpoint.Address, remoteOptions.Port), Convert.FromHexString(welcome.Session),
                (packet, endpoint) => receiver!.SendInput(packet, endpoint));
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<ClipboardSyncSession> ConnectRemoteClipboardAsync(IClipboardAccess clipboard, CancellationToken token, string? cacheRoot = null)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            await RemoteWire.WriteAsync(client.GetStream(), new RemoteRequest("clipboard", remoteOptions.Token, Session: welcome.Session), token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), token);
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

    public async Task<CursorClient> ConnectRemoteCursorAsync(Action<CursorUpdate> received, Action<Exception> failed, CancellationToken token)
    {
        if (remoteOptions == null || welcome == null) throw new InvalidOperationException("Remote session not connected.");
        var client = await FrdNetwork.ConnectAsync(remote!.Endpoint.Address.ToString(), remoteOptions.Port, token);
        try
        {
            await RemoteWire.WriteAsync(client.GetStream(), new RemoteRequest("cursor", remoteOptions.Token, Session: welcome.Session), token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), token);
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

sealed class RemoteUdpSession : IAsyncDisposable
{
    readonly Socket socket;
    readonly byte[] nonce;
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly TaskCompletionSource<IPEndPoint> registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate = new();
    IPEndPoint? peer;
    UdpVideoSender? sender;
    IRemoteInputInjector? injector;
    Func<bool>? inputEnabled;
    long latestInput;

    public RemoteUdpSession(Socket socket, string session, CancellationToken token)
    {
        this.socket = socket;
        nonce = Convert.FromHexString(session);
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        worker = Task.Run(Receive);
    }

    public async Task<IPEndPoint> WaitForRegistrationAsync(CancellationToken token) =>
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

    public void AttachSender(UdpVideoSender value)
    {
        lock (gate) sender = value;
    }

    public void AttachInput(IRemoteInputInjector value, Func<bool> enabled)
    {
        lock (gate) { injector = value; inputEnabled = enabled; latestInput = 0; }
    }

    public void DetachInput(IRemoteInputInjector value)
    {
        lock (gate) if (ReferenceEquals(injector, value)) { injector = null; inputEnabled = null; latestInput = 0; }
    }

    void Receive()
    {
        var packet = new byte[VideoDatagram.MaxSize];
        var any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!socket.Poll(20_000, SelectMode.SelectRead)) continue;
                EndPoint source = new IPEndPoint(any, 0);
                var length = socket.ReceiveFrom(packet, ref source);
                var endpoint = (IPEndPoint)source;
                var bytes = packet.AsSpan(0, length);
                if (bytes.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VideoDatagram.Magic && bytes[4] == 3 &&
                    CryptographicOperations.FixedTimeEquals(bytes[8..24], nonce))
                {
                    endpoint = new(FrdNetwork.Canonical(endpoint.Address), endpoint.Port);
                    lock (gate) peer = endpoint;
                    socket.SendTo(VideoDatagram.Registration(nonce, 4), endpoint);
                    registered.TrySetResult(endpoint);
                    continue;
                }
                UdpVideoSender? activeSender;
                IRemoteInputInjector? activeInjector;
                Func<bool>? enabled;
                IPEndPoint? activePeer;
                lock (gate) { activeSender = sender; activeInjector = injector; enabled = inputEnabled; activePeer = peer; }
                if (activePeer == null || !FrdNetwork.SameEndpoint(activePeer, endpoint)) continue;
                if (bytes.Length == VideoDatagram.FeedbackSize && bytes[4] == 2 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VideoDatagram.Magic)
                {
                    activeSender?.ProcessFeedback(bytes, activePeer);
                    continue;
                }
                if (activeInjector == null || enabled?.Invoke() != true ||
                    !UdpInputProtocol.TryRead(bytes, nonce, out var sequence, out var input) || sequence <= latestInput) continue;
                latestInput = sequence;
                var result = activeInjector.Inject(input!);
                if (!result.Accepted) Console.Error.WriteLine("[Input] UDP event rejected: " + result.Message);
            }
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote UDP session socket stopped."); }
        catch (SocketException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote UDP session socket stopped."); }
        catch (Exception error) { registered.TrySetException(error); Console.Error.WriteLine(error); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await worker;
        stop.Dispose();
    }
}

sealed class RemoteHost : IAsyncDisposable
{
    readonly AppConfiguration config;
    readonly CodecProbeSample[] probeSamples;
    readonly string token;
    readonly TcpListener[] listeners;
    readonly Socket[] inputSockets;
    readonly int port;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    CancellationTokenSource? activeStop;
    DesktopStreamSource? activeCapture;
    RemoteUdpSession? activeUdp;
    string sessionId = "";
    IPAddress? controller;
    bool hasInput, hasClipboard, hasCursor;
    bool inputAllowed = true, clipboardAllowed = true;
    CancellationTokenSource? inputPermissionStop, clipboardPermissionStop;
    nint clipboardOwner;
    int peerId;
    Task? acceptTask;
    public event Action<string>? Status;
    public event Action<IPAddress?>? ControllerChanged;
    public event Action<Exception>? Failed;
    public RemoteHost(AppConfiguration config, IPAddress address, int port, string token) : this(config, [address], port, token, []) { }
    public RemoteHost(AppConfiguration config, IPAddress[] addresses, int port, string token, CodecProbeSample[]? probeSamples = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        this.config = config; this.token = token; this.port = port; this.probeSamples = probeSamples ?? [];
        listeners = addresses.Select(address =>
        {
            var listener = new TcpListener(address, port);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = address.Equals(IPAddress.IPv6Any);
            return listener;
        }).ToArray();
        inputSockets = addresses.Select(address =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = address.Equals(IPAddress.IPv6Any);
            return socket;
        }).ToArray();
    }

    public void Start(nint clipboardOwner = 0)
    {
        this.clipboardOwner = clipboardOwner;
        try
        {
            foreach (var listener in listeners) listener.Start(8);
            for (var i = 0; i < inputSockets.Length; i++)
                inputSockets[i].Bind(new IPEndPoint(((IPEndPoint)listeners[i].LocalEndpoint).Address, port));
        }
        catch
        {
            foreach (var listener in listeners) listener.Stop();
            foreach (var socket in inputSockets) socket.Dispose();
            throw;
        }
        acceptTask = Task.WhenAll(listeners.Select(listener => Task.Run(() => AcceptAsync(listener))));
        var endpoints = string.Join(", ", listeners.Select(listener => listener.LocalEndpoint.ToString()));
        Console.Error.WriteLine($"Remote listener ready: TCP+UDP {endpoints}");
        Status?.Invoke($"被控端正在监听 TCP+UDP {endpoints}\n等待主控连接；关闭此窗口停止监听。");
    }

    public void DisconnectActive()
    {
        lock (gate) activeStop?.Cancel();
    }

    public void SetInputAllowed(bool allowed)
    {
        lock (gate) { inputAllowed = allowed; if (!allowed) inputPermissionStop?.Cancel(); }
        Console.Error.WriteLine($"[host permission] Keyboard and mouse {(allowed ? "allowed" : "revoked")}.");
    }

    public void SetClipboardAllowed(bool allowed)
    {
        lock (gate) { clipboardAllowed = allowed; if (!allowed) clipboardPermissionStop?.Cancel(); }
        Console.Error.WriteLine($"[host permission] Clipboard {(allowed ? "allowed" : "revoked")}.");
    }

    async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception error) when (stop.IsCancellationRequested &&
                    (error is ObjectDisposedException or InvalidOperationException ||
                    error is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted }))
                { Console.Error.WriteLine("Remote listener stopped: " + error.Message); break; }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref peerId);
                var task = HandleAsync(client); peers[id] = task;
                _ = task.ContinueWith(_ => peers.TryRemove(id, out var removed), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote listener stopped."); }
        catch (Exception error) { Console.Error.WriteLine(error); Status?.Invoke(error.Message); Failed?.Invoke(error); }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var hello = await RemoteWire.ReadAsync<RemoteRequest>(stream, timeout.Token);
                var received = Stopwatch.GetTimestamp();
                if (string.IsNullOrWhiteSpace(hello.Token) ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(hello.Token)), SHA256.HashData(Encoding.UTF8.GetBytes(token))))
                { await RemoteWire.WriteAsync(stream, new RemoteReply(false, "连接口令错误。"), timeout.Token); return; }
                var address = FrdNetwork.Canonical(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                if (hello.Kind == "cursor")
                {
                    CancellationToken cursorToken;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasCursor)
                            throw new InvalidDataException("Cursor channel does not belong to an available active session.");
                        hasCursor = true; cursorToken = activeStop.Token;
                    }
                    try
                    {
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true), timeout.Token);
                        await CursorWire.ServeAsync(client, cursorToken);
                    }
                    finally
                    {
                        SessionTrace.Event("host-cursor-tcp", "closed");
                        lock (gate) { if (sessionId == hello.Session) hasCursor = false; }
                    }
                    return;
                }
                if (hello.Kind == "clipboard")
                {
                    CancellationToken clipboardToken;
                    CancellationTokenSource clipboardChannel;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasClipboard || clipboardOwner == 0 || !clipboardAllowed)
                            throw new InvalidDataException("Clipboard channel does not belong to an available active session.");
                        hasClipboard = true;
                        clipboardPermissionStop = clipboardChannel = CancellationTokenSource.CreateLinkedTokenSource(activeStop.Token);
                        clipboardToken = clipboardChannel.Token;
                    }
                    try
                    {
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true), timeout.Token);
                        await using var clipboard = new ClipboardSyncSession(client, new WindowsClipboard(clipboardOwner), token: clipboardToken);
                        await clipboard.Completion;
                    }
                    finally
                    {
                        SessionTrace.Event("host-clipboard-tcp", "closed");
                        lock (gate) { if (sessionId == hello.Session) hasClipboard = false; clipboardPermissionStop = null; }
                        clipboardChannel.Dispose();
                    }
                    return;
                }
                if (hello.Kind == "input")
                {
                    CancellationToken inputToken;
                    CancellationTokenSource inputChannel;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasInput || !inputAllowed)
                            throw new InvalidDataException("Input channel does not belong to the active controller.");
                        hasInput = true;
                        inputPermissionStop = inputChannel = CancellationTokenSource.CreateLinkedTokenSource(activeStop.Token);
                        inputToken = inputChannel.Token;
                    }
                    try
                    {
                        var ordinary = new Win32InputInjector();
                        ordinary.RegisterControllerWindow(clipboardOwner);
                        using var injector = new SecureDesktopInputRouter(ordinary,
                            () => Volatile.Read(ref activeCapture)?.SecureActive == true);
                        var udpEnabled = 0;
                        RemoteUdpSession udp;
                        lock (gate) udp = activeUdp ?? throw new InvalidDataException("UDP session is not registered.");
                        udp.AttachInput(injector, () => Volatile.Read(ref udpEnabled) != 0);
                        try
                        {
                            await RemoteWire.WriteAsync(stream, new RemoteReply(true), timeout.Token);
                            await RemoteInputServer.ServePeerAsync(injector, client, inputToken,
                                enabledChanged: value =>
                                {
                                    Volatile.Write(ref udpEnabled, value ? 1 : 0);
                                    injector.SetEnabled(value);
                                });
                        }
                        finally
                        {
                            udp.DetachInput(injector);
                        }
                    }
                    finally
                    {
                        SessionTrace.Event("host-input-tcp", "closed");
                        lock (gate) { if (sessionId == hello.Session) hasInput = false; inputPermissionStop = null; }
                        inputChannel.Dispose();
                    }
                    return;
                }
                if (hello.Kind != "hello")
                    throw new InvalidDataException("Invalid connection handshake.");
                CancellationTokenSource lifetime;
                string id;
                lock (gate)
                {
                    if (activeStop != null) throw new InvalidOperationException("被控端已有主控连接。");
                    activeStop = lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    hasInput = hasClipboard = hasCursor = false;
                    controller = address; sessionId = id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                }
                ControllerChanged?.Invoke(address);
                try
                {
                    using var capture = CreateCapture();
                    Volatile.Write(ref activeCapture, capture);
                    await using var udpSession = new RemoteUdpSession(FindInputSocket(address), id, lifetime.Token);
                    lock (gate) activeUdp = udpSession;
                    var streamConfig = config with { Width = capture.Width, Height = capture.Height };
                    using var session = new DemoSession(streamConfig, capture.Capture, capture.Resize, capture.SourceWidth,
                        capture.SourceHeight, capture.CaptureMapped, capture.SetPixelMode, () => (capture.Width, capture.Height));
                    session.Failed += error => { lifetime.Cancel(); Failed?.Invoke(error); };
                    var welcome = new RemoteWelcome(id, capture.Width, capture.Height,
                        capture.SourceWidth, capture.SourceHeight,
                        config.FramesPerSecond, config.InitialPreset, config.InitialBitrateKbps, config.Presets, config.TransmissionScale,
                        config.MaximumBitrateMbps, probeSamples.Where(sample => sample.Width == capture.Width && sample.Height == capture.Height).ToArray());
                    await SendAsync(new(true, Welcome: welcome), received);
                    var udpPeer = await udpSession.WaitForRegistrationAsync(lifetime.Token);
                    session.StartSender(FindInputSocket(address), udpPeer, udpSession);
                    Status?.Invoke($"主控已连接：{address}\n真实屏幕 → FFmpeg → UDP；键鼠由主控手动启用。");
                    while (!lifetime.IsCancellationRequested)
                    {
                        var request = await RemoteWire.ReadAsync<RemoteRequest>(stream, lifetime.Token);
                        received = Stopwatch.GetTimestamp();
                        if (request.Kind == "status")
                        {
                            var status = session.SenderStatus();
                            if (capture.WaitingForSecureTransition)
                                status = status with { Message = "安全桌面切换中：正在等待捕获画面恢复；会话保持连接" };
                            else if (capture.SecureActive)
                                status = status with { Message = "安全桌面画面已连接；键鼠通过 SYSTEM 辅助进程转发" };
                            await SendAsync(status, received);
                        }
                        else if (request.Kind == "control" && request.Command != null)
                        {
                            var result = await session.SubmitControlAsync(request.Command, lifetime.Token);
                            await SendAsync(new(true, Control: result), received);
                        }
                        else throw new InvalidDataException("Unknown remote request.");
                    }
                    async Task SendAsync(RemoteReply reply, long tick) => await RemoteWire.WriteAsync(stream,
                        reply with { ReceiveTick = tick, SendTick = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency }, lifetime.Token);
                }
                finally
                {
                    SessionTrace.Event("host-control-tcp", "closed");
                    lifetime.Cancel();
                    Volatile.Write(ref activeCapture, null);
                    lifetime.Dispose();
                    lock (gate) { activeStop = null; activeUdp = null; controller = null; sessionId = ""; }
                    ControllerChanged?.Invoke(null);
                    Status?.Invoke("主控已断开；已停止捕获发送并释放输入。等待重新连接。");
                }
            }
            catch (EndOfStreamException) { SessionTrace.Event("host-control-tcp", "eof"); Console.Error.WriteLine("Remote controller disconnected."); }
            catch (OperationCanceledException) { SessionTrace.Event("host-control-tcp", "cancelled-or-timeout"); Console.Error.WriteLine("Remote connection cancelled or timed out."); }
            catch (Exception error)
            {
                SessionTrace.Error("host-tcp", error);
                Console.Error.WriteLine(error); Status?.Invoke(error.Message);
                try { await RemoteWire.WriteAsync(client.GetStream(), new RemoteReply(false, error.Message), stop.Token); }
                catch (Exception replyError) { Console.Error.WriteLine("Remote error reply failed: " + replyError.Message); }
            }
        }
    }

    Socket FindInputSocket(IPAddress controller)
    {
        controller = FrdNetwork.Canonical(controller);
        return inputSockets.FirstOrDefault(socket => socket.AddressFamily == controller.AddressFamily ||
            socket.AddressFamily == AddressFamily.InterNetworkV6 && socket.DualMode && controller.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"No UDP input socket can receive {controller.AddressFamily} traffic.");
    }

    internal static async Task ReceiveInputAsync(Socket socket, IRemoteInputInjector injector, IPAddress controller, string session,
        Func<bool> enabled, CancellationToken token)
    {
        if (socket.ReceiveBufferSize < 1024 * 1024) socket.ReceiveBufferSize = 1024 * 1024;
        var nonce = Convert.FromHexString(session);
        var packet = new byte[UdpInputProtocol.PacketSize];
        long latest = 0;
        var any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        await Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!socket.Poll(20_000, SelectMode.SelectRead)) continue;
                    EndPoint source = new IPEndPoint(any, 0);
                    var length = socket.ReceiveFrom(packet, ref source);
                    var valid = UdpInputProtocol.TryRead(packet.AsSpan(0, length), nonce, out var sequence, out var input);
                    if (Environment.GetEnvironmentVariable("FRD_TRACE_INPUT") == "1")
                        InputProtocol.Log($"UDP receive {length} bytes sequence {sequence}; valid={valid}; enabled={enabled()}");
                    if (!enabled() || !FrdNetwork.Canonical(((IPEndPoint)source).Address).Equals(controller) || !valid ||
                        sequence <= latest) continue;
                    latest = sequence;
                    var result = injector.Inject(input!);
                    if (!result.Accepted) Console.Error.WriteLine("[Input] UDP event rejected: " + result.Message);
                }
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { Console.Error.WriteLine("UDP input socket stopped."); }
            catch (SocketException) when (token.IsCancellationRequested) { Console.Error.WriteLine("UDP input socket stopped."); }
            finally { if (token.IsCancellationRequested) Console.Error.WriteLine("UDP input stopped."); }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); foreach (var listener in listeners) listener.Stop();
        if (acceptTask != null) await acceptTask;
        await Task.WhenAll(peers.Values);
        foreach (var socket in inputSockets) socket.Dispose();
        stop.Dispose();
    }

    DesktopStreamSource CreateCapture()
    {
        DesktopStreamSource? capture = null;
        try { capture = new(config.TransmissionScale, secureDesktop: true); capture.Capture(); return capture; }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try { capture?.Dispose(); }
            finally { Failed?.Invoke(error); }
            throw;
        }
    }
}

static class RemoteLaunch
{
    static int hostExitCode;
    public const string Usage = "FRD CLI\n被控：FRD.exe --host --listen localhost --port 5000 --token 自定口令\n主控：FRD.exe --connect 被控IP或主机名 --port 5000 --token 相同口令\n每次启动被控端都必须显式指定 --listen、--port、--token；无默认监听地址或口令。\n--listen localhost：仅 IPv4/IPv6 回环；::：所有网卡双栈；也可指定具体 IPv4/IPv6 地址。\n--help：文本帮助；--version：版本；--help-window：帮助窗口。\n无参数：localhost Demo。远程模式只使用 --port：被控端在该端口同时绑定 TCP/UDP；主控的全部 TCP 连接和 UDP 数据报均以该端口为目标。视频、反馈、诊断和键鼠复用同一个 UDP socket。";
    public static void Help(bool window)
    {
        Console.WriteLine(Usage);
        if (window) ShowMessage(Usage);
    }
    public sealed record Options(bool Host, string? Peer, IPAddress[] Addresses, int Port, string Token,
        int Seconds, string? Report, string? InteractionScript);
    public static Options Parse(string[] args)
    {
        var host = args[0] == "--host";
        if (!host && args.Length < 2) throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>();
        for (var i = host ? 1 : 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--port" or "--token" or "--listen" or "--test-seconds" or "--report" or "--interaction-script") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException(Usage);
        }
        if (!values.TryGetValue("--token", out var token) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("必须每次通过命令行 --token 指定非空连接口令；没有默认口令，也不会从配置文件读取口令。\n" + Usage);
        if (!values.TryGetValue("--port", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new ArgumentException(Usage);
        if (!host)
        {
            if (values.ContainsKey("--listen") || string.IsNullOrWhiteSpace(args[1])) throw new ArgumentException(Usage);
            var seconds = int.Parse(values.GetValueOrDefault("--test-seconds", "0"));
            if (seconds is < 0 or > 3600) throw new ArgumentException("Invalid test duration.");
            return new(false, args[1], [], port, token, seconds, values.GetValueOrDefault("--report"), values.GetValueOrDefault("--interaction-script"));
        }
        if (!values.TryGetValue("--listen", out var listen) || string.IsNullOrWhiteSpace(listen))
            throw new ArgumentException("被控端必须通过 --listen 明确指定 localhost 或 IPv4/IPv6 监听地址；不默认监听所有网卡。\n" + Usage);
        if (values.Keys.Any(key => key is "--test-seconds" or "--report" or "--interaction-script")) throw new ArgumentException(Usage);
        return new(true, null, FrdNetwork.ListenAddresses(listen), port, token, 0, null, null);
    }
    public static int Run(AppConfiguration config, Options options, CodecProbeSample[]? probeSamples = null)
    {
        if (!options.Host)
        {
            FfmpegUi.InteractionScriptPath = options.InteractionScript;
            FfmpegUi.Run(config, options.Seconds, options.Report, new(options.Peer!, options.Port, options.Token));
            return Environment.ExitCode;
        }
        HostApplication.Factory = () => new HostWindow(config, options.Addresses, options.Port, options.Token, probeSamples);
        hostExitCode = 0;
        AppBuilder.Configure<HostApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
        SessionTrace.Event("host-process", $"exit-code-{hostExitCode}");
        return hostExitCode;
    }

    static void ShowMessage(string text)
    {
        HostApplication.Factory = () => new Window { Title = "FRD 命令行", Width = 700, Height = 250,
            Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20) } };
        AppBuilder.Configure<HostApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }

    sealed class HostApplication : Application
    {
        public static Func<Window>? Factory;
        public override void Initialize() => Styles.Add(new SimpleTheme());
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = Factory!();
            base.OnFrameworkInitializationCompleted();
        }
    }

    sealed class HostWindow : Window
    {
        readonly RemoteHost server;
        readonly TextBlock status = new() { Text = "FRD · 等待主控", FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        readonly StackPanel permissions = new() { Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6, IsVisible = false };
        bool closing, finished;
        public HostWindow(AppConfiguration config, IPAddress[] addresses, int port, string token, CodecProbeSample[]? probeSamples)
        {
            Title = $"FRD 被控端 · TCP {port}"; Width = 290; Height = 50;
            CanResize = false; ShowInTaskbar = false; Topmost = true;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B825303C"));
            Foreground = Avalonia.Media.Brushes.White;
            var disconnect = new Button { Content = "断开", FontSize = 10, Padding = new Thickness(4, 0) };
            var clipboard = new CheckBox { Content = "剪贴板", FontSize = 10, IsChecked = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var input = new CheckBox { Content = "键鼠", FontSize = 10, IsChecked = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var sound = new CheckBox { Content = "声音", FontSize = 10, IsChecked = false, IsEnabled = false,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            permissions.Children.Add(disconnect); permissions.Children.Add(clipboard);
            permissions.Children.Add(input); permissions.Children.Add(sound);
            var content = new StackPanel { Spacing = 1, Margin = new Thickness(6, 3) };
            content.Children.Add(status); content.Children.Add(permissions);
            var surface = new Border { Child = content };
            surface.PointerPressed += (_, e) =>
            {
                if (e.Source is Border or StackPanel or TextBlock) BeginMoveDrag(e);
            };
            Content = surface;
            server = new(config, addresses, port, token, probeSamples);
            disconnect.Click += (_, _) => server.DisconnectActive();
            clipboard.IsCheckedChanged += (_, _) => server.SetClipboardAllowed(clipboard.IsChecked == true);
            input.IsCheckedChanged += (_, _) => server.SetInputAllowed(input.IsChecked == true);
            server.ControllerChanged += address => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                status.Text = address == null ? "FRD · 等待主控" : $"正在被 {address} 控制";
                permissions.IsVisible = address != null;
            });
            server.Status += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => ToolTip.SetTip(status, text));
            server.Failed += error => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { Console.Error.WriteLine(error); hostExitCode = 1; Close(); });
            Opened += (_, _) =>
            {
                try
                {
                    var screen = Screens.Primary?.WorkingArea;
                    if (screen is { } area) Position = new(area.Right - (int)Width - 8, area.Bottom - (int)Height - 8);
                    var handle = TryGetPlatformHandle()?.Handle ?? 0;
                    if (handle == 0 || !SetWindowDisplayAffinity(handle, 0x11))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                            "被控端提示窗口无法排除出屏幕捕获");
                    server.Start(handle);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error); status.Text = error.Message; hostExitCode = 1;
                    Avalonia.Threading.Dispatcher.UIThread.Post(Close);
                }
            };
            Closing += async (_, e) =>
            {
                if (finished) return;
                e.Cancel = true; if (closing) return; closing = true;
                try { await server.DisposeAsync(); }
                catch (Exception error) { Console.Error.WriteLine(error); hostExitCode = 1; }
                finally { finished = true; Avalonia.Threading.Dispatcher.UIThread.Post(Close); }
            };
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    }
}
