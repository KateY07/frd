using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Frd;

sealed class DiagnosticWatermark : Window
{
    const int ExtendedStyle = -20, NoActivate = 0x08000000;
    readonly SubclassProcedure procedure;
    nint handle;

    public DiagnosticWatermark()
    {
        procedure = WindowProcedure;
        Title = "FRD 诊断水印";
        WindowDecorations = WindowDecorations.None;
        CanResize = false; ShowInTaskbar = false; ShowActivated = false;
        Focusable = false; IsHitTestVisible = false;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    protected override void OnOpened(EventArgs e)
    {
        handle = TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Missing diagnostic watermark HWND.");
        var style = GetWindowLong(handle, ExtendedStyle);
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(handle, ExtendedStyle, style | NoActivate) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot disable watermark activation.");
        if (!SetWindowSubclass(handle, procedure, 1, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot make diagnostics click-through.");
        base.OnOpened(e);
    }

    nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        // Both windows belong to the UI thread; HTTRANSPARENT continues hit testing beneath this HWND.
        if (message == 0x0084) return -1;
        if (message == 0x0021) return 3;
        if (message == 0x0082 && !RemoveWindowSubclass(hwnd, procedure, id))
            Console.Error.WriteLine("[watermark] Cannot detach native hit-test handler: " + Marshal.GetLastWin32Error());
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public bool VerifyPassThrough(nint preview)
    {
        var origin = this.PointToScreen(new Point(8, 8));
        var position = (nint)((origin.X & 0xffff) | ((origin.Y & 0xffff) << 16));
        return handle != 0 && preview != 0 && !Focusable && !IsHitTestVisible &&
            (GetWindowLong(handle, ExtendedStyle) & NoActivate) != 0 &&
            GetWindowThreadProcessId(handle, out _) == GetWindowThreadProcessId(preview, out _) &&
            SendMessage(handle, 0x0084, 0, position) == -1 && SendMessage(handle, 0x0021, 0, 0) == 3;
    }

    delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
