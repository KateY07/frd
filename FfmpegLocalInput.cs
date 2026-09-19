using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Frd;

sealed class LocalDemoInputTarget : IRemoteInputInjector, IDisposable
{
    const uint DispatchMessage = 0x8001;
    readonly ConcurrentQueue<PendingInput> pending = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<int, (uint Key, nint Parameters)> held = new();
    readonly byte[] keyboard = new byte[256];
    readonly Thread thread;
    readonly WindowProcedure procedure;
    nint window, editor;
    int disposed, wheelRemainder;

    public nint WindowHandle => Volatile.Read(ref window);
    internal nint EditorHandle => Volatile.Read(ref editor);
    public bool IsOpen => Volatile.Read(ref disposed) == 0 && WindowHandle != 0;
    public string ModeDescription => "本机体验：仅向专用测试窗口转发滚轮和键盘；不移动鼠标、不点击、不抢焦点。";

    public LocalDemoInputTarget()
    {
        procedure = WindowProc;
        thread = new(Run) { IsBackground = true, Name = "FRD local input target" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        if (input.Kind is not (RemoteInputKind.Wheel or RemoteInputKind.KeyDown or RemoteInputKind.KeyUp or RemoteInputKind.ReleaseAll))
            return new(false, "Local demo refuses mouse movement, clicks and dragging.");
        if (!IsOpen) return new(false, "Local demo input target is closed.");
        var request = new PendingInput(input);
        pending.Enqueue(request);
        if (!PostMessageW(WindowHandle, DispatchMessage, 0, 0))
        {
            Interlocked.Exchange(ref request.Cancelled, 1);
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            InputProtocol.Log("Local demo input dispatch failed", error);
            return new(false, error.Message);
        }
        try { return request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (TimeoutException error)
        {
            Interlocked.Exchange(ref request.Cancelled, 1);
            InputProtocol.Log("Local demo input target did not respond", error);
            return new(false, "Local demo input target did not respond within 2 seconds.");
        }
    }

    public RemoteInputResult ReleaseAll() => IsOpen ? Inject(new(RemoteInputKind.ReleaseAll)) : new(true, "Local demo has no system input to release.");

    void Run()
    {
        var name = "FRD.LocalInput." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandleW(null);
        var registered = false;
        try
        {
            var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure), ClassName = name,
                Cursor = LoadCursorW(0, 32512), Background = 6 };
            if (RegisterClassExW(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            window = CreateWindowExW(0x08000000, name, "FRD 本机输入目标 — 滚轮 / 键盘；在预览中操作", 0x00cf0000,
                20, 80, 650, 720, 0, 0, instance, 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            editor = CreateWindowExW(0x200, "EDIT", InitialText(), 0x503010c4,
                8, 8, 620, 660, window, 0, instance, 0);
            if (editor == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            SendMessageW(editor, 0x30, GetStockObject(17), 1);
            SendMessageW(editor, 0xb1, 0, 0);
            if (Volatile.Read(ref disposed) != 0) return;
            ShowWindow(window, 4);
            ready.TrySetResult();
            while (true)
            {
                var result = GetMessageW(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result == 0) break;
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        catch (Exception error)
        {
            InputProtocol.Log("Local demo input window failed", error);
            ready.TrySetException(error);
        }
        finally
        {
            if (window != 0 && !DestroyWindow(window)) InputProtocol.Log("Local demo window cleanup failed", new Win32Exception(Marshal.GetLastWin32Error()));
            Volatile.Write(ref window, 0);
            Volatile.Write(ref editor, 0);
            while (pending.TryDequeue(out var request)) request.Completion.TrySetResult(new(false, "Local demo input target is closed."));
            if (registered && !UnregisterClassW(name, instance)) InputProtocol.Log("Local demo window class cleanup failed", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case DispatchMessage:
                    while (pending.TryDequeue(out var request))
                    {
                        if (Volatile.Read(ref request.Cancelled) != 0) continue;
                        try { request.Completion.TrySetResult(Apply(request.Input)); }
                        catch (Exception error)
                        {
                            InputProtocol.Log("Local demo input failed", error);
                            request.Completion.TrySetResult(new(false, error.Message));
                        }
                    }
                    return 0;
                case 5:
                    if (editor != 0) MoveWindow(editor, 8, 8, Math.Max(1, (int)((long)lParam & 0xffff) - 16),
                        Math.Max(1, (int)(((long)lParam >> 16) & 0xffff) - 16), true);
                    return 0;
                case 0x10:
                    if (!DestroyWindow(hwnd)) InputProtocol.Log("Closing local demo window failed", new Win32Exception(Marshal.GetLastWin32Error()));
                    return 0;
                case 2:
                    Volatile.Write(ref window, 0);
                    Volatile.Write(ref editor, 0);
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception error) { InputProtocol.Log("Local demo window message failed", error); }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    RemoteInputResult Apply(RemoteInputEvent input)
    {
        if (editor == 0) return new(false, "Local demo input target is closed.");
        if (input.Kind == RemoteInputKind.ReleaseAll)
        {
            foreach (var value in held.Values) SendMessageW(editor, 0x101, (nint)value.Key, value.Parameters | unchecked((nint)0xc0000000L));
            held.Clear();
            Array.Clear(keyboard);
            return new(true, "Local demo keys released; system input was not modified.");
        }
        if (input.Kind == RemoteInputKind.Wheel)
        {
            if (input.WheelDelta is < short.MinValue or > short.MaxValue) return new(false, "Invalid wheel delta.");
            wheelRemainder += input.WheelDelta;
            var notches = wheelRemainder / 120;
            wheelRemainder %= 120;
            if (notches != 0) SendMessageW(editor, 0xb6, 0, -notches * 3);
            return new(true, "Wheel delivered to local test editor.");
        }
        if (input.ScanCode is <= 0 or > 255) return new(false, "Invalid keyboard scan code.");
        var layout = GetKeyboardLayout(0);
        var scan = (uint)input.ScanCode | (input.Extended ? 0xe000u : 0);
        var key = MapVirtualKeyExW(scan, 3, layout);
        if (key is 0 or > 255) return new(false, "Keyboard scan code could not be mapped.");
        var id = input.ScanCode | (input.Extended ? 0x100 : 0);
        var down = input.Kind == RemoteInputKind.KeyDown;
        var repeated = held.ContainsKey(id);
        var parameters = (nint)(1L | ((long)input.ScanCode << 16) | (input.Extended ? 1L << 24 : 0) |
            (repeated ? 1L << 30 : 0) | (down ? 0 : 1L << 31));
        if (down && !repeated && key is 0x14 or 0x90 or 0x91) keyboard[key] ^= 1;
        keyboard[key] = (byte)((keyboard[key] & 1) | (down ? 0x80 : 0));
        keyboard[0x10] = (byte)(keyboard[0xa0] | keyboard[0xa1]);
        keyboard[0x11] = (byte)(keyboard[0xa2] | keyboard[0xa3]);
        keyboard[0x12] = (byte)(keyboard[0xa4] | keyboard[0xa5]);
        if (down) held[id] = (key, parameters); else held.Remove(id);
        var messageKey = key is 0xa0 or 0xa1 ? 0x10u : key is 0xa2 or 0xa3 ? 0x11u : key is 0xa4 or 0xa5 ? 0x12u : key;
        SendMessageW(editor, down ? 0x100u : 0x101u, (nint)messageKey, parameters);
        if (down)
        {
            var characters = new StringBuilder(8);
            // Flag 4 keeps ToUnicodeEx from changing the thread's keyboard/dead-key state.
            var length = ToUnicodeEx(key, (uint)input.ScanCode, keyboard, characters, characters.Capacity, 4, layout);
            for (var index = 0; index < Math.Min(Math.Max(length, 0), characters.Length); index++)
                SendMessageW(editor, 0x102, characters[index], parameters);
        }
        return new(true, "Keyboard delivered to local test editor.");
    }

    static string InitialText() => "FRD 本机输入体验：在预览画面开启键鼠，再滚动或打字。\r\n" +
        "此窗口仅接收定向滚轮/键盘消息，不移动系统鼠标，不转发点击。\r\n" +
        "Ctrl + Alt 退出转发。可把此窗口与 FRD 预览并排，比较画面返回。\r\n\r\n" +
        string.Join("\r\n", Enumerable.Range(1, 150).Select(index => $"{index:000}  FRD latency test — scroll, type, Backspace, arrows, Page Up / Down."));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var hwnd = WindowHandle;
        if (hwnd != 0 && !PostMessageW(hwnd, 0x10, 0, 0)) InputProtocol.Log("Stopping local demo window failed", new Win32Exception(Marshal.GetLastWin32Error()));
        if (Thread.CurrentThread != thread && !thread.Join(3000)) InputProtocol.Log("Local demo input window did not stop within 3 seconds.");
    }

    sealed class PendingInput(RemoteInputEvent input)
    {
        public readonly RemoteInputEvent Input = input;
        public readonly TaskCompletionSource<RemoteInputResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancelled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance, nint cursor);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetMessageW(out WindowMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref WindowMessage message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int result);
    [DllImport("user32.dll")] static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint code, uint type, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder characters, int capacity, uint flags, nint layout);
    [DllImport("gdi32.dll")] static extern nint GetStockObject(int index);
}
