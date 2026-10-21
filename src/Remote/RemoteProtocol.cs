using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

public sealed record RemoteOptions(string Host, int Port, string Token);
sealed record RemoteRequest(string Kind, string Session = "", ControlCommand? Command = null, int InputProtocol = 0, string ClientNonce = "", string Proof = "");
sealed record RemoteWelcome(string Session, int Width, int Height, int SourceWidth, int SourceHeight,
    int FramesPerSecond, string InitialPreset, int InitialBitrateKbps, Dictionary<string, CodecPreset> Presets, double TransmissionScale = 1,
    double MaximumBitrateMbps = 100, CodecProbeSample[]? ProbeSamples = null);
sealed record RemoteReply(bool Success, string Message = "", RemoteWelcome? Welcome = null, ControlResult? Control = null,
    NetworkSnapshot? Network = null, string Preset = "", int LimitKbps = 0, bool Diagnostics = false,
    long ReceiveTick = 0, long SendTick = 0, long Frequency = 0,
    int Generation = 0, int Width = 0, int Height = 0, string InputSession = "", string ServerProof = "", bool HostBusy = false);

static class RemoteWire
{
    public static async Task WriteAsync<T>(NetworkStream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, FrdJson.Plain);
        if (bytes.Length > (typeof(T) == typeof(RemoteReply) ? 2 * 1024 * 1024 : 65536))
            throw new InvalidDataException("Remote control message exceeds its size limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }

    public static async Task<T> ReadAsync<T>(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > (typeof(T) == typeof(RemoteReply) ? 2 * 1024 * 1024 : 65536))
            throw new InvalidDataException("Invalid remote control message length.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, FrdJson.Plain) ?? throw new InvalidDataException("Empty remote message.");
    }
}

sealed class RemoteConnection : IDisposable
{
    readonly TcpClient client;
    readonly SemaphoreSlim gate = new(1, 1);
    public ClockEstimate Clock { get; } = new();
    public IPEndPoint Endpoint => (IPEndPoint)client.Client.RemoteEndPoint!;
    /// <summary>Key shared with the host after the authenticated first exchange; signs the UDP input datagrams.</summary>
    public byte[] SessionKey { get; private set; } = [];
    RemoteConnection(TcpClient client) => this.client = client;

    public static async Task<RemoteConnection> ConnectAsync(RemoteOptions options, CancellationToken token)
    {
        return new(await FrdNetwork.ConnectAsync(options.Host, options.Port, token));
    }

    /// <summary>The first exchange of a control connection: answers the host's challenge (the token itself is never sent) and checks the host's proof.</summary>
    public async Task<RemoteReply> HelloAsync(string token, CancellationToken cancellation, TimeSpan? timeoutDuration = null)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if (timeoutDuration is { } duration) timeout.CancelAfter(duration);
            var started = Stopwatch.GetTimestamp();
            var (reply, sessionKey) = await RemoteAuth.HandshakeAsync(client.GetStream(), token, new RemoteRequest("hello"), timeout.Token);
            Clock.Observe(started, Stopwatch.GetTimestamp(), reply.ReceiveTick, reply.SendTick, reply.Frequency);
            if (!reply.Success) throw new IOException(reply.Message);
            SessionKey = sessionKey;
            return reply;
        }
        catch { client.Dispose(); throw; }
        finally { gate.Release(); }
    }

    public async Task<RemoteReply> ExchangeAsync(RemoteRequest request, CancellationToken token, TimeSpan? timeoutDuration = null)
    {
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (timeoutDuration is { } duration) timeout.CancelAfter(duration);
            var started = Stopwatch.GetTimestamp();
            await RemoteWire.WriteAsync(client.GetStream(), request, timeout.Token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(client.GetStream(), timeout.Token);
            Clock.Observe(started, Stopwatch.GetTimestamp(), reply.ReceiveTick, reply.SendTick, reply.Frequency);
            if (!reply.Success) throw new IOException(reply.Message);
            return reply;
        }
        catch { client.Dispose(); throw; }
        finally { gate.Release(); }
    }
    public void Dispose() => client.Dispose();
}

public sealed class ClockEstimate
{
    readonly object gate = new();
    readonly Queue<(long Local, double Offset, double Error)> samples = new();
    double offsetSeconds, uncertaintyMs = double.PositiveInfinity;
    public double UncertaintyMs { get { lock (gate) return uncertaintyMs; } }
    public void Observe(long t1, long t4, long t2, long t3, long frequency)
    {
        if (frequency <= 0 || t3 < t2 || t4 < t1) return;
        var localDuration = (t4 - t1) / (double)Stopwatch.Frequency;
        var remoteDuration = (t3 - t2) / (double)frequency;
        var uncertainty = (localDuration - remoteDuration) * 500;
        if (uncertainty < 0 || uncertainty > 2000) return;
        var offset = (t1 / (double)Stopwatch.Frequency - t2 / (double)frequency +
            t4 / (double)Stopwatch.Frequency - t3 / (double)frequency) / 2;
        lock (gate)
        {
            samples.Enqueue((t4, offset, uncertainty));
            while (samples.TryPeek(out var sample) && (t4 - sample.Local > Stopwatch.Frequency * 10 || samples.Count > 64)) samples.Dequeue();
            var best = samples.MinBy(sample => sample.Error);
            offsetSeconds = best.Offset; uncertaintyMs = best.Error;
        }
    }
    public long ToLocal(long tick, long frequency)
    {
        lock (gate) return checked((long)((tick / (double)frequency + offsetSeconds) * Stopwatch.Frequency));
    }
}
