using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Frd;

static class SecureDesktopUdp
{
    public const int Port = 59873, ChunkSize = 60000, HeaderSize = 36;
    const uint Magic = 0x46524455;
    public static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FRD", "SecureDesktopProbe", "capture.key");

    public static byte[] ReadKey()
    {
        var key = File.ReadAllBytes(KeyPath);
        if (key.Length != 32) throw new InvalidDataException("Secure capture UDP key must contain 32 bytes.");
        return key;
    }

    public static (int Packets, int CompressedBytes) Send(Socket socket, byte[] key, uint frameId, int width, int height, byte[]? pixels)
    {
        byte[] payload;
        if (pixels == null) payload = [];
        else
        {
            using var buffer = new MemoryStream();
            using (var encoder = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) encoder.Write(pixels);
            payload = buffer.ToArray();
        }
        var count = Math.Max(1, (payload.Length + ChunkSize - 1) / ChunkSize);
        if (count > ushort.MaxValue) throw new InvalidDataException("Secure capture frame has too many UDP packets.");
        var endpoint = new IPEndPoint(IPAddress.Loopback, Port);
        for (var pass = 0; pass < 2; pass++)
        for (var index = 0; index < count; index++)
        {
            var offset = index * ChunkSize;
            var size = Math.Min(ChunkSize, payload.Length - offset);
            var packet = new byte[HeaderSize + size];
            BitConverter.GetBytes(Magic).CopyTo(packet, 0);
            BitConverter.GetBytes(frameId).CopyTo(packet, 4);
            BitConverter.GetBytes((ushort)index).CopyTo(packet, 8);
            BitConverter.GetBytes((ushort)count).CopyTo(packet, 10);
            BitConverter.GetBytes(width).CopyTo(packet, 12);
            BitConverter.GetBytes(height).CopyTo(packet, 16);
            BitConverter.GetBytes(payload.Length).CopyTo(packet, 20);
            if (size > 0) payload.AsSpan(offset, size).CopyTo(packet.AsSpan(HeaderSize));
            Sign(key, packet).CopyTo(packet.AsSpan(24, 12));
            socket.SendTo(packet, endpoint);
            var next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000;
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(64);
        }
        return (count, payload.Length);
    }

    public static bool Verify(byte[] key, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize || BitConverter.ToUInt32(packet) != Magic) return false;
        return CryptographicOperations.FixedTimeEquals(Sign(key, packet), packet.Slice(24, 12));
    }

    static byte[] Sign(byte[] key, ReadOnlySpan<byte> packet)
    {
        var signed = new byte[24 + packet.Length - HeaderSize];
        packet[..24].CopyTo(signed);
        packet[HeaderSize..].CopyTo(signed.AsSpan(24));
        return HMACSHA256.HashData(key, signed)[..12];
    }
}

