using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Frd;

if (args.Contains("--network-stimulus"))
{
    NetworkStimulus.Run();
    return;
}

if (args.Contains("--udp-resilience-only"))
{
    await UdpResilienceRegression.RunAsync();
    return;
}

if (args.Contains("--auxiliary-path-only"))
{
    await AuxiliaryPathRegression.RunAsync();
    return;
}

if (args.Contains("--same-session-only"))
{
    await SameSessionRegression.RunAsync();
    return;
}
if (args.Contains("--same-session-window"))
{
    await SameSessionRegression.RunWindowAsync();
    return;
}

if (args.Contains("--secure-input-only"))
{
    var inputKey = RandomNumberGenerator.GetBytes(32);
    using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
    reservation.Dispose();
    using var stopInput = new CancellationTokenSource();
    var mock = new SecureInputMock();
    var active = true;
    var listener = Task.Run(() => SecureDesktopInputWorker.Run(inputKey, mock, () => active,
        message => Console.Error.WriteLine("[test input] " + message), stopInput.Token, port));
    await Task.Delay(100);
    using var clientInput = new SecureDesktopInputClient(inputKey, port);
    var down = clientInput.Send(new(RemoteInputKind.MouseDown, .4, .5));
    if (!down.Accepted || mock.Events.Count != 1) throw new InvalidOperationException("Authenticated secure input was not applied.");
    active = false;
    var denied = clientInput.Send(new(RemoteInputKind.KeyDown, ScanCode: 30));
    if (denied.Accepted || mock.Events.Count != 1) throw new InvalidOperationException("Inactive secure desktop accepted input.");
    active = true;
    using (var forged = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
        var packet = new byte[SecureInputWire.PacketSize];
        SecureInputWire.Write(packet, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), 1,
            new(RemoteInputKind.KeyDown, ScanCode: 30));
        forged.SendTo(packet, new IPEndPoint(IPAddress.Loopback, port));
    }
    await Task.Delay(50);
    if (mock.Events.Count != 1) throw new InvalidOperationException("Unauthenticated local input was applied.");
    var up = clientInput.Send(new(RemoteInputKind.MouseUp, .4, .5));
    if (!up.Accepted || mock.Events.Count != 2) throw new InvalidOperationException("Secure input did not resume.");
    stopInput.Cancel();
    await listener.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("PASS: authenticated local input, inactive-desktop rejection, forged-packet rejection, transition recovery and acknowledgement; no OS input injected.");
    return;
}

if (args is ["--shared-child", var childPath])
{
    using var childWriter = new SecureDesktopShared(childPath, writer: true);
    var childPixels = new byte[320 * 180 * 4];
    Array.Fill(childPixels, (byte)0x5A);
    if (!childWriter.Publish(320, 180, childPixels)) throw new InvalidOperationException("Child shared publish failed.");
    await Task.Delay(500);
    return;
}

