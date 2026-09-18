using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Frd;

var checks = new List<object>();
void Check(bool passed, string name, object evidence)
{
    checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) throw new InvalidOperationException(name);
}
async Task WaitUntil(Func<bool> condition)
{
    var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
    while (!condition() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(10);
}
var passed = false;
Exception? failure = null;
try
{
    var mock = new MockInjector();
    using var server = new RemoteInputServer(mock);
    var client = await RemoteInputClient.ConnectAsync(server.Endpoint);
    var disabled = await client.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x1E));
    Check(!disabled.Accepted && mock.Events.Count == 0, "input is disabled by default", disabled);
    Check((await client.SetEnabledAsync(true)).Accepted, "explicit enable round trip succeeds", new { client.Enabled });
    RemoteInputEvent[] sequence =
    [
        new(RemoteInputKind.MouseMove, .1, .2),
        new(RemoteInputKind.MouseDown, .1, .2, RemoteMouseButton.Left),
        new(RemoteInputKind.MouseUp, .3, .4, RemoteMouseButton.Left),
        new(RemoteInputKind.MouseDown, .3, .4, RemoteMouseButton.Right),
        new(RemoteInputKind.MouseUp, .3, .4, RemoteMouseButton.Right),
        new(RemoteInputKind.MouseDown, .3, .4, RemoteMouseButton.Middle),
        new(RemoteInputKind.MouseUp, .3, .4, RemoteMouseButton.Middle),
        new(RemoteInputKind.Wheel, .3, .4, WheelDelta: -120),
        new(RemoteInputKind.KeyDown, ScanCode: 0x1D, Extended: true),
        new(RemoteInputKind.KeyDown, ScanCode: 0x1E),
        new(RemoteInputKind.KeyUp, ScanCode: 0x1E),
        new(RemoteInputKind.KeyUp, ScanCode: 0x1D, Extended: true)
    ];
    foreach (var input in sequence) Check((await client.SendAsync(input)).Accepted, $"accepted {input.Kind}", input);
    Check(mock.Events.ToArray().SequenceEqual(sequence), "reliable TCP preserves complete event ordering and values", new { Expected = sequence.Length, Actual = mock.Events.Count });
    await client.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x2A));
    await client.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x1D, Extended: true));
    await client.SendAsync(new(RemoteInputKind.MouseDown, .4, .4, RemoteMouseButton.Right));
    Check(mock.Held == 3, "mock tracks held chord and mouse button", new { mock.Held });
    var disabledAgain = await client.SetEnabledAsync(false);
    Check(disabledAgain.Accepted && !client.Enabled && mock.Held == 0, "manual disable releases all held input", new { client.Enabled, mock.Held, mock.Releases });
    await client.SetEnabledAsync(true);
    await client.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x38));
    await client.SendAsync(new(RemoteInputKind.MouseDown, .5, .5, RemoteMouseButton.Left));
    client.Dispose();
    await WaitUntil(() => mock.Held == 0);
    Check(mock.Held == 0, "abrupt TCP disconnect releases all held input", new { mock.Held, mock.Releases });
    using (var reconnected = await RemoteInputClient.ConnectAsync(server.Endpoint))
    {
        Check(!reconnected.Enabled, "reconnected input remains disabled", new { reconnected.Enabled });
        await reconnected.SetEnabledAsync(true);
        await reconnected.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x2A));
        await reconnected.SendAsync(new(RemoteInputKind.ReleaseAll));
        Check(mock.Held == 0, "explicit Escape release message clears held input", new { mock.Held });
        await reconnected.SetEnabledAsync(false);
    }

    var left = InputCoordinates.MapFromVideo(new(RemoteInputKind.MouseMove, 64d / 1279, 0), 1280, 720, 2880, 1800)!;
    var right = InputCoordinates.MapFromVideo(new(RemoteInputKind.MouseMove, 1215d / 1279, 1), 1280, 720, 2880, 1800)!;
    var center = InputCoordinates.MapFromVideo(new(RemoteInputKind.MouseMove, .5, .5), 1280, 720, 2880, 1800)!;
    Check(left.X == 0 && left.Y == 0 && right.X == 1 && right.Y == 1 && Math.Abs(center.X - .5) < 1e-12 && center.Y == .5,
        "16:10 source in 16:9 video maps 64px side bars correctly", new { Left = left, Right = right, Center = center });
    var blackLeft = InputCoordinates.MapFromVideo(new(RemoteInputKind.MouseDown, 20d / 1279, .5), 1280, 720, 2880, 1800);
    var blackRight = InputCoordinates.MapFromVideo(new(RemoteInputKind.Wheel, 1250d / 1279, .5, WheelDelta: 120), 1280, 720, 2880, 1800);
    var blackRelease = InputCoordinates.MapFromVideo(new(RemoteInputKind.MouseUp, .01, .5), 1280, 720, 2880, 1800);
    Check(blackLeft is null && blackRight is null && blackRelease?.Kind == RemoteInputKind.ReleaseAll,
        "black bars never click or scroll and release outside content cannot stick", new { blackLeft, blackRight, blackRelease });
    var shifted = InputCoordinates.Map(0, 0, new InputMonitorBounds(-1920, 120, 1920, 1080), -1920, 0, 3840, 1200);
    var shiftedEnd = InputCoordinates.Map(1, 1, new InputMonitorBounds(-1920, 120, 1920, 1080), -1920, 0, 3840, 1200);
    Check(shifted.X == -1920 && shifted.Y == 120 && shifted.AbsoluteX == 0 && shiftedEnd.X == -1 && shiftedEnd.Y == 1199 && shiftedEnd.AbsoluteY == 65535,
        "source monitor origin maps into virtual desktop absolute coordinates", new { shifted, shiftedEnd });
    var native = NativeFixture.Run();
    Check(native.DisabledHit == -1 && native.EnabledHit == 1 && native.AfterEscapeHit == -1,
        "native hit testing routes real pointer input only while enabled", new { native.DisabledHit, native.EnabledHit, native.AfterEscapeHit });
    Console.WriteLine(JsonSerializer.Serialize(new { native.BeforeEscape, native.All, native.Exits }));
    var translated = native.BeforeEscape.Where(x => x.Kind != RemoteInputKind.ReleaseAll).ToArray();
    Check(translated.Length == 4 && translated[0].Kind == RemoteInputKind.KeyDown &&
        translated[0].ScanCode == 0x1D && translated[0].Extended &&
        translated[1].Kind == RemoteInputKind.KeyUp && translated[1].Extended &&
        translated[2].Kind == RemoteInputKind.MouseMove && translated[2].X == 1 && translated[2].Y == 1 &&
        translated[3].Kind == RemoteInputKind.Wheel && translated[3].WheelDelta == 120,
        "native hidden test HWND captures scan codes, extended keys, pointer coordinates and wheel", new { native.BeforeEscape, native.All, native.Exits });
    Check(native.Exits == 1 && native.All.Count == native.BeforeEscape.Count + 1 && native.All[^1].Kind == RemoteInputKind.ReleaseAll,
        "native Escape disables capture and requests release without forwarding Escape", new { native.BeforeEscape, native.All, native.Exits });
    passed = true;
}
catch (Exception error) { failure = error; Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    Directory.CreateDirectory("results/ffmpeg-input");
    File.WriteAllText(args.FirstOrDefault() ?? "results/ffmpeg-input/regression.json", JsonSerializer.Serialize(new
    {
        Passed = passed, ActualSystemInputInjected = false, Scope = "Real localhost TCP + mock injector, plus SendMessage to an invisible test-owned HWND for native capture translation. No SendInput calls or real mouse/keyboard injection; no mouse button/capture testing on the user's desktop.",
        Checks = checks, Error = failure?.ToString()
    }, new JsonSerializerOptions { WriteIndented = true }));
}

