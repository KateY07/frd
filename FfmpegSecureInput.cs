using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Frd;

static class SecureInputWire
{
    public const int Port = 59874, PacketSize = 80, AckSize = 48;
    const uint Magic = 0x4652494E, AckMagic = 0x46524941;

    public static void Write(Span<byte> packet, ReadOnlySpan<byte> key, ReadOnlySpan<byte> session, long sequence,
        RemoteInputEvent? input)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        session.CopyTo(packet[4..20]);
        BinaryPrimitives.WriteInt64LittleEndian(packet[20..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet[28..], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        packet[36] = input == null ? (byte)7 : (byte)input.Kind;
        if (input != null)
        {
            packet[37] = (byte)input.Button;
            packet[38] = input.Extended ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteDoubleLittleEndian(packet[40..], input.X);
            BinaryPrimitives.WriteDoubleLittleEndian(packet[48..], input.Y);
            BinaryPrimitives.WriteInt32LittleEndian(packet[56..], input.WheelDelta);
            BinaryPrimitives.WriteInt32LittleEndian(packet[60..], input.ScanCode);
        }
        Sign(key, packet[..64]).CopyTo(packet[64..]);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> key, out long sequence,
        out RemoteInputEvent? input)
    {
        sequence = 0; input = null;
        if (packet.Length != PacketSize || BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            !CryptographicOperations.FixedTimeEquals(Sign(key, packet[..64]), packet[64..])) return false;
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - BinaryPrimitives.ReadInt64LittleEndian(packet[28..]);
        if (age is < -2000 or > 2000) return false;
        sequence = BinaryPrimitives.ReadInt64LittleEndian(packet[20..]);
        if (sequence <= 0 || packet[36] > 7 || packet[37] > 2 || packet[38] > 1 || packet[39] != 0) return false;
        if (packet[36] == 7) return true;
        var kind = (RemoteInputKind)packet[36];
        var x = BinaryPrimitives.ReadDoubleLittleEndian(packet[40..]);
        var y = BinaryPrimitives.ReadDoubleLittleEndian(packet[48..]);
        var wheel = BinaryPrimitives.ReadInt32LittleEndian(packet[56..]);
        var scan = BinaryPrimitives.ReadInt32LittleEndian(packet[60..]);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 ||
            wheel is < -12000 or > 12000 || scan is < 0 or > ushort.MaxValue) return false;
        input = new(kind, x, y, (RemoteMouseButton)packet[37], wheel, scan, packet[38] != 0);
        return true;
    }

    public static void WriteAck(Span<byte> ack, ReadOnlySpan<byte> key, ReadOnlySpan<byte> session, long sequence, bool accepted)
    {
        ack.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(ack, AckMagic);
        session.CopyTo(ack[4..20]);
        BinaryPrimitives.WriteInt64LittleEndian(ack[20..], sequence);
        ack[28] = accepted ? (byte)1 : (byte)0;
        Sign(key, ack[..32]).CopyTo(ack[32..]);
    }

    public static bool TryReadAck(ReadOnlySpan<byte> ack, ReadOnlySpan<byte> key, ReadOnlySpan<byte> session,
        long sequence, out bool accepted)
    {
        accepted = false;
        if (ack.Length != AckSize || BinaryPrimitives.ReadUInt32LittleEndian(ack) != AckMagic ||
            !CryptographicOperations.FixedTimeEquals(ack[4..20], session) ||
            BinaryPrimitives.ReadInt64LittleEndian(ack[20..]) != sequence ||
            !CryptographicOperations.FixedTimeEquals(Sign(key, ack[..32]), ack[32..])) return false;
        accepted = ack[28] == 1;
        return true;
    }

    static byte[] Sign(ReadOnlySpan<byte> key, ReadOnlySpan<byte> bytes) => HMACSHA256.HashData(key, bytes)[..16];
}

sealed class SecureDesktopInputClient : IDisposable
{
    readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly IPEndPoint target;
    readonly byte[] key;
    readonly byte[] session = RandomNumberGenerator.GetBytes(16);
    readonly object gate = new();
    long sequence;

