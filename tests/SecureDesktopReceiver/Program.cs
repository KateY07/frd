using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Frd.SecureDesktopReceiver;

static class Program
{
    static readonly object gate = new();
    static byte[]? latest;
    static long frames;

    static async Task Main(string[] args)
    {
        var bind = IPAddress.Parse(args.Length > 0 ? args[0] : "172.20.10.7");
        var expected = IPAddress.Parse(args.Length > 1 ? args[1] : "172.20.10.12");
        var port = args.Length > 2 ? int.Parse(args[2]) : 14709;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        using var receiver = new UdpClient(new IPEndPoint(bind, port));
        receiver.Connect(expected, port);
        using var http = new HttpListener();
        http.Prefixes.Add("http://127.0.0.1:14708/");
        http.Start();
        Console.WriteLine($"Secure desktop UDP receiver: {bind}:{port}, peer {expected}:{port}; view http://127.0.0.1:14708/");
        try { await Task.WhenAll(ReceiveAsync(receiver, stop.Token), ServeAsync(http, stop.Token)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.WriteLine("Receiver stopped."); }
        finally { http.Stop(); }
    }

    static async Task ReceiveAsync(UdpClient receiver, CancellationToken token)
    {
        var opener = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await receiver.SendAsync("FRD-HOLE"u8.ToArray(), token);
                await Task.Delay(100, token);
            }
        }, token);
        int currentFrame = -1, received = 0;
        byte[][] parts = [];
        while (!token.IsCancellationRequested)
        {
            var packet = (await receiver.ReceiveAsync(token)).Buffer;
            if (packet.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(packet) != 0x44524650) continue;
            var frame = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10));
            if (count is < 1 or > 1000 || index >= count || frame < currentFrame) continue;
            if (frame > currentFrame || parts.Length != count)
            {
                currentFrame = frame; parts = new byte[count][]; received = 0;
            }
            if (parts[index] != null) continue;
            parts[index] = packet.AsSpan(12).ToArray();
            if (++received != count) continue;
            var size = parts.Sum(part => part.Length);
            if (size is < 100 or > 4_000_000) continue;
            var image = new byte[size];
            var offset = 0;
            foreach (var part in parts) { part.CopyTo(image, offset); offset += part.Length; }
            if (image[0] != 0xff || image[1] != 0xd8) continue;
            lock (gate) { latest = image; frames++; }
        }
        await opener;
    }

    static async Task ServeAsync(HttpListener http, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var context = await http.GetContextAsync().WaitAsync(token);
            _ = Task.Run(() => Handle(context), CancellationToken.None);
        }
    }

    static void Handle(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            byte[] data;
            if (context.Request.Url?.AbsolutePath == "/frame.jpg")
            {
                lock (gate) data = latest ?? [];
                if (data.Length == 0) { context.Response.StatusCode = 503; data = Encoding.UTF8.GetBytes("waiting for secure desktop"); }
                else context.Response.ContentType = "image/jpeg";
            }
            else if (context.Request.Url?.AbsolutePath == "/status")
            {
                lock (gate) data = Encoding.UTF8.GetBytes($"frames={frames}");
                context.Response.ContentType = "text/plain; charset=utf-8";
            }
            else
            {
                data = Encoding.UTF8.GetBytes("""
                    <!doctype html><meta charset="utf-8"><title>FRD 安全桌面预览</title>
                    <style>body{margin:0;background:#16191d;color:#eee;font:16px sans-serif}header{padding:10px 16px}img{display:block;max-width:100vw;max-height:calc(100vh - 50px);margin:auto}</style>
                    <header>FRD 安全桌面预览 · 仅测试；画面不落盘 <span id="state">等待对机...</span></header><img id="frame">
                    <script>let frame=document.getElementById('frame'),state=document.getElementById('state');
                    function refresh(){frame.onload=()=>{state.textContent='实时画面';setTimeout(refresh,250)};
                    frame.onerror=()=>{state.textContent='等待安全桌面画面';setTimeout(refresh,500)};
                    frame.src='/frame.jpg?t='+Date.now()}refresh();</script>
                    """);
                context.Response.ContentType = "text/html; charset=utf-8";
            }
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data);
        }
        catch (Exception error) { Console.Error.WriteLine("HTTP preview error: " + error); }
        finally { context.Response.Close(); }
    }
}