static class NativeFixture
{
    public static (List<RemoteInputEvent> BeforeEscape, List<RemoteInputEvent> All, int Exits, long DisabledHit, long EnabledHit, long AfterEscapeHit) Run()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "FRD hidden input test", 0x80000000, -10000, -10000, 200, 100, 0, 0, 0, 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            List<RemoteInputEvent> events = new();
            var exits = 0;
            using var source = new NativeInputSource(hwnd);
            source.Input += events.Add;
            source.ExitRequested += () => exits++;
            var hitPoint = new Point { X = 99, Y = 49 };
            if (!ClientToScreen(hwnd, ref hitPoint)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var hitParam = (nint)((ushort)hitPoint.X | ((uint)(ushort)hitPoint.Y << 16));
            var disabledHit = (long)SendMessage(hwnd, 0x0084, 0, hitParam);
            source.SetEnabled(true);
            var enabledHit = (long)SendMessage(hwnd, 0x0084, 0, hitParam);
            SendMessage(hwnd, 0x0100, 0x11, (nint)(1 | (0x1D << 16) | (1 << 24)));
            SendMessage(hwnd, 0x0101, 0x11, unchecked((nint)(1u | (0x1Du << 16) | (1u << 24) | (1u << 30) | (1u << 31))));
            SendMessage(hwnd, 0x0200, 0, (nint)(199 | (99 << 16)));
            var point = new Point { X = 99, Y = 49 };
            if (!ClientToScreen(hwnd, ref point)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SendMessage(hwnd, 0x020A, (nuint)(120 << 16), (nint)((ushort)point.X | ((uint)(ushort)point.Y << 16)));
            var before = events.ToList();
            SendMessage(hwnd, 0x0100, 0x1B, (nint)(1 | (1 << 16)));
            SendMessage(hwnd, 0x0100, 0x41, (nint)(1 | (0x1E << 16)));
            var afterEscapeHit = (long)SendMessage(hwnd, 0x0084, 0, hitParam);
            return (before, events, exits, disabledHit, enabledHit, afterEscapeHit);
        }
        finally { if (!DestroyWindow(hwnd)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ClientToScreen(nint hwnd, ref Point point);
}

sealed class MockInjector : IRemoteInputInjector
{
    readonly object gate = new();
    readonly HashSet<(int Code, bool Extended)> keys = new();
    readonly HashSet<RemoteMouseButton> buttons = new();
    public ConcurrentQueue<RemoteInputEvent> Events { get; } = new();
    public int Releases { get; set; }
    public int Held { get { lock (gate) return keys.Count + buttons.Count; } }
    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        lock (gate)
        {
            Events.Enqueue(input);
            switch (input.Kind)
            {
                case RemoteInputKind.KeyDown: keys.Add((input.ScanCode, input.Extended)); break;
                case RemoteInputKind.KeyUp: keys.Remove((input.ScanCode, input.Extended)); break;
                case RemoteInputKind.MouseDown: buttons.Add(input.Button); break;
                case RemoteInputKind.MouseUp: buttons.Remove(input.Button); break;
                case RemoteInputKind.ReleaseAll: return ReleaseAll();
            }
            return new(true, "Mock accepted; no OS injection.");
        }
    }
    public RemoteInputResult ReleaseAll()
    {
        lock (gate) { keys.Clear(); buttons.Clear(); Releases++; return new(true, "Mock released all held input."); }
    }
}
