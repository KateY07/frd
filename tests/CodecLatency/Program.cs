using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Frd;

namespace Frd.CodecLatency;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length is 4 or 5 && args[0] == "--low-color")
            {
                FfmpegRuntime.Initialize(args[1]);
                return LowColorProbe.Run(args[2], args[3], args.Length == 5 ? args[4] : "quick");
            }
            if (args.Length == 3 && args[0] == "--conversion")
            {
                FfmpegRuntime.Initialize(args[1]);
                var conversionReport = new { Computer = Environment.MachineName, Results = new[] { ConversionProbe.Run(1280, 720), ConversionProbe.Run(1920, 1080) } };
                File.WriteAllText(args[2], JsonSerializer.Serialize(conversionReport, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(conversionReport));
                return 0;
            }
            if (args.Length != 4) throw new ArgumentException("Usage: CodecLatency <native-directory> <configuration> <preset> <report>");
            var configuration = JsonDocument.Parse(File.ReadAllText(args[1]));
            var preset = configuration.RootElement.GetProperty("presets").GetProperty(args[2]);
            var encoderArguments = preset.GetProperty("encoderArguments").GetString()!;
            var decoderArguments = preset.GetProperty("decoderArguments").GetString()!;
            FfmpegRuntime.Initialize(args[0]);
            List<object> results = new();
            foreach (var dimensions in new[] { (Width: 1280, Height: 720), (Width: 1920, Height: 1080) })
                results.Add(Run(encoderArguments, decoderArguments, dimensions.Width, dimensions.Height));
            var report = new
            {
                Computer = Environment.MachineName, Runtime = Environment.Version.ToString(),
                Ffmpeg = FfmpegRuntime.Version, Preset = args[2], EncoderArguments = encoderArguments,
                DecoderArguments = decoderArguments, FramesPerSecond = 30, BitrateKbps = 5000,
                WarmupFrames = 10, MeasuredFrames = 60,
                Scope = "Synthetic moving pixels; production FfmpegBackend; encode includes CPU conversion/upload/wait/copy; decode includes download/BGRA conversion. No capture, network or presentation; not pure GPU timing.",
                SourceSha256 = File.Exists(Path.Combine(AppContext.BaseDirectory, "FfmpegBackend.cs"))
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FfmpegBackend.cs")))) : null,
                Results = results
            };
            File.WriteAllText(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static object Run(string encoderArguments, string decoderArguments, int width, int height)
    {
        // Generate before timing; reuse buffers so the benchmark does not measure its own pixel allocation.
        var images = Enumerable.Range(0, 8).Select(index => CreateImage(width, height, index)).ToArray();
        var started = Stopwatch.GetTimestamp();
        using var encoder = new FfmpegEncoder(encoderArguments, width, height, 30, 5000);
        using var decoder = new FfmpegDecoder(decoderArguments);
        var initializationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        List<double> encode = new(), decode = new(), frameLatency = new();
        Dictionary<long, long> submitted = new();
        long bytes = 0;
        var delivered = 0;
        var delayedPackets = 0;
        var idrCount = 0;
        var outputIds = new HashSet<long>();
        for (var index = 0; index < 70; index++)
        {
            var frameStarted = Stopwatch.GetTimestamp();
            submitted[index] = frameStarted;
            var packets = encoder.Encode(images[index % images.Length], index, index == 0);
            var encoded = Stopwatch.GetTimestamp();
            if (index >= 10) encode.Add(Stopwatch.GetElapsedTime(frameStarted, encoded).TotalMilliseconds);
            foreach (var packet in packets)
            {
                if (packet.Pts < 0 || packet.Pts > index) throw new InvalidDataException("Encoder returned invalid frame PTS.");
                var decodeStarted = Stopwatch.GetTimestamp();
                var decoded = decoder.Decode(packet.Data, packet.Pts);
                var decodeEnded = Stopwatch.GetTimestamp();
                if (packet.Pts >= 10)
                {
                    bytes += packet.Data.Length;
                    if (packet.KeyFrame) idrCount++;
                    if (packet.Pts != index) delayedPackets++;
                    decode.Add(Stopwatch.GetElapsedTime(decodeStarted, decodeEnded).TotalMilliseconds);
                }
                foreach (var frame in decoded)
                {
                    if (frame.Width != width || frame.Height != height || frame.Bgra.Length != width * height * 4)
                        throw new InvalidDataException("Decoded frame dimensions are incorrect.");
                    if (!outputIds.Add(frame.Pts) || !submitted.TryGetValue(frame.Pts, out var input))
                        throw new InvalidDataException("Decoded PTS is duplicated or does not match an input.");
                    if (frame.Pts >= 10)
                    {
                        delivered++;
                        frameLatency.Add(Stopwatch.GetElapsedTime(input, decodeEnded).TotalMilliseconds);
                    }
                }
            }
            var remaining = 1000d / 30 - Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
            if (remaining > 1) Thread.Sleep((int)remaining);
        }
        return new
        {
            Width = width, Height = height, InitializationMs = initializationMs,
            EncodeCallMs = Stats(encode), DecodeCallMs = Stats(decode), SubmitToDecodedMs = Stats(frameLatency),
            DecodedMeasuredFrames = delivered, ImmediateOutputComplete = delivered == 60,
            DelayedPackets = delayedPackets, MeasuredKeyframes = idrCount, PayloadBytes = bytes,
            NominalPayloadMbps = bytes * 8d / 2 / 1_000_000,
            PendingInputs = submitted.Count - outputIds.Count
        };
    }

    static object Stats(List<double> samples)
    {
        if (samples.Count == 0) throw new InvalidDataException("No timing samples were produced.");
        var ordered = samples.Order().ToArray();
        return new { Count = ordered.Length, Mean = samples.Average(),
            Median = ordered[ordered.Length / 2], P95 = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], Max = ordered[^1] };
    }

    static byte[] CreateImage(int width, int height, int phase)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var line = (y + phase * 3) % 36 < 3 || (x + phase * 5) % 96 < 3;
            var shade = line ? 35 : 220;
            pixels[offset] = (byte)shade;
            pixels[offset + 1] = (byte)(line ? 55 : 225);
            pixels[offset + 2] = (byte)(line ? 85 : 230);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }
}