if (args.Contains("--shared-only"))
{
    var path = Path.GetFullPath(Path.Combine("artifacts", "shared-regression-" + Guid.NewGuid().ToString("N") + ".map"));
    try
    {
        using var writer = new SecureDesktopShared(path, writer: true);
        using var reader = new SecureDesktopShared(path, writer: false);
        var original = RandomNumberGenerator.GetBytes(1920 * 1080 * 4);
        var watch = Stopwatch.StartNew();
        if (!writer.Publish(1920, 1080, original)) throw new InvalidOperationException("Shared frame publish failed.");
        var publishMs = watch.Elapsed.TotalMilliseconds;
        var first = reader.Take();
        if (!first.Active || first.Frame == null || first.Width != 1920 || first.Height != 1080)
            throw new InvalidOperationException("Shared frame was not received at native dimensions.");
        using (first.Frame)
        {
            var copied = new byte[original.Length];
            System.Runtime.InteropServices.Marshal.Copy(first.Frame.Pixels, copied, 0, copied.Length);
            if (!copied.AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Shared frame pixels changed.");
            for (var i = 0; i < 3; i++)
                if (!writer.Publish(320, 180, new byte[320 * 180 * 4]))
                    throw new InvalidOperationException("Writer could not replace older frames while a reader held one slot.");
            if (System.Runtime.InteropServices.Marshal.ReadByte(first.Frame.Pixels) != original[0])
                throw new InvalidOperationException("Writer overwrote the slot still claimed by the reader.");
        }
        var latest = reader.Take();
        if (!latest.Active || latest.Frame == null || latest.Width != 320 || latest.Height != 180)
            throw new InvalidOperationException("Reader did not skip to the latest native-size frame.");
        FfmpegRuntime.Initialize(Path.GetFullPath("third_party/ffmpeg/runtime"));
        using (var mapped = new MappedBgraFrame(latest.Frame.Pixels, latest.Width * 4, latest.Width, latest.Height, latest.Frame.Dispose))
        using (var encoder = new VideoEncoder("-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -bf 0 -g 30 -threads 1 -bufsize 100k",
            latest.Width, latest.Height, 30, 2000, CapturePixelMode.Bgra))
        using (var decoder = new VideoDecoder("-c:v h264 -threads 1"))
        {
            if (!encoder.SupportsMappedBgra) throw new InvalidOperationException("BGRA encoder rejected mapped input.");
            var packets = encoder.EncodeMapped(mapped, 1, true);
            var decoded = packets.SelectMany(packet => decoder.Decode(packet.Data, packet.Pts)).ToArray();
            if (decoded.Length == 0 || decoded[0].Width != 320 || decoded[0].Height != 180)
                throw new InvalidOperationException("Mapped shared pixels did not encode and decode.");
        }
        writer.SetInactive();
        if (reader.Take().Active) throw new InvalidOperationException("Desktop return did not clear shared video state.");
        using var childReader = new SecureDesktopShared(path, writer: false);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--shared-child");
        start.ArgumentList.Add(path);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start separate shared-frame writer process.");
        var deadline = Stopwatch.StartNew();
        (bool Active, SharedFrame? Frame, int Width, int Height) crossProcess = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            crossProcess = childReader.Take();
            if (crossProcess.Frame != null) break;
            await Task.Delay(10);
        }
        if (crossProcess.Frame == null || System.Runtime.InteropServices.Marshal.ReadByte(crossProcess.Frame.Pixels) != 0x5A)
            throw new InvalidOperationException("Separate process did not deliver its shared frame.");
        crossProcess.Frame.Dispose();
        await child.WaitForExitAsync();
        if (child.ExitCode != 0) throw new InvalidOperationException("Separate shared-frame writer failed.");
        Console.WriteLine($"PASS: native 1920x1080 shared frame, protected reader slot, newest-frame replacement, desktop return; publish {publishMs:F1}ms.");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
    return;
}

if (args.Contains("--resolution-only"))
{
    FfmpegRuntime.Initialize(Path.GetFullPath("third_party/ffmpeg/runtime"));
    var preset = new CodecPreset { EncoderArguments = "-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -bf 0 -g 30 -threads 1 -bufsize 100k",
        DecoderArguments = "-c:v h264 -threads 1" };
    var config = new AppConfiguration { Width = 320, Height = 180, FramesPerSecond = 30,
        InitialPreset = "h264", InitialBitrateKbps = 2000, Presets = new() { ["h264"] = preset } };
    var gate = new object();
    var current = (Width: 320, Height: 180, Pixels: new byte[320 * 180 * 4]);
    var captured = current;
    var decoded = new System.Collections.Concurrent.ConcurrentQueue<(int Width, int Height)>();
    Exception? failed = null;
    using var session = new DemoSession(config,
        () => { lock (gate) { captured = current; return captured.Pixels; } }, null, 320, 180, null, null,
        () => (captured.Width, captured.Height));
    session.FrameReceived += frame => decoded.Enqueue((frame.Width, frame.Height));
    session.Failed += error => failed = error;
    await session.StartAsync();
    await UntilSize(320, 180);
    lock (gate) current = (640, 360, new byte[640 * 360 * 4]);
    await UntilSize(640, 360);
    lock (gate) current = (320, 180, new byte[320 * 180 * 4]);
    await UntilSize(320, 180);
    Console.WriteLine("PASS: one live UDP session decoded 320x180 → 640x360 → 320x180 without resizing source pixels.");
    return;

    async Task UntilSize(int width, int height)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (failed != null) throw new InvalidOperationException("Session failed during resolution switch.", failed);
            if (decoded.TryDequeue(out var frame) && frame == (width, height)) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No decoded {width}x{height} frame after resolution switch.");
    }
}

