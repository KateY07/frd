using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Frd;

public enum RemoteInputKind { MouseMove, MouseDown, MouseUp, Wheel, KeyDown, KeyUp, ReleaseAll }
public enum RemoteMouseButton { Left, Right, Middle }
public sealed record RemoteInputEvent(RemoteInputKind Kind, double X = 0, double Y = 0,
    RemoteMouseButton Button = RemoteMouseButton.Left, int WheelDelta = 0, int ScanCode = 0, bool Extended = false);
public sealed record RemoteInputResult(bool Accepted, string Message);
public readonly record struct InputScreenPoint(int X, int Y, int AbsoluteX, int AbsoluteY);
public readonly record struct InputMonitorBounds(int Left, int Top, int Width, int Height);

public interface IRemoteInputInjector
{
    RemoteInputResult Inject(RemoteInputEvent input);
    RemoteInputResult ReleaseAll();
}

public static class InputCoordinates
{
    public static InputScreenPoint Map(double x, double y, int primaryWidth, int primaryHeight,
        int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight) =>
        Map(x, y, new(0, 0, primaryWidth, primaryHeight), virtualLeft, virtualTop, virtualWidth, virtualHeight);

    public static InputScreenPoint Map(double x, double y, InputMonitorBounds source,
        int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 ||
            source.Width < 1 || source.Height < 1 || virtualWidth < 1 || virtualHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Expected normalized coordinates and a valid desktop rectangle.");
        var px = source.Left + (int)Math.Round(x * (source.Width - 1));
        var py = source.Top + (int)Math.Round(y * (source.Height - 1));
        var absoluteX = (int)Math.Round((px - virtualLeft) * 65535d / Math.Max(1, virtualWidth - 1));
        var absoluteY = (int)Math.Round((py - virtualTop) * 65535d / Math.Max(1, virtualHeight - 1));
        return new(px, py, Math.Clamp(absoluteX, 0, 65535), Math.Clamp(absoluteY, 0, 65535));
    }

    public static (double X, double Y) Normalize(int x, int y, int width, int height) =>
        (Math.Clamp(x / (double)Math.Max(1, width - 1), 0, 1), Math.Clamp(y / (double)Math.Max(1, height - 1), 0, 1));

    public static RemoteInputEvent? MapFromVideo(RemoteInputEvent input, int videoWidth, int videoHeight, int sourceWidth, int sourceHeight)
    {
        if (input.Kind is RemoteInputKind.KeyDown or RemoteInputKind.KeyUp or RemoteInputKind.ReleaseAll) return input;
        if (videoWidth <= 0 || videoHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            !double.IsFinite(input.X) || !double.IsFinite(input.Y) || input.X is < 0 or > 1 || input.Y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(input), "Invalid video/content geometry.");
        var scale = Math.Min(videoWidth / (double)sourceWidth, videoHeight / (double)sourceHeight);
        var width = (int)Math.Round(sourceWidth * scale);
        var height = (int)Math.Round(sourceHeight * scale);
        var left = (videoWidth - width) / 2;
        var top = (videoHeight - height) / 2;
        var x = input.X * (videoWidth - 1);
        var y = input.Y * (videoHeight - 1);
        if (x < left || y < top || x > left + width - 1 || y > top + height - 1)
            return input.Kind == RemoteInputKind.MouseUp ? new(RemoteInputKind.ReleaseAll) : null;
        return input with { X = (x - left) / Math.Max(1, width - 1), Y = (y - top) / Math.Max(1, height - 1) };
    }
}

sealed record InputRequest(long Sequence, bool? Enable = null, RemoteInputEvent? Input = null);
sealed record InputReply(long Sequence, bool Accepted, string Message);

static class InputProtocol
{
    const int MaximumMessageBytes = 4096;

    public static async Task WriteAsync<T>(NetworkStream stream, T message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
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
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty input message.");
    }

    public static void Log(string operation, Exception? error = null) => Console.Error.WriteLine($"[Input] {operation}{(error is null ? "" : $": {error}")}");
}

static class UdpInputProtocol
{
    public const byte Version = 1;
    public const int PacketSize = 56;

