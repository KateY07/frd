using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Frd;

namespace Frd.InputLatency;

static class Program
{
    static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    static async Task<int> Main(string[] args)
    {
        try
        {
            var pipeline = args.Contains("--pipeline", StringComparer.Ordinal);
            var output = Path.GetFullPath(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ??
                (pipeline ? "results/input-latency/input-ack-pipeline.json" : "results/input-latency/input-ack-serial.json"));
            if (Path.GetFileName(output).Equals("input-ack-baseline.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Preserve the original baseline; select a new report filename.");
            var cases = new List<CaseResult>();
            var run = Stopwatch.StartNew();
            foreach (var delay in new[] { 0, 5, 10, 25 })
                foreach (var workload in new[] { "isolated16", "wheel120Hz", "key60Hz", "concurrentBurst64" })
                {
                    var result = await MeasureAsync(delay, workload, pipeline);
                    cases.Add(result);
                    Print(result);
                }
            if (pipeline)
                foreach (var delay in new[] { 10, 25 })
                {
                    var result = await MeasureAsync(delay, "wheel120Hz", true, delay);
                    cases.Add(result); Print(result);
                }
            var passed = cases.All(result => result.CompleteAndOrdered && result.NoSecondScaleBacklog && result.NetworkQueueHealthy);
            var report = new
            {
                Passed = passed,
                Mode = pipeline ? "pipeline" : "serial",
                Utc = DateTimeOffset.UtcNow,
                ElapsedSeconds = run.Elapsed.TotalSeconds,
                Stopwatch.Frequency,
                Environment.ProcessorCount,
                ProductionAssembly = typeof(RemoteInputClient).Assembly.FullName,
                Scope = "Actual RemoteInputClient/RemoteInputServer, loopback TCP relay, mock injector. No physical input, no capture/codec/render, no real LAN or RDP measurement.",
                AddedLatency = "Each complete frame immediately receives an absolute due timestamp. Independent readers enqueue frames; one writer per direction waits only until each due time, preserving order without serially accumulating the configured delay. Observed scheduling delay is reported. First 16 cases delay replies only; two pipeline network cases additionally delay requests. These are simulated directional delays, not measured LAN latency.",
                GeneratorQueue = pipeline
                    ? "FIFO consumer awaits QueueAsync only until the request is written, then observes its ACK independently. All acknowledgements complete before results are calculated. Isolated16 deliberately still waits for each ACK."
                    : "FIFO consumer awaits each SendAsync ACK before sending the next event, preserving the former serial UI behavior for comparison.",
                ScopeLimit = "The generator does not execute UI dispatch, coordinate conversion, its queue cap or mouse-move coalescing. Generation timestamps are actual enqueue times, not ideal cadence deadlines.",
                Burst = "All 64 calls are initiated in ID order without awaiting previous calls. Serial mode uses a benchmark semaphore held until ACK to preserve the former client's serialized behavior; pipeline mode uses only the production client's write ordering. Every delivered ID is checked.",
                Timing = "One machine monotonic Stopwatch; no timer-resolution change. Scheduler effects are included; generator lateness and achieved cadence are reported.",
                Acceptance = "All IDs must arrive exactly once in order and be acknowledged. Pipeline delayed wheel120Hz cases must stay below 1000 ms maximum generation-to-injection time. Bidirectional cases additionally require generator queue P95 < 30 ms and last-quarter minus first-quarter mean generation-to-injection after subtracting each request's actual relay scheduling delay < 30 ms. Any failure remains in the report and makes the process fail.",
                Cases = cases
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Report: {output}; {run.Elapsed.TotalSeconds:F1}s");
            return passed ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Print(CaseResult result) => Console.WriteLine($"forward/reply={result.RequestedForwardDelayMs}/{result.RequestedReplyDelayMs}ms {result.Workload}: inject mean/p95/max={result.GenerationToInjection.Mean:F2}/{result.GenerationToInjection.P95:F2}/{result.GenerationToInjection.Max:F2}ms, ack={result.SendToAck.Mean:F2}ms, queueP95={result.GeneratorQueueWait.P95:F2}ms, residual growth={result.ResidualQuarterGrowthMs:F2}ms, ordered={result.CompleteAndOrdered}, queued={result.MaximumGeneratorQueue}, pending={result.MaximumPendingInjection}");

    static async Task<CaseResult> MeasureAsync(int delay, string workload, bool pipeline, int forwardDelay = 0)
    {
        var injector = new Recorder();
        using var server = new RemoteInputServer(injector);
        var serverErrors = new ConcurrentQueue<string>();
        server.Failed += error => serverErrors.Enqueue(error.ToString());
        await using var relay = new ReplyDelayRelay(server.Endpoint, delay, forwardDelay);
        using var client = await RemoteInputClient.ConnectAsync(relay.Endpoint);
        if (!(await client.SetEnabledAsync(true)).Accepted) throw new InvalidOperationException("Enable rejected.");
        for (var i = 0; i < 4; i++) await client.SendAsync(new(RemoteInputKind.Wheel));
        relay.BeginMeasurement();

        var samples = new ConcurrentDictionary<int, Sample>();
        injector.Samples = samples;
        var generatorQueue = 0;
        var maximumGeneratorQueue = 0;
        var rate = workload == "wheel120Hz" ? 120 : workload == "key60Hz" ? 60 : 0;
        var count = rate > 0 ? rate * 2 : workload == "isolated16" ? 16 : 64;
        var started = Stopwatch.GetTimestamp();
        var acknowledgements = new ConcurrentBag<Task>();
        using var serialBurst = new SemaphoreSlim(1, 1);

        Sample Generate(int id, long scheduled)
        {
            var sample = new Sample { Id = id, Generated = Stopwatch.GetTimestamp(), Scheduled = scheduled };
            if (!samples.TryAdd(id, sample)) throw new InvalidOperationException("Duplicate benchmark ID.");
            injector.Enqueued();
            return sample;
        }

        async Task ObserveAcknowledgementAsync(Sample sample, Task<RemoteInputResult> reply)
        {
            var result = await reply;
            sample.Acknowledged = Stopwatch.GetTimestamp();
            if (!result.Accepted) throw new InvalidOperationException(result.Message);
        }

        async Task SendAsync(Sample sample, bool isolated = false)
        {
            sample.SendStarted = Stopwatch.GetTimestamp();
            var kind = workload == "key60Hz" ? (sample.Id % 2 == 0 ? RemoteInputKind.KeyUp : RemoteInputKind.KeyDown) : RemoteInputKind.Wheel;
            var value = new RemoteInputEvent(kind, ScanCode: sample.Id, WheelDelta: 120);
            if (!pipeline && workload == "concurrentBurst64")
            {
                await serialBurst.WaitAsync();
                try { await ObserveAcknowledgementAsync(sample, client.SendAsync(value)); }
                finally { serialBurst.Release(); }
                return;
            }
            if (pipeline && !isolated)
                acknowledgements.Add(ObserveAcknowledgementAsync(sample, await client.QueueAsync(value)));
            else await ObserveAcknowledgementAsync(sample, client.SendAsync(value));
        }

        if (rate > 0)
        {
            var queue = Channel.CreateUnbounded<Sample>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var consumer = Task.Run(async () =>
            {
                await foreach (var sample in queue.Reader.ReadAllAsync())
                {
                    Interlocked.Decrement(ref generatorQueue);
                    await SendAsync(sample);
                }
            });
            for (var i = 0; i < count; i++)
            {
                var scheduled = started + (long)(i * (double)Stopwatch.Frequency / rate);
                var remaining = Milliseconds(scheduled - Stopwatch.GetTimestamp());
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                var sample = Generate(i + 1, scheduled);
                maximumGeneratorQueue = Math.Max(maximumGeneratorQueue, Interlocked.Increment(ref generatorQueue));
                await queue.Writer.WriteAsync(sample);
            }
            queue.Writer.Complete();
            await consumer.WaitAsync(TimeSpan.FromSeconds(20));
        }
        else if (workload == "concurrentBurst64")
        {
            var tasks = Enumerable.Range(1, count).Select(id => SendAsync(Generate(id, Stopwatch.GetTimestamp()))).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
            for (var i = 1; i <= count; i++) await SendAsync(Generate(i, Stopwatch.GetTimestamp()), true);

        await Task.WhenAll(acknowledgements).WaitAsync(TimeSpan.FromSeconds(20));

        var duration = Milliseconds(Stopwatch.GetTimestamp() - started);
        if (!serverErrors.IsEmpty) throw new InvalidOperationException(string.Join("\n", serverErrors));
        var ordered = samples.Values.OrderBy(sample => sample.Id).ToArray();
        if (ordered.Any(sample => sample.Injected <= 0 || sample.Acknowledged <= sample.Injected)) throw new InvalidOperationException("Incomplete or reversed sample.");
        var injectionOrder = injector.InjectionOrder.ToArray();
        var completeAndOrdered = injectionOrder.SequenceEqual(Enumerable.Range(1, count));
        var injection = Stats(ordered.Select(sample => Milliseconds(sample.Injected - sample.Generated)));
        var generatorWait = Stats(ordered.Select(sample => Milliseconds(sample.SendStarted - sample.Generated)));
        var residual = ordered.Select(sample => Milliseconds(sample.Injected - sample.Generated) - relay.ForwardByInputId[sample.Id]).ToArray();
        var quarter = Math.Max(1, count / 4);
        var residualGrowth = residual.TakeLast(quarter).Average() - residual.Take(quarter).Average();
        var noSecondScaleBacklog = !pipeline || workload != "wheel120Hz" || delay == 0 || injection.Max < 1000;
        var networkHealthy = forwardDelay == 0 || generatorWait.P95 < 30 && residualGrowth < 30;
        return new(delay, forwardDelay, workload, count, rate,
            rate == 0 ? null : (count - 1) * 1000d / Milliseconds(ordered[^1].Generated - ordered[0].Generated),
            duration, maximumGeneratorQueue, injector.MaximumPending,
            injection,
            Stats(ordered.Select(sample => Milliseconds(sample.Acknowledged - sample.Generated))),
            Stats(ordered.Select(sample => Milliseconds(sample.Acknowledged - sample.SendStarted))),
            generatorWait,
            Stats(ordered.Select(sample => Math.Max(0, Milliseconds(sample.Generated - sample.Scheduled)))),
            Stats(relay.Delays), Stats(relay.ForwardDelays), Stats(residual), residualGrowth,
            completeAndOrdered, noSecondScaleBacklog, networkHealthy, injectionOrder,
            ordered.Select(sample => new SampleResult(sample.Id,
                Milliseconds(sample.Generated - started), Milliseconds(sample.SendStarted - sample.Generated),
                Milliseconds(sample.Injected - sample.Generated), Milliseconds(sample.Acknowledged - sample.Generated),
                relay.ForwardByInputId[sample.Id])).ToArray());
    }

    static Distribution Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new(sorted.Length, sorted.Average(), sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[^1]);
    }

    sealed class Recorder : IRemoteInputInjector
    {
        int pending, maximum;
        public ConcurrentDictionary<int, Sample>? Samples { get; set; }
        public int MaximumPending => Volatile.Read(ref maximum);
        public ConcurrentQueue<int> InjectionOrder { get; } = new();
        public void Enqueued()
        {
            var value = Interlocked.Increment(ref pending);
            int old;
            do { old = Volatile.Read(ref maximum); if (value <= old) return; }
            while (Interlocked.CompareExchange(ref maximum, value, old) != old);
        }
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            if (Samples?.TryGetValue(input.ScanCode, out var sample) == true)
            {
                sample.Injected = Stopwatch.GetTimestamp();
                InjectionOrder.Enqueue(input.ScanCode);
                Interlocked.Decrement(ref pending);
            }
            return new(true, "Mock accepted; no Windows input injected.");
        }
        public RemoteInputResult ReleaseAll() => new(true, "Mock released.");
    }

    sealed class Sample
    {
        public int Id;
        public long Scheduled, Generated, SendStarted, Injected, Acknowledged;
    }
    sealed record Distribution(int Count, double Mean, double P95, double Max);
    sealed record SampleResult(int Id, double GeneratedOffsetMs, double GeneratorWaitMs, double GenerationToInjectionMs, double GenerationToAckMs, double ActualForwardSchedulingDelayMs);
    sealed record CaseResult(int RequestedReplyDelayMs, int RequestedForwardDelayMs, string Workload, int Events, int NominalEventsPerSecond,
        double? ActualGeneratedEventsPerSecond, double DurationMs, int MaximumGeneratorQueue, int MaximumPendingInjection,
        Distribution GenerationToInjection, Distribution GenerationToAck, Distribution SendToAck,
        Distribution GeneratorQueueWait, Distribution GeneratorLateness, Distribution ActualRelayReplyDelay,
        Distribution ActualRelayForwardDelay, Distribution GenerationToInjectionMinusForwardDelay, double ResidualQuarterGrowthMs,
        bool CompleteAndOrdered, bool NoSecondScaleBacklog, bool NetworkQueueHealthy, int[] InjectionOrder, SampleResult[] Samples);

    sealed class ReplyDelayRelay : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task worker;
        readonly IPEndPoint target;
        readonly int delay, forwardDelay;
        long measurementStart = long.MaxValue;
        public ConcurrentQueue<double> Delays { get; } = new();
        public ConcurrentQueue<double> ForwardDelays { get; } = new();
        public ConcurrentDictionary<int, double> ForwardByInputId { get; } = new();
        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

        public ReplyDelayRelay(IPEndPoint target, int delay, int forwardDelay)
        {
            this.target = target;
            this.delay = delay;
            this.forwardDelay = forwardDelay;
            listener.Start(1);
            worker = RunAsync();
        }

        public void BeginMeasurement()
        {
            Delays.Clear(); ForwardDelays.Clear(); ForwardByInputId.Clear();
            Volatile.Write(ref measurementStart, Stopwatch.GetTimestamp());
        }

        async Task RunAsync()
        {
            try
            {
                using var downstream = await listener.AcceptTcpClientAsync(stop.Token);
                using var upstream = new TcpClient { NoDelay = true };
                downstream.NoDelay = true;
                await upstream.ConnectAsync(target, stop.Token);
                var fromClient = downstream.GetStream();
                var fromServer = upstream.GetStream();
                var forward = RelayFramesAsync(fromClient, fromServer, forwardDelay, true);
                var reply = RelayFramesAsync(fromServer, fromClient, delay, false);
                await Task.WhenAny(forward, reply);
                stop.Cancel();
                await Task.WhenAll(forward, reply);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[Benchmark] Relay cancelled after case."); }
            catch (EndOfStreamException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[Benchmark] Relay ended after case."); }
        }

        async Task RelayFramesAsync(NetworkStream source, NetworkStream destination, int delayMilliseconds, bool forward)
        {
            using var directionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var frames = Channel.CreateUnbounded<DelayedFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var read = ReadFramesAsync();
            var write = WriteFramesAsync();
            await Task.WhenAny(read, write);
            directionStop.Cancel();
            await Task.WhenAll(read, write);

            async Task ReadFramesAsync()
            {
                var header = new byte[4];
                try
                {
                    while (!directionStop.IsCancellationRequested)
                    {
                        await source.ReadExactlyAsync(header, directionStop.Token);
                        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                        if (length is <= 0 or > 4096) throw new InvalidDataException("Unexpected input protocol frame.");
                        var frame = new byte[4 + length];
                        header.CopyTo(frame, 0);
                        await source.ReadExactlyAsync(frame.AsMemory(4), directionStop.Token);
                        var received = Stopwatch.GetTimestamp();
                        var due = received + (long)(delayMilliseconds * (double)Stopwatch.Frequency / 1000);
                        var inputId = forward ? JsonSerializer.Deserialize<ObservedRequest>(frame.AsSpan(4))?.Input?.ScanCode ?? 0 : 0;
                        await frames.Writer.WriteAsync(new(frame, received, due, inputId), directionStop.Token);
                    }
                }
                finally { frames.Writer.TryComplete(); }
            }

            async Task WriteFramesAsync()
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(directionStop.Token))
                {
                    while (true)
                    {
                        var remaining = Milliseconds(frame.Due - Stopwatch.GetTimestamp());
                        if (remaining <= 0) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), directionStop.Token);
                    }
                    var observed = Milliseconds(Stopwatch.GetTimestamp() - frame.Received);
                    if (frame.Received >= Volatile.Read(ref measurementStart))
                    {
                        if (forward)
                        {
                            ForwardDelays.Enqueue(observed);
                            if (frame.InputId != 0 && !ForwardByInputId.TryAdd(frame.InputId, observed))
                                throw new InvalidDataException("Duplicate input ID entered the forward relay.");
                        }
                        else Delays.Enqueue(observed);
                    }
                    await destination.WriteAsync(frame.Bytes, directionStop.Token);
                }
            }
        }

        sealed record DelayedFrame(byte[] Bytes, long Received, long Due, int InputId);
        sealed record ObservedRequest(RemoteInputEvent? Input);

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await worker.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Dispose();
        }
    }
}