sealed class SecureDesktopFrames : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Socket socket;
    readonly Task reader;
    readonly object gate = new();
    readonly byte[] key;
    readonly SecureDesktopShared? shared;
    byte[]? latest;
    int latestWidth, latestHeight;
    long version, consumed;
    bool active, disposed;

    public static SecureDesktopFrames? TryStart()
    {
        if (!File.Exists(SecureDesktopUdp.KeyPath))
        {
            Console.Error.WriteLine("[secure capture] UDP helper key is absent; secure desktop capture is unavailable.");
            return null;
        }
        return new(ReadKeyForHost());
    }

    static byte[] ReadKeyForHost() => SecureDesktopUdp.ReadKey();

    internal SecureDesktopFrames(byte[] key, bool useShared = true)
    {
        this.key = key;
        if (useShared)
        {
            try { shared = SecureDesktopShared.TryOpen(writer: false); }
            catch (Exception error) { Console.Error.WriteLine("[secure capture] Shared frame buffer unavailable; using loopback UDP: " + error); }
        }
        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ExclusiveAddressUse = true, ReceiveBufferSize = 8 * 1024 * 1024 };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, SecureDesktopUdp.Port));
        reader = Task.Run(ReadAsync);
    }

    public bool Active { get { lock (gate) return shared?.Active == true || active; } }

    public (bool Active, SharedFrame? Frame, int Width, int Height) TakeSharedMapped(bool repeat = false) =>
        shared?.Take(repeat) ?? (false, null, 0, 0);

    public (bool Active, byte[]? Pixels, int Width, int Height) Take(bool repeat = false)
    {
        if (shared?.Take(repeat) is { Active: true } sharedFrame)
        {
            if (sharedFrame.Frame == null) return (true, null, sharedFrame.Width, sharedFrame.Height);
            using (sharedFrame.Frame)
            {
                var pixels = new byte[checked(sharedFrame.Width * sharedFrame.Height * 4)];
                System.Runtime.InteropServices.Marshal.Copy(sharedFrame.Frame.Pixels, pixels, 0, pixels.Length);
                return (true, pixels, sharedFrame.Width, sharedFrame.Height);
            }
        }
        lock (gate)
        {
            if (!active || latest == null) return (false, null, 0, 0);
            if (!repeat && consumed == version) return (true, null, latestWidth, latestHeight);
            consumed = version;
            return (true, latest, latestWidth, latestHeight);
        }
    }

    async Task ReadAsync()
    {
        var packet = new byte[SecureDesktopUdp.HeaderSize + SecureDesktopUdp.ChunkSize];
        uint frameId = 0;
        uint lastCompletedId = 0;
        long lastCompletedTick = 0;
        byte[]? compressed = null;
        bool[]? received = null;
        int receivedCount = 0, frameWidth = 0, frameHeight = 0;
        long frameStarted = 0;
        bool invalidAuthenticationLogged = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var datagram = await socket.ReceiveFromAsync(packet, SocketFlags.None,
                    new IPEndPoint(IPAddress.Loopback, 0), stop.Token);
                if (!((IPEndPoint)datagram.RemoteEndPoint).Address.Equals(IPAddress.Loopback)) continue;
                if (!SecureDesktopUdp.Verify(key, packet.AsSpan(0, datagram.ReceivedBytes)))
                {
                    if (!invalidAuthenticationLogged)
                        Console.Error.WriteLine("[secure capture] Rejected an unauthenticated local UDP packet.");
                    invalidAuthenticationLogged = true;
                    continue;
                }
                var span = packet.AsSpan(0, datagram.ReceivedBytes);
                var id = BitConverter.ToUInt32(span[4..]);
                var index = BitConverter.ToUInt16(span[8..]);
                var count = BitConverter.ToUInt16(span[10..]);
                var width = BitConverter.ToInt32(span[12..]);
                var height = BitConverter.ToInt32(span[16..]);
                var total = BitConverter.ToInt32(span[20..]);
                if (count == 0 || count > 2048 || index >= count || total < 0 || total > 32 * 1024 * 1024 ||
                    width < 0 || height < 0 || width > 8192 || height > 8192) continue;
                if (total == 0)
                {
                    var changed = false;
                    lock (gate)
                    {
                        changed = active;
                        active = false; latest = null; latestWidth = latestHeight = 0; version++;
                    }
                    compressed = null;
                    if (changed) Console.Error.WriteLine("[secure capture] Returned to ordinary desktop.");
                    continue;
                }
                if (width < 64 || height < 64 || (long)width * height * 4 > 128 * 1024 * 1024) continue;
                if (id == lastCompletedId && Stopwatch.GetElapsedTime(lastCompletedTick) < TimeSpan.FromSeconds(2)) continue;
                if (compressed == null || id != frameId || Stopwatch.GetElapsedTime(frameStarted) > TimeSpan.FromSeconds(2))
                {
                    if (compressed != null && receivedCount > 0)
                        Console.Error.WriteLine($"[secure capture] Dropped incomplete UDP frame {frameId}: {receivedCount}/{received!.Length} packets.");
                    frameId = id; frameWidth = width; frameHeight = height; frameStarted = Stopwatch.GetTimestamp();
                    compressed = new byte[total]; received = new bool[count]; receivedCount = 0;
                    Console.Error.WriteLine($"[secure capture] Receiving UDP frame {id}: {count} packets, {total} bytes.");
                }
                if (width != frameWidth || height != frameHeight || total != compressed.Length || received!.Length != count || received[index]) continue;
                var offset = index * SecureDesktopUdp.ChunkSize;
                var size = span.Length - SecureDesktopUdp.HeaderSize;
                if (offset + size > total || size != Math.Min(SecureDesktopUdp.ChunkSize, total - offset))
                    continue;
                span[SecureDesktopUdp.HeaderSize..].CopyTo(compressed.AsSpan(offset));
                received[index] = true;
                if (++receivedCount != count) continue;
                try
                {
                    var pixels = new byte[checked(width * height * 4)];
                    using var input = new MemoryStream(compressed, writable: false);
                    using var decoder = new ZLibStream(input, CompressionMode.Decompress);
                    var decodeStarted = Stopwatch.GetTimestamp();
                    decoder.ReadExactly(pixels);
                    if (decoder.ReadByte() != -1) throw new InvalidDataException("Secure capture frame has excess decoded bytes.");
                    lock (gate) { latest = pixels; latestWidth = width; latestHeight = height; active = true; version++; }
                    lastCompletedId = id; lastCompletedTick = Stopwatch.GetTimestamp();
                    Console.Error.WriteLine($"[secure capture] Authenticated UDP frame {width}x{height}, {count} packets; receive+decode={Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds:F1}ms, decode={Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds:F1}ms.");
                }
                catch (Exception error) { Console.Error.WriteLine("[secure capture] Invalid UDP frame: " + error); }
                compressed = null;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[secure capture] UDP receiver stopped."); }
        catch (Exception error) { Console.Error.WriteLine("[secure capture] UDP receiver failed: " + error); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); socket.Dispose();
        try { reader.GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine("[secure capture] UDP shutdown error: " + error); }
        stop.Dispose();
        shared?.Dispose();
    }
}