    public static void Write(Span<byte> packet, ReadOnlySpan<byte> session, long sequence, RemoteInputEvent input)
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
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> session, out long sequence, out RemoteInputEvent? input)
    {
        sequence = 0; input = null;
        if (packet.Length != PacketSize || session.Length != 16 || packet[24] != Version ||
            !CryptographicOperations.FixedTimeEquals(packet[..16], session)) return false;
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
        Action<bool>? enabledChanged = null)
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
                    result = enable ? new(true, "Input forwarding enabled.") : injector.ReleaseAll();
                    enabled = enable;
                    enabledChanged?.Invoke(enabled);
                }
                else if (request.Input is null) result = new(false, "No input event supplied.");
                else if (!enabled) result = new(false, "Input forwarding is disabled.");
                else result = injector.Inject(request.Input);
                await InputProtocol.WriteAsync(stream, new InputReply(request.Sequence, result.Accepted, result.Message), token).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { InputProtocol.Log("Controller disconnected; releasing held input."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { InputProtocol.Log("Input peer stopped."); }
        catch (Exception error) { if (failure != null) failure(error); else InputProtocol.Log("Input peer failed", error); }
        finally
        {
            enabledChanged?.Invoke(false);
            var released = injector.ReleaseAll();
            if (!released.Accepted) InputProtocol.Log(released.Message);
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
    readonly IPEndPoint? inputEndpoint;
    readonly byte[]? inputSession;
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
        }
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
        if (inputSocket != null && Enabled)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            try
            {
                Span<byte> packet = stackalloc byte[UdpInputProtocol.PacketSize];
                UdpInputProtocol.Write(packet, inputSession!, Interlocked.Increment(ref udpSequence), input);
                inputSocket.SendTo(packet, SocketFlags.None, inputEndpoint!);
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
        inputSocket?.Dispose();
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

public sealed class Win32InputInjector : IRemoteInputInjector, IDisposable
{
    static readonly bool traceInput = Environment.GetEnvironmentVariable("FRD_TRACE_INPUT") == "1";
    public const nuint InjectionMarker = 0x46524449;
    readonly ConcurrentDictionary<nint, byte> controllers = new();
    readonly HashSet<(int ScanCode, bool Extended)> keys = new();
    readonly HashSet<RemoteMouseButton> buttons = new();
    readonly object gate = new();
    readonly Func<InputMonitorBounds> sourceMonitor;
    public Win32InputInjector(Func<InputMonitorBounds>? sourceMonitor = null) => this.sourceMonitor = sourceMonitor ?? ReadPrimaryMonitor;
    public void RegisterControllerWindow(nint hwnd) { if (hwnd != 0) controllers[hwnd] = 0; }
    public void UnregisterControllerWindow(nint hwnd) => controllers.TryRemove(hwnd, out _);

    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        if (traceInput && input.Kind != RemoteInputKind.MouseMove)
            Console.Error.WriteLine($"[input trace] tick={Environment.TickCount64} kind={input.Kind} scan={input.ScanCode} foreground={GetForegroundWindow()}");
        lock (gate)
        {
            if (input.Kind == RemoteInputKind.ReleaseAll) return ReleaseAll();
            if (input.Kind is RemoteInputKind.KeyDown or RemoteInputKind.KeyUp)
            {
                if (input.ScanCode is <= 0 or > ushort.MaxValue) return new(false, "Invalid keyboard scan code.");
                if (input.Kind == RemoteInputKind.KeyUp && keys.Contains((input.ScanCode, input.Extended)))
                    return ReleaseKey(input.ScanCode, input.Extended);
                if (IsController(GetForegroundWindow())) return new(false, "Keyboard refused: the foreground window belongs to the local controller. A shared desktop cannot be both controller focus and remote keyboard target.");
                var result = Insert(Key(input.ScanCode, input.Extended, input.Kind == RemoteInputKind.KeyUp));
                if (result.Accepted && input.Kind == RemoteInputKind.KeyDown) keys.Add((input.ScanCode, input.Extended));
                return result;
            }
            if (input.Kind is not (RemoteInputKind.MouseMove or RemoteInputKind.MouseDown or RemoteInputKind.MouseUp or RemoteInputKind.Wheel))
                return new(false, "Unknown input event.");
            if (!Enum.IsDefined(input.Button) || input.WheelDelta is < -12000 or > 12000)
                return new(false, "Invalid mouse button or wheel delta.");
            if (input.Kind == RemoteInputKind.MouseUp && buttons.Contains(input.Button)) return ReleaseButton(input.Button);
            InputScreenPoint point;
            try
            {
                point = InputCoordinates.Map(input.X, input.Y, sourceMonitor(),
                    GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            }
            catch (ArgumentOutOfRangeException error) { InputProtocol.Log("Mouse coordinate rejected", error); return new(false, error.Message); }
            if (IsController(WindowFromPoint(new() { X = point.X, Y = point.Y })))
                return new(false, "Pointer refused: the target is a local controller/overlay window. Move the controller away from this target, or use a separate desktop/machine.");
            var moved = Insert(Mouse(0x0001 | 0x8000 | 0x4000, point.AbsoluteX, point.AbsoluteY));
            if (!moved.Accepted || input.Kind == RemoteInputKind.MouseMove) return moved;
            if (input.Kind == RemoteInputKind.Wheel) return Insert(Mouse(0x0800, data: unchecked((uint)input.WheelDelta)));
            var mouseResult = Insert(Mouse(ButtonFlag(input.Button, input.Kind == RemoteInputKind.MouseUp)));
            if (mouseResult.Accepted && input.Kind == RemoteInputKind.MouseDown) buttons.Add(input.Button);
            return mouseResult;
        }
    }

    public static InputMonitorBounds ReadPrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new(), 1);
        var information = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref information)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read input target monitor bounds.");
        return new(information.Monitor.Left, information.Monitor.Top,
            information.Monitor.Right - information.Monitor.Left, information.Monitor.Bottom - information.Monitor.Top);
    }

    bool IsController(nint hwnd)
    {
        if (hwnd == 0) return false;
        var root = GetAncestor(hwnd, 2);
        var owner = GetAncestor(hwnd, 3);
        return controllers.Keys.Any(controller => IsWindow(controller) &&
            (hwnd == controller || root == controller || owner == controller || IsChild(controller, hwnd)));
    }

    public RemoteInputResult ReleaseAll()
    {
        lock (gate)
        {
            List<string> failures = new();
            foreach (var key in keys.ToArray())
            {
                var result = ReleaseKey(key.ScanCode, key.Extended);
                if (!result.Accepted) failures.Add(result.Message);
            }
            foreach (var button in buttons.ToArray())
            {
                var result = ReleaseButton(button);
                if (!result.Accepted) failures.Add(result.Message);
            }
            return failures.Count == 0 ? new(true, "Held keys and mouse buttons released.") : new(false, string.Join("; ", failures));
        }
    }

    RemoteInputResult ReleaseKey(int scanCode, bool extended)
    {
        var result = Insert(Key(scanCode, extended, true));
        if (result.Accepted) keys.Remove((scanCode, extended));
        return result;
    }

    RemoteInputResult ReleaseButton(RemoteMouseButton button)
    {
        var result = Insert(Mouse(ButtonFlag(button, true)));
        if (result.Accepted) buttons.Remove(button);
        return result;
    }

    static uint ButtonFlag(RemoteMouseButton button, bool up) => button switch
    {
        RemoteMouseButton.Left => up ? 0x0004u : 0x0002u,
        RemoteMouseButton.Right => up ? 0x0010u : 0x0008u,
        RemoteMouseButton.Middle => up ? 0x0040u : 0x0020u,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    static NativeInput Mouse(uint flags, int x = 0, int y = 0, uint data = 0) => new()
    { Type = 0, Value = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags, Extra = InjectionMarker } } };
    static NativeInput Key(int scanCode, bool extended, bool up) => new()
    { Type = 1, Value = new() { Keyboard = new() { ScanCode = (ushort)scanCode, Flags = 0x0008u | (extended ? 1u : 0u) | (up ? 2u : 0u), Extra = InjectionMarker } } };

    static RemoteInputResult Insert(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1) return new(true, "Input injected.");
        var error = new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed; the destination may have higher integrity or be a secure desktop.");
        InputProtocol.Log("System input injection failed", error);
        return new(false, error.Message);
    }

    public void Dispose()
    {
        var result = ReleaseAll();
        if (!result.Accepted) InputProtocol.Log(result.Message);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeInput { public uint Type; public InputUnion Value; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInformation { public uint Size; public Rectangle Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsChild(nint parent, nint child);
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

    public NativeInputSource(nint hwnd)
    {
        this.hwnd = hwnd;
        id = (nuint)Interlocked.Increment(ref nextId);
        procedure = WindowProcedure;
        if (!SetWindowSubclass(hwnd, procedure, id, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot attach preview input capture.");
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

    nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
    {
        try
        {
            // STATIC defaults to HTTRANSPARENT, which bypasses this window's real mouse messages.
            if (enabled && message == 0x0084) return 1;
            if (enabled && hasRemoteCursor && message == 0x0020 && ((long)lParam & 0xFFFF) == 1)
            { NativeCursor.Set(remoteCursorVisible ? remoteCursor : 0); return 1; }
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
                    return 0;
                }
                if (keyDown && controlAlt && wParam is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return 0;
            }
            if (enabled && GetMessageExtraInfo() != (nint)Win32InputInjector.InjectionMarker)
            {
                if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    var scanCode = (int)(((long)lParam >> 16) & 0xFF);
                    var extended = (((long)lParam >> 24) & 1) != 0;
                    if (scanCode != 0) Input?.Invoke(new(message is 0x0101 or 0x0105 ? RemoteInputKind.KeyUp : RemoteInputKind.KeyDown,
                        ScanCode: scanCode, Extended: extended));
                    return 0;
                }
                if (message is 0x0200 or 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020A)
                {
                    if (!GetClientRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var point = new NativePoint { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
                    if (message == 0x020A && !ScreenToClient(hwnd, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var normalized = InputCoordinates.Normalize(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
                    return 0;
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
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false);
        ClearCursors();
        if (!RemoveWindowSubclass(hwnd, procedure, id)) InputProtocol.Log("Cannot detach preview input capture", new Win32Exception(Marshal.GetLastWin32Error()));
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
