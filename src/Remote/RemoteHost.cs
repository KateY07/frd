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

sealed class RemoteHost : IAsyncDisposable
{
    readonly AppConfiguration config;
    readonly CodecProbeSample[] probeSamples;
    readonly string token;
    readonly AuthThrottle throttle = new();
    /// <summary>Set by the host window: true while someone uses the host's own keyboard or mouse.</summary>
    public Func<bool>? LocalInputBusy { get; set; }
    /// <summary>File transfer progress of the clipboard channel (null when idle).</summary>
    public event Action<ClipboardTransferProgress?>? ClipboardProgress;
    /// <summary>A step of the controller's shared annotation (raised on a background thread).</summary>
    public event Action<AnnotationMessage>? AnnotationReceived;

    public void SetAnnotationAllowed(bool allowed)
    {
        lock (gate) annotateAllowed = allowed;
        Console.Error.WriteLine($"[host permission] Annotation {(allowed ? "allowed" : "revoked")}.");
    }
    byte[] sessionKey = [];
    readonly TcpListener[] listeners;
    readonly Socket[] inputSockets;
    readonly int port;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    CancellationTokenSource? activeStop;
    DesktopStreamSource? activeCapture;
    RemoteUdpSession? activeUdp;
    internal long AppliedUdpInputs { get { lock (gate) return activeUdp?.AppliedInputs ?? 0; } }
    string sessionId = "";
    IPAddress? controller;
    bool hasInput, hasClipboard, hasCursor, hasAnnotate, annotateAllowed = true;
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
        Console.Error.WriteLine("[security] FRD authenticates connections (the token is never sent) and signs keyboard/mouse datagrams, but it does not encrypt content. It is designed to run over an already-encrypted link (VPN, tunnel or private network).");
        Status?.Invoke(Loc.F("被控端正在监听 TCP+UDP {0}\n等待主控连接；关闭此窗口停止监听。", endpoints));
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