if (!args.Contains("--udp-only"))
{
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var server = Task.Run(async () =>
{
    using var peer = await listener.AcceptTcpClientAsync(stop.Token);
    var stream = peer.GetStream();
    var first = await RemoteWire.ReadAsync<RemoteRequest>(stream, stop.Token);
    if (first.Kind != "status") throw new InvalidDataException("First request was not status.");
    await Task.Delay(TimeSpan.FromSeconds(17), stop.Token);
    await RemoteWire.WriteAsync(stream, new RemoteReply(true, "resumed"), stop.Token);
    var second = await RemoteWire.ReadAsync<RemoteRequest>(stream, stop.Token);
    if (second.Kind != "status") throw new InvalidDataException("Session did not survive the pause.");
    await RemoteWire.WriteAsync(stream, new RemoteReply(true, "same-session"), stop.Token);
}, stop.Token);

using var client = await RemoteConnection.ConnectAsync(new("127.0.0.1", port, "test"), stop.Token);
var clock = Stopwatch.StartNew();
var resumed = await client.ExchangeAsync(new("status"), stop.Token);
if (resumed.Message != "resumed" || clock.Elapsed < TimeSpan.FromSeconds(16))
    throw new InvalidOperationException("A paused but open TCP session did not resume.");
var repeated = await client.ExchangeAsync(new("status"), stop.Token);
if (repeated.Message != "same-session") throw new InvalidOperationException("The original TCP session was replaced.");
await server;
listener.Stop();
Console.WriteLine($"PASS: paused {clock.Elapsed.TotalSeconds:F1}s; original TCP session answered again.");
}

var key = RandomNumberGenerator.GetBytes(32);
var large = args.Contains("--large-udp");
var width = large ? 1536 : 320;
var height = large ? 864 : 180;
var pixels = RandomNumberGenerator.GetBytes(width * height * 4);
using var secure = new SecureDesktopFrames(key, useShared: false);
using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
var sendClock = Stopwatch.StartNew();
var sent = SecureDesktopUdp.Send(udp, key, 1, width, height, pixels);
var sendMs = sendClock.Elapsed.TotalMilliseconds;
var until = Stopwatch.StartNew();
while (!secure.Active && until.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
var frame = secure.Take();
if (!frame.Active || frame.Width != width || frame.Height != height || frame.Pixels == null ||
    !frame.Pixels.AsSpan().SequenceEqual(pixels))
    throw new InvalidOperationException("Authenticated multi-packet secure UDP frame was not restored exactly.");
SecureDesktopUdp.Send(udp, key, 2, 0, 0, null);
until.Restart();
while (secure.Active && until.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
if (secure.Active) throw new InvalidOperationException("Ordinary desktop return was not signaled.");
Console.WriteLine($"PASS: authenticated multi-packet secure UDP frame and desktop return; {width}x{height}, {sent.Packets} packets x2, send {sendMs:F1}ms.");

sealed class SecureInputMock : IRemoteInputInjector
{
    public List<RemoteInputEvent> Events { get; } = new();
    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        lock (Events) Events.Add(input);
        return new(true, "Mock applied.");
    }
    public RemoteInputResult ReleaseAll() => new(true, "Mock released.");
}
