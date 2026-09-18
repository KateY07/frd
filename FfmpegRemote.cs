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
using Avalonia.Themes.Fluent;

namespace Frd;

public sealed record RemoteOptions(string Host, int Port, string Token);
sealed record RemoteRequest(string Kind, string Token = "", int VideoPort = 0, int DiagnosticPort = 0,
    string Session = "", ControlCommand? Command = null);
sealed record RemoteWelcome(string Session, int SenderPort, int Width, int Height, int SourceWidth, int SourceHeight,
    int FramesPerSecond, string InitialPreset, int InitialBitrateKbps, Dictionary<string, CodecPreset> Presets);
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
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try { await client.ConnectAsync(options.Host, options.Port, token); return new(client); }
        catch { client.Dispose(); throw; }
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
        receiver = new(); receiver.VideoReceived += ReceiveVideo;
        diagnosticsReceiver = new(remote: true); diagnosticsReceiver.Received += ReceiveDiagnostic;
        remote = await RemoteConnection.ConnectAsync(remoteOptions!, stop.Token);
        var reply = await remote.ExchangeAsync(new("hello", remoteOptions!.Token, receiver.Port, diagnosticsReceiver.Port), stop.Token);
        welcome = reply.Welcome ?? throw new InvalidDataException("Host did not return stream configuration.");
        config = config with { Width = welcome.Width, Height = welcome.Height, FramesPerSecond = welcome.FramesPerSecond,
            InitialPreset = welcome.InitialPreset, InitialBitrateKbps = welcome.InitialBitrateKbps, Presets = welcome.Presets };
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
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(remoteOptions.Host, remoteOptions.Port, token);
            await RemoteWire.WriteAsync(client.GetStream(), new RemoteRequest("input", remoteOptions.Token, Session: welcome.Session), token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), token);
            if (!reply.Success) throw new IOException(reply.Message);
            return new(client);
        }
        catch { client.Dispose(); throw; }
    }

    internal void StartSender(IPEndPoint video, IPEndPoint diagnostics)
    {
        diagnosticDestination = diagnostics;
        sender = new(video, config.Simulation); sender.SetEncoderBitrateKbps(config.InitialBitrateKbps);
        sender.FrameSending += (generation, id, timing) =>
        {
            if (frameClocks.TryGetValue(id, out var clock) && clock.Generation == generation) Volatile.Write(ref clock.Sending, timing);
        };
        encodeTask = Task.Run(EncodeLoop);
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
            frameClocks.TryRemove(video.FrameId - 4096, out _);
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
            remoteDiagnostics.Remove(diagnostic.FrameId - 4096);
            remotePresented.TryGetValue(diagnostic.FrameId, out completed);
        }
        if (completed > 0) ReportPresented(diagnostic.FrameId, completed);
    }

    bool PrepareRemotePresentation(long id, long tick)
    {
        lock (remoteTimingGate)
        {
            if (remotePresented.TryAdd(id, tick)) remotePresentationTicks.Enqueue(tick);
            remotePresented.Remove(id - 4096);
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
}

sealed class RemoteHost : IAsyncDisposable
{
    readonly AppConfiguration config;
    readonly string token;
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    CancellationTokenSource? activeStop;
    string sessionId = "";
    IPAddress? controller;
    bool hasInput;
    int peerId;
    Task? acceptTask;
    public event Action<string>? Status;
    public RemoteHost(AppConfiguration config, IPAddress address, int port, string token)
    { this.config = config; this.token = token; listener = new(address, port); }

    public void Start()
    {
        listener.Start(8); acceptTask = Task.Run(AcceptAsync);
        Console.Error.WriteLine($"Remote listener ready: {listener.LocalEndpoint}");
        Status?.Invoke($"被控端正在监听 {listener.LocalEndpoint}\n等待主控连接；关闭此窗口停止监听。");
    }

    async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token); client.NoDelay = true;
                var id = Interlocked.Increment(ref peerId);
                var task = HandleAsync(client); peers[id] = task;
                _ = task.ContinueWith(_ => peers.TryRemove(id, out var removed), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote listener stopped."); }
        catch (Exception error) { Console.Error.WriteLine(error); Status?.Invoke(error.Message); }
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
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(hello.Token)), SHA256.HashData(Encoding.UTF8.GetBytes(token))))
                { await RemoteWire.WriteAsync(stream, new RemoteReply(false, "连接口令错误。"), timeout.Token); return; }
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
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
                    finally { lock (gate) hasInput = false; }
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
                    controller = address; sessionId = id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                }
                try
                {
                    using var capture = new DesktopCapture(config.Width, config.Height);
                    capture.Capture();
                    using var session = new DemoSession(config, capture.Capture);
                    session.StartSender(new(address, hello.VideoPort), new(address, hello.DiagnosticPort));
                    var welcome = new RemoteWelcome(id, session.SenderPort, config.Width, config.Height,
                        capture.Statistics?.SourceWidth ?? config.Width, capture.Statistics?.SourceHeight ?? config.Height,
                        config.FramesPerSecond, config.InitialPreset, config.InitialBitrateKbps, config.Presets);
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
        stop.Cancel(); listener.Stop();
        if (acceptTask != null) await acceptTask;
        await Task.WhenAll(peers.Values); stop.Dispose();
    }
}

static class RemoteLaunch
{
    public const string Usage = "被控：FRD.exe --host --port 5000 --token 自定口令 [--listen 0.0.0.0]\n主控：FRD.exe --connect 被控IPv4或主机名 --port 5000 --token 相同口令\n无参数：原 localhost Demo。TCP 控制及独立键鼠连接共用被控监听端口，视频和诊断走协商的 UDP 端口。";
    public static int Run(AppConfiguration config, string[] args)
    {
        if (args[0] == "--help") { Console.WriteLine(Usage); ShowMessage(Usage); return 0; }
        var host = args[0] == "--host";
        if (!host && args.Length < 2) throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>();
        for (var i = host ? 1 : 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--port" or "--token" or "--listen" or "--test-seconds" or "--report") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException(Usage);
        }
        if (!values.TryGetValue("--port", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535 ||
            !values.TryGetValue("--token", out var token) || string.IsNullOrWhiteSpace(token)) throw new ArgumentException(Usage);
        if (!host)
        {
            var seconds = int.Parse(values.GetValueOrDefault("--test-seconds", "0"));
            if (seconds is < 0 or > 3600) throw new ArgumentException("Invalid test duration.");
            FfmpegUi.Run(config, seconds, values.GetValueOrDefault("--report"), new(args[1], port, token)); return 0;
        }
        var address = IPAddress.Parse(values.GetValueOrDefault("--listen", "0.0.0.0"));
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("目前支持 IPv4。");
        HostApplication.Factory = () => new HostWindow(config, address, port, token);
        AppBuilder.Configure<HostApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]); return 0;
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
        public override void Initialize() => Styles.Add(new FluentTheme());
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
        public HostWindow(AppConfiguration config, IPAddress address, int port, string token)
        {
            Title = $"FRD 被控端 · TCP {port}"; Width = 600; Height = 200;
            var status = new TextBlock { Text = "正在监听…", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20) };
            Content = status; server = new(config, address, port, token);
            server.Status += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = text);
            Opened += (_, _) => { try { server.Start(); } catch (Exception error) { Console.Error.WriteLine(error); status.Text = error.Message; } };
            Closing += async (_, e) =>
            {
                if (finished) return;
                e.Cancel = true; if (closing) return; closing = true;
                try { await server.DisposeAsync(); }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally { finished = true; Avalonia.Threading.Dispatcher.UIThread.Post(Close); }
            };
        }
    }
}