    internal void ReleasePermissionChannel(string session, CancellationTokenSource channel, bool input)
    {
        lock (gate)
        {
            if (sessionId != session) return;
            if (input && ReferenceEquals(inputPermissionStop, channel)) { hasInput = false; inputPermissionStop = null; }
            if (!input && ReferenceEquals(clipboardPermissionStop, channel)) { hasClipboard = false; clipboardPermissionStop = null; }
        }
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
                var address = FrdNetwork.Canonical(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                if (!throttle.TryBegin(address)) return;       // backing off after failures, or too many handshakes at once: drop silently
                var hostNonce = RandomNumberGenerator.GetBytes(RemoteAuth.NonceBytes);
                RemoteRequest hello;
                byte[] key, clientNonce;
                try
                {
                    await RemoteWire.WriteAsync(stream, new RemoteChallenge(RemoteAuth.Version, Convert.ToHexString(hostNonce)), timeout.Token);
                    hello = await RemoteWire.ReadAsync<RemoteRequest>(stream, timeout.Token);
                    if (!RemoteAuth.VerifyClient(token, hostNonce, hello, out key, out clientNonce))
                    {
                        throttle.Failed(address);
                        await RemoteWire.WriteAsync(stream, new RemoteReply(false, Loc.T("连接口令错误。")), timeout.Token);
                        return;
                    }
                    throttle.Succeeded(address);
                }
                finally { throttle.End(); }
                var received = Stopwatch.GetTimestamp();
                var serverProof = RemoteAuth.ServerProof(key, hostNonce, clientNonce, hello.Kind, hello.Session);
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
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true, ServerProof: serverProof), timeout.Token);
                        await CursorWire.ServeAsync(client, cursorToken);
                    }
                    finally
                    {
                        SessionTrace.Event("host-cursor-tcp", "closed");
                        lock (gate) { if (sessionId == hello.Session) hasCursor = false; }
                    }
                    return;
                }
                if (hello.Kind == "annotate")
                {
                    CancellationToken annotateToken;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasAnnotate)
                            throw new InvalidDataException("Annotation channel does not belong to an available active session.");
                        hasAnnotate = true; annotateToken = activeStop.Token;
                    }
                    try
                    {
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true, ServerProof: serverProof), timeout.Token);
                        await AnnotationWire.ServeAsync(client, message =>
                        {
                            bool allowed;
                            lock (gate) allowed = annotateAllowed;
                            if (allowed) AnnotationReceived?.Invoke(message);
                        }, annotateToken);
                    }
                    finally
                    {
                        SessionTrace.Event("host-annotate-tcp", "closed");
                        lock (gate) { if (sessionId == hello.Session) hasAnnotate = false; }
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
                        await RemoteWire.WriteAsync(stream, new RemoteReply(true, ServerProof: serverProof), timeout.Token);
                        await using var clipboard = new ClipboardSyncSession(client, new WindowsClipboard(clipboardOwner), token: clipboardToken);
                        clipboard.ProgressChanged += value => ClipboardProgress?.Invoke(value);
                        await clipboard.Completion;
                    }
                    finally
                    {
                        SessionTrace.Event("host-clipboard-tcp", "closed");
                        ReleasePermissionChannel(hello.Session, clipboardChannel, false);
                        clipboardChannel.Dispose();
                    }
                    return;
                }
                if (hello.Kind == "input")
                {
                    if (hello.InputProtocol != 2) throw new InvalidDataException("UDP input protocol 2 is required; update both endpoints.");
                    CancellationToken inputToken;
                    CancellationTokenSource inputChannel;
                    RemoteUdpSession udp;
                    DesktopStreamSource inputCapture;
                    lock (gate)
                    {
                        if (activeStop == null || hello.Session != sessionId || !address.Equals(controller) || hasInput || !inputAllowed)
                            throw new InvalidDataException("Input channel does not belong to the active controller.");
                        udp = activeUdp ?? throw new InvalidDataException("UDP session is not registered.");
                        inputCapture = activeCapture ?? throw new InvalidDataException("Capture session is unavailable.");
                        hasInput = true;
                        inputPermissionStop = inputChannel = CancellationTokenSource.CreateLinkedTokenSource(activeStop.Token);
                        inputToken = inputChannel.Token;
                    }
                    try
                    {
                        var ordinary = new Win32InputInjector();
                        ordinary.RegisterControllerWindow(clipboardOwner);
                        using var injector = new SecureDesktopInputRouter(ordinary,
                            () => inputCapture.SecureActive);
                        var udpEnabled = 0;
                        byte[] inputMacKey;
                        lock (gate) inputMacKey = RemoteAuth.InputKey(sessionKey);
                        var inputNonce = udp.AttachInput(injector, () => Volatile.Read(ref udpEnabled) != 0, inputMacKey,
                            error => { SessionTrace.Error("host-input", error); inputChannel.Cancel(); });
                        try
                        {
                            await RemoteWire.WriteAsync(stream, new RemoteReply(true, InputSession: Convert.ToHexString(inputNonce), ServerProof: serverProof), timeout.Token);
                            await RemoteInputServer.ServePeerAsync(injector, client, inputToken,
                                enabledChanged: value => udp.UpdateInputEnabled(injector, value, () =>
                                {
                                    Volatile.Write(ref udpEnabled, value ? 1 : 0);
                                    injector.SetEnabled(value);
                                }), allowTcpEvents: false);
                        }
                        finally
                        {
                            udp.DetachInput(injector);
                        }
                    }
                    finally
                    {
                        SessionTrace.Event("host-input-tcp", "closed");
                        ReleasePermissionChannel(hello.Session, inputChannel, true);
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
                    if (activeStop != null) throw new InvalidOperationException(Loc.T("被控端已有主控连接。"));
                    activeStop = lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    hasInput = hasClipboard = hasCursor = hasAnnotate = false;
                    controller = address; sessionId = id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    sessionKey = RemoteAuth.SessionKey(key, hostNonce, clientNonce);
                }
                ControllerChanged?.Invoke(address);
                try
                {
                    using var capture = CreateCapture();
                    Volatile.Write(ref activeCapture, capture);
                    await using var udpSession = new RemoteUdpSession(FindInputSocket(address), id, lifetime.Token,
                        error => { SessionTrace.Error("host-udp", error); lifetime.Cancel(); });
                    udpSession.LocalBusy = () => LocalInputBusy?.Invoke() == true;
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
                    Status?.Invoke(Loc.F("主控已连接：{0}\n真实屏幕 → FFmpeg → UDP；键鼠由主控手动启用。", address));
                    while (!lifetime.IsCancellationRequested)
                    {
                        var request = await RemoteWire.ReadAsync<RemoteRequest>(stream, lifetime.Token);
                        received = Stopwatch.GetTimestamp();
                        if (request.Kind == "status")
                        {
                            var status = session.SenderStatus() with { HostBusy = LocalInputBusy?.Invoke() == true };
                            if (capture.WaitingForSecureTransition)
                                status = status with { Message = Loc.T("安全桌面切换中：正在等待捕获画面恢复；会话保持连接") };
                            else if (capture.SecureActive)
                                status = status with { Message = Loc.T("安全桌面画面已连接；键鼠通过 SYSTEM 辅助进程转发") };
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
                        reply with { ReceiveTick = tick, SendTick = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency, ServerProof = serverProof }, lifetime.Token);
                }
                finally
                {
                    SessionTrace.Event("host-control-tcp", "closed");
                    lifetime.Cancel();
                    Volatile.Write(ref activeCapture, null);
                    lifetime.Dispose();
                    lock (gate) { activeStop = null; activeUdp = null; controller = null; sessionId = ""; }
                    ControllerChanged?.Invoke(null);
                    Status?.Invoke(Loc.T("主控已断开；已停止捕获发送并释放输入。等待重新连接。"));
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
        Func<bool> enabled, CancellationToken token, byte[]? macKey = null)
    {
        macKey ??= UdpInputProtocol.LocalKey;
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
                    var valid = UdpInputProtocol.TryRead(packet.AsSpan(0, length), nonce, macKey, out var sequence, out var input);
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
