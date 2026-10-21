using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Frd;

namespace FrdLocalInputTests;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        var checks = new List<string>();
        var foreground = GetForegroundWindow();
        GetCursorPos(out var originalCursor);
        try
        {
            using var target = new LocalDemoInputTarget();
            Check(target.IsOpen && target.WindowHandle != 0, "Independent target window opened without activation.");
            Check(GetForegroundWindow() == foreground, "Creating the target preserved the foreground window.");
            var editor = target.EditorHandle;
            var original = Text(editor);
            foreach (var kind in new[] { RemoteInputKind.MouseMove, RemoteInputKind.MouseDown, RemoteInputKind.MouseUp })
                Check(!target.Inject(new(kind, .5, .5)).Accepted, $"{kind} refused.");
            Check(Text(editor) == original, "Refused mouse events did not alter the editor.");
            Check(target.Inject(new(RemoteInputKind.Wheel, WheelDelta: -120)).Accepted, "Wheel accepted.");
            Check((long)SendMessageW(editor, 0xce, 0, 0) > 0, "Wheel scrolled the directed editor without focus.");
            target.Inject(new(RemoteInputKind.Wheel, WheelDelta: 120));
            Check((long)SendMessageW(editor, 0xce, 0, 0) == 0, "Reverse wheel restored the first visible line.");

            Stroke(0x1e);
            Check(Text(editor).StartsWith("a"), "Scan-code A generated a lowercase character.");
            Stroke(0x0e);
            Check(Text(editor) == original, "Backspace removed the inserted character.");
            Check(target.Inject(new(RemoteInputKind.KeyDown, ScanCode: 0x2a)).Accepted, "Directed Shift accepted.");
            Stroke(0x1e);
            Check(Text(editor).StartsWith("A"), "Instance-local Shift produced uppercase text.");
            Check(target.ReleaseAll().Accepted, "ReleaseAll released only directed state.");
            Stroke(0x1e);
            Check(Text(editor).StartsWith("Aa"), "ReleaseAll cleared the local Shift state.");
            SendMessageW(editor, 0xb1, 0, 0);
            Stroke(0x4d, true);
            Stroke(0x30);
            Check(Text(editor).StartsWith("Aba"), "Extended right-arrow moved the directed editor caret.");
            Stroke(0x51, true);
            Check((long)SendMessageW(editor, 0xce, 0, 0) > 0, "Page Down scrolled the directed editor.");
            Stroke(0x49, true);
            Check((long)SendMessageW(editor, 0xce, 0, 0) == 0, "Page Up returned the directed editor to the first page.");
            Check(!target.Inject(new(RemoteInputKind.KeyDown, ScanCode: 256)).Accepted, "Invalid scan code refused.");

            using (var server = new RemoteInputServer(target))
            using (var client = await RemoteInputClient.ConnectAsync(server.Endpoint))
            {
                Check((await client.SetEnabledAsync(true)).Accepted, "Production TCP input channel enabled.");
                Check((await client.SendAsync(new(RemoteInputKind.Wheel, WheelDelta: -120))).Accepted, "Production TCP channel delivered a wheel event.");
                Check((long)SendMessageW(editor, 0xce, 0, 0) > 0, "TCP wheel changed the directed editor.");
                Check(!(await client.SendAsync(new(RemoteInputKind.MouseMove, .8, .8))).Accepted, "Target rejected movement over the production TCP channel.");
                await client.SetEnabledAsync(false);
            }
            var foregroundAfter = GetForegroundWindow();
            GetCursorPos(out var finalCursor);
            Check(PostMessageW(target.WindowHandle, 0x10, 0, 0), "Target close requested.");
            Check(SpinWait.SpinUntil(() => !target.IsOpen, 2000), "Target window closed.");
            Check(!target.Inject(new(RemoteInputKind.Wheel, WheelDelta: -120)).Accepted, "Closed target explicitly refused input.");
            Check(target.ReleaseAll().Accepted, "Closed target release is safe without global input.");
            var report = JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks,
                Observation = new { ForegroundUnchanged = foregroundAfter == foreground, PointerUnchanged = finalCursor.X == originalCursor.X && finalCursor.Y == originalCursor.Y,
                    Note = "Observed values only: concurrent human activity can change focus/pointer. The target has no system input or focus-injection API." },
                Scope = "Directed local window messages and production TCP; no system input injection, pointer movement, focus change or latency claim." }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (args.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!); File.WriteAllText(args[0], report); }
            return 0;

            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks.Add(message);
            }
            void Stroke(int scan, bool extended = false)
            {
                if (!target.Inject(new(RemoteInputKind.KeyDown, ScanCode: scan, Extended: extended)).Accepted || !target.Inject(new(RemoteInputKind.KeyUp, ScanCode: scan, Extended: extended)).Accepted)
                    throw new InvalidOperationException($"Scan code {scan:x} was refused.");
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static string Text(nint window)
    {
        var text = new StringBuilder(32768);
        SendMessageW(window, 0x0d, (nint)text.Capacity, text);
        return text.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SendMessageW(nint window, uint message, nint wParam, StringBuilder text);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
