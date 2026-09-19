using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Frd;

sealed record LocalDesktopInputSnapshot(long PostedEvents, long RejectedEvents, nint TargetWindow,
    uint TargetProcessId, string TargetDescription, string LastResult, int HeldKeys, int HeldButtons, InputScreenPoint? VirtualPointer);

sealed class LocalDesktopInputRouter : IRemoteInputInjector, IDisposable
{
    const uint DispatchTimeoutMilliseconds = 20;
    readonly object gate = new();
    readonly HashSet<nint> controllers = new();
    readonly Dictionary<int, HeldKey> keys = new();
    readonly Dictionary<RemoteMouseButton, HeldButton> buttons = new();
    readonly byte[] keyboard = new byte[256];
    nint keyboardRoot, keyboardTarget, lastClickTarget;
    RemoteMouseButton lastClickButton;
    Point lastClickPoint;
    long lastClickTime, posted, rejected;
    InputScreenPoint? virtualPointer;
    string lastResult = "尚未选择目标；请在预览中点击真实应用的内容区域。";
    bool disposed;

    public string ModeDescription => "本机体验：鼠标移动仅改变虚拟指针；点击、滚轮及键盘定向到画面中的真实应用，不移动系统鼠标。系统快捷键、IME、Raw Input 及部分应用不支持。";
    public event Action<InputScreenPoint>? PointerMoved;

    public void RegisterControllerWindow(nint hwnd)
    {
        lock (gate) if (hwnd != 0) controllers.Add(hwnd);
    }

