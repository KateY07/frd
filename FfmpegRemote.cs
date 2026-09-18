using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
sealed record RemoteRequest(string Kind, string Token = "", int VideoPort = 0, int DiagnosticPort = 0,
    string Session = "", ControlCommand? Command = null);
sealed record RemoteWelcome(string Session, int SenderPort, int Width, int Height, int SourceWidth, int SourceHeight,
    int FramesPerSecond, string InitialPreset, int InitialBitrateKbps, Dictionary<string, CodecPreset> Presets, double TransmissionScale = 1, double MaximumBitrateMbps = 100);
sealed record RemoteReply(bool Success, string Message = "", RemoteWelcome? Welcome = null, ControlResult? Control = null,
    NetworkSnapshot? Network = null, string Preset = "", int LimitKbps = 0, bool Diagnostics = false,
    long ReceiveTick = 0, long SendTick = 0, long Frequency = 0);

static class RemoteWire
{
    public static async Task WriteAsync<T>(NetworkStream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 65536) throw new InvalidDataException("Remote control message exceeds 64 KiB.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }

    public static async Task<T> ReadAsync<T>(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 65536) throw new InvalidDataException("Invalid remote control message length.");
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

    public async Task<RemoteReply> ExchangeAsync(RemoteRequest request, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
    public bool IsRemote => remoteOptions != null;
    public AppConfiguration Configuration => config;
    internal int SenderPort => sender?.ActualPort ?? throw new InvalidOperationException("Sender not started.");
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
        receiver.Failed += error => ReportError("UDP 接收", error);
        diagnosticsReceiver = new(remote: true); diagnosticsReceiver.Received += ReceiveDiagnostic;
        diagnosticsReceiver.Failed += error => ReportError("UDP 诊断", error);
        remote = await RemoteConnection.ConnectAsync(remoteOptions!, stop.Token);
        var reply = await remote.ExchangeAsync(new("hello", remoteOptions!.Token, receiver.Port, diagnosticsReceiver.Port), stop.Token);
        welcome = reply.Welcome ?? throw new InvalidDataException("Host did not return stream configuration.");
        config = config with { Width = welcome.Width, Height = welcome.Height, FramesPerSecond = welcome.FramesPerSecond,
            InitialPreset = welcome.InitialPreset, InitialBitrateKbps = welcome.InitialBitrateKbps, Presets = welcome.Presets, TransmissionScale = welcome.TransmissionScale, MaximumBitrateMbps = welcome.MaximumBitrateMbps };
        receiver.SetExpectedSource(new(remote.Endpoint.Address, welcome.SenderPort));
        diagnosticsReceiver.SetExpectedSource(new(remote.Endpoint.Address, welcome.SenderPort));
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
        var reply = await remote!.ExchangeAsync(new("status"), stop.Token);
        activePreset = reply.Preset; appliedLimit = reply.LimitKbps; packetDiagnosticsEnabled = reply.Diagnostics;
        message = "远端发送；预设和码率完全手动";
        return remoteNetwork = reply.Network ?? throw new InvalidDataException("Missing sender network statistics.");
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
            return new(client);
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
        LimitKbps: appliedLimit, Diagnostics: packetDiagnosticsEnabled);

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

sealed class RemoteHost : IAsyncDisposable
{
    readonly AppConfiguration config;
    readonly string token;
    readonly TcpListener[] listeners;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    CancellationTokenSource? activeStop;
    string sessionId = "";
    IPAddress? controller;
    bool hasInput, hasClipboard, hasCursor;
    nint clipboardOwner;
    int peerId;
    Task? acceptTask;
    public event Action<string>? Status;
    public event Action<Exception>? Failed;
    public RemoteHost(AppConfiguration config, IPAddress address, int port, string token) : this(config, [address], port, token) { }
    public RemoteHost(AppConfiguration config, IPAddress[] addresses, int port, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        this.config = config; this.token = token;
        listeners = addresses.Select(address =>
        {
            var listener = new TcpListener(address, port);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = address.Equals(IPAddress.IPv6Any);
            return listener;
        }).ToArray();
    }

    public void Start(nint clipboardOwner = 0)
    {
        this.clipboardOwner = clipboardOwner;
        try { foreach (var listener in listeners) listener.Start(8); }
        catch { foreach (var listener in listeners) listener.Stop(); throw; }
        acceptTask = Task.WhenAll(listeners.Select(listener => Task.Run(() => AcceptAsync(listener))));
        var endpoints = string.Join(", ", listeners.Select(listener => listener.LocalEndpoint.ToString()));
        Console.Error.WriteLine($"Remote listener ready: {endpoints}");
        Status?.Invoke($"被控端正在监听 {endpoints}\n等待主控连接；关闭此窗口停止监听。");
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
                    finally { lock (gate) { if (sessionId == hello.Session) hasCursor = false; } }
                    return;
                }
                if (hello.Kind == "clipboard")
                {
                    CancellationToken clipboardToken;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasClipboard || clipboardOwner == 0)
                            throw new InvalidDataException("Clipboard channel does not belong to an available active session.");
                        hasClipboard = true; clipboardToken = activeStop.Token;
                    }
                    try
                    {
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true), timeout.Token);
                        await using var clipboard = new ClipboardSyncSession(client, new WindowsClipboard(clipboardOwner), token: clipboardToken);
                        await clipboard.Completion;
                    }
                    finally { lock (gate) { if (sessionId == hello.Session) hasClipboard = false; } }
                    return;
                }
                if (hello.Kind == "input")
                {
                    CancellationToken inputToken;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasInput)
                            throw new InvalidDataException("Input channel does not belong to the active controller.");
                        hasInput = true; inputToken = activeStop.Token;
                    }
                    try
                    {
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true), timeout.Token);
                        using var injector = new Win32InputInjector();
                        await RemoteInputServer.ServePeerAsync(injector, client, inputToken);
                    }
                    finally { lock (gate) { if (sessionId == hello.Session) hasInput = false; } }
                    return;
                }
                if (hello.Kind != "hello" || hello.VideoPort is < 1 or > 65535 || hello.DiagnosticPort is < 1 or > 65535 || hello.VideoPort == hello.DiagnosticPort)
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
                try
                {
                    using var capture = CreateCapture();
                    var streamConfig = config with { Width = capture.Width, Height = capture.Height };
                    using var session = new DemoSession(streamConfig, capture.Capture, capture.Resize, capture.SourceWidth, capture.SourceHeight);
                    session.Failed += error => { lifetime.Cancel(); Failed?.Invoke(error); };
                    session.StartSender(new(address, hello.VideoPort), new(address, hello.DiagnosticPort));
                    var welcome = new RemoteWelcome(id, session.SenderPort, capture.Width, capture.Height,
                        capture.SourceWidth, capture.SourceHeight,
                        config.FramesPerSecond, config.InitialPreset, config.InitialBitrateKbps, config.Presets, config.TransmissionScale, config.MaximumBitrateMbps);
                    await SendAsync(new(true, Welcome: welcome), received);
                    Status?.Invoke($"主控已连接：{address}\n真实屏幕 → FFmpeg → UDP；键鼠由主控手动启用。");
                    while (!lifetime.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        idle.CancelAfter(TimeSpan.FromSeconds(20));
                        var request = await RemoteWire.ReadAsync<RemoteRequest>(stream, idle.Token);
                        received = Stopwatch.GetTimestamp();
                        if (request.Kind == "status") await SendAsync(session.SenderStatus(), received);
                        else if (request.Kind == "control" && request.Command != null)
                        {
                            var result = await session.SubmitControlAsync(request.Command, idle.Token);
                            await SendAsync(new(true, Control: result), received);
                        }
                        else throw new InvalidDataException("Unknown remote request.");
                    }
                    async Task SendAsync(RemoteReply reply, long tick) => await RemoteWire.WriteAsync(stream,
                        reply with { ReceiveTick = tick, SendTick = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency }, lifetime.Token);
                }
                finally
                {
                    lifetime.Cancel();
                    lifetime.Dispose();
                    lock (gate) { activeStop = null; controller = null; sessionId = ""; }
                    Status?.Invoke("主控已断开；已停止捕获发送并释放输入。等待重新连接。");
                }
            }
            catch (EndOfStreamException) { Console.Error.WriteLine("Remote controller disconnected."); }
            catch (OperationCanceledException) { Console.Error.WriteLine("Remote connection cancelled or timed out."); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error); Status?.Invoke(error.Message);
                try { await RemoteWire.WriteAsync(client.GetStream(), new RemoteReply(false, error.Message), stop.Token); }
                catch (Exception replyError) { Console.Error.WriteLine("Remote error reply failed: " + replyError.Message); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); foreach (var listener in listeners) listener.Stop();
        if (acceptTask != null) await acceptTask;
        await Task.WhenAll(peers.Values); stop.Dispose();
    }

    DesktopStreamSource CreateCapture()
    {
        DesktopStreamSource? capture = null;
        try { capture = new(config.TransmissionScale); capture.Capture(); return capture; }
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
    public const string Usage = "FRD CLI\n被控：FRD.exe --host --listen localhost --port 5000 --token 自定口令\n主控：FRD.exe --connect 被控IP或主机名 --port 5000 --token 相同口令\n每次启动被控端都必须显式指定 --listen、--port、--token；无默认监听地址或口令。\n--listen localhost：仅 IPv4/IPv6 回环；::：所有网卡双栈；也可指定具体 IPv4/IPv6 地址。\n--help：文本帮助；--version：版本；--help-window：帮助窗口。\n无参数：localhost Demo。TCP 控制与附属连接共用监听端口，视频和诊断走 UDP。";
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
    public static int Run(AppConfiguration config, Options options)
    {
        if (!options.Host)
        {
            FfmpegUi.InteractionScriptPath = options.InteractionScript;
            FfmpegUi.Run(config, options.Seconds, options.Report, new(options.Peer!, options.Port, options.Token));
            return Environment.ExitCode;
        }
        HostApplication.Factory = () => new HostWindow(config, options.Addresses, options.Port, options.Token);
        hostExitCode = 0;
        AppBuilder.Configure<HostApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]); return hostExitCode;
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
        bool closing, finished;
        public HostWindow(AppConfiguration config, IPAddress[] addresses, int port, string token)
        {
            Title = $"FRD 被控端 · TCP {port}"; Width = 600; Height = 200;
            var status = new TextBlock { Text = "正在监听…", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20) };
            Content = status; server = new(config, addresses, port, token);
            server.Status += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = text);
            server.Failed += error => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { Console.Error.WriteLine(error); hostExitCode = 1; Close(); });
            Opened += (_, _) =>
            {
                try { server.Start(TryGetPlatformHandle()?.Handle ?? 0); }
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
    }
}
