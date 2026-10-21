using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Frd;

/// <summary>
/// Tells whether someone is using the host's own keyboard or mouse. While they are, remote input is dropped so the two people do not fight
/// over the pointer, and the controller is told to wait.
/// <para>
/// Uses low-level hooks and looks only at the "injected" flag and the time: key codes, characters and positions are never read or stored.
/// Input that FRD itself injects (or any other SendInput caller) carries the injected flag and is ignored. The hooks need a message loop,
/// so <see cref="Start"/> must run on the UI thread. They do not see the secure desktop (UAC, lock screen).
/// </para>
/// </summary>
static unsafe class LocalInputMonitor
{
    const int WhKeyboardLl = 13, WhMouseLl = 14, HcAction = 0;
    const uint MouseInjected = 0x01, KeyboardInjected = 0x10;
    static nint mouseHook, keyboardHook;
    static long lastPhysical;

    /// <summary>How long after the last physical event the host still counts as "being used".</summary>
    public static int HoldMilliseconds { get; set; } = 1500;

    public static bool IsRunning => mouseHook != 0 || keyboardHook != 0;

    public static bool IsActive
    {
        get
        {
            var last = Volatile.Read(ref lastPhysical);
            return last != 0 && (Stopwatch.GetTimestamp() - last) * 1000 / Stopwatch.Frequency < HoldMilliseconds;
        }
    }

    public static void Start()
    {
        if (IsRunning) return;
        var module = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(WhMouseLl, (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&MouseProc, module, 0);
        keyboardHook = SetWindowsHookEx(WhKeyboardLl, (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&KeyboardProc, module, 0);
        if (mouseHook == 0 || keyboardHook == 0)
            Console.Error.WriteLine($"[input] Local input monitor is only partly available (hook error {Marshal.GetLastWin32Error()}); remote input will not yield to local use.");
    }

    public static void Stop()
    {
        if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
        if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
        mouseHook = keyboardHook = 0;
        Volatile.Write(ref lastPhysical, 0);
    }

    [StructLayout(LayoutKind.Sequential)] struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public nuint Extra; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint MouseProc(int code, nuint wParam, nint lParam)
    {
        if (code == HcAction)
        {
            var data = (MouseHookData*)lParam;
            if ((data->Flags & MouseInjected) == 0 && data->Extra != Win32InputInjector.InjectionMarker) Volatile.Write(ref lastPhysical, Stopwatch.GetTimestamp());
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint KeyboardProc(int code, nuint wParam, nint lParam)
    {
        if (code == HcAction)
        {
            var data = (KeyboardHookData*)lParam;
            if ((data->Flags & KeyboardInjected) == 0 && data->Extra != Win32InputInjector.InjectionMarker) Volatile.Write(ref lastPhysical, Stopwatch.GetTimestamp());
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] static extern nint SetWindowsHookEx(int id, nint callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
}