    public SecureDesktopInputClient(byte[] key, int port = SecureInputWire.Port)
    {
        this.key = key;
        target = new(IPAddress.Loopback, port);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.ReceiveTimeout = 60;
    }

    public RemoteInputResult Send(RemoteInputEvent? input)
    {
        lock (gate)
        {
            var id = ++sequence;
            Span<byte> packet = stackalloc byte[SecureInputWire.PacketSize];
            SecureInputWire.Write(packet, key, session, id, input);
            var acknowledge = input != null && input.Kind != RemoteInputKind.MouseMove;
            for (var attempt = 0; attempt < (acknowledge ? 3 : 1); attempt++)
            {
                try
                {
                    socket.SendTo(packet, target);
                    if (!acknowledge) return new(true, "Secure desktop input sent.");
                    var ack = new byte[SecureInputWire.AckSize];
                    EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
                    var count = socket.ReceiveFrom(ack, ref peer);
                    if (!((IPEndPoint)peer).Address.Equals(IPAddress.Loopback) ||
                        !SecureInputWire.TryReadAck(ack.AsSpan(0, count), key, session, id, out var accepted)) continue;
                    return accepted ? new(true, "Secure desktop input applied.") : new(false, "Secure desktop input rejected by SYSTEM worker.");
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut && acknowledge)
                { Console.Error.WriteLine($"[secure input] ACK timeout, attempt {attempt + 1}."); }
                catch (Exception error)
                { Console.Error.WriteLine("[secure input] Local send failed: " + error); return new(false, error.Message); }
            }
            return new(false, "Secure desktop input worker did not acknowledge the event.");
        }
    }

    public void Dispose() => socket.Dispose();
}

sealed class SecureDesktopInputRouter : IRemoteInputInjector, IDisposable
{
    readonly Win32InputInjector ordinary;
    readonly SecureDesktopInputClient? secure;
    readonly Func<bool> secureActive;
    readonly Timer heartbeat;
    readonly object gate = new();
    bool enabled, previousSecure;

