using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Frd.ConstrainedNetwork;

enum Direction { Up, Down }
enum Traffic { Input, InputReply, Video, Feedback, ModeledTcpAck }
sealed record WireItem(Direction Direction, Traffic Traffic, int Bytes, long Enqueued, Action Deliver, string? Correlation)
{
    public long ServiceStarted, SerializationEnds;
}

sealed class FiniteLink : IDisposable
{
    readonly object gate = new();
    readonly AutoResetEvent changed = new(false);
    readonly PrecisionTimer timer = new();
    readonly Thread worker;
    readonly Lane[] lanes;
    readonly bool priority;
    readonly double propagationMs;
    readonly bool detailedTiming;
    readonly long started = Stopwatch.GetTimestamp();
    readonly Queue<object> packetTimings = new();
    readonly PriorityQueue<(WireItem Item, long Due), long> deliveries = new();
    readonly Dictionary<Traffic, TrafficStats> stats = Enum.GetValues<Traffic>().ToDictionary(x => x, _ => new TrafficStats());
    readonly List<string> errors = new();
    double bitsPerSecond;
    bool stopped;
    public const double MaximumVideoQueueMs = 150;

    public FiniteLink(bool fullDuplex, bool priority, double mbps, double propagationMs = 20, bool detailedTiming = false)
    {
        if (!double.IsFinite(propagationMs) || propagationMs < 0) throw new ArgumentOutOfRangeException(nameof(propagationMs));
        lanes = fullDuplex ? [new(), new()] : [new()];
        this.priority = priority; bitsPerSecond = mbps * 1_000_000;
        this.propagationMs = propagationMs; this.detailedTiming = detailedTiming;
        worker = new(Run) { IsBackground = true, Name = "FRD test finite link scheduler" }; worker.Start();
    }

