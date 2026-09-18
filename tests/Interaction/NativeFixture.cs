using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Frd;

namespace Frd.InteractionTests;

static class Fixture
{
    static readonly WindowProcedure procedure = WindowProc;
    static readonly List<object> inputs = new();
    static string root = "", lastCommand = "", commandError = "";
    static object? clipboardResult;
    static ClipboardBackup? backup;
    static nint window, foreground;
    static Point originalPointer;
    static long deadline;
    static bool passive;
    public static void Run(string path, bool clipboardOnly = false)
    {
        passive = clipboardOnly;
        root = path; Directory.CreateDirectory(root);
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        foreground = GetForegroundWindow(); GetCursorPos(out originalPointer);
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), ClassName = "FRD.InteractionFixture", Background = (nint)6, Cursor = LoadCursor(0, 32512) };
        if (RegisterClassEx(ref cls) == 0) throw new Win32Exception();
        window = CreateWindowEx(passive ? 0u : 8u, cls.ClassName, "FRD 自动化测试目标（测试结束自动关闭）", passive ? 0u : 0x10CF0000u, 32, Math.Max(32, bounds.Height - 580), Math.Min(1100, bounds.Width / 2 - 40), 520, 0, 0, cls.Instance, 0);
        if (window == 0) throw new Win32Exception();
        if (!passive) { ShowWindow(window, 5); ShowWindow(window, 5); }
        ImmAssociateContext(window, 0);
        deadline = Environment.TickCount64 + 600000;
        SetTimer(window, 1, 75, 0);
        Publish();
        try { while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); } }
        finally
        {
            backup?.Dispose(); backup = null;
            if (!passive) { SetCursorPos(originalPointer.X, originalPointer.Y); if (foreground != 0) SetForegroundWindow(foreground); }
            File.WriteAllText(Path.Combine(root, "closed.json"), JsonSerializer.Serialize(new { Closed = true, ClipboardRestored = true }));
        }
    }
    static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == 0x0020 && ((long)lParam & 0xFFFF) == 1)
            {
                GetCursorPos(out var p); ScreenToClient(hwnd, ref p); GetClientRect(hwnd, out var r);
                SetCursor(LoadCursor(0, p.X < r.Right / 3 ? 32644 : p.X < r.Right * 2 / 3 ? 32645 : 32513)); return 1;
            }
            if (message is 0x0200 or 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x020A or 0x0100 or 0x0101 or 0x0104 or 0x0105 or 0x0102)
            {
                if (message == 0x0201) SetFocus(hwnd);
                GetCursorPos(out var p);
                if (inputs.Count >= 2000) inputs.RemoveAt(0);
                inputs.Add(new { Message = message, WParam = (long)wParam, LParam = (long)lParam, ScreenX = p.X, ScreenY = p.Y, Tick = Environment.TickCount64, Injected = GetMessageExtraInfo() == (nint)Win32InputInjector.InjectionMarker });
            }
            if (message == 0x0113)
            {
                ReadCommand(); Publish();
                if (Environment.TickCount64 >= deadline || File.Exists(Path.Combine(root, "stop"))) CloseFixture(hwnd);
                return 0;
            }
            if (message == 0x0010) { CloseFixture(hwnd); return 0; }
            if (message == 0x0002) { PostQuitMessage(0); return 0; }
        }
        catch (Exception error) { Console.Error.WriteLine(error); commandError = error.ToString(); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    static void CloseFixture(nint hwnd)
    {
        backup?.Dispose(); backup = null;
        DestroyWindow(hwnd);
    }
    static void ReadCommand()
    {
        var file = Path.Combine(root, "command.json"); if (!File.Exists(file)) return;
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (IOException error) { Console.Error.WriteLine("Command file busy: " + error.Message); return; }
        catch (JsonException error) { Console.Error.WriteLine("Command file incomplete: " + error.Message); return; }
        using (document)
        {
            var command = document.RootElement;
            var id = command.GetProperty("id").GetString()!;
            if (id == lastCommand) return;
            lastCommand = id; commandError = "";
            var clipboard = new WindowsClipboard(window);
            switch (command.GetProperty("kind").GetString())
            {
                case "beginClipboard": backup ??= new(window); break;
                case "writeText":
                    if (backup == null) throw new InvalidOperationException("Clipboard backup required.");
                    clipboard.Write(new(command.GetProperty("text").GetString(), Origin: "FRD-test")); break;
                case "writeFiles":
                    if (backup == null) throw new InvalidOperationException("Clipboard backup required.");
                    clipboard.Write(new(Paths: TestData.Create(root, "remote-" + id), Origin: "FRD-test")); break;
                case "readClipboard":
                    var value = clipboard.Read();
                    clipboardResult = new { value?.Text, value?.Paths, Digests = value?.Paths?.Select(TestData.Digest).ToArray() }; break;
                case "pasteFiles":
                    var paths = clipboard.Read()?.Paths ?? throw new InvalidOperationException("No received files on Windows clipboard.");
                    var expected = command.GetProperty("digests").EnumerateArray().Select(item => item.GetString()).ToArray();
                    if (!paths.Select(TestData.Digest).SequenceEqual(expected)) throw new InvalidOperationException("Clipboard changed before paste test.");
                    clipboardResult = ShellCopyFixture.Run(paths, Path.Combine(root, "paste-" + id)); break;
                case "clearInput": inputs.Clear(); break;
                case "restoreClipboard": backup?.Dispose(); backup = null; break;
                default: throw new ArgumentException("Unknown fixture command.");
            }
        }
    }
    static void Publish()
    {
        if (window == 0) return;
        GetClientRect(window, out var rect); var origin = new Point(); ClientToScreen(window, ref origin);
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        var center = new Point { X = origin.X + rect.Right / 2, Y = origin.Y + rect.Bottom / 2 };
        var report = new { Ready = true, Visible = IsWindowVisible(window), TargetReady = WindowFromPoint(center) == window, FixtureWindow = (long)window, SourceWidth = bounds.Width, SourceHeight = bounds.Height, Left = origin.X, Top = origin.Y, Width = rect.Right, Height = rect.Bottom,
            LastCommand = lastCommand, CommandError = commandError, Clipboard = clipboardResult, Inputs = inputs.ToArray() };
        var target = Path.Combine(root, "state.json"); File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(report, AppConfiguration.JsonOptions)); File.Move(target + ".tmp", target, true);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(uint ex, string cls, string text, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint procedure);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    [DllImport("imm32.dll")] static extern nint ImmAssociateContext(nint hwnd, nint context);
}

