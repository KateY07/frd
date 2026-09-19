using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Frd;

namespace Frd.ConstrainedNetwork;

static class Program
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static async Task<int> Main(string[] args)
    {
        var watch = Stopwatch.StartNew();
        var smokeOnly = args.Contains("--smoke-only", StringComparer.Ordinal);
        var delayComparison = args.Contains("--delay-vs-queue", StringComparer.Ordinal);
        var expectedCases = delayComparison ? 2 : smokeOnly ? 1 : 5;
        var report = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ??
            (delayComparison ? "results/input-latency/delay-vs-queue.json" : "results/input-latency/constrained-network.json"));
        List<CaseResult> results = new();
        Exception? failure = null;
        try
        {
            if (smokeOnly && delayComparison) throw new ArgumentException("Choose either --smoke-only or --delay-vs-queue.");
            Scenario[] scenarios = delayComparison ?
            [
                new("high-propagation-ample-capacity", false, false, true, false, 3.5, 10, 80),
                new("low-propagation-overloaded-capacity", false, false, true, false, 3.5, 1, 2)
            ] :
            [
                new("shared-airtime-idle", false, false, false, false, 3.5),
                new("shared-airtime-video-fifo", false, false, true, false, 3.5),
                new("shared-airtime-video-control-first", false, true, true, false, 3.5),
                new("full-duplex-video-fifo", true, false, true, false, 3.5),
                new("shared-airtime-capacity-step-control-first", false, true, true, true, 5)
            ];
            foreach (var scenario in scenarios.Take(expectedCases))
            {
                Console.WriteLine("Starting " + scenario.Name);
                var result = await Run(smokeOnly ? scenario with { Seconds = .1 } : scenario, delayComparison); results.Add(result);
                Console.WriteLine($"{scenario.Name}: injected {result.InputCount}, input mean {result.Input.MeanMs:F2}/p95 {result.Input.P95Ms:F2} ms; ACK p95 {result.Acknowledgement.P95Ms:F2} ms; complete video {result.CompleteVideoFrames}/{result.SentVideoFrames}; drop {result.DroppedVideoPackets}");
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); failure = error; }
        var fifo = results.FirstOrDefault(x => x.Name == "shared-airtime-video-fifo");
        var idle = results.FirstOrDefault(x => x.Name == "shared-airtime-idle");
        var priority = results.FirstOrDefault(x => x.Name == "shared-airtime-video-control-first");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            Passed = failure is null && results.Count == expectedCases && results.All(x => x.IntegrityPassed), DiagnosticSmokeOnly = smokeOnly,
            DelayVersusQueueComparison = delayComparison, DurationSeconds = watch.Elapsed.TotalSeconds,
            PassMeaning = "Pass means input ordering, UDP smoke/frame integrity and experiment completion. It does not mean overloaded video is usable; inspect VideoDeliveryState, VideoStarved, complete frame counts and goodput.",
            Scope = "Network-only experiment: production pipelined TCP input with mock injector plus production UDP fragmentation/reassembly. Synthetic video payloads, no encoding/decoding/rendering, physical input, real Internet, TCP loss/retransmission, firewall or production tuning changes.",
            Model = new
            {
                Capacity = "Shared airtime: all directions/classes serialize through one link. Full duplex: each direction independently receives the configured capacity.",
                Directions = "Up: input requests + video receiver feedback. Down: video + input application ACKs. Modeled TCP transport ACKs reverse each segment's direction.",
                Delay = "Each virtual packet uses the case's fixed one-way propagation after serialization (normally 20 ms; comparison uses 80/2 ms); propagation does not occupy link capacity.",
                WireAccounting = "UDP uses actual production datagram length + 28 IPv4/UDP bytes. TCP uses actual 4-byte-prefix/JSON record bytes, modeled MSS 1160, 40-byte TCP/IP header per segment plus one modeled reverse 40-byte TCP ACK. These TCP overheads are explicit model assumptions, not measured kernel packets; no retransmissions are invented.",
                Queue = "Video UDP admission limited to 150 ms of current link backlog, and queued video older than 150 ms is dropped. Input/replies, modeled transport ACKs and small feedback messages are never deliberately dropped. TCP FIFO order is preserved within each direction.",
                Priority = "Experimental scheduler can identify control traffic; non-video work overtakes queued video but cannot preempt a packet already serializing. This is not a guarantee from public Internet QoS.",
                Scheduling = "Dedicated thread + high-resolution waitable timer; absolute next serialization/delivery deadline, no fixed frame/packet sleep tick. Scheduler wakeup overrun and actual queue wait are recorded.",
                TimingBoundary = "Scheduled serialization and configured propagation are simulator inputs, not measurements of a real line. QueueWait, ActualProxyRelease, delivery overrun, mock injection and frame-complete times are measured by Stopwatch. Packet traces use link-construction origin; InputEvents/VideoFrames use timed-load origin. RequestSequence and frame/offset correlation link them without pretending the clocks share the same zero.",
                Video = "4 Mbps generated payload at 30 FPS before transport overhead, capped by finite link only; no automatic rate adaptation.",
                IntegritySmoke = "Before timed load, a 4 KB payload traverses real UDP fragmentation/reassembly and SHA256 verification. Timed overload is allowed to produce zero complete video frames; such frame-age samples remain absent. Link per-class counters include this startup smoke, but reported timed video goodput excludes its 4352 wire bytes.",
                Input = "60 generated events per second; generation to mock injection and generation to true application ACK measured on one monotonic clock. ACK does not mean target repaint."
            }, Cases = results,
            DelayComparisonMeaning = delayComparison ? "Both cases keep the sender at 4 Mbps and use FIFO shared capacity. Compare configured propagation with measured link queue wait; absolute RTT or ACK age alone does not identify congestion. No bitrate controller, baseline-delay estimator or automatic adaptation is implemented." : null,
            CausalComparison = new
            {
                SharedFifoMinusIdleInputP95Ms = fifo is null || idle is null ? (double?)null : fifo.Input.P95Ms - idle.Input.P95Ms,
                SharedFifoMinusPriorityInputP95Ms = fifo is null || priority is null ? (double?)null : fifo.Input.P95Ms - priority.Input.P95Ms,
                SharedFifoMinusPriorityAckP95Ms = fifo is null || priority is null ? (double?)null : fifo.Acknowledgement.P95Ms - priority.Acknowledgement.P95Ms,
                Note = "Observed differences are reported without hard-coded latency pass thresholds. Pass checks delivery integrity/order and clean completion, not a desired speedup."
            }, Error = failure?.ToString()
        }, Json));
        Console.WriteLine($"Finished in {watch.Elapsed.TotalSeconds:F2}s: {report}");
        return failure is null && results.Count == expectedCases && results.All(x => x.IntegrityPassed) ? 0 : 1;
    }

    static async Task<CaseResult> Run(Scenario scenario, bool detailedTiming)
    {
        var generation = new ConcurrentQueue<long>();
        var injection = new ConcurrentBag<double>(); var acknowledgements = new ConcurrentBag<double>();
        var faults = new ConcurrentQueue<string>();
        var frames = new ConcurrentDictionary<long, (long Started, byte[] Hash, int Bytes, long Planned)>();
        var frameArrivals = new ConcurrentDictionary<long, long>();
        var eventClocks = new ConcurrentDictionary<int, EventClock>();
        var smoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ages = new ConcurrentBag<double>();
        long completedBytes = 0, completedFrames = 0; int inputCount = 0, sentFrames = 0;
        var source = new MockInput(generation, injection, faults, eventClocks);
        using var server = new RemoteInputServer(source);
        using var receiver = new UdpVideoReceiver();
        using var link = new FiniteLink(scenario.FullDuplex, scenario.Priority, scenario.Step ? 5 : scenario.CapacityMbps, scenario.PropagationMs, detailedTiming);
        await using var relay = new NetworkRelay(link, server.Endpoint, new(IPAddress.Loopback, receiver.Port), faults);
        using var sender = new UdpVideoSender(relay.UdpEndpoint);
        sender.SetEncoderBitrateKbps(4000);
        relay.SetSender(new(IPAddress.Loopback, sender.ActualPort));
        receiver.SetExpectedSource(relay.UdpEndpoint);
        receiver.Failed += error => faults.Enqueue(error.ToString()); sender.Failed += error => faults.Enqueue(error.ToString());
        receiver.VideoReceived += frame =>
        {
            if (!frames.TryGetValue(frame.FrameId, out var expected) || !SHA256.HashData(frame.Data).AsSpan().SequenceEqual(expected.Hash))
            { faults.Enqueue("Video frame integrity mismatch: " + frame.FrameId); return; }
            if (frame.FrameId == -1) { smoke.TrySetResult(); return; }
            var arrived = Stopwatch.GetTimestamp(); frameArrivals[frame.FrameId] = arrived;
            ages.Add(FiniteLink.Milliseconds(arrived - expected.Started));
            Interlocked.Add(ref completedBytes, expected.Bytes); Interlocked.Increment(ref completedFrames);
        };
        using var input = await RemoteInputClient.ConnectAsync(relay.TcpEndpoint).WaitAsync(Timeout);
        await input.SetEnabledAsync(true).WaitAsync(Timeout);
        var smokePayload = new byte[4096]; new Random(97).NextBytes(smokePayload);
        frames[-1] = (Stopwatch.GetTimestamp(), SHA256.HashData(smokePayload), smokePayload.Length, 0);
        sender.Send(new(1, -1, true, smokePayload), CancellationToken.None);
        try { await smoke.Task.WaitAsync(Timeout); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("UDP startup smoke failed: " + JsonSerializer.Serialize(new { Sender = sender.Snapshot, Link = link.Snapshot(), Errors = faults.ToArray() }), error);
        }
        var start = Stopwatch.GetTimestamp();
        var end = start + FiniteLink.Ticks(scenario.Seconds * 1000);
        var trace = new ConcurrentQueue<object>();
        var inputTask = Task.Run(async () =>
        {
            using var timer = new PrecisionTimer(); List<Task> pending = new(); var count = 0;
            while (true)
            {
                var due = start + (long)(count * Stopwatch.Frequency / 60d);
                if (due >= end) break;
                timer.WaitUntil(due);
                var at = Stopwatch.GetTimestamp(); var eventId = count + 1; eventClocks[eventId] = new(at, due); generation.Enqueue(at);
                var acknowledgement = await input.QueueAsync(new(RemoteInputKind.Wheel, (count + 1) / 1000d, .5, WheelDelta: 120)).WaitAsync(Timeout);
                pending.Add(Observe(acknowledgement, at, eventId)); count++; Interlocked.Increment(ref inputCount);
            }
            await Task.WhenAll(pending).WaitAsync(Timeout);
            async Task Observe(Task<RemoteInputResult> pendingResult, long at, int eventId)
            {
                var result = await pendingResult;
                if (!result.Accepted) faults.Enqueue("Input rejected: " + result.Message);
                var acknowledged = Stopwatch.GetTimestamp(); eventClocks[eventId].Acknowledged = acknowledged;
                acknowledgements.Add(FiniteLink.Milliseconds(acknowledged - at));
            }
        });
        var videoTask = Task.Run(() =>
        {
            if (!scenario.Video) return;
            using var timer = new PrecisionTimer();
            for (var index = 0; ; index++)
            {
                var due = start + (long)(index * Stopwatch.Frequency / 30d);
                if (due >= end) break;
                timer.WaitUntil(due);
                var bytes = new byte[16_666]; new Random(index + 19).NextBytes(bytes);
                var hash = SHA256.HashData(bytes);
                var now = Stopwatch.GetTimestamp(); frames[index] = (now, hash, bytes.Length, due);
                sender.Send(new(1, index, true, bytes), CancellationToken.None); Interlocked.Increment(ref sentFrames);
            }
        });
        var traceTask = Task.Run(() =>
        {
            using var timer = new PrecisionTimer(); var priorCapacity = scenario.Step ? 5d : scenario.CapacityMbps;
            var previousSample = start; var previousBytes = link.DeliveredVideoBytes;
            for (var index = 0; ; index++)
            {
                var due = start + FiniteLink.Ticks(index * 250);
                if (due >= end) break;
                timer.WaitUntil(due);
                var seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                var capacity = scenario.Step ? (seconds < 1.5 || seconds >= 3.5 ? 5d : 1d) : scenario.CapacityMbps;
                if (capacity != priorCapacity) { link.SetCapacity(capacity); priorCapacity = capacity; }
                var sampleTick = Stopwatch.GetTimestamp(); var sampleBytes = link.DeliveredVideoBytes;
                var sampleSeconds = (sampleTick - previousSample) / (double)Stopwatch.Frequency;
                trace.Enqueue(new { Seconds = seconds, CapacityMbps = capacity, Injected = injection.Count,
                    Input = Summary(injection), Acknowledgement = Summary(acknowledgements), CompletedFrames = Interlocked.Read(ref completedFrames),
                    VideoDeliveredWireBytes = sampleBytes, VideoDrops = link.DroppedVideoPackets,
                    DeliveredVideoWireMbpsSincePriorSample = sampleSeconds > 0 ? (sampleBytes - previousBytes) * 8 / sampleSeconds / 1_000_000 : 0 });
                previousSample = sampleTick; previousBytes = sampleBytes;
            }
        });
        await Task.WhenAll(inputTask, videoTask, traceTask).WaitAsync(TimeSpan.FromSeconds(scenario.Seconds + 4));
        await input.SetEnabledAsync(false).WaitAsync(Timeout);
        await Task.Delay(220);
        var elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        var integrity = faults.IsEmpty && !link.HasErrors && smoke.Task.IsCompletedSuccessfully && inputCount == injection.Count && inputCount == acknowledgements.Count && generation.IsEmpty;
        return new(scenario.Name, scenario.FullDuplex ? "full-duplex" : "shared-airtime", scenario.Priority, scenario.Step ? 5 : scenario.CapacityMbps, scenario.PropagationMs, scenario.Seconds, elapsed,
            inputCount, Summary(injection), Summary(acknowledgements), sentFrames, completedFrames, ages.IsEmpty ? null : Summary(ages),
            link.DroppedVideoPackets, Math.Max(0, link.DeliveredVideoBytes - 4352) * 8 / elapsed / 1_000_000,
            completedBytes * 8 / elapsed / 1_000_000,
            !scenario.Video ? "No video load" : completedFrames == 0 ? "STARVED: no complete video frames under load" : "Partial/complete frame delivery; visual usability is not assessed",
            scenario.Video && completedFrames == 0, integrity, link.Snapshot(), trace.ToArray(),
            detailedTiming ? eventClocks.OrderBy(x => x.Key).Select(x => (object)new
            {
                Event = x.Key, RequestSequence = x.Key + 1, PlannedGenerationMs = FiniteLink.Milliseconds(x.Value.Planned - start),
                ActualGenerationMs = FiniteLink.Milliseconds(x.Value.Generated - start), MockInjectionMs = FiniteLink.Milliseconds(x.Value.Injected - start),
                AcknowledgementMs = FiniteLink.Milliseconds(x.Value.Acknowledged - start),
                GeneratedToInjectedMs = FiniteLink.Milliseconds(x.Value.Injected - x.Value.Generated)
            }).ToArray() : [],
            detailedTiming ? frames.Where(x => x.Key >= 0).OrderBy(x => x.Key).Select(x => (object)new
            {
                FrameId = x.Key, PlannedSendMs = FiniteLink.Milliseconds(x.Value.Planned - start), ActualSenderStartMs = FiniteLink.Milliseconds(x.Value.Started - start),
                CompleteAtReceiverMs = frameArrivals.TryGetValue(x.Key, out var arrived) ? (double?)FiniteLink.Milliseconds(arrived - start) : null
            }).ToArray() : [], faults.ToArray());
    }

    public static Distribution Summary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, sorted.Length == 0 ? 0 : sorted.Average(), Percentile(.5), Percentile(.95), sorted.LastOrDefault());
    }

    sealed record Scenario(string Name, bool FullDuplex, bool Priority, bool Video, bool Step, double Seconds, double CapacityMbps = 1, double PropagationMs = 20);
    public sealed record Distribution(int Count, double MeanMs, double P50Ms, double P95Ms, double MaxMs);
    sealed record CaseResult(string Name, string Model, bool Priority, double InitialCapacityMbps, double OneWayPropagationMs, double ProductionSeconds, double MeasurementSeconds,
        int InputCount, Distribution Input, Distribution Acknowledgement, long SentVideoFrames, long CompleteVideoFrames,
        Distribution? VideoFrameAge, long DroppedVideoPackets, double VideoWireGoodputMbps, double CompleteFramePayloadGoodputMbps,
        string VideoDeliveryState, bool VideoStarved, bool IntegrityPassed, object Link, object[] RateTrace, object[] InputEvents, object[] VideoFrames, string[] Errors);

    sealed class EventClock(long generated, long planned)
    {
        public long Generated = generated, Planned = planned, Injected, Acknowledged;
    }

    sealed class MockInput(ConcurrentQueue<long> generation, ConcurrentBag<double> latency, ConcurrentQueue<string> faults, ConcurrentDictionary<int, EventClock> clocks) : IRemoteInputInjector
    {
        int ordinal;
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            if (input.Kind != RemoteInputKind.Wheel || input.X != ++ordinal / 1000d || !generation.TryDequeue(out var started))
            { faults.Enqueue("Unexpected or out-of-order input delivery."); return new(false, "Unexpected input"); }
            var injected = Stopwatch.GetTimestamp(); clocks[ordinal].Injected = injected;
            latency.Add(FiniteLink.Milliseconds(injected - started)); return new(true, "mock applied");
        }
        public RemoteInputResult ReleaseAll() => new(true, "mock released");
    }
}

