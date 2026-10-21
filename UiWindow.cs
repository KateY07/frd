using System.Runtime.InteropServices;
using static Frd.UiNative;

namespace Frd;

enum UiWindowState { Normal, Minimized, Maximized, FullScreen }

sealed class UiClosingEventArgs { public bool Cancel { get; set; } }

/// <summary>A native top-level window. Subclasses draw with D2D and receive pointer/keyboard messages already decoded.</summary>
abstract unsafe class UiWindow : IDisposable
{
    const string ClassName = "FrdUiWindow";
    static bool classRegistered;
    WINDOWPLACEMENT restorePlacement;
    uint restoreStyle, restoreExStyle;
    bool fullScreen, destroyed;
    public nint Handle { get; internal set; }
    public bool IsMainWindow { get; set; }
    public int MinWidth { get; set; } = 0;
    public int MinHeight { get; set; } = 0;
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }
    public uint Dpi { get; private set; } = 96;
    public float Scale => Dpi / 96f;
    public bool IsDestroyed => destroyed;
    public event Action<UiClosingEventArgs>? Closing;
    public event Action? Closed;
    public event Action? Opened;
    public event Action? SizeChanged;

    protected void CreateNative(string title, uint style, uint exStyle, int x, int y, int width, int height, nint parent = 0)
    {
        EnsureClass();
        UiThread.BeginCreate(this);
        var hwnd = CreateWindowEx(exStyle, ClassName, title, style, x, y, width, height, parent, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create window.");
        Handle = hwnd;
        Dpi = GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi : 96;
        UpdateClientSize();
    }

    static void EnsureClass()
    {
        if (classRegistered) return;
        fixed (char* name = ClassName)
        {
            var cls = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), style = 0x0008 /* CS_DBLCLKS */, lpfnWndProc = &UiThread.WindowProcedure, hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(0, IDC_ARROW), lpszClassName = name
            };
            if (RegisterClassEx(cls) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot register the window class.");
        }
        classRegistered = true;
    }

    internal nint Dispatch(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_CLOSE:
            {
                var args = new UiClosingEventArgs();
                Closing?.Invoke(args);
                if (!args.Cancel) DestroyWindow(Handle);
                return 0;
            }
            case WM_DESTROY:
                destroyed = true;
                try { OnDestroying(); } catch (Exception error) { CrashLog.Write("ui-destroying", error); }
                UiThread.Unregister(Handle);
                Closed?.Invoke();
                if (IsMainWindow) UiThread.Quit();
                return 0;
            case WM_SIZE:
                UpdateClientSize();
                OnResized();
                SizeChanged?.Invoke();
                OnMessage(message, wParam, lParam, out _);
                return 0;
            case WM_DPICHANGED:
            {
                Dpi = (uint)HiWord(wParam);
                var suggested = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(Handle, 0, suggested.Left, suggested.Top, suggested.Width, suggested.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                OnDpiChanged();
                return 0;
            }
            case WM_GETMINMAXINFO when MinWidth > 0 || MinHeight > 0:
            {
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var frame = new RECT { Right = MinWidth, Bottom = MinHeight };
                var style = (uint)GetWindowLongPtr(Handle, GWL_STYLE); var ex = (uint)GetWindowLongPtr(Handle, GWL_EXSTYLE);
                AdjustWindowRectExForDpi(ref frame, style, false, ex, Dpi);
                info.ptMinTrackSize = new POINT { X = frame.Width, Y = frame.Height };
                Marshal.StructureToPtr(info, lParam, false);
                return 0;
            }
            case WM_ERASEBKGND: return 1;
        }
        var result = OnMessage(message, wParam, lParam, out var handled);
        return handled ? result : DefWindowProc(Handle, message, wParam, lParam);
    }

    /// <summary>Return true in <paramref name="handled"/> to stop default processing.</summary>
    protected virtual nint OnMessage(uint message, nuint wParam, nint lParam, out bool handled) { handled = false; return 0; }
    protected virtual void OnResized() { }
    protected virtual void OnDpiChanged() { }
    protected virtual void OnDestroying() { }
    protected void RaiseOpened() => Opened?.Invoke();

    void UpdateClientSize()
    {
        if (Handle != 0 && GetClientRect(Handle, out var rect)) { ClientWidth = rect.Width; ClientHeight = rect.Height; }
    }

    public void Show(int command = SW_SHOW) { ShowWindow(Handle, command); }
    public void Hide() { ShowWindow(Handle, SW_HIDE); }
    public bool IsVisible => IsWindowVisible(Handle);
    public void Close() { if (UiThread.CheckAccess()) { if (!destroyed) Dispatch(WM_CLOSE, 0, 0); } else PostMessage(Handle, WM_CLOSE, 0, 0); }
    public void SetTitle(string title) { if (Handle != 0) SetWindowText(Handle, title); }

    public (int X, int Y, int Width, int Height) OuterBounds
    {
        get { GetWindowRect(Handle, out var rect); return (rect.Left, rect.Top, rect.Width, rect.Height); }
    }

    public void SetOuterBounds(int x, int y, int width, int height, bool activate = false) =>
        SetWindowPos(Handle, 0, x, y, width, height, SWP_NOZORDER | (activate ? 0 : SWP_NOACTIVATE));

    public void SetPosition(int x, int y) => SetWindowPos(Handle, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Resizes so the client area is the given size (in pixels).</summary>
    public void SetClientSize(int width, int height)
    {
        var frame = new RECT { Right = width, Bottom = height };
        AdjustWindowRectExForDpi(ref frame, (uint)GetWindowLongPtr(Handle, GWL_STYLE), false, (uint)GetWindowLongPtr(Handle, GWL_EXSTYLE), Dpi);
        SetWindowPos(Handle, 0, 0, 0, frame.Width, frame.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public (int X, int Y, int Width, int Height) MonitorWorkArea()
    {
        var monitor = MonitorFromWindow(Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(monitor, ref info);
        return (info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height);
    }

    public void CenterOnScreen()
    {
        var area = MonitorWorkArea(); var bounds = OuterBounds;
        SetPosition(area.X + Math.Max(0, (area.Width - bounds.Width) / 2), area.Y + Math.Max(0, (area.Height - bounds.Height) / 2));
    }

    public UiWindowState State
    {
        get => fullScreen ? UiWindowState.FullScreen : IsIconic(Handle) ? UiWindowState.Minimized : IsZoomed(Handle) ? UiWindowState.Maximized : UiWindowState.Normal;
        set
        {
            if (value == UiWindowState.FullScreen) { EnterFullScreen(); return; }
            if (fullScreen) LeaveFullScreen();
            ShowWindow(Handle, value switch { UiWindowState.Minimized => SW_MINIMIZE, UiWindowState.Maximized => SW_SHOWMAXIMIZED, _ => SW_RESTORE });
        }
    }

    void EnterFullScreen()
    {
        if (fullScreen) return;
        restorePlacement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        GetWindowPlacement(Handle, ref restorePlacement);
        restoreStyle = (uint)GetWindowLongPtr(Handle, GWL_STYLE); restoreExStyle = (uint)GetWindowLongPtr(Handle, GWL_EXSTYLE);
        var monitor = MonitorFromWindow(Handle, 2);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(monitor, ref info);
        fullScreen = true;
        SetWindowLongPtr(Handle, GWL_STYLE, (nint)((restoreStyle & ~(WS_OVERLAPPEDWINDOW)) | WS_POPUP | WS_VISIBLE));
        SetWindowPos(Handle, 0, info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    void LeaveFullScreen()
    {
        if (!fullScreen) return;
        fullScreen = false;
        SetWindowLongPtr(Handle, GWL_STYLE, (nint)restoreStyle);
        SetWindowPlacement(Handle, ref restorePlacement);
        SetWindowPos(Handle, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
    }

    public void ToggleFullScreen() { if (fullScreen) LeaveFullScreen(); else EnterFullScreen(); }

    public bool ExcludeFromCapture()
    {
        // WDA_EXCLUDEFROMCAPTURE: screen capture (including this app's own desktop capture) does not see the window.
        if (Environment.GetEnvironmentVariable("FRD_UI_ALLOW_CAPTURE") == "1") return true;   // support/diagnostic aid: lets screenshots include FRD windows
        return SetWindowDisplayAffinity(Handle, 0x11);
    }

    public void SetTopmost(bool topmost) => SetWindowPos(Handle, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void Dispose()
    {
        if (!destroyed && Handle != 0) DestroyWindow(Handle);
    }
}