static class NativeClipboardTest
{
    public static int Run(string report)
    {
        var hwnd = CreateWindowEx(0, "STATIC", "FRD clipboard test", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        if (hwnd == 0) throw new Win32Exception();
        try
        {
            using var backup = new ClipboardBackup(hwnd);
            var real = new WindowsClipboard(hwnd); var memory = new MemoryClipboard();
            var root = Path.Combine(Path.GetDirectoryName(report)!, "native-data-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            async Task Test()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                using var a = new TcpClient { NoDelay = true }; await a.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                using var b = await listener.AcceptTcpClientAsync(); listener.Stop(); b.NoDelay = true;
                await using var left = new ClipboardSyncSession(a, real, Path.Combine(root, "real-cache"));
                await using var right = new ClipboardSyncSession(b, memory, Path.Combine(root, "memory-cache"));
                foreach (var (source, destination, name) in new (IClipboardAccess, IClipboardAccess, string)[] { (real, memory, "OS to peer"), (memory, real, "peer to OS") })
                {
                    var text = name + " 中文\r\n🙂";
                    source.Write(new(text, Origin: "FRD-test")); await Program.Wait(() => destination.Read()?.Text == text);
                    var paths = TestData.Create(root, name.Replace(' ', '-'));
                    var before = destination.Sequence; source.Write(new(Paths: paths, Origin: "FRD-test"));
                    await Program.Wait(() => destination.Sequence != before);
                    if (!TestData.Equal(paths, destination.Read()!.Paths!)) throw new Exception("Native CF_HDROP file content mismatch: " + name);
                    Console.WriteLine("PASS " + name + ": text, file, nested and empty directory");
                }
                var sent = left.Snapshot.Sent + right.Snapshot.Sent; await Task.Delay(350);
                if (sent != left.Snapshot.Sent + right.Snapshot.Sent) throw new Exception("Native clipboard echo loop");
            }
            Test().GetAwaiter().GetResult();
        }
        finally { DestroyWindow(hwnd); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Bidirectional = true, Formats = new[] { "CF_UNICODETEXT", "CF_HDROP", "Preferred DropEffect COPY" }, ClipboardRestored = true }, AppConfiguration.JsonOptions)); return 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(uint ex, string cls, string text, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
}

sealed class ClipboardBackup : IDisposable
{
    readonly nint owner;
    readonly List<(uint Format, nint Handle)> saved = new();
    public ClipboardBackup(nint owner)
    {
        this.owner = owner; Open();
        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                var handle = GetClipboardData(format);
                if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot preserve existing clipboard format " + format);
                nint clone;
                if (format == 2) clone = CopyImage(handle, 0, 0, 0, 0x2000);
                else if (format == 14) clone = CopyEnhMetaFile(handle, null);
                else
                {
                    var size = GlobalSize(handle);
                    if (size == 0 || size > 128 * 1024 * 1024 || format is 3 or 9 or 0x80)
                        throw new InvalidOperationException("Existing clipboard cannot be safely backed up; test leaves it untouched. Format " + format);
                    clone = GlobalAlloc(2, size); var source = GlobalLock(handle); var destination = GlobalLock(clone);
                    if (clone == 0 || source == 0 || destination == 0) throw new Win32Exception();
                    try { var bytes = new byte[(int)size]; Marshal.Copy(source, bytes, 0, bytes.Length); Marshal.Copy(bytes, 0, destination, bytes.Length); }
                    finally { GlobalUnlock(handle); GlobalUnlock(clone); }
                }
                if (clone == 0) throw new Win32Exception(); saved.Add((format, clone));
            }
        }
        catch { Free(); throw; }
        finally { CloseClipboard(); }
    }
    public void Dispose()
    {
        Open();
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception();
            for (var i = 0; i < saved.Count; i++)
            {
                var item = saved[i];
                if (SetClipboardData(item.Format, item.Handle) == 0) throw new Win32Exception();
                saved[i] = (item.Format, 0);
            }
        }
        finally { CloseClipboard(); Free(); }
    }
    void Open()
    {
        for (var i = 0; i < 40; i++) { if (OpenClipboard(owner)) return; Thread.Sleep(15); }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Clipboard busy during test backup/restore.");
    }
    void Free()
    {
        foreach (var (format, handle) in saved)
        { if (handle == 0) continue; if (format == 2) DeleteObject(handle); else if (format == 14) DeleteEnhMetaFile(handle); else GlobalFree(handle); }
        saved.Clear();
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint handle);
    [DllImport("user32.dll")] static extern nint CopyImage(nint image, uint type, int x, int y, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern nint CopyEnhMetaFile(nint image, string? name);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint image);
    [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(nint image);
}
