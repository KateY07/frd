using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Frd;

public record ReceivedVideo(int Generation, long FrameId, bool KeyFrame, byte[] Data)
{
    public long ReassembledTick { get; init; }
}
public sealed record VideoSendTiming(int PayloadBytes, int Packets, long FirstSendTick, long LastSendTick,
    double PlannedWaitMs, double WakeupOverrunMs);
public record NetworkSnapshot(double SentMbps, double ReceivedMbps, double EstimatedMbps, double DelayTrendMs,
    double LossRate, long SentPackets, long ReceivedPackets, string EstimateState)
{
    public double DiagnosticMbps { get; init; }
    public long DiagnosticPackets { get; init; }
    public int SendBudgetKbps { get; init; }
    public int BurstBudgetWireBytes { get; init; }
    public double SimulatedCapacityMbps { get; init; }
}
public record NetworkSimulation(double CapacityMbps = 0, double LossRate = 0, int MaxDelayMs = 0);

static class VideoDatagram
{
    static readonly ConcurrentDictionary<(string Operation, SocketError Error), byte> transientErrors = new();
    public const uint Magic = 0x32445246;
    public const int Header = 36, MaxSize = 1172, Payload = MaxSize - Header, FeedbackSize = 72;
    public const int MaxFrameSize = 8 * 1024 * 1024, MaxBufferedBytes = 16 * 1024 * 1024;
    public static long Now => Stopwatch.GetTimestamp();
    public static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
    public static void Log(string message, Exception? error = null) =>
        Console.Error.WriteLine($"[UDP] {message}{(error is null ? "" : $": {error}")}");

    public static void OpenReturnPath(Socket socket, IPEndPoint peer)
    {
        // The receiving socket initiates its UDP flow so stateful firewalls can admit replies.
        Span<byte> registration = stackalloc byte[8];
        registration.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(registration, Magic);
        registration[4] = 3;
        socket.SendTo(registration, SocketFlags.None, peer);
    }

    public static bool Recoverable(SocketException error, string operation)
    {
        if (error.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionRefused or
            SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or
            SocketError.TimedOut or SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable)) return false;
        if (transientErrors.TryAdd((operation, error.SocketErrorCode), 0))
            Log($"{operation}: transient socket error; packet skipped, socket retained. Repeated {error.SocketErrorCode} errors are suppressed", error);
        return true;
    }
}

public sealed class UdpVideoSender : IDisposable
{
    readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly SafeWaitHandle pacingTimer = CreatePacingTimer();
    readonly IPEndPoint destination;
    readonly CancellationTokenSource shutdown = new();
    readonly Thread feedbackThread, delayedThread;
    readonly object sendGate = new(), statsGate = new(), delayGate = new();
    readonly PriorityQueue<(byte[] Data, IPEndPoint Destination), long> delayed = new();
    readonly AutoResetEvent delayedChanged = new(false);
    readonly SendStamp[] stamps = new SendStamp[65536];
    readonly Queue<(long Time, int Sent, long Received)> rates = new();
    readonly Queue<(long Time, int Bytes)> diagnosticRates = new();
    NetworkSimulation simulation;
    int limitKbps = 1000, disposed;
    uint sequence;
    double nextSendTick, delayTrend, deliveryEstimate;
    bool rateLimited = true;
    long sentPackets, receivedPackets, receivedWireBytes, lastFeedbackTick;
    long diagnosticPackets;
    long lastReceiverTick, lastSenderTick, previousReceiverTotal;
    double lossRate;

