using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using K4os.Compression.LZ4;

namespace Frd.CaptureEncodeBench;

sealed record Lz4Sample(int Index, bool Warmup, long AcquireTicks, long PresentTicks, double CaptureMs,
    double MapMs, double EncodeMs, double PacketCopyMs, double TotalMs, int Bytes, uint AccumulatedFrames,
    byte[] SourceSha256, byte[] Encoded);

sealed unsafe class StripeLz4 : IDisposable
{
    readonly int width, height, stripes;
    readonly byte[][] scratch;
    readonly int[] compressedLengths;

    public StripeLz4(int width, int height, int stripes)
    {
        this.width = width; this.height = height; this.stripes = stripes;
        scratch = new byte[stripes][]; compressedLengths = new int[stripes];
    }

    public byte[] Encode(nint pointer, int stride, out double compressionMs, out double packetCopyMs)
    {
        if (pointer == 0 || stride < width * 4) throw new ArgumentException("Mapped BGRA surface is invalid.");
        var rowsPerStripe = (height + stripes - 1) / stripes;
        var started = Stopwatch.GetTimestamp();
        Parallel.For(0, stripes, new ParallelOptions { MaxDegreeOfParallelism = stripes }, index =>
        {
            var startRow = index * rowsPerStripe;
            var rows = Math.Min(rowsPerStripe, height - startRow);
            if (rows <= 0) { compressedLengths[index] = 0; return; }
            var length = checked(rows * stride);
            var capacity = LZ4Codec.MaximumOutputSize(length);
            if (scratch[index] == null || scratch[index].Length < capacity) scratch[index] = new byte[capacity];
            fixed (byte* destination = scratch[index])
                compressedLengths[index] = LZ4Codec.Encode((byte*)pointer + startRow * stride, length,
                    destination, scratch[index].Length, LZ4Level.L00_FAST);
            if (compressedLengths[index] <= 0) throw new InvalidDataException($"LZ4 stripe {index} failed to encode.");
        });
        compressionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var headerSize = checked(16 + stripes * 8);
        var total = checked(headerSize + compressedLengths.Sum());
        var packet = new byte[total];
        var header = packet.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], stride);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], stripes);
        var offset = headerSize;
        for (var index = 0; index < stripes; index++)
        {
            var startRow = index * rowsPerStripe;
            var rows = Math.Min(rowsPerStripe, height - startRow);
            BinaryPrimitives.WriteInt32LittleEndian(header[(16 + index * 8)..], rows);
            BinaryPrimitives.WriteInt32LittleEndian(header[(20 + index * 8)..], compressedLengths[index]);
            scratch[index].AsSpan(0, compressedLengths[index]).CopyTo(header[offset..]);
            offset += compressedLengths[index];
        }
        packetCopyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return packet;
    }

    public static byte[] Decode(byte[] packet)
    {
        var input = packet.AsSpan();
        if (input.Length < 16) throw new InvalidDataException("LZ4 packet header is short.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(input);
        var height = BinaryPrimitives.ReadInt32LittleEndian(input[4..]);
        var stride = BinaryPrimitives.ReadInt32LittleEndian(input[8..]);
        var stripes = BinaryPrimitives.ReadInt32LittleEndian(input[12..]);
        if (width != 2880 || height != 1800 || stride < width * 4 || stride > 65536 || stripes != 8 || input.Length < 16 + stripes * 8)
            throw new InvalidDataException("Unexpected LZ4 packet dimensions or stripe count.");
        var decoded = new byte[checked(stride * height)];
        var offset = 16 + stripes * 8;
        var row = 0;
        for (var index = 0; index < stripes; index++)
        {
            var rows = BinaryPrimitives.ReadInt32LittleEndian(input[(16 + index * 8)..]);
            var bytes = BinaryPrimitives.ReadInt32LittleEndian(input[(20 + index * 8)..]);
            if (rows <= 0 || row + rows > height || bytes <= 0 || offset + bytes > input.Length)
                throw new InvalidDataException("Invalid LZ4 stripe boundaries.");
            var target = decoded.AsSpan(row * stride, rows * stride);
            var written = LZ4Codec.Decode(input.Slice(offset, bytes), target);
            if (written != target.Length) throw new InvalidDataException($"LZ4 stripe {index} decoded {written}, expected {target.Length}.");
            row += rows; offset += bytes;
        }
        if (row != height || offset != input.Length) throw new InvalidDataException("LZ4 packet has missing or trailing data.");
        return decoded;
    }

    public void Dispose() { }
}

static unsafe class Program
{
    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeEndPeriod(uint period);

