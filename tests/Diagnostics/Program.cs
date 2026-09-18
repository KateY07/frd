using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Frd;

var checks = new List<object>();
var passed = false;
Exception? failure = null;
void Check(bool condition, string name, object evidence)
{
    checks.Add(new { Name = name, Passed = condition, Evidence = evidence });
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) throw new InvalidOperationException(name);
}
async Task Until(Func<bool> condition)
{
    var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
    while (!condition() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(5);
}
FrameDiagnostic Timing(long id) => new(1, id, 100, 120, 150, Stopwatch.Frequency);

try
{
    using (var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    using (var videoPort = new UdpVideoReceiver())
    using (var diagnosticPort = new UdpFrameDiagnosticsReceiver())
    {
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)peer.LocalEndPoint!;
        foreach (var (kind, port, register) in new (string, int, Action<IPEndPoint>)[]
            { ("video", videoPort.Port, videoPort.SetExpectedSource), ("diagnostic", diagnosticPort.Port, diagnosticPort.SetExpectedSource) })
        {
            register(target);
            if (!peer.Poll(1_000_000, SelectMode.SelectRead)) throw new TimeoutException("Missing UDP registration");
            var bytes = new byte[64];
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            var length = peer.ReceiveFrom(bytes, ref source);
            Check(length == 8 && bytes[4] == 3 && ((IPEndPoint)source).Port == port,
                kind + " receive socket opens its own UDP return path", new { Bytes = length, SourcePort = port });
        }
    }
    var extended = Timing(22) with { Sending = new(2000, 2, 170, 190, 9.6, .2) };
    var extendedBytes = FrameDiagnosticProtocol.Encode(extended);
    Check(extendedBytes.Length == 88 && FrameDiagnosticProtocol.TryDecode(extendedBytes, out var restored) && restored == extended,
        "extended remote diagnostic preserves transfer timing", new { Bytes = extendedBytes.Length });
    BinaryPrimitives.WriteDoubleLittleEndian(extendedBytes.AsSpan(72), double.NaN);
    Check(!FrameDiagnosticProtocol.TryDecode(extendedBytes, out _), "invalid remote wait timing rejected", new { Rejected = true });
    using var receiver = new UdpVideoReceiver();
    using var diagnostics = new UdpFrameDiagnosticsReceiver();
    using var sender = new UdpVideoSender(new(IPAddress.Loopback, receiver.Port));
    var diagnosticTarget = new IPEndPoint(IPAddress.Loopback, diagnostics.Port);
    var videos = new ConcurrentDictionary<long, byte[]>();
    var clocks = new ConcurrentDictionary<long, FrameDiagnostic>();
    receiver.VideoReceived += video => videos[video.FrameId] = video.Data;
    diagnostics.Received += clock => clocks[clock.FrameId] = clock;
    sender.SetLimitKbps(1000);
    sender.Send(new(1, 0, true, new byte[1]), CancellationToken.None);
    await Until(() => videos.ContainsKey(0));
    var data = Enumerable.Range(0, 2000).Select(x => (byte)(x % 251)).ToArray();
    sender.Send(new(1, 1, true, data), CancellationToken.None);
    sender.SendDiagnostic(FrameDiagnosticProtocol.Encode(Timing(2)), diagnosticTarget, CancellationToken.None);
    sender.Send(new(1, 2, true, data), CancellationToken.None);
    sender.Send(new(1, 3, true, data), CancellationToken.None);
    sender.Send(new(1, 4, true, data), CancellationToken.None);
    await Until(() => videos.ContainsKey(4));
    Check(videos.ContainsKey(4) && !clocks.ContainsKey(4), "video delivery does not wait for separate diagnostics", new { Videos = videos.Count, Diagnostics = clocks.Count });
    sender.SendDiagnostic(FrameDiagnosticProtocol.Encode(Timing(4)), diagnosticTarget, CancellationToken.None);
    await Until(() => clocks.ContainsKey(4));
    Check(Enumerable.Range(1, 4).All(x => videos.TryGetValue(x, out var received) && received.SequenceEqual(data)),
        "off/on/off/on diagnostics leave video bytes unchanged", new { Frames = videos.Count - 1 });
    Check(clocks.Count == 2 && clocks[2] == Timing(2) && clocks[4] == Timing(4),
        "independent diagnostics round trip before and after corresponding video", new { Frames = clocks.Keys.Order().ToArray(), PacketBytes = FrameDiagnosticProtocol.PacketBytes });
    var snapshot = sender.Snapshot;
    Check(snapshot.DiagnosticPackets == 2 && Math.Abs(snapshot.DiagnosticMbps - 2 * 76 * 8 / 1_000_000d) < 1e-12 && snapshot.SentMbps > snapshot.DiagnosticMbps,
        "diagnostics wire overhead is separate and included in total sent rate", snapshot);

    using var capReceiver = new UdpVideoReceiver();
    using var capSender = new UdpVideoSender(new(IPAddress.Loopback, capReceiver.Port));
    capSender.SetLimitKbps(500);
    var watch = Stopwatch.StartNew();
    for (var index = 0; index < 20; index++)
    {
        capSender.Send(new(1, index, true, new byte[1136]), CancellationToken.None);
        capSender.SendDiagnostic(FrameDiagnosticProtocol.Encode(Timing(100 + index)), diagnosticTarget, CancellationToken.None);
    }
    var seconds = watch.Elapsed.TotalSeconds;
    var wireBits = 20 * (1200 + 76) * 8;
    Check(wireBits <= seconds * 500_000 + 1200 * 8 && capSender.Snapshot.SentPackets == 20 && capSender.Snapshot.DiagnosticPackets == 20,
        "video plus diagnostics share one 500kbps pacing budget", new { seconds, WireBits = wireBits, TotalMbps = wireBits / seconds / 1_000_000, capSender.Snapshot });

    using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    raw.ReceiveBufferSize = 1024 * 1024;
    raw.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    using var wireSender = new UdpVideoSender((IPEndPoint)raw.LocalEndPoint!);
    wireSender.SetLimitKbps(1000);
    wireSender.Send(new(1, 0, true, new byte[1]), CancellationToken.None);
    var buffer = new byte[65536];
    if (!raw.Poll(1_000_000, SelectMode.SelectRead)) throw new TimeoutException("Wire fixture startup packet missing.");
    raw.Receive(buffer);
    wireSender.SendDiagnostic(FrameDiagnosticProtocol.Encode(Timing(300)), diagnosticTarget, CancellationToken.None);
    wireSender.Send(new(1, 5, true, data), CancellationToken.None);
    List<object> packetFacts = new();
    var reconstructed = new byte[data.Length];
    var count = 0;
    while (raw.Poll(100_000, SelectMode.SelectRead))
    {
        var bytes = raw.Receive(buffer);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(28));
        var payload = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(32));
        if (bytes > 1172 || buffer[4] != 1 || buffer[5] != 1 || bytes != 36 + payload) throw new InvalidDataException("Video datagram structure changed.");
        buffer.AsSpan(36, payload).CopyTo(reconstructed.AsSpan(offset));
        packetFacts.Add(new { UdpBytes = bytes, HeaderBytes = bytes - payload, PayloadBytes = payload, Offset = offset, Flags = buffer[5] });
        count++;
    }
    Check(count == 2 && reconstructed.SequenceEqual(data), "wire inspection preserves 36-byte header, 1136-byte payload and 1200-byte IP MTU", packetFacts);
    var malformed = FrameDiagnosticProtocol.Encode(Timing(1));
    malformed[0] ^= 1;
    Check(!FrameDiagnosticProtocol.TryDecode(malformed, out _) && !FrameDiagnosticProtocol.TryDecode(new byte[100], out _),
        "invalid diagnostic metadata cannot enter frame journal", new { });
    passed = true;
}
catch (Exception error) { failure = error; Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    Directory.CreateDirectory("results/ffmpeg-diagnostics");
    File.WriteAllText("results/ffmpeg-diagnostics/regression.json", JsonSerializer.Serialize(new
    { Passed = passed, VideoWireFormatChanged = false, Checks = checks, Error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
}
