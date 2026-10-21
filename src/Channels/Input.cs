using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Frd;


sealed record InputRequest(long Sequence, bool? Enable = null, RemoteInputEvent? Input = null);
sealed record InputReply(long Sequence, bool Accepted, string Message);

static class InputProtocol
{
    const int MaximumMessageBytes = 4096;

    public static async Task WriteAsync<T>(NetworkStream stream, T message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, FrdJson.Plain);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Input message exceeds 4096 bytes.");
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid input message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, FrdJson.Plain) ?? throw new InvalidDataException("Empty input message.");
    }

    public static void Log(string operation, Exception? error = null) => Console.Error.WriteLine($"[Input] {operation}{(error is null ? "" : $": {error}")}");
}

static class UdpInputProtocol
{
    public const byte Version = 1;
    /// <summary>56 bytes of event followed by a 16-byte truncated HMAC-SHA256 over them, keyed per session; datagrams without a valid tag are dropped.</summary>
    public const int BodySize = 56, MacSize = 16, PacketSize = BodySize + MacSize;
    /// <summary>Key for loopback-only paths (local demo and tests) where there is no remote peer to authenticate.</summary>
    public static readonly byte[] LocalKey = new byte[32];

    static void Sign(ReadOnlySpan<byte> key, Span<byte> packet) => HMACSHA256.HashData(key, packet[..BodySize])[..MacSize].CopyTo(packet[BodySize..]);

    public static void Write(Span<byte> packet, ReadOnlySpan<byte> session, ReadOnlySpan<byte> macKey, long sequence, RemoteInputEvent input)
    {
        if (packet.Length != PacketSize) throw new ArgumentException($"UDP input packet must be {PacketSize} bytes.", nameof(packet));
        if (session.Length != 16) throw new ArgumentException("UDP input session must be 16 bytes.", nameof(session));
        packet.Clear();
        session.CopyTo(packet);
        BinaryPrimitives.WriteInt64LittleEndian(packet[16..], sequence);
        packet[24] = Version;
        packet[25] = checked((byte)input.Kind);
        packet[26] = checked((byte)input.Button);
        packet[27] = input.Extended ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(packet[28..], input.WheelDelta);
        BinaryPrimitives.WriteInt32LittleEndian(packet[32..], input.ScanCode);
        BinaryPrimitives.WriteDoubleLittleEndian(packet[36..], input.X);
        BinaryPrimitives.WriteDoubleLittleEndian(packet[44..], input.Y);
        Sign(macKey, packet);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> session, ReadOnlySpan<byte> macKey, out long sequence, out RemoteInputEvent? input)
    {
        sequence = 0; input = null;
        if (packet.Length != PacketSize || session.Length != 16 || packet[24] != Version ||
            !CryptographicOperations.FixedTimeEquals(packet[..16], session) ||
            !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(macKey, packet[..BodySize])[..MacSize], packet[BodySize..])) return false;
        var kind = (RemoteInputKind)packet[25];
        var button = (RemoteMouseButton)packet[26];
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(button) || packet[27] > 1) return false;
        sequence = BinaryPrimitives.ReadInt64LittleEndian(packet[16..]);
        if (sequence <= 0) return false;
        var x = BinaryPrimitives.ReadDoubleLittleEndian(packet[36..]);
        var y = BinaryPrimitives.ReadDoubleLittleEndian(packet[44..]);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        input = new(kind, x, y, button, BinaryPrimitives.ReadInt32LittleEndian(packet[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[32..]), packet[27] != 0);
        return true;
    }
}

public sealed class RemoteInputServer : IDisposable
{
    readonly IRemoteInputInjector injector;
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    int disposed;
    public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;
    public event Action<Exception>? Failed;

    public RemoteInputServer(IRemoteInputInjector injector, int port = 0)
    {
        this.injector = injector;
        listener = new(IPAddress.Loopback, port);
        listener.Start(1);
        worker = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient peer;
                try { peer = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (stop.IsCancellationRequested &&
                    (error is InvalidOperationException ||
                    error is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted }))
                {
                    // Stop can dispose the socket or clear the listening state before cancellation reaches accept.
                    InputProtocol.Log("Input listener stopped during accept.");
                    return;
                }
                using (peer) await ServePeerAsync(injector, peer, stop.Token, Report);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { InputProtocol.Log("Input listener stopped."); }
        catch (Exception error) { Report(error); }
    }

