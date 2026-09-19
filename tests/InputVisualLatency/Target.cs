using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Frd;

namespace Frd.InputVisualLatency;

sealed record TargetState(string Machine, int Process, long Window, string Instance, int Left, int Top, int Width, int Height,
    int SourceLeft, int SourceTop, int SourceWidth, int SourceHeight, long Frequency, bool Closed = false);

static class Target
{
    const uint Stimulus = 0x8000 + 217;
    const string ClassName = "FRD.InputVisualLatency.Target";
    static readonly Procedure procedure = WindowProc;
    static nint dark, light, background;
    static nint window;
    static string statePath = "";
    static TargetState state = null!;
    static long deadline;
    static int id;

    public static void Run(string path)
    {
        statePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var monitor = Win32InputInjector.ReadPrimaryMonitor();
        dark = CreateSolidBrush(0x202020); light = CreateSolidBrush(0xe0e0e0); background = CreateSolidBrush(0x505050);
        if (dark == 0 || light == 0 || background == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), ClassName = ClassName };
        if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var title = "FRD Visual Latency " + Guid.NewGuid().ToString("N");
        window = CreateWindowEx(0x08000000 | 0x00000080 | 8, ClassName, title, 0x80000000, monitor.Left + 24, monitor.Top + 24,
            528, 208, 0, 0, cls.Instance, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            ShowWindow(window, 4);
            GetClientRect(window, out var rect); var origin = new Point();
            if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
            state = new(Environment.MachineName, Environment.ProcessId, window, title, origin.X, origin.Y, rect.Right, rect.Bottom,
                monitor.Left, monitor.Top, monitor.Width, monitor.Height, Stopwatch.Frequency);
            Paint();
            File.WriteAllText(path, JsonSerializer.Serialize(state, AppConfiguration.JsonOptions));
            deadline = Environment.TickCount64 + 600000;
            if (SetTimer(window, 1, 200, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (IsWindow(window)) DestroyWindow(window);
            if (state != null) File.WriteAllText(path, JsonSerializer.Serialize(state with { Closed = true }, AppConfiguration.JsonOptions));
            DeleteObject(dark); DeleteObject(light); DeleteObject(background);
        }
    }

    public static void Validate(TargetState target)
    {
        if (target.Closed || target.Machine != Environment.MachineName || target.Frequency != Stopwatch.Frequency)
            throw new InvalidDataException("Only a live target on this machine is allowed; independent machine clocks cannot be subtracted.");
        var handle = (nint)target.Window;
        if (!IsWindow(handle) || !IsWindowVisible(handle)) throw new InvalidOperationException("Target window no longer exists or is not visible.");
        GetWindowThreadProcessId(handle, out var process);
        var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
        var cls = new StringBuilder(128); GetClassName(handle, cls, cls.Capacity);
        if (process != target.Process || title.ToString() != target.Instance || cls.ToString() != ClassName) throw new InvalidOperationException("Target window identity changed; refusing stimulus.");
        var point = new Point(); GetClientRect(handle, out var rect);
        if (!ClientToScreen(handle, ref point) || point.X != target.Left || point.Y != target.Top || rect.Right != target.Width || rect.Bottom != target.Height)
            throw new InvalidOperationException("Target geometry changed; regenerate its state.");
        if (target.Width < Marker.Left + Marker.Cell * Marker.Cells || target.Height < Marker.Top + Marker.Height ||
            target.Left < target.SourceLeft || target.Top < target.SourceTop || target.Left + target.Width > target.SourceLeft + target.SourceWidth || target.Top + target.Height > target.SourceTop + target.SourceHeight)
            throw new InvalidOperationException("Marker falls outside the captured primary monitor.");
    }

    public static long Stimulate(TargetState target, int value)
    {
        if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(value));
        if (SendMessageTimeout((nint)target.Window, Stimulus, (nuint)value, 0, 0x0002 | 0x0020, 1000, out var handled) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Target stimulus failed or timed out.");
        return checked((long)handled);
    }

    public static void ExcludePreview(nint hwnd)
    {
        if (hwnd == 0 || !SetWindowDisplayAffinity(hwnd, 0x11)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclude the test preview from capture; refusing recursive capture.");
    }

    static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == Stimulus)
            {
                if (wParam > 255) return 0;
                id = (int)wParam; Paint(); return (nint)Stopwatch.GetTimestamp();
            }
            if (message == 0x000f) { Paint(); ValidateRect(hwnd, 0); return 0; }
            if (message == 0x0113 && (Environment.TickCount64 >= deadline || File.Exists(statePath + ".stop"))) { DestroyWindow(hwnd); return 0; }
            if (message == 0x0010) { DestroyWindow(hwnd); return 0; }
            if (message == 0x0002) { PostQuitMessage(0); return 0; }
            if (message == 0x0021) return 3;
        }
        catch (Exception error) { Console.Error.WriteLine(error); PostQuitMessage(1); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    static void Paint()
    {
        if (window == 0) return;
        var dc = GetDC(window);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            GetClientRect(window, out var rect); FillRect(dc, ref rect, background);
            for (var cell = 0; cell < Marker.Cells; cell++)
            {
                var bright = cell == 0 || cell >= 2 && (id & (1 << (cell - 2))) != 0;
                var block = new Rect { Left = Marker.Left + cell * Marker.Cell, Top = Marker.Top, Right = Marker.Left + (cell + 1) * Marker.Cell, Bottom = Marker.Top + Marker.Height };
                if (FillRect(dc, ref block, bright ? light : dark) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            SetTextColor(dc, 0xffffff); SetBkMode(dc, 1);
            var caption = $"FRD marker {id} - no keyboard/mouse injection";
            if (!TextOut(dc, 16, 16, caption, caption.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { ReleaseDC(window, dc); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Reserved; }
    delegate nint Procedure(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool TextOut(nint dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SendMessageTimeout(nint hwnd, uint msg, nuint wp, nint lp, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
}
