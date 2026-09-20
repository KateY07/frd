using System.Diagnostics;
using System.Text.Json;

namespace Frd;

static class GpuQuantizedEncodeRegression
{
    sealed record Timing(double MeanMs, double P95Ms, double MaxMs);
    sealed record Sample(double CaptureMs, double EncodeMs, double TotalMs, int PacketBytes);
    sealed record RouteResult(string Name, int Width, int Height, double CapturedBytesPerPixel, string Encoder,
        int ValidFrames, int DecodedFrames, Timing? Capture, Timing? Encode, Timing? CaptureToPacket,
        double PacketMbpsAt30Fps, int LargestPacketBytes, int SourcePatterns, int DecodedPatterns,
        int LastSourceDistinctByteValues, string? Error);

    public static int Run(string output, string? only = null)
    {
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        var width = bounds.Width & ~1;
        var height = bounds.Height & ~1;
        var halfWidth = width / 2 & ~1;
        var halfHeight = height / 2 & ~1;
        var routes = new (string Name, DesktopCapturePath Path, int Width, int Height, string Encoder, bool Mapped, bool? Gray)[]
        {
            ("native-mapped-x264rgb", DesktopCapturePath.NativeNoScale, width, height, "libx264rgb", true, null),
            ("half-bgra-x264", DesktopCapturePath.PixelShader, halfWidth, halfHeight, "libx264", false, null),
            ("half-rgb332-x264", DesktopCapturePath.PixelShaderRgb332, halfWidth, halfHeight, "libx264", false, false),
            ("half-gray8-x264", DesktopCapturePath.PixelShaderGray8, halfWidth, halfHeight, "libx264", false, true),
            ("native-rgb332-x264", DesktopCapturePath.PixelShaderRgb332, width, height, "libx264", false, false),
            ("native-rgb565-x264", DesktopCapturePath.PixelShaderRgb565, width, height, "libx264", false, null),
            ("native-gray8-x264", DesktopCapturePath.PixelShaderGray8, width, height, "libx264", false, true),
            ("native-gray4-x264", DesktopCapturePath.PixelShaderGray4, width, height, "libx264", false, true)
        };
        List<RouteResult> results = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var selected = only?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (selected != null && selected.Any(name => !routes.Any(route => route.Name == name)))
            throw new ArgumentException($"Unknown route in '{only}'.", nameof(only));
        foreach (var route in routes.Where(route => selected == null ? route.Name is not ("half-rgb332-x264" or "native-rgb332-x264" or "native-rgb565-x264") : selected.Contains(route.Name)))
        {
            try
            {
                var arguments = route.Mapped
                    ? "-c:v libx264rgb -pix_fmt bgr0 -preset ultrafast -tune zerolatency -bf 0 -g 300 -threads 16 -bufsize 100k"
                    : "-c:v libx264 -pix_fmt yuv420p -preset ultrafast -tune zerolatency -bf 0 -g 300 -threads 4 -bufsize 100k";
                using var source = new DxgiDesktopCapture(route.Width, route.Height, route.Path, reusePixelBuffer: !route.Mapped);
                using var encoder = new FfmpegEncoder(arguments, route.Width, route.Height, 30, 2000);
                List<Sample> samples = new();
                List<CodecPacket> packets = new();
                HashSet<ulong> sourcePatterns = new();
                HashSet<ulong> decodedPatterns = new();
                byte[]? lastSource = null;
                var valid = 0;
                var deadline = Stopwatch.StartNew();
                while (valid < 35 && deadline.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Thread.Sleep(31);
                    var start = Stopwatch.GetTimestamp();
                    IReadOnlyList<CodecPacket> encoded;
                    double captureMs;
                    if (route.Mapped)
                    {
                        using var pixels = source.CaptureMapped(false);
                        if (pixels == null) continue;
                        captureMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        encoded = encoder.EncodeMapped(pixels, valid);
                    }
                    else
                    {
                        var pixels = source.Capture();
                        if (source.Statistics?.NewDesktopImage != true) continue;
                        captureMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        encoded = route.Path == DesktopCapturePath.PixelShaderRgb565 ? encoder.EncodeRgb565(pixels, valid)
                            : route.Path == DesktopCapturePath.PixelShaderGray4 ? encoder.EncodeGray4(pixels, valid)
                            : route.Gray is bool gray ? encoder.EncodeQuantized(pixels, gray, valid) : encoder.Encode(pixels, valid);
                        lastSource = pixels;
                    }
                    var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (encoded.Count != 1 || encoded[0].Pts != valid)
                        throw new InvalidDataException($"Encoder returned {encoded.Count} packets for frame {valid}; immediate one-in/one-out encoding is required.");
                    if (lastSource != null) sourcePatterns.Add(Fingerprint(lastSource));
                    packets.Add(encoded[0]);
                    if (valid >= 5) samples.Add(new(captureMs, totalMs - captureMs, totalMs, encoded[0].Data.Length));
                    valid++;
                }
                if (samples.Count != 30) throw new TimeoutException($"Only {valid}/35 changing desktop frames in eight seconds.");
                using var decoder = new FfmpegDecoder("-c:v h264 -threads 1");
                var decoded = 0;
                foreach (var packet in packets)
                    foreach (var frame in decoder.Decode(packet.Data, packet.Pts))
                    {
                        if (frame.Width != route.Width || frame.Height != route.Height || frame.Bgra.Length != checked(route.Width * route.Height * 4))
                            throw new InvalidDataException("Decoded frame dimensions or pixel size are incorrect.");
                        decodedPatterns.Add(Fingerprint(frame.Bgra));
                        decoded++;
                    }
                decoded += decoder.Flush().Count;
                if (decoded != valid) throw new InvalidDataException($"Decoded {decoded}/{valid} frames.");
                if (decodedPatterns.Count < 2 || (lastSource != null && sourcePatterns.Count < 2))
                    throw new InvalidDataException($"Captured patterns={sourcePatterns.Count}, decoded patterns={decodedPatterns.Count}; full-motion source did not survive encoding.");
                var total = Measure(samples.Select(sample => sample.TotalMs));
                results.Add(new(route.Name, route.Width, route.Height, source.BytesPerFrame / (double)(route.Width * route.Height), route.Encoder,
                    valid, decoded, Measure(samples.Select(sample => sample.CaptureMs)),
                    Measure(samples.Select(sample => sample.EncodeMs)), total,
                    samples.Sum(sample => sample.PacketBytes) * 8d * 30 / samples.Count / 1_000_000,
                    samples.Max(sample => sample.PacketBytes), sourcePatterns.Count, decodedPatterns.Count,
                    lastSource?.Distinct().Count() ?? 0, null));
                Console.WriteLine($"{route.Name}: capture to packet {total.MeanMs:F2} ms mean / {total.P95Ms:F2} P95; {decoded} decoded; {results[^1].PacketMbpsAt30Fps:F2} Mbps");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[GPU quantized encode] {route.Name}: {error}");
                results.Add(new(route.Name, route.Width, route.Height,
                    route.Path == DesktopCapturePath.PixelShaderGray4 ? 0.5
                        : route.Path is DesktopCapturePath.PixelShaderGray8 or DesktopCapturePath.PixelShaderRgb332 ? 1
                        : route.Path == DesktopCapturePath.PixelShaderRgb565 ? 2 : 4,
                    route.Encoder, 0, 0, null, null, null, 0, 0, 0, 0, 0, error.ToString()));
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Source = $"Live {width}x{height} primary desktop captured with DDA; intended controlled full-motion stimulus at 30 FPS.",
                Boundary = "Capture API call start through complete H.264 packet; excludes source-frame waiting, UDP, decode timing and rendering.",
                BitrateLimitKbps = 2000, WarmupValidFrames = 5, TimedValidFrames = 30,
                Rgb332 = "One byte total: 3 red, 3 green, 2 blue bits. This route is diagnostic-only because the controlled muted-color stimulus collapsed to one source pattern and one decoded pattern.",
                Gray8 = "One byte luma. GPU output is downloaded, converted to YUV420 and encoded by CPU x264.",
                Gray4 = "Two four-bit grayscale pixels per byte. GPU packs; CPU unpacks to gray8 before YUV420 conversion and x264.",
                ComparisonLimit = "Routes observe successive live desktop frames, not byte-identical source frames. Output resolutions/color precision differ; packet rate is observed, not a quality-equivalence claim.",
                Routes = results
            }, AppConfiguration.JsonOptions));
        }
        return results.All(result => result.Error == null) ? 0 : 1;
    }

    public static async Task RunSessionAsync(AppConfiguration config, string output)
    {
        using var capture = new DesktopStreamSource(1);
        var active = config with { AutoSelectCodec = false, InitialPreset = "h264_fast", InitialBitrateKbps = 2000,
            Width = capture.Width, Height = capture.Height };
        using var session = new DemoSession(active, capture.Capture, capture.Resize, capture.SourceWidth,
            capture.SourceHeight, capture.CaptureMapped, capture.SetPixelMode);
        Exception? failure = null;
        session.Failed += error => failure ??= error;
        List<object> observations = new();
        try
        {
            await session.StartAsync();
            foreach (var (preset, expectedPath) in new[]
            {
                ("h264_rgb332", "PixelShaderRgb332"),
                ("h264_rgb565", "PixelShaderRgb565"),
                ("h264_gray8", "PixelShaderGray8"),
                ("h264_gray4", "PixelShaderGray4"),
                ("h264_fast", "NativeNoScale"),
                ("h264_rgb_fast", "NativeNoScale")
            })
            {
                var applied = await session.ApplyAsync(preset, 2000);
                if (!applied.Success) throw new InvalidOperationException($"{preset}: {applied.Message}");
                var timeout = Stopwatch.StartNew();
                while (session.DecodedGeneration < applied.Generation)
                {
                    if (failure != null) throw new InvalidOperationException("Session failed during packed preset switch.", failure);
                    if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException($"{preset} did not decode a fresh UDP frame.");
                    await Task.Delay(25);
                }
                var stats = capture.Statistics ?? throw new InvalidDataException("Missing real desktop capture statistics.");
                if (stats.CapturePath != expectedPath || stats.OutputWidth != capture.SourceWidth || stats.OutputHeight != capture.SourceHeight)
                    throw new InvalidDataException($"{preset}: expected {expectedPath} at native size, got {stats.CapturePath} {stats.OutputWidth}x{stats.OutputHeight}.");
                observations.Add(new { Preset = preset, applied.Generation, CapturePath = stats.CapturePath,
                    stats.OutputWidth, stats.OutputHeight, session.DecodedGeneration, CaptureMs = stats.TotalCaptureMs });
                Console.WriteLine($"{preset}: UDP roundtrip decoded generation {session.DecodedGeneration}; {stats.CapturePath} {stats.OutputWidth}x{stats.OutputHeight}");
                if (preset == "h264_rgb_fast")
                {
                    await Task.Delay(500);
                    if (failure != null) throw new InvalidOperationException("Mapped capture failed after returning to the desktop RGB preset.", failure);
                }
            }
        }
        finally { await session.StopAsync(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Passed = true,
            Scope = "Real DDA capture, live preset switch, x264 H.264, localhost UDP, decoder callback; excludes UI rendering and subjective color review.",
            Observations = observations
        }, AppConfiguration.JsonOptions));
    }

    static Timing Measure(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new(ordered.Average(), ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }

    static ulong Fingerprint(byte[] bytes)
    {
        var hash = 14695981039346656037ul;
        var step = Math.Max(1, bytes.Length / 4096);
        for (var i = 0; i < bytes.Length; i += step) hash = (hash ^ bytes[i]) * 1099511628211ul;
        return hash;
    }
}
