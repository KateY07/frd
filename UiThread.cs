using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Frd.UiNative;

namespace Frd;

/// <summary>The UI thread: a Win32 message loop plus a work queue (the replacement for Dispatcher.UIThread).</summary>
static unsafe class UiThread
{
    const uint WM_RUN = WM_APP + 1;
    static readonly ConcurrentQueue<Action> Work = new();
    static readonly Dictionary<nint, UiWindow> Windows = new();
    static readonly Dictionary<nuint, UiTimer> Timers = new();
    static int threadId;
    static nint messageWindow;
    static nuint nextTimerId = 100;
    [ThreadStatic] static UiWindow? creating;

    public static bool CheckAccess() => Environment.CurrentManagedThreadId == threadId;

    /// <summary>Must run on the thread that will pump messages (the process main thread).</summary>
    public static void Initialize()
    {
        if (messageWindow != 0) return;
        threadId = Environment.CurrentManagedThreadId;
        // Windows opts a process into per-monitor DPI awareness here; the manifest also declares it.
        try { SetProcessDpiAwarenessContext(-4); } catch (Exception) { }
        fixed (char* name = "FrdUiMessage")
        {
            var cls = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &MessageProcedure, hInstance = GetModuleHandle(null), lpszClassName = name };
            RegisterClassEx(cls);
        }
        messageWindow = CreateWindowEx(0, "FrdUiMessage", "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandle(null), 0);
        if (messageWindow == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the UI message window.");
    }

    public static void Post(Action action)
    {
        Work.Enqueue(action);
        if (messageWindow != 0) PostMessage(messageWindow, WM_RUN, 0, 0);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits; runs inline when already there.</summary>
    public static T Invoke<T>(Func<T> action)
    {
        if (CheckAccess()) return action();
        using var done = new ManualResetEventSlim();
        T result = default!; Exception? failure = null;
        Post(() => { try { result = action(); } catch (Exception error) { failure = error; } finally { done.Set(); } });
        done.Wait();
        if (failure != null) throw failure;
        return result;
    }

    public static int Run()
    {
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(message);
            DispatchMessage(message);
        }
        return 0;
    }

    public static void Quit() => PostQuitMessage(0);

    internal static void BeginCreate(UiWindow window) => creating = window;
    internal static void Register(nint hwnd, UiWindow window) { lock (Windows) Windows[hwnd] = window; }
    internal static void Unregister(nint hwnd) { lock (Windows) Windows.Remove(hwnd); }
    internal static int OpenWindowCount { get { lock (Windows) return Windows.Count; } }

    internal static nuint StartTimer(UiTimer timer, int milliseconds)
    {
        var id = nextTimerId++;
        Timers[id] = timer;
        SetTimer(messageWindow, id, (uint)Math.Max(10, milliseconds), 0);
        return id;
    }

    internal static void StopTimer(nuint id) { KillTimer(messageWindow, id); Timers.Remove(id); }

    [UnmanagedCallersOnly]
    static nint MessageProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WM_RUN)
            {
                while (Work.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception error) { CrashLog.Write("ui-work", error); }
                }
                return 0;
            }
            if (message == WM_TIMER && Timers.TryGetValue(wParam, out var timer)) { timer.Fire(); return 0; }
        }
        catch (Exception error) { CrashLog.Write("ui-message", error); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    internal static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        UiWindow? window;
        lock (Windows) Windows.TryGetValue(hwnd, out window);
        if (window == null && creating != null) { window = creating; creating = null; window.Handle = hwnd; Register(hwnd, window); }
        if (window == null) return DefWindowProc(hwnd, message, wParam, lParam);
        try { return window.Dispatch(message, wParam, lParam); }
        catch (Exception error)
        {
            CrashLog.Write("ui-window", error);
            return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}

/// <summary>A repeating UI-thread timer (WM_TIMER based; roughly 15 ms granularity).</summary>
sealed class UiTimer : IDisposable
{
    nuint id;
    public int IntervalMilliseconds { get; set; } = 100;
    public bool IsEnabled => id != 0;
    public event Action? Tick;

    public UiTimer(int milliseconds = 100) => IntervalMilliseconds = milliseconds;

    public void Start() { Stop(); id = UiThread.StartTimer(this, IntervalMilliseconds); }
    public void Stop() { if (id != 0) { UiThread.StopTimer(id); id = 0; } }
    internal void Fire()
    {
        try { Tick?.Invoke(); }
        catch (Exception error) { CrashLog.Write("ui-timer", error); }
    }
    public void Dispose() => Stop();
}