    public SecureDesktopInputRouter(Win32InputInjector ordinary, Func<bool> secureActive)
    {
        this.ordinary = ordinary;
        this.secureActive = secureActive;
        if (File.Exists(SecureDesktopUdp.KeyPath)) secure = new(SecureDesktopUdp.ReadKey());
        else Console.Error.WriteLine("[secure input] SYSTEM helper key absent; ordinary desktop input remains available.");
        heartbeat = new(_ => Heartbeat(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            enabled = value;
            heartbeat.Change(value ? 500 : Timeout.Infinite, value ? 500 : Timeout.Infinite);
            if (!value) ReleaseAllCore();
        }
    }

    public RemoteInputResult Inject(RemoteInputEvent input)
    {
        lock (gate)
        {
            var active = secureActive();
            if (active != previousSecure)
            {
                var released = active ? ordinary.ReleaseAll() : secure?.Send(new(RemoteInputKind.ReleaseAll)) ?? new(true, "No secure worker.");
                if (!released.Accepted) Console.Error.WriteLine("[secure input] Desktop transition release: " + released.Message);
                previousSecure = active;
            }
            return active ? secure?.Send(input) ?? new(false, "Secure desktop input worker is unavailable.") : ordinary.Inject(input);
        }
    }

    public RemoteInputResult ReleaseAll() { lock (gate) return ReleaseAllCore(); }

    RemoteInputResult ReleaseAllCore()
    {
        var ordinaryResult = ordinary.ReleaseAll();
        var secureResult = secure?.Send(new(RemoteInputKind.ReleaseAll)) ?? new(true, "No secure worker.");
        return !ordinaryResult.Accepted ? ordinaryResult : secureResult;
    }

    void Heartbeat()
    {
        lock (gate)
        {
            if (!enabled || !secureActive()) return;
            var result = secure?.Send(null) ?? new(false, "Secure desktop input worker is unavailable.");
            if (!result.Accepted) Console.Error.WriteLine("[secure input] Heartbeat failed: " + result.Message);
        }
    }

    public void Dispose()
    {
        heartbeat.Dispose();
        var result = ReleaseAll();
        if (!result.Accepted) Console.Error.WriteLine("[secure input] Release failed: " + result.Message);
        secure?.Dispose(); ordinary.Dispose();
    }
}

static class SecureDesktopInputWorker
{
    public static void Run(byte[] key, IRemoteInputInjector injector, Func<bool> active,
        Action<string> log, CancellationToken token, int port = SecureInputWire.Port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ExclusiveAddressUse = true, ReceiveTimeout = 250 };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        log($"Input listener ready on 127.0.0.1:{port}.");
        var packet = new byte[SecureInputWire.PacketSize];
        var ack = new byte[SecureInputWire.AckSize];
        var session = new byte[16];
        long lastSequence = 0, lastActivity = Stopwatch.GetTimestamp();
        RemoteInputResult lastResult = new(true, "No input.");
        var pendingRelease = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
                var count = socket.ReceiveFrom(packet, ref peer);
                if (!((IPEndPoint)peer).Address.Equals(IPAddress.Loopback) ||
                    !SecureInputWire.TryRead(packet.AsSpan(0, count), key, out var sequence, out var input)) continue;
                var desktopActive = active();
                if (!CryptographicOperations.FixedTimeEquals(packet.AsSpan(4, 16), session))
                {
                    if (desktopActive)
                    {
                        var released = injector.ReleaseAll();
                        pendingRelease = !released.Accepted;
                        if (!released.Accepted) log("Prior session release failed: " + released.Message);
                    }
                    else pendingRelease = true;
                    packet.AsSpan(4, 16).CopyTo(session);
                    lastSequence = 0;
                    log("Authenticated local input session changed.");
                }
                if (desktopActive && pendingRelease)
                {
                    var released = injector.ReleaseAll();
                    pendingRelease = !released.Accepted;
                    if (!released.Accepted) log("Pending input release failed: " + released.Message);
                }
                if (sequence < lastSequence) continue;
                if (sequence > lastSequence)
                {
                    lastSequence = sequence;
                    if (!desktopActive && input?.Kind == RemoteInputKind.ReleaseAll)
                    {
                        pendingRelease = true;
                        lastResult = new(true, "Release deferred until Winlogon is active.");
                    }
                    else if (!desktopActive) lastResult = new(false, "Winlogon desktop is not active.");
                    else if (input == null) lastResult = new(true, "Heartbeat.");
                    else lastResult = injector.Inject(input);
                    if (!lastResult.Accepted) log($"Input {input?.Kind} rejected: {lastResult.Message}");
                    else if (input != null && input.Kind != RemoteInputKind.MouseMove)
                        log($"Input {input.Kind} applied.");
                }
                lastActivity = Stopwatch.GetTimestamp();
                if (input == null || input.Kind == RemoteInputKind.MouseMove) continue;
                SecureInputWire.WriteAck(ack, key, session, sequence, lastResult.Accepted);
                socket.SendTo(ack, peer);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
            {
                if (Stopwatch.GetElapsedTime(lastActivity) <= TimeSpan.FromSeconds(2)) continue;
                try
                {
                    if (active())
                    {
                        var released = injector.ReleaseAll();
                        pendingRelease = !released.Accepted;
                        if (!released.Accepted) log("Input lease expired; release failed: " + released.Message);
                    }
                    else pendingRelease = true;
                }
                catch (Exception checkError) { log("Input lease desktop check failed: " + checkError); }
                lastActivity = Stopwatch.GetTimestamp();
            }
            catch (Exception error) { log("Input listener error: " + error); }
        }
        var finalRelease = injector.ReleaseAll();
        if (!finalRelease.Accepted) log("Final input release failed: " + finalRelease.Message);
    }
}
