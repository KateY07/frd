using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Frd;

static class AuxiliaryPathRegression
{
    public static async Task RunAsync()
    {
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var relay = new LocalPathRelay(((IPEndPoint)listener.LocalEndpoint).Port);
        var leftClipboard = new MemoryClipboard();
        var rightClipboard = new MemoryClipboard();
        var root = Path.GetFullPath(Path.Combine("artifacts", "auxiliary-path", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var clipboardTcp = new TcpClient { NoDelay = true };
        using var cursorTcp = new TcpClient { NoDelay = true };
        await clipboardTcp.ConnectAsync(IPAddress.Loopback, relay.Port);
        await RemoteWire.WriteAsync(clipboardTcp.GetStream(), new RemoteRequest("clipboard"), stop.Token);
        using var clipboardPeer = await listener.AcceptTcpClientAsync(stop.Token);
        if ((await RemoteWire.ReadAsync<RemoteRequest>(clipboardPeer.GetStream(), stop.Token)).Kind != "clipboard") throw new InvalidDataException("Wrong clipboard route.");
        await RemoteWire.WriteAsync(clipboardPeer.GetStream(), new RemoteReply(true), stop.Token);
        if (!(await RemoteWire.ReadAsync<RemoteReply>(clipboardTcp.GetStream(), stop.Token)).Success) throw new IOException("Clipboard handshake failed.");
        await using var left = new ClipboardSyncSession(clipboardTcp, leftClipboard, Path.Combine(root, "left"), stop.Token);
        await using var right = new ClipboardSyncSession(clipboardPeer, rightClipboard, Path.Combine(root, "right"), stop.Token);
        await cursorTcp.ConnectAsync(IPAddress.Loopback, relay.Port);
        await RemoteWire.WriteAsync(cursorTcp.GetStream(), new RemoteRequest("cursor"), stop.Token);
        using var cursorPeer = await listener.AcceptTcpClientAsync(stop.Token);
        if ((await RemoteWire.ReadAsync<RemoteRequest>(cursorPeer.GetStream(), stop.Token)).Kind != "cursor") throw new InvalidDataException("Wrong cursor route.");
        await RemoteWire.WriteAsync(cursorPeer.GetStream(), new RemoteReply(true), stop.Token);
        if (!(await RemoteWire.ReadAsync<RemoteReply>(cursorTcp.GetStream(), stop.Token)).Success) throw new IOException("Cursor handshake failed.");
        CursorUpdate? latest = null;
        Exception? cursorFailure = null;
        await using var cursor = new CursorClient(cursorTcp, update => Volatile.Write(ref latest, update), error => cursorFailure = error, stop.Token);
        var shape = new CursorShape(2, 2, 1, 1, Enumerable.Repeat((byte)255, 16).ToArray(), new byte[8]);
        for (var round = 0; round < 2; round++)
        {
            relay.Pause();
            var text = "auxiliary recovery " + round;
            leftClipboard.Write(new(text));
            await CursorWire.WriteAsync(cursorPeer.GetStream(), new(round + 1, true, .3, .7, shape), stop.Token);
            await Task.Delay(round == 0 ? 500 : 3000);
            relay.Resume();
            await UntilAsync(() => rightClipboard.Read()?.Text == text && Volatile.Read(ref latest)?.Id == round + 1);
            if (latest!.Shape == null || !latest.Shape.Color.SequenceEqual(shape.Color) || latest.Shape.HotX != 1)
                throw new InvalidDataException("Cursor shape changed across resumed byte stream.");
            rightClipboard.Write(new("reverse " + text));
            await UntilAsync(() => leftClipboard.Read()?.Text == "reverse " + text);
            Console.WriteLine($"PASS: auxiliary pause round {round + 1}: bidirectional clipboard text and cursor shape restored on original TCP connections.");
        }
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        var file = Path.Combine(source, "nested", "payload.bin");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(131123));
        foreach (var pair in new[] { (From: leftClipboard, To: rightClipboard), (From: rightClipboard, To: leftClipboard) })
        {
            var sequence = pair.To.Sequence;
            relay.Pause(); pair.From.Write(new(Paths: [source]));
            await Task.Delay(500); relay.Resume();
            await UntilAsync(() => pair.To.Sequence != sequence && pair.To.Read()?.Paths?.Length == 1);
            var copied = pair.To.Read()!.Paths![0];
            if (!Directory.Exists(Path.Combine(copied, "nested", "empty")) ||
                !SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(copied, "nested", "payload.bin")))))
                throw new InvalidDataException("Clipboard file/directory recovery corrupted data.");
        }
        if (relay.TcpConnections != 2 || cursorFailure != null || !left.Snapshot.Connected || !right.Snapshot.Connected)
            throw new InvalidOperationException("Auxiliary channel was lost or silently replaced.");
        Console.WriteLine("PASS: files and nested/empty directories recovered in both directions, SHA-256 verified; unchanged two TCP connections. Isolated protocol fixture; system clipboard untouched.");
    }

    static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 6000)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Auxiliary channel did not recover.");
    }

    sealed class MemoryClipboard : IClipboardAccess
    {
        readonly object gate = new();
        uint sequence;
        ClipboardContents? value;
        public uint Sequence { get { lock (gate) return sequence; } }
        public ClipboardContents? Read() { lock (gate) return value; }
        public void Write(ClipboardContents contents) { lock (gate) { value = contents; sequence++; } }
    }
}