    internal static async Task ServePeerAsync(IRemoteInputInjector injector, TcpClient peer, CancellationToken token, Action<Exception>? failure = null,
        Action<bool>? enabledChanged = null, bool allowTcpEvents = true)
    {
        peer.NoDelay = true;
        var stream = peer.GetStream();
        var enabled = false;
        long previousSequence = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await InputProtocol.ReadAsync<InputRequest>(stream, token).ConfigureAwait(false);
                if (request.Sequence != previousSequence + 1) throw new InvalidDataException("Input sequence is not contiguous.");
                previousSequence = request.Sequence;
                RemoteInputResult result;
                if (request.Enable is { } enable)
                {
                    if (request.Input is not null) throw new InvalidDataException("Input enable and event are mutually exclusive.");
                    if (enabled != enable)
                    {
                        enabledChanged?.Invoke(enable);
                        result = !enable && enabledChanged == null ? injector.ReleaseAll() : new(true, "Input forwarding state changed.");
                        enabled = enable;
                    }
                    else result = new(true, "Input forwarding state unchanged.");
                }
                else if (request.Input is null) result = new(false, "No input event supplied.");
                else if (!enabled) result = new(false, "Input forwarding is disabled.");
                else if (!allowTcpEvents) result = new(false, "Remote input events require UDP; TCP only controls input lifecycle.");
                else result = injector.Inject(request.Input);
                await InputProtocol.WriteAsync(stream, new InputReply(request.Sequence, result.Accepted, result.Message), token).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { InputProtocol.Log("Controller disconnected; releasing held input."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { InputProtocol.Log("Input peer stopped."); }
        catch (Exception error) { if (failure != null) failure(error); else InputProtocol.Log("Input peer failed", error); }
        finally
        {
            try { enabledChanged?.Invoke(false); }
            catch (Exception error) { InputProtocol.Log("Input disable during cleanup failed", error); }
            try
            {
                var released = injector.ReleaseAll();
                if (!released.Accepted) InputProtocol.Log(released.Message);
            }
            catch (Exception error) { InputProtocol.Log("Input release during cleanup failed", error); }
        }
    }

    void Report(Exception error)
    {
        InputProtocol.Log("Input receiver failed", error);
        try { Failed?.Invoke(error); }
        catch (Exception callbackError) { InputProtocol.Log("Input error callback failed", callbackError); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        if (!worker.Wait(2000)) InputProtocol.Log("Input receiver has not stopped within 2 seconds.");
        else stop.Dispose();
    }
}

public sealed class RemoteInputClient : IDisposable
{
    const int MaximumInFlight = 64;
    static readonly TimeSpan AcknowledgementWarning = TimeSpan.FromSeconds(5);
    readonly TcpClient connection;
    readonly NetworkStream stream;
    readonly Socket? inputSocket;
    readonly Action<byte[], IPEndPoint>? sendDatagram;
    readonly bool ownsInputSocket;
    readonly IPEndPoint? inputEndpoint;
    readonly byte[]? inputSession;
    readonly byte[] inputMacKey = UdpInputProtocol.LocalKey;
    readonly SemaphoreSlim writeGate = new(1, 1), slots = new(MaximumInFlight, MaximumInFlight);
    readonly CancellationTokenSource stop = new();
    readonly object pendingGate = new();
    readonly Dictionary<long, PendingInput> pending = new();
    readonly Task reader;
    Exception? failure;
    long sequence, repliedSequence;
    long udpSequence;
    long enableRevision;
    int warnedAboutDelay;
    int enabled, disposed;
    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public Exception? Failure => Volatile.Read(ref failure);

    internal RemoteInputClient(TcpClient connection, IPEndPoint? inputEndpoint = null, byte[]? inputSession = null)
    {
        this.connection = connection;
        this.inputEndpoint = inputEndpoint;
        this.inputSession = inputSession;
        if (inputEndpoint != null)
        {
            if (inputSession?.Length != 16) throw new ArgumentException("UDP input session must be 16 bytes.", nameof(inputSession));
            inputSocket = new Socket(inputEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            ownsInputSocket = true;
        }
        connection.NoDelay = true;
        stream = connection.GetStream();
        reader = Task.Run(ReadRepliesAsync);
    }

    internal RemoteInputClient(TcpClient connection, IPEndPoint inputEndpoint, byte[] inputSession,
        Action<byte[], IPEndPoint> sendDatagram, byte[] macKey)
    {
        inputMacKey = macKey;
        if (inputSession.Length != 16) throw new ArgumentException("UDP input session must be 16 bytes.", nameof(inputSession));
        this.connection = connection;
        this.inputEndpoint = inputEndpoint;
        this.inputSession = inputSession;
        this.sendDatagram = sendDatagram;
        connection.NoDelay = true;
        stream = connection.GetStream();
        reader = Task.Run(ReadRepliesAsync);
    }

    public static async Task<RemoteInputClient> ConnectAsync(IPEndPoint endpoint, CancellationToken token = default)
    {
        var connection = new TcpClient { NoDelay = true };
        try
        {
            await connection.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return new(connection);
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task<RemoteInputResult> SetEnabledAsync(bool value, CancellationToken token = default)
    {
        try
        {
            var revision = Interlocked.Increment(ref enableRevision);
            if (!value) Volatile.Write(ref enabled, 0);
            var acknowledgement = await QueueRequestAsync(value, null, token).ConfigureAwait(false);
            var result = await acknowledgement.ConfigureAwait(false);
            if (!value && !result.Accepted) Fail(new IOException("Remote input disable was rejected: " + result.Message));
            lock (pendingGate)
                if (revision == Interlocked.Read(ref enableRevision) && failure == null && Volatile.Read(ref disposed) == 0)
                    Volatile.Write(ref enabled, value && result.Accepted ? 1 : 0);
            return result;
        }
        catch (Exception error)
        {
            // Even cancellation before writing disable must release keys already held at the remote endpoint.
            if (!value) Fail(error);
            throw;
        }
    }

    public async Task<RemoteInputResult> SendAsync(RemoteInputEvent input, CancellationToken token = default)
    {
        var acknowledgement = await QueueAsync(input, token).ConfigureAwait(false);
        return await acknowledgement.ConfigureAwait(false);
    }

    // Await the outer task to preserve write order, then observe the returned task for the remote result.
    public async Task<Task<RemoteInputResult>> QueueAsync(RemoteInputEvent input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if ((inputSocket != null || sendDatagram != null) && Enabled)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            try
            {
                var datagramSequence = Interlocked.Increment(ref udpSequence);
                if (Environment.GetEnvironmentVariable("FRD_TRACE_INPUT") == "1")
                    InputProtocol.Log($"UDP send sequence {datagramSequence}: {input.Kind}");
                if (sendDatagram != null)
                {
                    var packet = new byte[UdpInputProtocol.PacketSize];
                    UdpInputProtocol.Write(packet, inputSession!, inputMacKey, datagramSequence, input);
                    sendDatagram(packet, inputEndpoint!);
                }
                else
                {
                    Span<byte> packet = stackalloc byte[UdpInputProtocol.PacketSize];
                    UdpInputProtocol.Write(packet, inputSession!, inputMacKey, datagramSequence, input);
                    inputSocket!.SendTo(packet, SocketFlags.None, inputEndpoint!);
                }
                return Task.FromResult(new RemoteInputResult(true, "Input event sent over UDP."));
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                InputProtocol.Log("UDP input event failed", error);
                throw;
            }
        }
        return !Enabled ? Task.FromResult(new RemoteInputResult(false, "Input forwarding is disabled.")) :
            await QueueRequestAsync(null, input, token).ConfigureAwait(false);
    }

    async Task<Task<RemoteInputResult>> QueueRequestAsync(bool? enable, RemoteInputEvent? input, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
        var ownsSlot = false;
        var ownsWriter = false;
        PendingInput? request = null;
        try
        {
            await slots.WaitAsync(linked.Token).ConfigureAwait(false);
            ownsSlot = true;
            await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            ownsWriter = true;
            if (input is not null && !Enabled) return Task.FromResult(new RemoteInputResult(false, "Input forwarding is disabled."));
            lock (pendingGate)
            {
                if (failure != null) throw new IOException("Input connection is closed.", failure);
                linked.Token.ThrowIfCancellationRequested();
                request = new(++sequence);
                pending.Add(request.Sequence, request);
                ownsSlot = false;
            }
            ObserveFault(request.Completion.Task);
            var acknowledgement = AwaitAcknowledgementAsync(request, token);
            // A write may fail before the caller receives this task. Observing its fault does not change its result.
            ObserveFault(acknowledgement);
            await InputProtocol.WriteAsync(stream, new InputRequest(request.Sequence, enable, input), linked.Token).ConfigureAwait(false);
            return acknowledgement;
        }
        catch (Exception error)
        {
            if (request != null) Fail(error);
            throw;
        }
        finally
        {
            if (ownsWriter) writeGate.Release();
            if (ownsSlot) slots.Release();
        }
    }

    async Task<RemoteInputResult> AwaitAcknowledgementAsync(PendingInput request, CancellationToken token)
    {
        try
        {
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(AcknowledgementWarning, token)).ConfigureAwait(false) != request.Completion.Task &&
                Interlocked.Exchange(ref warnedAboutDelay, 1) == 0)
                InputProtocol.Log("Input acknowledgement delayed; preserving the open TCP channel while the path recovers.");
            return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) { Fail(error); throw; }
    }

    async Task ReadRepliesAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var reply = await InputProtocol.ReadAsync<InputReply>(stream, stop.Token).ConfigureAwait(false);
                lock (pendingGate)
                {
                    if (failure != null) return;
                    if (reply.Sequence != repliedSequence + 1 || !pending.Remove(reply.Sequence, out var request))
                        throw new InvalidDataException("Input reply sequence mismatch.");
                    repliedSequence = reply.Sequence;
                    Volatile.Write(ref warnedAboutDelay, 0);
                    slots.Release();
                    request.Completion.TrySetResult(new(reply.Accepted, reply.Message));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { InputProtocol.Log("Input reply reader stopped."); }
        catch (Exception error) { Fail(error); }
    }

    void Fail(Exception error, bool disposing = false)
    {
        PendingInput[] abandoned;
        lock (pendingGate)
        {
            if (failure != null) return;
            failure = error;
            Volatile.Write(ref enabled, 0);
            abandoned = pending.Values.ToArray();
            pending.Clear();
        }
        InputProtocol.Log(disposing ? "Input connection disposed; releasing held input." : "Input connection failed; closing it to release held input", disposing ? null : error);
        stop.Cancel();
        connection.Close();
        foreach (var request in abandoned)
        {
            slots.Release();
            request.Completion.TrySetException(error);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Connection closure makes the controlled endpoint release every key/button even on abrupt disconnect.
        Fail(new ObjectDisposedException(nameof(RemoteInputClient)), disposing: true);
        if (ownsInputSocket) inputSocket?.Dispose();
        // The reader handles shutdown itself; disposal never blocks its own continuation.
        ObserveFault(reader);
    }

    static void ObserveFault(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    sealed class PendingInput(long sequence)
    {
        public long Sequence { get; } = sequence;
        public TaskCompletionSource<RemoteInputResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}


public sealed class NativeInputSource : IDisposable
{
    readonly nint hwnd;
    readonly SubclassProcedure procedure;
    readonly nuint id;
    static long nextId;
    bool enabled, disposed, localFullscreenKeyDown;
    int heldButtons;
    readonly Dictionary<long, nint> cursors = new();
    nint remoteCursor;
    bool hasRemoteCursor, remoteCursorVisible = true;

    public void SetRemoteCursor(CursorUpdate update)
    {
        if (disposed) return;
        if (update.Reset) ClearCursors();
        if (update.Shape != null)
        {
            if (cursors.Count >= 64 && !cursors.ContainsKey(update.Id)) throw new InvalidDataException("Cursor cache limit exceeded.");
            var cursor = NativeCursor.Create(update.Shape);
            if (cursors.Remove(update.Id, out var previous)) NativeCursor.Release(previous);
            cursors[update.Id] = cursor;
        }
        if (update.Id != 0 && !cursors.TryGetValue(update.Id, out remoteCursor)) throw new InvalidDataException("Unknown remote cursor shape.");
        if (update.Id == 0) remoteCursor = 0;
        hasRemoteCursor = true; remoteCursorVisible = update.Visible;
        if (enabled && GetCursorPos(out var position) && WindowFromPoint(position) == hwnd)
            NativeCursor.Set(remoteCursorVisible ? remoteCursor : 0);
    }

    void ClearCursors()
    {
        if (hasRemoteCursor) NativeCursor.Set(NativeCursor.Arrow);
        foreach (var cursor in cursors.Values) NativeCursor.Release(cursor);
        cursors.Clear(); remoteCursor = 0; hasRemoteCursor = false;
    }
    public event Action<RemoteInputEvent>? Input;
    public event Action? ExitRequested;
    public event Action<int>? LocalShortcutRequested;
    public event Action<Exception>? Failed;

    public NativeInputSource(nint hwnd, bool subclass)
    {
        this.hwnd = hwnd;
        id = (nuint)Interlocked.Increment(ref nextId);
        procedure = WindowProcedure;
        if (subclass && !SetWindowSubclass(hwnd, procedure, id, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot attach preview input capture.");
        subclassed = subclass;
    }

    bool subclassed;

    public NativeInputSource(nint hwnd)
    {
        this.hwnd = hwnd;
        id = (nuint)Interlocked.Increment(ref nextId);
        procedure = WindowProcedure;
        if (!SetWindowSubclass(hwnd, procedure, id, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot attach preview input capture.");
        subclassed = true;
    }

    public void SetEnabled(bool value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        enabled = value;
        if (value) SetFocus(hwnd);
        else
        {
            heldButtons = 0;
            if (GetCapture() == hwnd) ReleaseCapture();
            if (hasRemoteCursor && GetCursorPos(out var point) && WindowFromPoint(point) == hwnd) NativeCursor.Set(NativeCursor.Arrow);
        }
    }

    /// <summary>The video area inside the window (client coordinates); defaults to the whole client rectangle.</summary>
    public Func<(int X, int Y, int Width, int Height)>? VideoArea { get; set; }
    /// <summary>True when the client point belongs to local UI (a floating panel) and must not be forwarded.</summary>
    public Func<int, int, bool>? PointerIsLocal { get; set; }

    nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
        => TryProcess(message, wParam, lParam, out var result) ? result : DefSubclassProc(window, message, wParam, lParam);

    /// <summary>Handles one window message; returns false when the window's own procedure should process it.</summary>
    public bool TryProcess(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        try
        {
            // STATIC defaults to HTTRANSPARENT, which bypasses this window's real mouse messages.
            if (enabled && message == 0x0084) { result = 1; return true; }
            if (enabled && hasRemoteCursor && message == 0x0020 && ((long)lParam & 0xFFFF) == 1 && !CursorOverLocalUi())
            { NativeCursor.Set(remoteCursorVisible ? remoteCursor : 0); { result = 1; return true; } }
            if (message == 0x0008) localFullscreenKeyDown = false;
            if (GetMessageExtraInfo() != (nint)Win32InputInjector.InjectionMarker &&
                message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
            {
                var keyDown = message is 0x0100 or 0x0104;
                var controlAlt = GetKeyState(0x11) < 0 && GetKeyState(0x12) < 0;
                if (keyDown && controlAlt && enabled)
                {
                    SetEnabled(false);
                    Input?.Invoke(new(RemoteInputKind.ReleaseAll));
                    ExitRequested?.Invoke();
                }
                if (wParam == 0x7A && (controlAlt || localFullscreenKeyDown))
                {
                    localFullscreenKeyDown = keyDown;
                    if (keyDown && controlAlt && ((long)lParam & (1L << 30)) == 0)
                    {
                        LocalShortcutRequested?.Invoke(0x7A);
                    }
                    { result = 0; return true; }
                }
                if (keyDown && controlAlt && wParam is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5) { result = 0; return true; }
            }
            if (enabled && GetMessageExtraInfo() != (nint)Win32InputInjector.InjectionMarker)
            {
                if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    var scanCode = (int)(((long)lParam >> 16) & 0xFF);
                    var extended = (((long)lParam >> 24) & 1) != 0;
                    if (scanCode != 0) Input?.Invoke(new(message is 0x0101 or 0x0105 ? RemoteInputKind.KeyUp : RemoteInputKind.KeyDown,
                        ScanCode: scanCode, Extended: extended));
                    { result = 0; return true; }
                }
                if (message is 0x0200 or 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020A)
                {
                    if (!GetClientRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var point = new NativePoint { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
                    if (message == 0x020A && !ScreenToClient(hwnd, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    // A held button keeps forwarding even when the pointer drifts over local UI; otherwise local UI wins.
                    // A release with nothing held was pressed on local UI, so the local UI must see it to finish its own click or drag.
                    var area = VideoArea?.Invoke() ?? (0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    // The black bars around the video are not part of the remote screen: with nothing held they stay local.
                    if (heldButtons == 0 && (message is 0x0202 or 0x0205 or 0x0208 || IsLocalPoint(point.X, point.Y, area))) return false;
                    var normalized = InputCoordinates.Normalize(point.X - area.X, point.Y - area.Y, area.Width, area.Height);
                    var kind = message switch
                    {
                        0x0200 => RemoteInputKind.MouseMove,
                        0x0201 or 0x0204 or 0x0207 => RemoteInputKind.MouseDown,
                        0x020A => RemoteInputKind.Wheel,
                        _ => RemoteInputKind.MouseUp
                    };
                    var button = message is 0x0204 or 0x0205 ? RemoteMouseButton.Right : message is 0x0207 or 0x0208 ? RemoteMouseButton.Middle : RemoteMouseButton.Left;
                    if (kind == RemoteInputKind.MouseDown) { SetFocus(hwnd); SetCapture(hwnd); heldButtons |= 1 << (int)button; }
                    Input?.Invoke(new(kind, normalized.X, normalized.Y, button, message == 0x020A ? (short)((wParam >> 16) & 0xFFFF) : 0));
                    if (kind == RemoteInputKind.MouseUp)
                    {
                        heldButtons &= ~(1 << (int)button);
                        if (heldButtons == 0 && GetCapture() == hwnd) ReleaseCapture();
                    }
                    { result = 0; return true; }
                }
                // Normal mouse-up clears heldButtons before ReleaseCapture; keyboard modifiers must stay held.
                if (message == 0x0008 || message == 0x0215 && heldButtons != 0)
                {
                    heldButtons = 0;
                    if (message == 0x0008 && GetCapture() == hwnd) ReleaseCapture();
                    Input?.Invoke(new(RemoteInputKind.ReleaseAll));
                }
            }
        }
        catch (Exception error)
        {
            InputProtocol.Log("Native input source failed", error);
            try { Failed?.Invoke(error); }
            catch (Exception callbackError) { InputProtocol.Log("Native input error callback failed", callbackError); }
        }
        return false;
    }

    bool CursorOverLocalUi()
    {
        if (!GetCursorPos(out var position)) return false;
        var point = new NativePoint { X = position.X, Y = position.Y };
        return ScreenToClient(hwnd, ref point) && IsLocalPoint(point.X, point.Y, VideoArea?.Invoke());
    }

    bool IsLocalPoint(int x, int y, (int X, int Y, int Width, int Height)? area)
    {
        if (PointerIsLocal?.Invoke(x, y) == true) return true;
        return area is { } a && a.Width > 0 && a.Height > 0 && (x < a.X || y < a.Y || x >= a.X + a.Width || y >= a.Y + a.Height);
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false);
        ClearCursors();
        if (subclassed && !RemoveWindowSubclass(hwnd, procedure, id)) InputProtocol.Log("Cannot detach preview input capture", new Win32Exception(Marshal.GetLastWin32Error()));
        disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