sealed class NetworkRelay : IAsyncDisposable
{
    readonly FiniteLink link;
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentQueue<string> faults;
    readonly IPEndPoint inputServer, receiver;
    readonly Task tcpWorker, udpWorker;
    IPEndPoint? sender;
    TcpClient? downstream, upstream;
    public IPEndPoint TcpEndpoint => (IPEndPoint)listener.LocalEndpoint;
    public IPEndPoint UdpEndpoint => (IPEndPoint)udp.LocalEndPoint!;

    public NetworkRelay(FiniteLink link, IPEndPoint inputServer, IPEndPoint receiver, ConcurrentQueue<string> faults)
    {
        this.link = link; this.inputServer = inputServer; this.receiver = receiver; this.faults = faults;
        listener.Start(1); udp.Bind(new IPEndPoint(IPAddress.Loopback, 0)); udp.ReceiveBufferSize = 1 << 20;
        tcpWorker = Tcp(); udpWorker = Udp();
    }

    public void SetSender(IPEndPoint endpoint) => sender = endpoint;

    async Task Tcp()
    {
        try
        {
            downstream = await listener.AcceptTcpClientAsync(stop.Token); downstream.NoDelay = true;
            upstream = new() { NoDelay = true }; await upstream.ConnectAsync(inputServer, stop.Token);
            var requests = Pump(downstream.GetStream(), upstream.GetStream(), Direction.Up, Traffic.Input);
            var replies = Pump(upstream.GetStream(), downstream.GetStream(), Direction.Down, Traffic.InputReply);
            await Task.WhenAll(requests, replies);
        }
        catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        { Console.Error.WriteLine("Network relay TCP stopped: " + error.GetType().Name); }
        catch (Exception error) { Record(error); }
    }