    public void SetCapacity(double mbps)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp(); var replacement = mbps * 1_000_000;
            foreach (var lane in lanes)
                if (lane.Active is not null)
                {
                    lane.End = now + Ticks(Math.Max(0, lane.End - now) / (double)Stopwatch.Frequency * bitsPerSecond / replacement * 1000);
                    lane.Active.SerializationEnds = lane.End;
                }
            bitsPerSecond = replacement;
        }
        changed.Set();
    }

    public void Enqueue(Direction direction, Traffic traffic, int wireBytes, Action deliver, string? correlation = null)
    {
        lock (gate)
        {
            if (stopped) return;
            var lane = lanes.Length == 1 ? lanes[0] : lanes[(int)direction];
            var item = new WireItem(direction, traffic, wireBytes, Stopwatch.GetTimestamp(), deliver, correlation);
            var measurements = stats[traffic]; measurements.OfferedPackets++; measurements.OfferedBytes += wireBytes;
            var backlogBytes = lane.WaitingBytes + (lane.Active?.Bytes ?? 0);
            if (traffic == Traffic.Video && (backlogBytes + wireBytes) * 8000d / bitsPerSecond > MaximumVideoQueueMs)
            { measurements.DroppedPackets++; measurements.DroppedBytes += wireBytes; return; }
            (priority && traffic != Traffic.Video ? lane.Control : lane.Normal).Enqueue(item);
            lane.WaitingBytes += wireBytes;
        }
        changed.Set();
    }

    public void TcpRecord(Direction direction, Traffic traffic, byte[] frame, Action deliver, string? correlation = null)
    {
        // Explicit IPv4 model: MSS 1160, 40-byte TCP/IP header and one reverse 40-byte ACK per segment.
        // Actual loopback TCP segmentation, ACK coalescing and retransmissions are not observed or emulated.
        for (var offset = 0; offset < frame.Length; offset += 1160)
        {
            var count = Math.Min(1160, frame.Length - offset); var final = offset + count == frame.Length;
            Enqueue(direction, traffic, count + 40, () =>
            {
                Enqueue(direction == Direction.Up ? Direction.Down : Direction.Up, Traffic.ModeledTcpAck, 40, () => { });
                if (final) deliver();
            }, correlation);
        }
    }

    public object Snapshot()
    {
        lock (gate) return new
        {
            CapacityMbpsPerLane = bitsPerSecond / 1_000_000, Lanes = lanes.Length, Priority = priority, OneWayPropagationMs = propagationMs,
            WaitingBytes = lanes.Sum(x => x.WaitingBytes), InService = lanes.Count(x => x.Active is not null), InPropagation = deliveries.Count,
            Traffic = stats.ToDictionary(x => x.Key.ToString(), x => new
            {
                x.Value.OfferedPackets, x.Value.OfferedBytes, x.Value.DeliveredPackets, x.Value.DeliveredBytes,
                x.Value.DroppedPackets, x.Value.DroppedBytes, QueueWait = Program.Summary(x.Value.Waits), WakeupOverrun = Program.Summary(x.Value.Overruns)
            }), PacketTimings = packetTimings.ToArray(),
            PacketTimingMeaning = "Last 256 delivered packets, milliseconds relative to link construction. ActualProxyRelease is the real socket handoff; target mock injection and complete-frame arrival are recorded separately. PlannedArrival = ServiceStarted + planned serialization + fixed propagation. No queueing is counted as propagation.", Errors = errors.ToArray()
        };
    }

    public long DeliveredVideoBytes { get { lock (gate) return stats[Traffic.Video].DeliveredBytes; } }
    public long DroppedVideoPackets { get { lock (gate) return stats[Traffic.Video].DroppedPackets; } }
    public bool HasErrors { get { lock (gate) return errors.Count != 0; } }
    public bool DetailedTiming => detailedTiming;

    void Run()
    {
        try
        {
            while (true)
            {
                List<(WireItem Item, long Due)> ready = new();
                long next = long.MaxValue;
                lock (gate)
                {
                    if (stopped) return;
                    var now = Stopwatch.GetTimestamp();
                    foreach (var lane in lanes)
                    {
                        if (lane.Active is { } active && lane.End <= now)
                        {
                            var due = lane.End + Ticks(propagationMs);
                            deliveries.Enqueue((active, due), due); lane.Active = null;
                        }
                        while (lane.Active is null && (lane.Control.Count != 0 || lane.Normal.Count != 0))
                        {
                            var item = lane.Control.Count != 0 ? lane.Control.Dequeue() : lane.Normal.Dequeue();
                            lane.WaitingBytes -= item.Bytes;
                            var wait = Milliseconds(now - item.Enqueued);
                            var measurement = stats[item.Traffic];
                            if (item.Traffic == Traffic.Video && wait > MaximumVideoQueueMs)
                            { measurement.DroppedPackets++; measurement.DroppedBytes += item.Bytes; continue; }
                            measurement.Waits.Add(wait); lane.Active = item;
                            lane.End = now + Ticks(item.Bytes * 8000d / bitsPerSecond);
                            item.ServiceStarted = now; item.SerializationEnds = lane.End;
                        }
                        if (lane.Active is not null) next = Math.Min(next, lane.End);
                    }
                    while (deliveries.TryPeek(out var item, out var due) && due <= now) { deliveries.Dequeue(); ready.Add(item); }
                    if (deliveries.TryPeek(out _, out var future)) next = Math.Min(next, future);
                }
                foreach (var delivery in ready)
                {
                    try
                    {
                        delivery.Item.Deliver();
                        var released = Stopwatch.GetTimestamp();
                        lock (gate)
                        {
                            var measurement = stats[delivery.Item.Traffic]; measurement.DeliveredPackets++; measurement.DeliveredBytes += delivery.Item.Bytes;
                            measurement.Overruns.Add(Math.Max(0, Milliseconds(released - delivery.Due)));
                            if (detailedTiming)
                            {
                                var item = delivery.Item;
                                if (packetTimings.Count == 256) packetTimings.Dequeue();
                                packetTimings.Enqueue(new
                                {
                                    Traffic = item.Traffic.ToString(), Direction = item.Direction.ToString(), WireBytes = item.Bytes, item.Correlation,
                                    QueuedAtMs = Milliseconds(item.Enqueued - started), ServiceStartedMs = Milliseconds(item.ServiceStarted - started),
                                    QueueWaitMs = Milliseconds(item.ServiceStarted - item.Enqueued), PlannedSerializationMs = Milliseconds(item.SerializationEnds - item.ServiceStarted),
                                    OneWayPropagationMs = propagationMs, PlannedArrivalMs = Milliseconds(delivery.Due - started),
                                    ActualProxyReleaseMs = Milliseconds(released - started), ArrivalOverrunMs = Math.Max(0, Milliseconds(released - delivery.Due))
                                });
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine("Finite link delivery failed: " + error);
                        lock (gate) errors.Add(error.ToString());
                    }
                }
                if (ready.Count != 0) continue;
                if (next == long.MaxValue) changed.WaitOne();
                else timer.WaitUntil(next, changed);
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Finite link scheduler failed: " + error); lock (gate) errors.Add(error.ToString()); }
    }

    public void Dispose()
    {
        lock (gate) stopped = true;
        changed.Set();
        if (!worker.Join(2000)) throw new TimeoutException("Finite link scheduler failed to stop.");
        changed.Dispose(); timer.Dispose();
    }

    public static long Ticks(double milliseconds) => (long)Math.Ceiling(milliseconds * Stopwatch.Frequency / 1000);
    public static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    sealed class Lane
    {
        public readonly Queue<WireItem> Control = new(), Normal = new();
        public WireItem? Active;
        public long End, WaitingBytes;
    }
    sealed class TrafficStats
    {
        public long OfferedPackets, OfferedBytes, DeliveredPackets, DeliveredBytes, DroppedPackets, DroppedBytes;
        public readonly List<double> Waits = new(), Overruns = new();
    }
}

sealed class PrecisionTimer : WaitHandle
{
    public PrecisionTimer()
    {
        SafeWaitHandle = CreateWaitableTimerExW(0, null, 2, 0x1f0003);
        if (SafeWaitHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void WaitUntil(long tick, WaitHandle? wake = null)
    {
        var remaining = tick - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        var due = -Math.Max(1, (long)Math.Ceiling(remaining * 10_000_000d / Stopwatch.Frequency));
        if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, 0, 0, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (wake is null) WaitOne(); else WaitAny([this, wake]);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint state, bool resume);
}
