using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Frd;

namespace Frd.InteractionTests;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args is ["--fixture", var path]) { Fixture.Run(Path.GetFullPath(path)); return 0; }
            if (args is ["--clipboard-fixture", var clipboardPath]) { Fixture.Run(Path.GetFullPath(clipboardPath), true); return 0; }
            if (args is ["--clipboard", var report]) return NativeClipboardTest.Run(Path.GetFullPath(report));
            return RunAsync(args.Length == 0 ? "results/interaction/protocol.json" : args[0]).GetAwaiter().GetResult();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static async Task<int> RunAsync(string report)
    {
        List<object> checks = new();
        void Check(bool pass, string name) { checks.Add(new { Name = name, Passed = pass }); if (!pass) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(report)!, "data-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var left = new TcpClient { NoDelay = true }; await left.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var right = await listener.AcceptTcpClientAsync(); right.NoDelay = true; listener.Stop();
        var a = new MemoryClipboard(); var b = new MemoryClipboard();
        await using (var send = new ClipboardSyncSession(left, a, Path.Combine(root, "cache-a")))
        await using (var receive = new ClipboardSyncSession(right, b, Path.Combine(root, "cache-b")))
        {
            foreach (var direction in new[] { (From: a, To: b, Name: "A→B"), (From: b, To: a, Name: "B→A") })
            {
                var text = direction.Name + " 中文🙂\r\nline 2\t " + Guid.NewGuid();
                direction.From.Write(new(text)); await Wait(() => direction.To.Read()?.Text == text);
                Check(direction.To.Read()!.Text == text, direction.Name + " Unicode multiline text");
                direction.From.Write(new("")); await Wait(() => direction.To.Read()?.Text == "");
                Check(direction.To.Read()!.Text == "", direction.Name + " empty text");
                var data = TestData.Create(root, direction.Name == "A→B" ? "forward" : "reverse");
                var before = direction.To.Sequence;
                direction.From.Write(new(Paths: data)); await Wait(() => direction.To.Sequence != before);
                var received = direction.To.Read()!.Paths!;
                Check(TestData.Equal(data, received), direction.Name + " files, SHA256, nested/empty directories, duplicate basenames");
                Check(received.All(path => !data.Contains(path) && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)), direction.Name + " published paths are local staged copies");
            }
            var sentCount = send.Snapshot.Sent + receive.Snapshot.Sent;
            await Task.Delay(500);
            Check(sentCount == send.Snapshot.Sent + receive.Snapshot.Sent, "No clipboard echo loop");
            Check(send.Snapshot.Sent == 3 && receive.Snapshot.Sent == 3 && send.Snapshot.Received == 3 && receive.Snapshot.Received == 3, "Exactly three transfers in each direction");
        }
        var ids = new[] { 32512, 32513, 32514, 32642, 32643, 32644, 32645, 32649 };
        foreach (var id in ids)
        {
            var original = NativeCursor.ReadShape(LoadCursor(0, id));
            var cursor = NativeCursor.Create(original);
            try
            {
                var copy = NativeCursor.ReadShape(cursor);
                Check(original.Width == copy.Width && original.Height == copy.Height && original.HotX == copy.HotX && original.HotY == copy.HotY &&
                    original.Color.SequenceEqual(copy.Color) && original.Mask.SequenceEqual(copy.Mask), "Native cursor bitmap/mask/hotspot roundtrip " + id);
            }
            finally { NativeCursor.Release(cursor); }
        }
        foreach (var (source, scale, expected) in new[] { ((2880, 1800), 1d, (2880, 1800)), ((2560, 1440), 1d, (2560, 1440)), ((1366, 768), 1d, (1366, 768)) })
            Check(TransmissionGeometry.Dimensions(source.Item1, source.Item2, scale) == expected, $"Transmission geometry {source} at {scale}");
        Check(!TransmissionGeometry.IsValidScale(double.NaN) && !TransmissionGeometry.IsValidScale(2) &&
            !TransmissionGeometry.IsValidScale(.75) && !TransmissionGeometry.IsValidScale(.5), "Invalid transmission scale rejected");
        foreach (var path in new[] { "../escape", "C:/absolute", "r/a:ads", "r/CON", "r/../x", "r/trailing.", "r\\x" })
        {
            var refused = false;
            try { ClipboardSyncSession.Validate(new("files", Guid.NewGuid(), 0, [path], [new(path, false, 1)])); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "Unsafe clipboard path rejected: " + path);
        }
        await VerifyPartialTransfer(root, Check);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Checks = checks }, AppConfiguration.JsonOptions));
        return 0;
    }

    static async Task VerifyPartialTransfer(string root, Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var raw = new TcpClient(); await raw.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync(); listener.Stop();
        var clipboard = new MemoryClipboard(); clipboard.Write(new("preserve"));
        var cache = Path.Combine(root, "cancel-cache");
        await using var session = new ClipboardSyncSession(peer, clipboard, cache);
        var manifest = new ClipboardManifest("files", Guid.NewGuid(), 0, ["root/file.bin"], [new("root/file.bin", false, 1000000)]);
        var header = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await raw.GetStream().WriteAsync(BitConverter.GetBytes(header.Length)); await raw.GetStream().WriteAsync(header);
        await raw.GetStream().WriteAsync(new byte[100]); raw.Close();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        check(clipboard.Read()!.Text == "preserve", "Interrupted transfer never publishes partial files");
        check(!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any(), "Interrupted staging directory cleaned");
    }

    internal static async Task Wait(Func<bool> ready, int milliseconds = 6000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!ready()) { if (Environment.TickCount64 > until) throw new TimeoutException("Test condition not reached."); await Task.Delay(20); }
    }
    [DllImport("user32.dll")] static extern nint LoadCursor(nint instance, nint name);
}

sealed class MemoryClipboard : IClipboardAccess
{
    readonly object gate = new(); uint sequence; ClipboardContents? contents;
    public uint Sequence { get { lock (gate) return sequence; } }
    public ClipboardContents? Read() { lock (gate) return contents; }
    public void Write(ClipboardContents value) { lock (gate) { contents = value; sequence++; } }
}

static class TestData
{
    public static string[] Create(string root, string name)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        var folder = Path.Combine(path, "目录 中文"); Directory.CreateDirectory(Path.Combine(folder, "nested", "empty"));
        File.WriteAllText(Path.Combine(folder, "nested", "说明.txt"), "FRD clipboard verification 中文🙂 " + name);
        File.WriteAllBytes(Path.Combine(folder, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(path, "sample.bin"), RandomNumberGenerator.GetBytes(131123));
        Directory.CreateDirectory(Path.Combine(path, "other"));
        File.WriteAllText(Path.Combine(path, "other", "sample.bin"), "duplicate basename");
        return [Path.Combine(path, "sample.bin"), folder, Path.Combine(path, "other", "sample.bin")];
    }
    public static string Digest(string path)
    {
        if (File.Exists(path)) return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return string.Join('\n', Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file =>
            Path.GetRelativePath(path, file) + ":" + (Directory.Exists(file) ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))));
    }
    public static bool Equal(string[] a, string[] b) => a.Length == b.Length && a.Zip(b).All(pair => Path.GetFileName(pair.First) == Path.GetFileName(pair.Second) && Digest(pair.First) == Digest(pair.Second));
}
