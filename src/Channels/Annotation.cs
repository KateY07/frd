using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

/// <summary>
/// One step of a shared annotation, controller to host. Coordinates are fractions of the remote picture (0..1, x then y in
/// <see cref="Points"/>); <see cref="Width"/> is the line width or font size as a fraction of the picture width, so marks land on the
/// same spot at any size. <see cref="Kind"/> is "pen", "rect" or "text"; "add" extends a pen stroke or moves a rectangle's far corner.
/// </summary>
public sealed record AnnotationMessage(string Op, uint Color = 0, float Width = 0, float[]? Points = null, string? Kind = null, string? Text = null);

public static class AnnotationWire
{
    public const int MaximumMessageBytes = 65536, MaximumPointsPerMessage = 4000, MaximumTextLength = 200;

    public static async Task WriteAsync(NetworkStream stream, AnnotationMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, FrdJson.Plain);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Annotation message too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }

    public static async Task<AnnotationMessage> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid annotation message length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        var message = JsonSerializer.Deserialize<AnnotationMessage>(bytes, FrdJson.Plain) ?? throw new InvalidDataException("Empty annotation message.");
        Validate(message);
        return message;
    }

    public static void Validate(AnnotationMessage message)
    {
        if (message.Op is not ("begin" or "add" or "end" or "undo" or "clear" or "text")) throw new InvalidDataException("Unknown annotation operation.");
        if (message.Kind is not (null or "pen" or "rect" or "text")) throw new InvalidDataException("Unknown annotation kind.");
        if (!float.IsFinite(message.Width) || message.Width < 0 || message.Width > .2f) throw new InvalidDataException("Invalid annotation size.");
        if (message.Text is { Length: > MaximumTextLength }) throw new InvalidDataException("Annotation text too long.");
        var points = message.Points;
        if (points != null && (points.Length % 2 != 0 || points.Length > MaximumPointsPerMessage * 2 || points.Any(value => !float.IsFinite(value) || value < -1 || value > 2)))
            throw new InvalidDataException("Invalid annotation points.");
    }

    /// <summary>
    /// Host side: reads messages until the channel closes. Annotation is the lowest-priority channel, so a controller that floods it
    /// (more than <see cref="MaximumPerSecond"/> messages a second) only loses drawing detail: "add" steps beyond the budget are dropped,
    /// while begin/end/undo/clear/text always pass.
    /// </summary>
    public static async Task ServeAsync(TcpClient client, Action<AnnotationMessage> received, CancellationToken token)
    {
        const int MaximumPerSecond = 120;
        var stream = client.GetStream();
        long windowStart = Stopwatch.GetTimestamp(); var count = 0;
        while (!token.IsCancellationRequested)
        {
            var message = await ReadAsync(stream, token);
            var now = Stopwatch.GetTimestamp();
            if (now - windowStart >= Stopwatch.Frequency) { windowStart = now; count = 0; }
            if (++count > MaximumPerSecond && message.Op == "add") continue;
            received(message);
        }
    }
}

/// <summary>
/// Controller side of the annotation channel. Strictly background work: one below-normal-priority thread that wakes thirty times a
/// second, merges point batches and writes them; a full queue drops the oldest steps, so drawing can never delay video or input.
/// </summary>
public sealed class AnnotationClient : IAsyncDisposable
{
    readonly TcpClient client;
    readonly CancellationTokenSource stop;
    readonly Channel<AnnotationMessage> queue = Channel.CreateBounded<AnnotationMessage>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly Thread writer;
    readonly object gate = new();
    List<float> pending = new();
    bool rectangle;
    int disposed;
    public event Action<Exception>? Failed;

    public AnnotationClient(TcpClient client, CancellationToken token)
    {
        this.client = client;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        writer = new Thread(WriteLoop) { IsBackground = true, Name = "FRD annotation sender", Priority = ThreadPriority.BelowNormal };
        writer.Start();
    }

    // Four decimals (about a quarter of a pixel at 2560 wide) keep the JSON small.
    static float Round(float value) => MathF.Round(value, 4);

    public void Begin(string kind, uint color, float width, float x, float y)
    {
        Flush();
        lock (gate) rectangle = kind == "rect";
        queue.Writer.TryWrite(new("begin", color, MathF.Round(width, 5), [Round(x), Round(y)], kind));
    }

    /// <summary>Points are collected and sent about thirty times a second; a rectangle only needs its latest corner.</summary>
    public void Add(float x, float y)
    {
        lock (gate)
        {
            if (rectangle) pending.Clear();
            if (pending.Count < AnnotationWire.MaximumPointsPerMessage * 2) { pending.Add(Round(x)); pending.Add(Round(y)); }
        }
    }

    public void Text(uint color, float fontSize, float x, float y, string text)
    {
        Flush();
        queue.Writer.TryWrite(new("text", color, MathF.Round(fontSize, 5), [Round(x), Round(y)], "text", text.Length > AnnotationWire.MaximumTextLength ? text[..AnnotationWire.MaximumTextLength] : text));
    }

    public void End() { Flush(); queue.Writer.TryWrite(new("end")); }
    public void Undo() { Flush(); queue.Writer.TryWrite(new("undo")); }
    public void Clear() { Flush(); queue.Writer.TryWrite(new("clear")); }

    void Flush()
    {
        float[]? points = null;
        lock (gate) { if (pending.Count > 0) { points = pending.ToArray(); pending = new(); } }
        if (points != null) queue.Writer.TryWrite(new("add", Points: points));
    }

    void WriteLoop()
    {
        try
        {
            var stream = client.GetStream();
            var reader = queue.Reader;
            while (!stop.IsCancellationRequested)
            {
                Thread.Sleep(33);
                Flush();
                while (reader.TryRead(out var message))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, FrdJson.Plain);
                    var frame = new byte[4 + bytes.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
                    bytes.CopyTo(frame, 4);
                    stream.Write(frame, 0, frame.Length);      // one write per message, so it never interleaves
                }
            }
        }
        catch (Exception error) { if (!stop.IsCancellationRequested) Failed?.Invoke(error); }
        finally { try { client.Dispose(); } catch (Exception) { } }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        stop.Cancel();
        try { client.Dispose(); } catch (Exception) { }     // unblocks a pending write
        writer.Join(1000);
        stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
