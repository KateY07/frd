using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Frd;

static class InputLoadRegression
{
    public static async Task RunAsync(string report)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;
        var nonce = Guid.NewGuid().ToByteArray();
        await using var host = new RemoteUdpSession(socket, Convert.ToHexString(nonce), CancellationToken.None);
        using var receiver = new UdpVideoReceiver();
        await receiver.RegisterRemoteAsync(endpoint, nonce, CancellationToken.None);
        using var sender = new UdpVideoSender(socket, await host.WaitForRegistrationAsync(CancellationToken.None));
        sender.SetEncoderBitrateKbps(100000); host.AttachSender(sender);
        var key = host.AttachInput(new Probe(), () => true);
        ConcurrentDictionary<long, long> sent = new();
        ConcurrentQueue<(RemoteUdpSession.InputTiming Timing, long Sent)> observations = new();
        host.InputMeasured += timing =>
        {
            if (sent.TryRemove(timing.Sequence, out var tick)) observations.Enqueue((timing, tick));
        };
        long sequence = 0, frameId = 0, frames = 0;
        receiver.VideoReceived += _ => Interlocked.Increment(ref frames);
        List<object> rows = new();
        foreach (var rate in new[] { 0, 5, 20, 60, 0 })
        {
            using var stop = new CancellationTokenSource();
            var beforeFrames = Interlocked.Read(ref frames);
            var beforePackets = sender.Snapshot.SentPackets;
            var timer = Stopwatch.StartNew();
            var video = Task.Factory.StartNew(() =>
            {
                var payload = new byte[Math.Max(1, rate * 1_000_000 / 8 / 30)];
                var next = Stopwatch.GetTimestamp();
                while (!stop.IsCancellationRequested)
                {
                    if (rate > 0) sender.Send(new(1, Interlocked.Increment(ref frameId), true, payload), CancellationToken.None);
                    next += Stopwatch.Frequency / 30;
                    while (!stop.IsCancellationRequested && Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            for (var i = 0; i < 250; i++) { Send(); await Task.Delay(4); }
            stop.Cancel(); await video; await Task.Delay(100);
            var samples = Drain();
            var elapsed = timer.Elapsed.TotalSeconds;
            var receivedFrames = Interlocked.Read(ref frames) - beforeFrames;
            if (samples.Length < 200 || rate > 0 && receivedFrames == 0) throw new InvalidOperationException("Insufficient real input/video delivery.");
            var row = new { RateMbps = rate, Samples = samples.Length, MissingOrCoalesced = 250 - samples.Length,
                Frames = receivedFrames, Packets = sender.Snapshot.SentPackets - beforePackets, Seconds = elapsed,
                Total = Summary(samples.Select(x => Ms(x.Timing.Completed - x.Sent))),
                SendToReceive = Summary(samples.Select(x => Ms(x.Timing.Received - x.Sent))),
                ReceiveToExecute = Summary(samples.Select(x => Ms(x.Timing.Executing - x.Timing.Received))),
                Execute = Summary(samples.Select(x => Ms(x.Timing.Completed - x.Timing.Executing))) };
            rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
        }

        // Hold the real statistics lock to reproduce feedback blocking, without adding a production delay hook.
        var statsGate = typeof(UdpVideoSender).GetField("statsGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        using var held = new ManualResetEventSlim();
        var contention = Task.Factory.StartNew(() =>
        {
            lock (statsGate) { held.Set(); Thread.Sleep(180); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        held.Wait();
        var feedback = new byte[VideoDatagram.FeedbackSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(feedback, VideoDatagram.Magic);
        feedback[4] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(16), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(20), 100);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(feedback.AsSpan(40), Stopwatch.Frequency);
        receiver.SendInput(feedback, endpoint);
        await Task.Delay(15);
        for (var i = 0; i < 15; i++) { Send(); await Task.Delay(4); }
        await contention; await Task.Delay(100);
        var blocked = Drain();
        var max = blocked.Length == 0 ? double.PositiveInfinity : blocked.Max(x => Ms(x.Timing.Completed - x.Sent));
        var isolated = blocked.Length >= 12 && max < 75;
        Console.WriteLine($"Feedback lock isolation: {isolated}; samples={blocked.Length}/15, max={max:F2} ms");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { Scope = "Real production single UDP socket each end; synthetic video at 30 FPS, mock input injector; no real desktop, decode, render, or network bottleneck.", Rows = rows,
            FeedbackLockIsolation = new { Passed = isolated, HoldMs = 180, Samples = blocked.Length, MaximumMs = max } }, new JsonSerializerOptions { WriteIndented = true }));
        if (!isolated) throw new InvalidOperationException("Feedback statistics contention blocked input; see report.");

        void Send()
        {
            var id = ++sequence;
            var bytes = new byte[UdpInputProtocol.PacketSize];
            UdpInputProtocol.Write(bytes, key, id, new(RemoteInputKind.MouseMove, .5, .5));
            sent[id] = Stopwatch.GetTimestamp();
            receiver.SendInput(bytes, endpoint);
        }
        (RemoteUdpSession.InputTiming Timing, long Sent)[] Drain()
        {
            List<(RemoteUdpSession.InputTiming, long)> values = new();
            while (observations.TryDequeue(out var value)) values.Add(value);
            return values.ToArray();
        }
    }

    static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    static object Summary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { MeanMs = sorted.Average(), P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            P99Ms = sorted[(int)Math.Ceiling(sorted.Length * .99) - 1], MaxMs = sorted[^1] };
    }
    sealed class Probe : IRemoteInputInjector
    {
        public RemoteInputResult Inject(RemoteInputEvent input) => new(true, "Measured without physical injection.");
        public RemoteInputResult ReleaseAll() => new(true, "Released.");
    }
}