    async Task Pump(NetworkStream source, NetworkStream destination, Direction direction, Traffic traffic)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[4]; await source.ReadExactlyAsync(header, stop.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > 4096) throw new InvalidDataException("Invalid input frame length at relay.");
                var frame = new byte[length + 4]; header.CopyTo(frame, 0); await source.ReadExactlyAsync(frame.AsMemory(4), stop.Token);
                string? correlation = null;
                if (link.DetailedTiming)
                {
                    using var document = JsonDocument.Parse(frame.AsMemory(4));
                    correlation = traffic + ":" + document.RootElement.GetProperty("Sequence").GetInt64();
                }
                link.TcpRecord(direction, traffic, frame, () => { if (!stop.IsCancellationRequested) destination.Write(frame); }, correlation);
            }
        }
        catch (EndOfStreamException) { Console.Error.WriteLine("Network relay TCP peer closed."); }
    }

    async Task Udp()
    {
        var buffer = new byte[65536];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var bytes = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                if (bytes.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VideoDatagram.Magic) continue;
                if (FrdNetwork.SameEndpoint(received.RemoteEndPoint, receiver))
                {
                    var target = sender;
                    if (target is not null) link.Enqueue(Direction.Up, Traffic.Feedback, bytes.Length + 28, () =>
                    { if (!stop.IsCancellationRequested) udp.SendTo(bytes, target); });
                }
                else if (sender is { } expected && FrdNetwork.SameEndpoint(received.RemoteEndPoint, expected))
                    link.Enqueue(Direction.Down, Traffic.Video, bytes.Length + 28, () =>
                    { if (!stop.IsCancellationRequested) udp.SendTo(bytes, receiver); }, link.DetailedTiming && bytes.Length >= VideoDatagram.Header ?
                        $"frame:{BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16))}/offset:{BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28))}" : null);
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException)
        { Console.Error.WriteLine("Network relay UDP stopped: " + error.GetType().Name); }
        catch (Exception error) { Record(error); }
    }

    void Record(Exception error) { Console.Error.WriteLine("Network relay failed: " + error); faults.Enqueue(error.ToString()); }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); udp.Close(); downstream?.Close(); upstream?.Close();
        await Task.WhenAll(tcpWorker, udpWorker).WaitAsync(TimeSpan.FromSeconds(2));
        downstream?.Dispose(); upstream?.Dispose(); udp.Dispose(); stop.Dispose();
    }
}
