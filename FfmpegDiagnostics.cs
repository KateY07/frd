using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Frd;

public sealed record FrameDiagnostic(int Generation, long FrameId, long CaptureStarted, long CaptureCompleted, long EncodeCompleted, long Frequency)
{
    public VideoSendTiming? Sending { get; init; }
}

public static class FrameDiagnosticProtocol
{
    public const int PacketBytes = 48;
    public const int ExtendedPacketBytes = 88;
    const uint Magic = 0x44445246;

    public static byte[] Encode(FrameDiagnostic frame)
    {
        if (!Valid(frame)) throw new ArgumentException("Frame diagnostics require monotonic capture/encode ticks and a positive clock frequency.", nameof(frame));
        var bytes = new byte[frame.Sending == null ? PacketBytes : ExtendedPacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), frame.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), frame.FrameId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), frame.CaptureStarted);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), frame.CaptureCompleted);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), frame.EncodeCompleted);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), frame.Frequency);
        if (frame.Sending is { } sent)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), sent.PayloadBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), sent.Packets);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), sent.FirstSendTick);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), sent.LastSendTick);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(72), sent.PlannedWaitMs);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(80), sent.WakeupOverrunMs);
        }
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out FrameDiagnostic? frame)
    {
        frame = null;
        if (bytes.Length is not (PacketBytes or ExtendedPacketBytes) || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) return false;
        var decoded = new FrameDiagnostic(BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]));
        if (bytes.Length == ExtendedPacketBytes)
            decoded = decoded with { Sending = new(BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[64..]),
                BinaryPrimitives.ReadDoubleLittleEndian(bytes[72..]), BinaryPrimitives.ReadDoubleLittleEndian(bytes[80..])) };
        if (!Valid(decoded)) return false;
        frame = decoded;
        return true;
    }

    static bool Valid(FrameDiagnostic frame) => frame.Generation >= 0 && frame.FrameId >= 0 && frame.CaptureStarted >= 0 &&
        frame.CaptureCompleted >= frame.CaptureStarted && frame.EncodeCompleted >= frame.CaptureCompleted && frame.Frequency > 0 &&
        (frame.Sending is not { } sending || sending.PayloadBytes is > 0 and <= VideoDatagram.MaxFrameSize && sending.Packets > 0 &&
            sending.FirstSendTick >= frame.EncodeCompleted && sending.LastSendTick >= sending.FirstSendTick &&
            double.IsFinite(sending.PlannedWaitMs) && sending.PlannedWaitMs >= 0 && double.IsFinite(sending.WakeupOverrunMs) && sending.WakeupOverrunMs >= 0);
}

public sealed class UdpFrameDiagnosticsReceiver : IDisposable
{
    readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource stop = new();
    readonly Thread worker;
    long receivedPackets;
    int disposed;
    IPEndPoint? expectedSource;
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
    public long ReceivedPackets => Interlocked.Read(ref receivedPackets);
    public event Action<FrameDiagnostic>? Received;

    public UdpFrameDiagnosticsReceiver(int port = 0, bool remote = false)
    {
        socket.ReceiveBufferSize = 256 * 1024;
        socket.Bind(new IPEndPoint(remote ? IPAddress.Any : IPAddress.Loopback, port));
        worker = new(Read) { IsBackground = true, Name = "FRD separate frame diagnostics" };
        worker.Start();
    }

    public void SetExpectedSource(IPEndPoint endpoint) => Volatile.Write(ref expectedSource, endpoint);

    void Read()
    {
        var bytes = new byte[65536];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int length;
                try
                {
                    if (!socket.Poll(100_000, SelectMode.SelectRead)) continue;
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    length = socket.ReceiveFrom(bytes, ref source);
                    if (Volatile.Read(ref expectedSource) is { } expected && !expected.Equals(source)) continue;
                }
                catch (SocketException error) when (!stop.IsCancellationRequested && VideoDatagram.Recoverable(error, "Diagnostic receive")) { continue; }
                if (!FrameDiagnosticProtocol.TryDecode(bytes.AsSpan(0, length), out var frame)) continue;
                Interlocked.Increment(ref receivedPackets);
                try { Received?.Invoke(frame!); }
                catch (Exception error) { VideoDatagram.Log("Diagnostic callback failed", error); }
            }
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { VideoDatagram.Log("Diagnostic receiver stopped."); }
        catch (SocketException error) when (stop.IsCancellationRequested) { VideoDatagram.Log("Diagnostic receiver stopped", error); }
        catch (Exception error) { VideoDatagram.Log("Diagnostic receiver failed", error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        socket.Dispose();
        if (worker != Thread.CurrentThread && !worker.Join(1500)) VideoDatagram.Log("Diagnostic callback is still finishing after shutdown.");
        else stop.Dispose();
    }
}