    public LocalDesktopInputSnapshot Snapshot()
    {
        lock (gate)
        {
            GetWindowThreadProcessId(keyboardTarget, out var process);
            return new(posted, rejected, keyboardTarget, process, Describe(keyboardTarget), lastResult, keys.Count, buttons.Count, virtualPointer);
        }
    }

    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        lock (gate)
        {
            if (disposed) return Result(false, "本机输入路由已经关闭。");
            if (input.Kind == RemoteInputKind.ReleaseAll) return ReleaseAll();
            try
            {
                return input.Kind switch
                {
                    RemoteInputKind.KeyDown or RemoteInputKind.KeyUp => Keyboard(input),
                    RemoteInputKind.MouseMove or RemoteInputKind.MouseDown or RemoteInputKind.MouseUp or RemoteInputKind.Wheel => Mouse(input),
                    _ => Result(false, "未知本机输入事件。")
                };
            }
            catch (Exception error) when (error is Win32Exception or ArgumentOutOfRangeException)
            {
                InputProtocol.Log("Local desktop input rejected", error);
                return Result(false, error.Message);
            }
        }
    }

    RemoteInputResult Mouse(RemoteInputEvent input)
    {
        if (!Enum.IsDefined(input.Button) || input.WheelDelta is < -12000 or > 12000)
            return Result(false, "鼠标按钮或滚轮增量无效。");
        var monitor = Win32InputInjector.ReadPrimaryMonitor();
        var mapped = InputCoordinates.Map(input.X, input.Y, monitor, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        virtualPointer = mapped;
        try { PointerMoved?.Invoke(mapped); }
        catch (Exception error) { InputProtocol.Log("Local virtual pointer callback failed", error); }
        var screen = new Point { X = mapped.X, Y = mapped.Y };
        var up = input.Kind == RemoteInputKind.MouseUp;
        var held = buttons.GetValueOrDefault(input.Button);
        var target = up && held is not null ? held.Window : input.Kind == RemoteInputKind.MouseMove && buttons.Count != 0 ? buttons.Values.First().Window : FindTarget(screen);
        if (input.Kind == RemoteInputKind.MouseMove && !Usable(target)) return Result(true, "虚拟指针已移动；该位置无可接受悬停消息的应用。");
        if (!Usable(target)) return Result(false, "该位置没有可接受定向输入的真实应用窗口，或原按钮目标已关闭。");
        var client = screen;
        if (!ScreenToClient(target, ref client)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法换算目标窗口坐标。");
        if (!GetClientRect(target, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取目标窗口内容区域。");
        if (!up && input.Kind != RemoteInputKind.MouseMove && !Contains(bounds, client)) return Result(false, "本机定向输入需要点击应用内容区域；标题栏及系统窗口拖动需要双机体验。");
        var mask = MouseMask();
        if (input.Kind == RemoteInputKind.MouseMove)
        {
            Dispatch(target, 0x200, mask, Pack(client));
            return Result(true, "虚拟鼠标悬停消息已送达 " + Describe(target) + "；系统指针位置未改变。");
        }
        if (input.Kind == RemoteInputKind.Wheel)
        {
            Dispatch(target, 0x20a, (nuint)(mask | ((uint)(ushort)(short)input.WheelDelta << 16)), Pack(screen));
            return Result(true, "滚轮已送达 " + Describe(target) + "；不表示应用已经处理或重绘。");
        }
        var flag = ButtonMask(input.Button);
        mask = up ? mask & ~flag : mask | flag;
        var message = ButtonMessage(input.Button, up);
        if (!up)
        {
            var now = Environment.TickCount64;
            if (target == lastClickTarget && input.Button == lastClickButton && now - lastClickTime <= GetDoubleClickTime() &&
                Math.Abs(screen.X - lastClickPoint.X) <= GetSystemMetrics(36) / 2 && Math.Abs(screen.Y - lastClickPoint.Y) <= GetSystemMetrics(37) / 2 &&
                (GetClassLongPtrW(target, -26).ToInt64() & 8) != 0)
            {
                message += 2;
                lastClickTime = 0;
            }
            else { lastClickTime = now; lastClickTarget = target; lastClickButton = input.Button; lastClickPoint = screen; }
        }
        if (!up)
        {
            // A timed-out window may already have received down; retain it until a release is confirmed.
            buttons[input.Button] = new(target, client);
            keyboardRoot = GetAncestor(target, 2);
            keyboardTarget = target;
        }
        Dispatch(target, message, mask, Pack(client));
        if (up) buttons.Remove(input.Button);
        return Result(true, "鼠标按钮已送达 " + Describe(target) + "；系统指针位置未改变。");
    }

    RemoteInputResult Keyboard(RemoteInputEvent input)
    {
        if (input.ScanCode is <= 0 or > 255) return Result(false, "键盘扫描码必须介于 1 和 255。");
        var id = input.ScanCode | (input.Extended ? 0x100 : 0);
        var down = input.Kind == RemoteInputKind.KeyDown;
        var held = keys.GetValueOrDefault(id);
        if (!down && held is not null) return ReleaseKey(id, held);
        var target = held?.Window ?? ResolveKeyboardTarget();
        if (!Usable(target)) return Result(false, "请先在预览中点击目标网页或应用内容，再输入键盘；原目标可能已经关闭。");
        var thread = GetWindowThreadProcessId(target, out _);
        var layout = GetKeyboardLayout(thread);
        var key = MapVirtualKeyExW((uint)input.ScanCode | (input.Extended ? 0xe000u : 0), 3, layout);
        if (key is 0 or > 255) return Result(false, "目标键盘布局无法映射该扫描码。");
        var messageKey = key is 0xa0 or 0xa1 ? 0x10u : key is 0xa2 or 0xa3 ? 0x11u : key is 0xa4 or 0xa5 ? 0x12u : key;
        var alt = (keyboard[0x12] & 0x80) != 0;
        var system = alt || messageKey == 0x12 || messageKey == 0x79;
        var parameters = (nint)(1L | ((long)input.ScanCode << 16) | (input.Extended ? 1L << 24 : 0) |
            (alt ? 1L << 29 : 0) | (held is not null || !down ? 1L << 30 : 0) | (!down ? 1L << 31 : 0));
        if (down)
        {
            keys[id] = new(target, key, messageKey, parameters, system);
            SetKeyState(key, true, held is null);
        }
        Dispatch(target, down ? system ? 0x104u : 0x100u : system ? 0x105u : 0x101u, messageKey, parameters);
        if (!down) return Result(true, "键盘抬起已送达 " + Describe(target));
        var text = new StringBuilder(8);
        // Direct, bounded WndProc dispatch prevents TranslateMessage from producing duplicate WM_CHAR.
        // Flag 4 avoids changing the caller's keyboard/dead-key state.
        var count = ToUnicodeEx(key, (uint)input.ScanCode, keyboard, text, text.Capacity, 4, layout);
        for (var index = 0; index < Math.Min(Math.Max(0, count), text.Length); index++)
            Dispatch(target, system ? 0x106u : 0x102u, text[index], parameters);
        return Result(true, "键盘已送达 " + Describe(target) + "；定向消息不保证应用快捷键、IME 或 Raw Input 语义。");
    }

    RemoteInputResult ReleaseKey(int id, HeldKey key)
    {
        Dispatch(key.Window, key.System ? 0x105u : 0x101u, key.MessageKey, key.Parameters | unchecked((nint)0xc0000000L));
        keys.Remove(id);
        SetKeyState(key.Key, false, false);
        return Result(true, "已向原窗口释放键盘按键。");
    }

    public RemoteInputResult ReleaseAll()
    {
        lock (gate)
        {
            List<string> failures = new();
            foreach (var (id, key) in keys.ToArray())
            {
                try { ReleaseKey(id, key); }
                catch (Win32Exception error) { InputProtocol.Log("Local desktop key release failed", error); failures.Add(error.Message); }
            }
            foreach (var (button, held) in buttons.ToArray())
            {
                try
                {
                    Dispatch(held.Window, ButtonMessage(button, true), MouseMask() & ~ButtonMask(button), Pack(held.ClientPoint));
                    buttons.Remove(button);
                }
                catch (Win32Exception error) { InputProtocol.Log("Local desktop button release failed", error); failures.Add(error.Message); }
            }
            if (failures.Count != 0) return Result(false, "部分本机定向按键未确认释放：" + string.Join("; ", failures));
            Array.Clear(keyboard);
            return Result(true, "本机定向按键已释放；系统鼠标和键盘状态未修改。");
        }
    }

    nint ResolveKeyboardTarget()
    {
        if (!Usable(keyboardTarget) || !Usable(keyboardRoot)) return 0;
        var thread = GetWindowThreadProcessId(keyboardRoot, out _);
        var information = new GuiThreadInformation { Size = (uint)Marshal.SizeOf<GuiThreadInformation>() };
        if (GetGUIThreadInfo(thread, ref information) && Usable(information.Focus) && GetAncestor(information.Focus, 2) == keyboardRoot)
            return information.Focus;
        return keyboardTarget;
    }

    nint FindTarget(Point screen)
    {
        nint target = 0;
        EnumWindows((window, _) =>
        {
            if (!Usable(window) || !IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var bounds) || !Contains(bounds, screen)) return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            target = window;
            return false;
        }, 0);
        for (var depth = 0; target != 0 && depth < 32; depth++)
        {
            var client = screen;
            if (!ScreenToClient(target, ref client)) break;
            // Chromium's input-host child uses WS_EX_TRANSPARENT; do not skip transparent children.
            var child = ChildWindowFromPointEx(target, client, 1 | 2);
            if (child == 0 || child == target || !Usable(child)) break;
            target = child;
        }
        return target;
    }

    bool Usable(nint window)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowEnabled(window)) return false;
        GetWindowThreadProcessId(window, out var process);
        return process != Environment.ProcessId && !controllers.Contains(window) && !controllers.Contains(GetAncestor(window, 2));
    }

    void Dispatch(nint window, uint message, nuint word, nint parameter)
    {
        if (!Usable(window)) throw new Win32Exception(1400, "目标窗口已经关闭、禁用或属于 FRD 主控。");
        Marshal.SetLastPInvokeError(0);
        if (SendMessageTimeoutW(window, message, word, parameter, 3, DispatchTimeoutMilliseconds, out _) == 0)
        {
            var code = Marshal.GetLastWin32Error();
            throw new Win32Exception(code == 0 ? 1460 : code, $"目标窗口未在 {DispatchTimeoutMilliseconds} ms 内接受消息 0x{message:X}，或被权限隔离拒绝。");
        }
    }

    void SetKeyState(uint key, bool down, bool first)
    {
        if (down && first && key is 0x14 or 0x90 or 0x91) keyboard[key] ^= 1;
        keyboard[key] = (byte)((keyboard[key] & 1) | (down ? 0x80 : 0));
        keyboard[0x10] = (byte)(keyboard[0xa0] | keyboard[0xa1]);
        keyboard[0x11] = (byte)(keyboard[0xa2] | keyboard[0xa3]);
        keyboard[0x12] = (byte)(keyboard[0xa4] | keyboard[0xa5]);
    }

    uint MouseMask() => buttons.Keys.Aggregate(0u, (mask, button) => mask | ButtonMask(button)) |
        ((keyboard[0x10] & 0x80) != 0 ? 4u : 0) | ((keyboard[0x11] & 0x80) != 0 ? 8u : 0);
    static uint ButtonMask(RemoteMouseButton button) => button switch { RemoteMouseButton.Left => 1, RemoteMouseButton.Right => 2, _ => 16 };
    static uint ButtonMessage(RemoteMouseButton button, bool up) => (button switch { RemoteMouseButton.Left => 0x201u, RemoteMouseButton.Right => 0x204u, _ => 0x207u }) + (up ? 1u : 0);
    static bool Contains(Rect rectangle, Point point) => point.X >= rectangle.Left && point.X < rectangle.Right && point.Y >= rectangle.Top && point.Y < rectangle.Bottom;
    static nint Pack(Point point) => (nint)(unchecked((uint)(ushort)(short)point.X) | ((uint)(ushort)(short)point.Y << 16));

    RemoteInputResult Result(bool accepted, string message)
    {
        if (accepted) posted++; else rejected++;
        lastResult = message;
        if (!accepted) InputProtocol.Log(message);
        return new(accepted, message);
    }

    static string Describe(nint window)
    {
        if (window == 0) return "未选择目标";
        GetWindowThreadProcessId(window, out var process);
        var name = new StringBuilder(256);
        GetWindowTextW(GetAncestor(window, 2), name, name.Capacity);
        return $"{name} (PID {process}, HWND 0x{window:X})";
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            var result = ReleaseAll();
            if (!result.Accepted) InputProtocol.Log("Local desktop router closed with unconfirmed releases: " + result.Message);
            disposed = true;
        }
    }

    sealed record HeldKey(nint Window, uint Key, uint MessageKey, nint Parameters, bool System);
    sealed record HeldButton(nint Window, Point ClientPoint);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInformation
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRectangle;
    }
    delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flag);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(nint window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll")] static extern nint ChildWindowFromPointEx(nint parent, Point point, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInformation information);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint SendMessageTimeoutW(nint window, uint message, nuint word, nint parameter, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint code, uint type, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder characters, int capacity, uint flags, nint layout);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern nint GetClassLongPtrW(nint window, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
