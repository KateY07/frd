using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
                using var peer = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                await ServePeerAsync(injector, peer, stop.Token, Report);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { InputProtocol.Log("Input listener stopped."); }
        catch (Exception error) { Report(error); }
    }

    internal static async Task ServePeerAsync(IRemoteInputInjector injector, TcpClient peer, CancellationToken token, Action<Exception>? failure = null)
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
    readonly TcpClient connection;
    readonly NetworkStream stream;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource stop = new();
    long sequence;
    long enableRevision;
    int enabled, disposed;
    public bool Enabled => Volatile.Read(ref enabled) != 0;

    internal RemoteInputClient(TcpClient connection) { this.connection = connection; stream = connection.GetStream(); }

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
        var revision = Interlocked.Increment(ref enableRevision);
        if (!value) Volatile.Write(ref enabled, 0);
        var result = await ExchangeAsync(value, null, token).ConfigureAwait(false);
        if (revision == Interlocked.Read(ref enableRevision)) Volatile.Write(ref enabled, value && result.Accepted ? 1 : 0);
        return result;
    }

    public Task<RemoteInputResult> SendAsync(RemoteInputEvent input, CancellationToken token = default) =>
        !Enabled ? Task.FromResult(new RemoteInputResult(false, "Input forwarding is disabled.")) : ExchangeAsync(null, input, token);

    async Task<RemoteInputResult> ExchangeAsync(bool? enable, RemoteInputEvent? input, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (input is not null && !Enabled) return new(false, "Input forwarding is disabled.");
            var current = ++sequence;
            await InputProtocol.WriteAsync(stream, new InputRequest(current, enable, input), linked.Token).ConfigureAwait(false);
            var reply = await InputProtocol.ReadAsync<InputReply>(stream, linked.Token).ConfigureAwait(false);
            if (reply.Sequence != current) throw new InvalidDataException("Input reply sequence mismatch.");
            return new(reply.Accepted, reply.Message);
        }
        catch (Exception error)
        {
            InputProtocol.Log("Input connection failed; closing it to release held input", error);
            Volatile.Write(ref enabled, 0);
            connection.Close();
            throw;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref enabled, 0);
        stop.Cancel();
        connection.Dispose();
        // Connection closure makes the controlled endpoint release every key/button even on abrupt disconnect.
    }
}

public sealed class Win32InputInjector : IRemoteInputInjector, IDisposable
{
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
    bool enabled, disposed;
    int heldButtons;
    public event Action<RemoteInputEvent>? Input;
    public event Action? ExitRequested;
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
        }
    }

    nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
    {
        try
        {
            // STATIC defaults to HTTRANSPARENT, which bypasses this window's real mouse messages.
            if (enabled && message == 0x0084) return 1;
            if (enabled && GetMessageExtraInfo() != (nint)Win32InputInjector.InjectionMarker)
            {
                if (message is 0x0100 or 0x0104 && wParam == 0x1B)
                {
                    SetEnabled(false);
                    Input?.Invoke(new(RemoteInputKind.ReleaseAll));
                    ExitRequested?.Invoke();
                    return 0;
                }
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
                if (message is 0x0008 or 0x0215) Input?.Invoke(new(RemoteInputKind.ReleaseAll));
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
        if (!RemoveWindowSubclass(hwnd, procedure, id)) InputProtocol.Log("Cannot detach preview input capture", new Win32Exception(Marshal.GetLastWin32Error()));
        disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
