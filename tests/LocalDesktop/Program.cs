using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Frd;

namespace FrdLocalDesktopTests;

static class Program
{
    static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, IncludeFields = true };
    static readonly List<object> checks = new();
    static readonly List<object> snapshots = new();

    static async Task<int> Main(string[] args)
    {
        var reportPath = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "results/input-latency/local-browser-input.json");
        var runDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, "browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var marker = "FRD-Input-" + Guid.NewGuid().ToString("N");
        var pagePath = Path.Combine(runDirectory, "input.html");
        File.WriteAllText(pagePath, Html.Replace("__MARKER__", marker), new UTF8Encoding(false));
        var started = Stopwatch.StartNew();
        Process? browser = null;
        nint browserWindow = 0;
        string? failure = null;
        bool pointerStable = false;
        if (args.Contains("--demo", StringComparer.Ordinal)) throw new ArgumentException("The Demo path is not implemented. Use --background for the isolated native controller fixture.");
        var browserPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        try
        {
            if (!SetProcessDpiAwarenessContext(-4) && GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != 2)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish physical screen coordinate mode.");
            if (!File.Exists(browserPath)) throw new FileNotFoundException("Microsoft Edge was not found; pass its path as the second argument.", browserPath);
            var info = new ProcessStartInfo(browserPath) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal };
            foreach (var argument in new[] { "--user-data-dir=" + Path.Combine(runDirectory, "edge-profile"), "--no-first-run", "--no-default-browser-check",
                "--window-position=40,60", "--window-size=760,850", "--app=" + new Uri(pagePath).AbsoluteUri }) info.ArgumentList.Add(argument);
            browser = Process.Start(info) ?? throw new InvalidOperationException("Cannot start the isolated Edge test window.");
            var page = await WaitPageAsync("isolated browser page loaded", state => state.Width > 300 && state.Height > 250,
                () => { browserWindow = FindWindow(marker); return ReadPage(browserWindow, marker); }, 20000);
            Check(browserWindow != 0, "A separately identifiable Edge test window exists", new { Window = browserWindow.ToInt64(), BrowserProcess = browser.Id });
            var renderWindow = FindRenderer(browserWindow);
            Check(renderWindow != 0, "Visible Chromium rendering HWND found", new { Window = renderWindow.ToInt64() });
            GetClientRect(renderWindow, out var clientBounds);
            Check(Math.Abs((clientBounds.Right - clientBounds.Left) - page.Width * page.Dpr) <= 4 &&
                  Math.Abs((clientBounds.Bottom - clientBounds.Top) - page.Height * page.Dpr) <= 4,
                "CSS viewport and physical client size match through devicePixelRatio", new { Client = clientBounds, page.Width, page.Height, page.Dpr });
            if (!GetCursorPos(out var beforePointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var router = new LocalDesktopInputRouter();
            using var server = new RemoteInputServer(router);
            using var input = await RemoteInputClient.ConnectAsync(server.Endpoint);
            Check((await input.SetEnabledAsync(true)).Accepted, "Production TCP input channel enabled");
            var buttonPoint = Coordinates(page.Button);
            var moved = await input.SendAsync(new(RemoteInputKind.MouseMove, buttonPoint.X, buttonPoint.Y));
            Check(moved.Accepted, "Mouse movement reaches the virtual target without global pointer injection", moved);
            Check(router.Snapshot().VirtualPointer != null, "Virtual pointer position is recorded independently of the system cursor");
            await ClickAsync(buttonPoint, RemoteMouseButton.Left);
            page = await Observe("left click reached HTML button", state => state.Clicks == 1 && state.Downs >= 1 && state.Ups >= 1);
            await ClickAsync(buttonPoint, RemoteMouseButton.Right);
            page = await Observe("right button reached HTML without system pointer movement", state => state.RightDowns >= 1 && state.RightUps >= 1);
            await ClickAsync(buttonPoint, RemoteMouseButton.Middle);
            page = await Observe("middle button reached HTML without system pointer movement", state => state.MiddleDowns >= 1 && state.MiddleUps >= 1);
            Check((await input.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: 0x01))).Accepted, "Escape down exits the browser's native middle-button autoscroll");
            Check((await input.SendAsync(new(RemoteInputKind.KeyUp, ScanCode: 0x01))).Accepted, "Escape up delivered");
            await Task.Delay(150);
            var textPoint = Coordinates(page.Textbox);
            await ClickAsync(textPoint, RemoteMouseButton.Left);
            page = await Observe("click focused the HTML text box", state => state.Active == "text");
            foreach (var scan in new[] { 0x21, 0x13, 0x20 })
            {
                Check((await input.SendAsync(new(RemoteInputKind.KeyDown, ScanCode: scan))).Accepted, $"Scan {scan:x} key-down accepted");
                Check((await input.SendAsync(new(RemoteInputKind.KeyUp, ScanCode: scan))).Accepted, $"Scan {scan:x} key-up accepted");
            }
            page = await Observe("exact text appears once, with key-down and key-up events", state => state.Value == "frd" && state.KeyDowns >= 3 && state.KeyUps >= 3);
            var wheelPoint = Coordinates(new Rect { X = page.Width - 90, Y = Math.Min(page.Height - 80, 440), Width = 1, Height = 1 });
            Check((await input.SendAsync(new(RemoteInputKind.Wheel, wheelPoint.X, wheelPoint.Y, WheelDelta: -360))).Accepted,
                "Wheel accepted by production TCP input channel");
            page = await Observe("wheel reached webpage and actually scrolled", state => state.Wheels > 0 && state.ScrollY > 0);
            if (args.Contains("--background", StringComparer.Ordinal))
            {
                await using var control = new PreviewFixture(input, browserWindow);
                try
                {
                    Check(control.ControllerHasFocus, "Dedicated NativeInputSource controller owns foreground and keyboard focus");
                    var previousClicks = page.Clicks;
                    var previousText = page.Value;
                    var previousScroll = page.ScrollY;
                    var previousKeyDowns = page.KeyDowns;
                    var previousKeyUps = page.KeyUps;
                    await control.ClickAsync(buttonPoint.X, buttonPoint.Y);
                    page = await Observe("background Edge button received the native-preview/TCP click", state => state.Clicks == previousClicks + 1);
                    Check(control.ControllerHasFocus, "Background button click did not steal controller focus");
                    await control.ClickAsync(textPoint.X, textPoint.Y);
                    page = await Observe("background Edge textbox received the native-preview/TCP click", state => state.Active == "text");
                    await control.KeyAsync(0x58, 0x2d);
                    page = await Observe("background Edge received one X and its native key pair", state => state.Value == previousText + "x" &&
                        state.KeyDowns > previousKeyDowns && state.KeyUps > previousKeyUps);
                    Check(control.ControllerHasFocus, "Background text input did not steal controller focus");
                    await control.WheelAsync(wheelPoint.X, wheelPoint.Y, -360);
                    page = await Observe("background Edge actually scrolled through the native-preview/TCP path", state => state.ScrollY > previousScroll);
                    Check(control.ControllerHasFocus, "Controller retained foreground and keyboard focus after the full input sequence");
                    Check(control.NativeReleases == 0, "No unexpected WM_KILLFOCUS or WM_CAPTURECHANGED released held input");
                    var held = router.Snapshot();
                    Check(held.HeldKeys == 0 && held.HeldButtons == 0, "Ordered button and keyboard release left no held input");
                }
                finally { control.Snapshot("background browser case finished"); snapshots.Add(new { Stage = "foreground controller / background browser", State = control.Report() }); }
            }
            Check((await input.SendAsync(new(RemoteInputKind.ReleaseAll))).Accepted, "ReleaseAll accepted");
            Check((await input.SetEnabledAsync(false)).Accepted, "Input disabled and held events released");
            if (!GetCursorPos(out var afterPointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
            pointerStable = beforePointer.X == afterPointer.X && beforePointer.Y == afterPointer.Y;
            snapshots.Add(new { Stage = "pointer observation", Before = beforePointer, After = afterPointer,
                Unchanged = pointerStable, Note = "Concurrent human mouse activity can change this observation. No test calls global SendInput or SetCursorPos." });
            var routed = router.Snapshot();
            snapshots.Add(new { Stage = "local router final state", State = new { routed.PostedEvents, routed.RejectedEvents,
                TargetWindow = routed.TargetWindow.ToInt64(), routed.TargetProcessId, routed.TargetDescription,
                routed.LastResult, routed.HeldKeys, routed.HeldButtons, routed.VirtualPointer } });
            (double X, double Y) Coordinates(Rect rectangle)
            {
                var point = Physical(rectangle);
                var screen = Win32InputInjector.ReadPrimaryMonitor();
                if (point.X < screen.Left || point.Y < screen.Top || point.X >= screen.Left + screen.Width || point.Y >= screen.Top + screen.Height)
                    throw new InvalidOperationException("Browser test point is outside the captured primary screen.");
                return ((point.X - screen.Left) / (double)(screen.Width - 1), (point.Y - screen.Top) / (double)(screen.Height - 1));
            }

            Point Physical(Rect rectangle)
            {
                var point = new Point { X = (int)Math.Round((rectangle.X + rectangle.Width / 2) * page.Dpr),
                    Y = (int)Math.Round((rectangle.Y + rectangle.Height / 2) * page.Dpr) };
                if (!ClientToScreen(renderWindow, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return point;
            }

            async Task ClickAsync((double X, double Y) point, RemoteMouseButton button)
            {
                Check((await input.SendAsync(new(RemoteInputKind.MouseDown, point.X, point.Y, button))).Accepted, $"{button} mouse-down accepted");
                Check((await input.SendAsync(new(RemoteInputKind.MouseUp, point.X, point.Y, button))).Accepted, $"{button} mouse-up accepted");
            }

            Task<PageState> Observe(string stage, Func<PageState, bool> ready) =>
                WaitPageAsync(stage, ready, () => ReadPage(browserWindow, marker));
        }
        catch (Exception error) { Console.Error.WriteLine(error); failure = error.ToString(); }
        finally
        {
            if (browserWindow != 0 && IsWindow(browserWindow) && !PostMessageW(browserWindow, 0x10, 0, 0))
                Console.Error.WriteLine(new Win32Exception(Marshal.GetLastWin32Error(), "Cannot close the isolated Edge window."));
            if (browser != null)
            {
                try
                {
                    if (!browser.HasExited)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await browser.WaitForExitAsync(timeout.Token); }
                        catch (OperationCanceledException)
                        {
                            Console.Error.WriteLine("Isolated browser process did not exit after WM_CLOSE; stopping only its created process tree.");
                            browser.Kill(true); await browser.WaitForExitAsync();
                        }
                    }
                }
                catch (Exception error) { Console.Error.WriteLine("Isolated browser cleanup: " + error); failure ??= error.ToString(); }
                browser.Dispose();
            }
            var report = new { Passed = failure == null, Scope = "Production TCP input channel and actual Edge HTML mouse/key events; no global system input, physical mouse movement, existing browser profile, clipboard or video-latency claim.",
                DurationSeconds = started.Elapsed.TotalSeconds, RunDirectory = runDirectory, Browser = browserPath, PointerUnchangedObservation = pointerStable,
                Checks = checks, Snapshots = snapshots, Failure = failure };
            var text = JsonSerializer.Serialize(report, json); File.WriteAllText(reportPath, text); Console.WriteLine(text);
        }
        return failure == null ? 0 : 1;
    }

    static async Task<PageState> WaitPageAsync(string stage, Func<PageState, bool> ready, Func<PageState?> read, int timeoutMs = 5000)
    {
        var deadline = Stopwatch.GetTimestamp() + timeoutMs / 1000d * Stopwatch.Frequency;
        PageState? last = null;
        do
        {
            last = read() ?? last;
            if (last != null && ready(last)) { snapshots.Add(new { Stage = stage, State = last }); Check(true, stage); return last; }
            await Task.Delay(50);
        } while (Stopwatch.GetTimestamp() < deadline);
        snapshots.Add(new { Stage = stage, State = last });
        throw new TimeoutException($"{stage}; last browser page state: {JsonSerializer.Serialize(last, json)}");
    }

    static void Check(bool condition, string name, object? detail = null)
    {
        checks.Add(new { Passed = condition, Name = name, Detail = detail });
        if (!condition) throw new InvalidOperationException(name);
    }

    static nint FindWindow(string marker)
    {
        nint found = 0;
        EnumWindows((window, _) => { if (Title(window).StartsWith(marker, StringComparison.Ordinal)) { found = window; return false; } return true; }, 0);
        return found;
    }

    static nint FindRenderer(nint parent)
    {
        nint result = 0;
        EnumChildWindows(parent, (window, _) =>
        {
            var name = new StringBuilder(256); GetClassNameW(window, name, name.Capacity);
            if (name.ToString() == "Chrome_RenderWidgetHostHWND" && IsWindowVisible(window)) { result = window; return false; }
            return true;
        }, 0);
        return result;
    }

    static string Title(nint window)
    {
        if (window == 0) return "";
        var text = new StringBuilder(32768); GetWindowTextW(window, text, text.Capacity); return text.ToString();
    }

    static PageState? ReadPage(nint window, string marker)
    {
        var title = Title(window);
        var prefix = marker + "|";
        if (!title.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var encoded = new string(title[prefix.Length..].TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
        if (encoded.Length == 0) return null;
        return JsonSerializer.Deserialize<PageState>(Convert.FromBase64String(encoded), json);
    }

    sealed class PageState
    {
        public double Dpr { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Clicks { get; set; }
        public int Downs { get; set; }
        public int Ups { get; set; }
        public int RightDowns { get; set; }
        public int RightUps { get; set; }
        public int MiddleDowns { get; set; }
        public int MiddleUps { get; set; }
        public int KeyDowns { get; set; }
        public int KeyUps { get; set; }
        public int Wheels { get; set; }
        public double ScrollY { get; set; }
        public string? Active { get; set; }
        public string? Value { get; set; }
        public Rect Button { get; set; } = new();
        public Rect Textbox { get; set; } = new();
        public string[] Events { get; set; } = [];
    }

    sealed class Rect { public double X { get; set; } public double Y { get; set; } public double Width { get; set; } public double Height { get; set; } }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate bool EnumerateWindow(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumerateWindow callback, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(nint parent, EnumerateWindow callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    const string Html = """
<!doctype html><meta charset="utf-8"><title>__MARKER__</title>
<style>body{margin:0;height:2400px;background:linear-gradient(#183848,#749caa);color:white;font:20px sans-serif}h1{position:fixed;top:8px;left:32px;font-size:21px}button,input{position:fixed;left:32px;width:300px;height:64px;box-sizing:border-box;font:22px sans-serif}button{top:68px}input{top:150px}pre{position:fixed;left:32px;top:240px;white-space:pre-wrap;font:18px monospace;width:480px}</style>
<h1>FRD actual browser input test</h1><button id="button">Click target</button><input id="text" autocomplete="off" placeholder="Click, then type"><pre id="status"></pre>
<script>
const marker='__MARKER__';const button=document.getElementById('button'),text=document.getElementById('text'),status=document.getElementById('status');
const state={clicks:0,downs:0,ups:0,rightDowns:0,rightUps:0,middleDowns:0,middleUps:0,keyDowns:0,keyUps:0,wheels:0,events:[]};
function record(e){state.events.push(e.type+':'+(e.key??e.button??''));if(state.events.length>12)state.events.shift();}
button.addEventListener('click',()=>state.clicks++);
document.addEventListener('mousedown',e=>{state.downs++;if(e.button===2)state.rightDowns++;if(e.button===1)state.middleDowns++;record(e)});
document.addEventListener('mouseup',e=>{state.ups++;if(e.button===2)state.rightUps++;if(e.button===1)state.middleUps++;record(e)});
document.addEventListener('contextmenu',e=>e.preventDefault());document.addEventListener('auxclick',e=>e.preventDefault());
document.addEventListener('keydown',e=>{state.keyDowns++;record(e)});document.addEventListener('keyup',e=>{state.keyUps++;record(e)});
document.addEventListener('wheel',e=>{state.wheels++;record(e)},{passive:true});
function rect(element){const r=element.getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height}}
function publish(){const result={...state,dpr:devicePixelRatio,width:innerWidth,height:innerHeight,scrollY,active:document.activeElement?.id??'',value:text.value,button:rect(button),textbox:rect(text)};document.title=marker+'|'+btoa(JSON.stringify(result));status.textContent='Clicks: '+state.clicks+'\nText: '+text.value+'\nWheel events: '+state.wheels+'\nScroll: '+scrollY+'\nKeys down/up: '+state.keyDowns+'/'+state.keyUps;}
setInterval(publish,100);publish();
</script>
""";
}
