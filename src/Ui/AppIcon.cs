using System.Runtime.InteropServices;

namespace Frd;

/// <summary>The fixed FRD icon, embedded in FRD.exe (assets/frd.ico). Windows does not use the exe's icon for a window by itself, so it is set on the window.</summary>
static class AppIcon
{
    const uint WM_SETICON = 0x0080;

    public static void Apply(nint window)
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || ExtractIconEx(path, 0, out var large, out var small, 1) == 0) return;
            if (large != 0) SendMessage(window, WM_SETICON, 1 /* ICON_BIG */, large);
            if (small != 0) SendMessage(window, WM_SETICON, 0 /* ICON_SMALL */, small);
        }
        catch (Exception error) { Console.Error.WriteLine("[icon] " + error.Message); }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint icons);
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
