using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Frd;

namespace FrdLocalDesktopTests;

sealed class PreviewFixture : IAsyncDisposable
{
    const uint CloseMessage = 0x10;
    readonly RemoteInputClient client;
    readonly nint browser;
    readonly Channel<QueuedInput> queue = Channel.CreateUnbounded<QueuedInput>(new() { SingleReader = true });
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate = new();
    readonly List<object> events = new();
    readonly List<object> states = new();
    readonly List<string> errors = new();
    readonly WindowProcedure procedure;
    readonly Thread thread;
    readonly Task sender;
    NativeInputSource? source;
    nint window, preview;
    uint threadId;
    long queued, completed;
    int released, disposed;

    public PreviewFixture(RemoteInputClient client, nint browser)
    {
        this.client = client; this.browser = browser;
        procedure = WindowProc;
        sender = Task.Run(SendAsync);
        thread = new(Run) { IsBackground = true, Name = "FRD input preview fixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { ready.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); }
        catch { DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public int NativeReleases => Volatile.Read(ref released);

    public object Snapshot(string stage)
    {
        var control = Gui(threadId);
        var browserThread = GetWindowThreadProcessId(browser, out _);
        var target = Gui(browserThread);
        GetCursorPos(out var pointer);
        var state = new { Stage = stage, Tick = Stopwatch.GetTimestamp(), Foreground = GetForegroundWindow().ToInt64(),
            ControllerWindow = window.ToInt64(), PreviewWindow = preview.ToInt64(), BrowserWindow = browser.ToInt64(),
            Controller = control, Browser = target, Pointer = new { pointer.X, pointer.Y },
            NativeReleases, Queued = Interlocked.Read(ref queued), Completed = Interlocked.Read(ref completed) };
        lock (gate) states.Add(state);
        return state;
    }

    public bool ControllerHasFocus => GetForegroundWindow() == window && Gui(threadId).Focus == preview.ToInt64();

    public object Report()
    {
        lock (gate) return new { NativeReleases, Events = events.ToArray(), States = states.ToArray(), Errors = errors.ToArray() };
    }

    public async Task ClickAsync(double x, double y)
    {
        var point = ClientPoint(x, y);
        Send(0x201, 1, Pack(point));
        await DrainAsync(); Snapshot("mouse-down processed while preview holds capture");
        Send(0x202, 0, Pack(point));
        await DrainAsync(); Snapshot("mouse-up processed and capture released");
    }

    public async Task KeyAsync(int virtualKey, int scan)
    {
        Send(0x100, (nuint)virtualKey, (nint)(1 | scan << 16));
        Send(0x101, (nuint)virtualKey, unchecked((nint)(0xc0000001u | (uint)scan << 16)));
        await DrainAsync(); Snapshot("keyboard pair processed");
    }

    public async Task WheelAsync(double x, double y, int delta)
    {
        var point = ClientPoint(x, y);
        if (!ClientToScreen(preview, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Send(0x20a, (nuint)((uint)(ushort)(short)delta << 16), Pack(point));
        await DrainAsync(); Snapshot("wheel processed");
    }

    async Task DrainAsync()
    {
        var target = Interlocked.Read(ref queued);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        while (Interlocked.Read(ref completed) < target)
        {
            ThrowErrors();
            if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("Preview fixture input queue did not drain in 3 seconds.");
            await Task.Delay(5);
        }
        ThrowErrors();
    }

    void ThrowErrors()
    {
        lock (gate) if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
    }

    void Enqueue(RemoteInputEvent input, string origin = "NativeInputSource")
    {
        if (origin == "NativeInputSource" && input.Kind == RemoteInputKind.ReleaseAll) Interlocked.Increment(ref released);
        var sequence = Interlocked.Increment(ref queued);
        if (!queue.Writer.TryWrite(new(sequence, input, origin))) RecordError(new InvalidOperationException("Preview fixture input queue is closed."));
    }

    async Task SendAsync()
    {
        try
        {
            await foreach (var next in queue.Reader.ReadAllAsync())
            {
                var result = await client.SendAsync(next.Input);
                lock (gate) events.Add(new { next.Sequence, next.Origin, Input = next.Input, Result = result, Tick = Stopwatch.GetTimestamp() });
                if (!result.Accepted) RecordError(new InvalidOperationException(result.Message));
                Interlocked.Exchange(ref completed, next.Sequence);
            }
        }
        catch (Exception error) { RecordError(error); }
    }

    void Run()
    {
        var className = "FRD.InputPreviewFixture." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandleW(null);
        var registered = false;
        try
        {
            threadId = GetCurrentThreadId();
            var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance, ClassName = className, Cursor = LoadCursorW(0, 32512), Background = 6 };
            if (RegisterClassExW(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            var monitor = Win32InputInjector.ReadPrimaryMonitor();
            window = CreateWindowExW(0x80, className, "FRD input fixture — controller focus; background browser target", 0x00cf0000,
                monitor.Left + monitor.Width - 760, monitor.Top + 70, 720, 460, 0, 0, instance, 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            preview = CreateWindowExW(0, "STATIC", "NativeInputSource → TCP → background Edge", 0x50000000,
                0, 0, 690, 390, window, 0, instance, 0);
            if (preview == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            source = new(preview);
            source.Input += input => Enqueue(input);
            source.Failed += RecordError;
            source.ExitRequested += () => RecordError(new InvalidOperationException("Unexpected input-exit shortcut during preview fixture."));
            ShowWindow(window, 5);
            if (!SetForegroundWindow(window) && GetForegroundWindow() != window)
                throw new InvalidOperationException("Windows did not allow the dedicated test controller to become foreground.");
            source.SetEnabled(true);
            SetFocus(preview);
            Snapshot("controller activated; browser must remain background");
            ready.TrySetResult();
            while (true)
            {
                var result = GetMessageW(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result == 0) break;
                TranslateMessage(ref message); DispatchMessageW(ref message);
            }
        }
        catch (Exception error) { RecordError(error); ready.TrySetException(error); }
        finally
        {
            source?.Dispose(); source = null;
            if (window != 0 && IsWindow(window) && !DestroyWindow(window)) RecordError(new Win32Exception(Marshal.GetLastWin32Error()));
            preview = window = 0;
            if (registered && !UnregisterClassW(className, instance)) RecordError(new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == CloseMessage)
        {
            source?.Dispose(); source = null;
            if (!DestroyWindow(hwnd)) RecordError(new Win32Exception(Marshal.GetLastWin32Error()));
            return 0;
        }
        if (message == 2) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    void Send(uint message, nuint word, nint parameter)
    {
        if (SendMessageTimeoutW(preview, message, word, parameter, 3, 2000, out _) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot dispatch to the test preview's NativeInputSource.");
    }

    Point ClientPoint(double x, double y)
    {
        if (!GetClientRect(preview, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new() { X = (int)Math.Round(x * (bounds.Right - 1)), Y = (int)Math.Round(y * (bounds.Bottom - 1)) };
    }

    static GuiState Gui(uint id)
    {
        var value = new GuiThreadInformation { Size = (uint)Marshal.SizeOf<GuiThreadInformation>() };
        if (!GetGUIThreadInfo(id, ref value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(id, value.Active.ToInt64(), value.Focus.ToInt64(), value.Capture.ToInt64());
    }

    void RecordError(Exception error) { Console.Error.WriteLine(error); lock (gate) errors.Add(error.ToString()); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Enqueue(new(RemoteInputKind.ReleaseAll), "fixture cleanup");
        try { await DrainAsync(); }
        catch (Exception error) { RecordError(error); }
        var handle = window;
        if (handle != 0 && IsWindow(handle) && !PostMessageW(handle, CloseMessage, 0, 0)) RecordError(new Win32Exception(Marshal.GetLastWin32Error()));
        if (Thread.CurrentThread != thread && !thread.Join(3000)) RecordError(new TimeoutException("Preview fixture window did not close."));
        queue.Writer.TryComplete();
        await sender;
    }

    static nint Pack(Point point) => (nint)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16);
    sealed record QueuedInput(long Sequence, RemoteInputEvent Input, string Origin);
    sealed record GuiState(uint Thread, long Active, long Focus, long Capture);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInformation
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRectangle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Reserved;
    }
    delegate nint WindowProcedure(nint window, uint message, nuint word, nint parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint window, uint message, nuint word, nint parameter);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance, nint cursor);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint SetFocus(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInformation information);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(nint window, uint message, nuint word, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SendMessageTimeoutW(nint window, uint message, nuint word, nint parameter, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetMessageW(out WindowMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref WindowMessage message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(nint window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out Point point);
}