    readonly record struct SendStamp(uint Sequence, long Tick);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint callback, nint state,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    static SafeWaitHandle CreatePacingTimer()
    {
        var timer = CreateWaitableTimerEx(0, 0, 2, 0x1F0003);
        if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "High-resolution packet pacing timer creation failed.");
        return timer;
    }

    public UdpVideoSender(IPEndPoint destination, NetworkSimulation? simulation = null)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The 1200-byte MTU budget currently requires IPv4.", nameof(destination));
        this.destination = destination;
        this.simulation = ValidateSimulation(simulation ?? new());
        socket.SendBufferSize = 1024 * 1024;
        socket.ReceiveBufferSize = 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        feedbackThread = new(ReceiveFeedback) { IsBackground = true, Name = "FRD UDP feedback" };
        delayedThread = new(DeliverDelayed) { IsBackground = true, Name = "FRD simulated packet delay" };
        feedbackThread.Start();
        delayedThread.Start();
    }

    public int ActualPort => ((IPEndPoint)socket.LocalEndPoint!).Port;
    public event Action<int, long, VideoSendTiming>? FrameSending;
    public void SetLimitKbps(int kbps)
    {
        if (kbps is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(kbps));
        lock (sendGate) { rateLimited = true; Volatile.Write(ref limitKbps, kbps); }
    }
    public void SetEncoderBitrateKbps(int kbps)
    {
        if (kbps is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(kbps));
        // FfmpegEncoder controls bitrate; ordinary UDP delivery does not pace against that target.
        lock (sendGate) { rateLimited = false; Volatile.Write(ref limitKbps, kbps); nextSendTick = 0; }
    }
    public void SetSimulation(NetworkSimulation value) => Volatile.Write(ref simulation, ValidateSimulation(value));

    static NetworkSimulation ValidateSimulation(NetworkSimulation value)
    {
        if (!double.IsFinite(value.CapacityMbps) || value.CapacityMbps is < 0 or > 1000 ||
            !double.IsFinite(value.LossRate) || value.LossRate is < 0 or > 1 || value.MaxDelayMs is < 0 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    public void Send(ReceivedVideo video, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (video.Data.Length is <= 0 or > VideoDatagram.MaxFrameSize)
            throw new ArgumentOutOfRangeException(nameof(video), "Encoded access units must contain 1–8388608 bytes.");
        lock (sendGate)
        {
            long firstSendTick = 0;
            double plannedWaitMs = 0, wakeupOverrunMs = 0;
            var packetCount = 0;
            for (var offset = 0; offset < video.Data.Length; offset += VideoDatagram.Payload)
            {
                token.ThrowIfCancellationRequested();
                shutdown.Token.ThrowIfCancellationRequested();
                var count = Math.Min(VideoDatagram.Payload, video.Data.Length - offset);
                var packet = new byte[VideoDatagram.Header + count];
                var seq = unchecked(++sequence);
                BinaryPrimitives.WriteUInt32LittleEndian(packet, VideoDatagram.Magic);
                packet[4] = 1;
                packet[5] = video.KeyFrame ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), seq);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), video.Generation);
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), video.FrameId);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24), video.Data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28), offset);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32), (ushort)count);
                video.Data.AsSpan(offset, count).CopyTo(packet.AsSpan(VideoDatagram.Header));
                var wait = Pace(packet.Length + 28, token);
                plannedWaitMs += wait.PlannedMs;
                wakeupOverrunMs += wait.OverrunMs;
                var now = VideoDatagram.Now;
                if (firstSendTick == 0) firstSendTick = now;
                packetCount++;
                lock (statsGate)
                {
                    stamps[seq % stamps.Length] = new(seq, now);
                    sentPackets++;
                    rates.Enqueue((now, packet.Length + 28, 0));
                    TrimRates(now);
                }
                if (offset + count == video.Data.Length)
                    FrameSending?.Invoke(video.Generation, video.FrameId, new(video.Data.Length, packetCount,
                        firstSendTick, now, plannedWaitMs, wakeupOverrunMs));
                RoutePacket(packet, destination, now);
            }
        }
    }

    public void SendDiagnostic(byte[] packet, IPEndPoint diagnosticDestination, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (packet.Length is <= 0 or > VideoDatagram.MaxSize || diagnosticDestination.AddressFamily != AddressFamily.InterNetwork || diagnosticDestination.Equals(destination))
            throw new ArgumentException("Diagnostics require a separate IPv4 destination and at most 1172 UDP payload bytes.");
        lock (sendGate)
        {
            token.ThrowIfCancellationRequested();
            shutdown.Token.ThrowIfCancellationRequested();
            Pace(packet.Length + 28, token);
            var now = VideoDatagram.Now;
            lock (statsGate)
            {
                diagnosticPackets++;
                diagnosticRates.Enqueue((now, packet.Length + 28));
                rates.Enqueue((now, packet.Length + 28, 0));
                TrimRates(now);
            }
            RoutePacket(packet, diagnosticDestination, now);
        }
    }

    void RoutePacket(byte[] packet, IPEndPoint target, long now)
    {
        var conditions = Volatile.Read(ref simulation);
        if (Random.Shared.NextDouble() < conditions.LossRate) return;
        if (conditions.MaxDelayMs == 0) SendPacket(packet, target);
        else
        {
            lock (delayGate)
            {
                if (delayed.Count >= 4096)
                {
                    VideoDatagram.Log("Simulated-delay queue full; dropping packet.");
                    return;
                }
                var due = now + (long)(Random.Shared.NextDouble() * conditions.MaxDelayMs * Stopwatch.Frequency / 1000);
                delayed.Enqueue((packet, target), due);
            }
            delayedChanged.Set();
        }
    }

    void SendPacket(byte[] packet, IPEndPoint target)
    {
        try { socket.SendTo(packet, target); }
        catch (SocketException error) when (!shutdown.IsCancellationRequested && VideoDatagram.Recoverable(error, "Video send"))
        {
            // UDP delivery failure drops this packet; subsequent packets may still succeed.
        }
    }

    (double PlannedMs, double OverrunMs) Pace(int wireBytes, CancellationToken token)
    {
        var conditions = Volatile.Read(ref simulation);
        if (!rateLimited && conditions.CapacityMbps <= 0) return (0, 0);
        var started = VideoDatagram.Now;
        var plannedTicks = Math.Max(0, nextSendTick - started);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            shutdown.Token.ThrowIfCancellationRequested();
            var remainingMs = (nextSendTick - VideoDatagram.Now) * 1000 / Stopwatch.Frequency;
            if (remainingMs <= 0) break;
            var dueTime = -(long)Math.Ceiling(Math.Min(remainingMs, 20) * 10_000);
            if (!SetWaitableTimer(pacingTimer, ref dueTime, 0, 0, 0, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(pacingTimer, 100) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var rate = rateLimited ? Volatile.Read(ref limitKbps) * 1000d : double.PositiveInfinity;
        if (conditions.CapacityMbps > 0) rate = Math.Min(rate, conditions.CapacityMbps * 1_000_000);
        // Carry no idle credit and no timer catch-up credit: the maximum burst is one datagram.
        var finished = VideoDatagram.Now;
        nextSendTick = finished + wireBytes * 8d * Stopwatch.Frequency / rate;
        return (plannedTicks * 1000 / Stopwatch.Frequency,
            plannedTicks > 0 ? Math.Max(0, finished - started - plannedTicks) * 1000 / Stopwatch.Frequency : 0);
    }

    void DeliverDelayed()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                (byte[] Data, IPEndPoint Destination)? packet = null;
                var waitMs = 100;
                lock (delayGate)
                {
                    if (delayed.TryPeek(out _, out var due))
                    {
                        var ms = (due - VideoDatagram.Now) * 1000d / Stopwatch.Frequency;
                        if (ms <= 0) packet = delayed.Dequeue();
                        else waitMs = Math.Clamp((int)Math.Ceiling(ms), 1, 100);
                    }
                }
                if (packet is { } ready) SendPacket(ready.Data, ready.Destination);
                else delayedChanged.WaitOne(waitMs);
            }
        }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Delayed sender stopped."); }
        catch (SocketException error) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Delayed sender stopped", error); }
        catch (Exception error) { VideoDatagram.Log("Delayed sender failed", error); }
    }

    void ReceiveFeedback()
    {
        var bytes = new byte[VideoDatagram.MaxSize];
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    if (!socket.Poll(100_000, SelectMode.SelectRead)) continue;
                    length = socket.ReceiveFrom(bytes, ref peer);
                }
                catch (SocketException error) when (!shutdown.IsCancellationRequested && VideoDatagram.Recoverable(error, "Feedback receive"))
                {
                    continue;
                }
                if (!destination.Equals(peer) || length != VideoDatagram.FeedbackSize || bytes[4] != 2 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VideoDatagram.Magic) continue;
                var firstSeq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                var lastSeq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                var packetCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
                var wireBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
                var firstReceive = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24));
                var lastReceive = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32));
                var frequency = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40));
                var totalPackets = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48));
                var totalWireBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56));
                var highestSeq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(64));
                if (packetCount == 0 || wireBytes == 0 || frequency <= 0 || lastReceive < firstReceive || totalPackets < 0) continue;
                var now = VideoDatagram.Now;
                lock (statsGate)
                {
                    if (totalPackets <= receivedPackets || totalPackets > sentPackets || totalWireBytes < receivedWireBytes) continue;
                    var first = stamps[firstSeq % stamps.Length];
                    var last = stamps[lastSeq % stamps.Length];
                    if (first.Sequence != firstSeq || last.Sequence != lastSeq) continue;
                    rates.Enqueue((now, 0, totalWireBytes - receivedWireBytes));
                    receivedPackets = totalPackets;
                    receivedWireBytes = totalWireBytes;
                    var receiverDelta = lastReceive - lastReceiverTick;
                    var senderDelta = last.Tick - lastSenderTick;
                    if (lastReceiverTick != 0 && receiverDelta > 0 && senderDelta > 0)
                    {
                        var receiveSeconds = (double)receiverDelta / frequency;
                        var trend = (receiveSeconds - VideoDatagram.Seconds(senderDelta)) * 1000;
                        delayTrend = .8 * delayTrend + .2 * Math.Clamp(trend, -1000, 1000);
                        var sampleSeconds = Math.Max(receiveSeconds, VideoDatagram.Seconds(senderDelta));
                        var delivered = (totalWireBytes - previousReceiverTotal) * 8 / sampleSeconds / 1_000_000;
                        if (rateLimited) delivered = Math.Min(delivered, Volatile.Read(ref limitKbps) / 1000d);
                        deliveryEstimate = deliveryEstimate == 0 ? delivered : .8 * deliveryEstimate + .2 * delivered;
                    }
                    lastReceiverTick = lastReceive;
                    lastSenderTick = last.Tick;
                    previousReceiverTotal = totalWireBytes;
                    lastFeedbackTick = now;
                    lossRate = highestSeq == 0 ? 0 : Math.Clamp(1 - (double)totalPackets / highestSeq, 0, 1);
                    TrimRates(now);
                }
            }
        }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Feedback listener stopped."); }
        catch (SocketException error) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Feedback listener stopped", error); }
        catch (Exception error) { VideoDatagram.Log("Feedback listener failed", error); }
    }

    void TrimRates(long now)
    {
        while (rates.TryPeek(out var entry) && VideoDatagram.Seconds(now - entry.Time) > 1) rates.Dequeue();
        while (diagnosticRates.TryPeek(out var entry) && VideoDatagram.Seconds(now - entry.Time) > 1) diagnosticRates.Dequeue();
    }

    public NetworkSnapshot Snapshot
    {
        get
        {
            lock (statsGate)
            {
                var now = VideoDatagram.Now;
                TrimRates(now);
                var sent = rates.Sum(x => (long)x.Sent) * 8 / 1_000_000d;
                var diagnostics = diagnosticRates.Sum(x => (long)x.Bytes) * 8 / 1_000_000d;
                var videoSent = sent - diagnostics;
                var received = rates.Sum(x => x.Received) * 8 / 1_000_000d;
                var stale = lastFeedbackTick == 0 || VideoDatagram.Seconds(now - lastFeedbackTick) > 2;
                var state = videoSent <= 0 ? "idle; capacity unknown" : stale ? "awaiting feedback" :
                    videoSent < Volatile.Read(ref limitKbps) / 1000d * .8 ? "application-limited; measured lower bound" :
                    delayTrend > 2 ? "queue growing; advisory only" : "observed delivery; measured lower bound";
                return new(sent, received, stale ? 0 : deliveryEstimate, delayTrend, lossRate, sentPackets, receivedPackets, state)
                    { DiagnosticMbps = diagnostics, DiagnosticPackets = diagnosticPackets, SendBudgetKbps = rateLimited ? Volatile.Read(ref limitKbps) : 0,
                        BurstBudgetWireBytes = rateLimited ? 1200 : 0, SimulatedCapacityMbps = Volatile.Read(ref simulation).CapacityMbps };
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        delayedChanged.Set();
        socket.Dispose();
        feedbackThread.Join(2000);
        delayedThread.Join(2000);
        delayedChanged.Dispose();
        pacingTimer.Dispose();
        shutdown.Dispose();
    }
}

public sealed class UdpVideoReceiver : IDisposable
{
    readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource shutdown = new();
    readonly Thread receiveThread, dispatchThread;
    readonly Channel<ReceivedVideo> completed = Channel.CreateBounded<ReceivedVideo>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Dictionary<(int Generation, long Frame), Assembly> frames = new();
    readonly uint[] seen = new uint[65536];
    readonly bool[] seenValid = new bool[65536];
    EndPoint? source;
    IPEndPoint? expectedSource;
    long bufferedBytes, queuedBytes, totalPackets, totalWireBytes, firstReceiveTick, lastReceiveTick, feedbackTick;
    uint firstSequence, lastSequence, highestSequence, batchPackets, batchWireBytes;
    int disposed, currentGeneration = int.MinValue;

    sealed class Assembly(int total, bool keyFrame)
    {
        public readonly byte[] Data = new byte[total];
        public readonly bool[] Fragments = new bool[(total + VideoDatagram.Payload - 1) / VideoDatagram.Payload];
        public readonly bool KeyFrame = keyFrame;
        public readonly long Created = VideoDatagram.Now;
        public int Count;
    }

    public UdpVideoReceiver(int port = 0)
    {
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.ReceiveBufferSize = 1024 * 1024;
        receiveThread = new(Receive) { IsBackground = true, Name = "FRD UDP receive and feedback" };
        dispatchThread = new(Dispatch) { IsBackground = true, Name = "FRD decoded-stream dispatch" };
        receiveThread.Start();
        dispatchThread.Start();
    }

    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
    public event Action<ReceivedVideo>? VideoReceived;
    public void SetExpectedSource(IPEndPoint endpoint)
    {
        Volatile.Write(ref expectedSource, endpoint);
        VideoDatagram.OpenReturnPath(socket, endpoint);
    }

    void Receive()
    {
        var bytes = new byte[65536];
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    if (!socket.Poll(20_000, SelectMode.SelectRead))
                    {
                        FlushFeedback();
                        ExpireFrames();
                        continue;
                    }
                    length = socket.ReceiveFrom(bytes, ref peer);
                    if (Volatile.Read(ref expectedSource) is { } expected && !expected.Equals(peer)) continue;
                }
                catch (SocketException error) when (!shutdown.IsCancellationRequested && VideoDatagram.Recoverable(error, "Video receive"))
                {
                    continue;
                }
                var now = VideoDatagram.Now;
                if (length < VideoDatagram.Header || length > VideoDatagram.MaxSize || bytes[4] != 1 || bytes[5] > 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VideoDatagram.Magic) continue;
                var seq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                var generation = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
                var frameId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
                var total = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
                var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32));
                if (total is <= 0 or > VideoDatagram.MaxFrameSize || offset < 0 || offset >= total ||
                    offset % VideoDatagram.Payload != 0 || count != Math.Min(VideoDatagram.Payload, total - offset) ||
                    length != VideoDatagram.Header + count) continue;
                if (source is not null && !source.Equals(peer)) continue;
                source ??= peer;
                var slot = seq % seen.Length;
                if (seenValid[slot] && seen[slot] == seq) continue;
                if (totalPackets > 0 && unchecked((int)(seq - highestSequence)) < -seen.Length) continue;
                seen[slot] = seq;
                seenValid[slot] = true;
                if (totalPackets == 0 || unchecked((int)(seq - highestSequence)) > 0) highestSequence = seq;
                if (batchPackets == 0) { firstSequence = seq; firstReceiveTick = now; }
                lastSequence = seq;
                lastReceiveTick = now;
                batchPackets++;
                batchWireBytes += (uint)(length + 28);
                totalPackets++;
                totalWireBytes += length + 28;
                if (generation < currentGeneration) { MaybeFlushFeedback(now); continue; }
                if (generation > currentGeneration)
                {
                    frames.Clear();
                    bufferedBytes = 0;
                    currentGeneration = generation;
                }
                ExpireFrames();
                var key = (generation, frameId);
                if (!frames.TryGetValue(key, out var frame))
                {
                    while (frames.Count >= 32 || bufferedBytes + total > VideoDatagram.MaxBufferedBytes)
                    {
                        var oldest = frames.MinBy(x => x.Value.Created);
                        bufferedBytes -= oldest.Value.Data.Length;
                        frames.Remove(oldest.Key);
                    }
                    frame = new(total, bytes[5] == 1);
                    frames.Add(key, frame);
                    bufferedBytes += total;
                }
                if (frame.Data.Length != total || frame.KeyFrame != (bytes[5] == 1)) { MaybeFlushFeedback(now); continue; }
                var index = offset / VideoDatagram.Payload;
                if (!frame.Fragments[index])
                {
                    bytes.AsSpan(VideoDatagram.Header, count).CopyTo(frame.Data.AsSpan(offset));
                    frame.Fragments[index] = true;
                    frame.Count++;
                }
                if (frame.Count == frame.Fragments.Length)
                {
                    var reassembledTick = VideoDatagram.Now;
                    frames.Remove(key);
                    bufferedBytes -= total;
                    FlushFeedback();
                    var queued = Interlocked.Add(ref queuedBytes, total);
                    if (queued > VideoDatagram.MaxBufferedBytes || !completed.Writer.TryWrite(new(generation, frameId, frame.KeyFrame, frame.Data)
                        { ReassembledTick = reassembledTick }))
                    {
                        Interlocked.Add(ref queuedBytes, -total);
                        VideoDatagram.Log($"Decoder dispatch queue full; dropped access unit {generation}/{frameId}.");
                    }
                }
                else MaybeFlushFeedback(now);
            }
        }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Receiver stopped."); }
        catch (SocketException error) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Receiver stopped", error); }
        catch (Exception error) { VideoDatagram.Log("Receiver failed", error); }
        finally { completed.Writer.TryComplete(); }
    }

    void MaybeFlushFeedback(long now)
    {
        if (VideoDatagram.Seconds(now - feedbackTick) >= .02) FlushFeedback();
    }

    void FlushFeedback()
    {
        if (batchPackets == 0 || source is null) return;
        Span<byte> bytes = stackalloc byte[VideoDatagram.FeedbackSize];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VideoDatagram.Magic);
        bytes[4] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], firstSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], lastSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], batchPackets);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], batchWireBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], firstReceiveTick);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], lastReceiveTick);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[40..], Stopwatch.Frequency);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], totalPackets);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[56..], totalWireBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], highestSequence);
        try { socket.SendTo(bytes, SocketFlags.None, source); }
        catch (SocketException error) when (!shutdown.IsCancellationRequested && VideoDatagram.Recoverable(error, "Feedback send"))
        {
            // The next feedback carries cumulative counters, so losing this batch is recoverable.
        }
        batchPackets = batchWireBytes = 0;
        feedbackTick = VideoDatagram.Now;
    }

    void ExpireFrames()
    {
        var now = VideoDatagram.Now;
        foreach (var key in frames.Where(x => VideoDatagram.Seconds(now - x.Value.Created) > 2).Select(x => x.Key).ToArray())
        {
            bufferedBytes -= frames[key].Data.Length;
            frames.Remove(key);
        }
    }

    void Dispatch()
    {
        try
        {
            while (completed.Reader.WaitToReadAsync(shutdown.Token).AsTask().GetAwaiter().GetResult())
                while (completed.Reader.TryRead(out var video))
                {
                    Interlocked.Add(ref queuedBytes, -video.Data.Length);
                    try { VideoReceived?.Invoke(video); }
                    catch (Exception error) { VideoDatagram.Log("Video callback failed", error); }
                }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { VideoDatagram.Log("Video dispatch stopped."); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        socket.Dispose();
        receiveThread.Join(2000);
        completed.Writer.TryComplete();
        if (Thread.CurrentThread != dispatchThread && !dispatchThread.Join(2000))
            VideoDatagram.Log("Video callback is still finishing after receiver shutdown.");
        shutdown.Dispose();
    }
}