    static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: CaptureLz4Bench.exe <new-output-directory> (real primary desktop, 30 FPS, 2880x1800)");
            return 1;
        }
        var output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Output directory must be new or empty.");
        Directory.CreateDirectory(output);
        if (timeBeginPeriod(1) != 0) throw new InvalidOperationException("Could not request 1 ms timer resolution.");
        try { return Run(output); }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        finally { if (timeEndPeriod(1) != 0) Console.Error.WriteLine("timeEndPeriod failed."); }
    }

    static int Run(string output)
    {
        using DesktopSource desktop = new(null);
        if (desktop.Width != 2880 || desktop.Height != 1800)
            throw new InvalidOperationException($"Expected 2880x1800, got {desktop.Width}x{desktop.Height}.");
        using StripeLz4 codec = new(desktop.Width, desktop.Height, 8);
        List<Lz4Sample> samples = new(70);
        var next = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency / 30;
        var elapsed = Stopwatch.StartNew();
        var missing = 0;
        while (samples.Count < 70 && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            var now = Stopwatch.GetTimestamp();
            if (now < next) { Thread.Sleep(1); continue; }
            var started = Stopwatch.GetTimestamp();
            if (!desktop.Acquire()) { missing++; Thread.Sleep(1); continue; }
            var acquired = Stopwatch.GetTimestamp();
            next += interval;
            if (next <= acquired) next = acquired + interval;
            try
            {
                var mapStarted = Stopwatch.GetTimestamp();
                var mapped = desktop.MapBgra();
                var mappedAt = Stopwatch.GetTimestamp();
                var encoded = codec.Encode(mapped.Pixels, mapped.Stride, out var compressionMs, out var packetCopyMs);
                var finished = Stopwatch.GetTimestamp();
                var hash = SHA256.HashData(new ReadOnlySpan<byte>((byte*)mapped.Pixels, checked(mapped.Stride * desktop.Height)));
                samples.Add(new(samples.Count, samples.Count < 10, acquired, desktop.LastPresentTime,
                    Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(mapStarted, mappedAt).TotalMilliseconds,
                    compressionMs, packetCopyMs, Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds,
                    encoded.Length, desktop.AccumulatedFrames, hash, encoded));
            }
            finally { desktop.Release(); }
        }
        var measured = samples.Where(sample => !sample.Warmup).ToArray();
        foreach (var sample in samples)
        {
            var reconstructed = StripeLz4.Decode(sample.Encoded);
            if (!SHA256.HashData(reconstructed).AsSpan().SequenceEqual(sample.SourceSha256))
                throw new InvalidDataException($"LZ4 frame {sample.Index} did not round-trip exactly.");
        }
        var summary = new
        {
            Schema = 1, Path = "DDA BGRA -> staging readback -> 8 independent LZ4 fast stripes -> complete copied packet",
            desktop.AdapterName, desktop.DisplayName, desktop.Width, desktop.Height,
            Quality = "Byte-exact lossless; original 32-bit BGRA pixels and row pitch retained.",
            Scenario = "Live desktop stimulus supplied by the caller; capture limited to 30 FPS.",
            Status = measured.Length == 60 ? "completed" : "insufficient-fresh-frames",
            WarmupFrames = 10, MeasuredFrames = measured.Length, MissingImageAttempts = missing,
            UniqueCapturedFrameHashes = measured.Select(sample => Convert.ToHexString(sample.SourceSha256)).Distinct().Count(),
            ConsecutiveChangedFrames = measured.Skip(1).Zip(measured, (current, previous) =>
                !current.SourceSha256.AsSpan().SequenceEqual(previous.SourceSha256)).Count(changed => changed),
            MeasuredCaptureFps = measured.Length > 1 ? (measured.Length - 1d) * Stopwatch.Frequency /
                (measured[^1].AcquireTicks - measured[0].AcquireTicks) : 0,
            CaptureToPacketMs = Stats(measured.Select(sample => sample.TotalMs)),
            MapMs = Stats(measured.Select(sample => sample.MapMs)),
            CompressionMs = Stats(measured.Select(sample => sample.EncodeMs)),
            PacketCopyMs = Stats(measured.Select(sample => sample.PacketCopyMs)),
            PayloadMbpsAt30Fps = measured.Length == 0 ? 0 : measured.Average(sample => sample.Bytes) * 30 * 8 / 1_000_000,
            Validation = $"{samples.Count}/{samples.Count} frames reconstructed byte-exact by independent LZ4 decoding and SHA-256 match",
            PerFrame = samples.Select(sample => new { sample.Index, sample.Warmup, sample.AcquireTicks, sample.PresentTicks,
                sample.CaptureMs, sample.MapMs, sample.EncodeMs, sample.PacketCopyMs, sample.TotalMs, sample.Bytes, sample.AccumulatedFrames }).ToArray()
        };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{summary.Status}: mean {measured.Average(sample => sample.TotalMs):F3} ms; {summary.PayloadMbpsAt30Fps:F2} Mbps; {summary.MeasuredCaptureFps:F2} FPS; report {output}");
        return measured.Length == 60 ? 0 : 2;
    }

    static object Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { Mean = sorted.Average(), P50 = sorted[(sorted.Length - 1) / 2],
            P95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], Max = sorted[^1] };
    }
}
