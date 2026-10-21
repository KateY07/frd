using System.Diagnostics;
using System.Text.Json;

namespace Frd;

/// <summary>Self-contained Media Foundation codec check: synthetic desktop frames → mf-h264 → mf-h264-decode, with timing and PSNR.</summary>
static class MfRegression
{
    sealed record Size(int Width, int Height);

    public static int Run(string[] args)
    {
        var output = args.Length > 1 ? args[1] : "results/mf-regression.json";
        var quick = args.Contains("quick");
        var sizes = quick ? new[] { new Size(1280, 720) } : new[] { new Size(1280, 720), new Size(2560, 1440), new Size(2880, 1800) };
        var caps = quick ? new[] { 4000 } : new[] { 2500, 5000, 10000 };
        var report = new List<object>();
        var passed = true;
        Console.WriteLine($"Media Foundation H.264 encoder present: {MediaFoundationRuntime.HasH264Encoder()}, decoder present: {MediaFoundationRuntime.HasH264Decoder()}");
        if (!MediaFoundationRuntime.HasH264Encoder() || !MediaFoundationRuntime.HasH264Decoder()) return 2;
        var ffmpegDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var haveFfmpeg = Directory.Exists(ffmpegDirectory);
        if (haveFfmpeg) FfmpegRuntime.Initialize(ffmpegDirectory);
        foreach (var size in sizes)
            foreach (var cap in caps)
            {
                var variants = new List<(string Label, string Arguments, bool Optional)>
                {
                    ("mf-sw-pvbr", "-c:v mf-h264 -rc pvbr -profile high -g 300", false),
                    ("mf-sw-cbr", "-c:v mf-h264 -rc cbr -profile high -g 300", false),
                    ("mf-hw-pvbr", "-c:v mf-h264 -rc pvbr -profile high -g 300 -hw on", true),
                    ("mf-hw-cbr", "-c:v mf-h264 -rc cbr -profile high -g 300 -hw on", true),
                };
                if (haveFfmpeg) variants.Add(("x264-veryfast", "-c:v libx264 -preset veryfast -tune zerolatency -crf 20 -bf 0 -g 300 -threads 4 -bufsize 100k", true));
                foreach (var (label, arguments, optional) in variants)
                {
                    try
                    {
                        var result = RunCase(label, size, arguments, 30, cap);
                        report.Add(result.Report);
                        passed &= result.Passed || optional;
                        Console.WriteLine(result.Summary);
                    }
                    catch (Exception error) when (optional)
                    {
                        Console.WriteLine($"{size.Width}x{size.Height} {cap}k {label} skipped: {error.Message}");
                        report.Add(new { Size = $"{size.Width}x{size.Height}", Label = label, CapKbps = cap, Skipped = error.Message });
                    }
                    catch (Exception error)
                    {
                        passed = false;
                        Console.WriteLine($"{size.Width}x{size.Height} {cap}k {label} FAILED: {error}");
                        report.Add(new { Size = $"{size.Width}x{size.Height}", Label = label, CapKbps = cap, Failure = error.Message });
                    }
                }
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, ReportJson.Serialize(new { Passed = passed, Cases = report }, AppConfiguration.JsonOptions));
        Console.WriteLine(passed ? $"MF regression PASS: {output}" : $"MF regression FAIL: {output}");
        return passed ? 0 : 1;
    }

    sealed record CaseResult(bool Passed, string Summary, object Report);

    static CaseResult RunCase(string label, Size size, string arguments, int fps, int bitrateKbps)
    {
        const int frames = 90;
        var sources = DesktopFrames(size.Width, size.Height, frames);
        using var encoder = new VideoEncoder(arguments, size.Width, size.Height, fps, bitrateKbps);
        using var decoder = new VideoDecoder("-c:v mf-h264-decode");
        var packets = new List<(CodecPacket Packet, double EncodeMs)>();
        var encodeTimes = new List<double>();
        var keyAfterRequest = false;
        var forced = 45;
        for (var i = 0; i < frames; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var produced = encoder.Encode(sources[i], i, i == 0 || i == forced);
            var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            encodeTimes.Add(ms);
            foreach (var packet in produced) packets.Add((packet, ms));
        }
        foreach (var packet in encoder.Flush()) packets.Add((packet, 0));
        var decoded = new Dictionary<long, byte[]>();
        var decodeTimes = new List<double>();
        foreach (var (packet, _) in packets)
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var frame in decoder.Decode(packet.Data, packet.Pts))
            {
                if (frame.Width != size.Width || frame.Height != size.Height) throw new InvalidDataException($"Decoded {frame.Width}x{frame.Height}, expected {size.Width}x{size.Height}.");
                decoded[frame.Pts] = frame.Bgra;
            }
            decodeTimes.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
        foreach (var frame in decoder.Flush()) decoded[frame.Pts] = frame.Bgra;

        var keyFrames = packets.Where(p => p.Packet.KeyFrame).Select(p => p.Packet.Pts).ToArray();
        keyAfterRequest = keyFrames.Contains(forced);
        var headersOnKeys = packets.Where(p => p.Packet.KeyFrame).All(p => H264AnnexB.Contains(p.Packet.Data, 7) && H264AnnexB.Contains(p.Packet.Data, 8));
        var startCodes = packets.All(p => p.Packet.Data.Length > 4 && p.Packet.Data[0] == 0 && p.Packet.Data[1] == 0 && (p.Packet.Data[2] == 1 || (p.Packet.Data[2] == 0 && p.Packet.Data[3] == 1)));
        var totalBytes = packets.Sum(p => (long)p.Packet.Data.Length);
        var actualKbps = totalBytes * 8.0 / (frames / (double)fps) / 1000.0;
        var psnr = new List<double>();
        var steady = new List<double>();
        foreach (var (index, pixels) in decoded.OrderBy(pair => pair.Key))
            if (index >= 0 && index < frames) { var value = Psnr(sources[index], pixels); psnr.Add(value); if (index >= frames - 30) steady.Add(value); }
        var decodedAll = decoded.Count == frames;
        var meanPsnr = psnr.Count > 0 ? psnr.Average() : 0;
        var steadyPsnr = steady.Count > 0 ? steady.Average() : 0;
        var passed = decodedAll && keyAfterRequest && headersOnKeys && startCodes && meanPsnr > 15;
        var warm = encodeTimes.Skip(5).ToArray();
        var warmDecode = decodeTimes.Skip(5).ToArray();
        var summary = $"{size.Width}x{size.Height} {bitrateKbps}k {label,-13} \"{encoder.Description}\": decoded {decoded.Count}/{frames}, keys {keyFrames.Length} (forced@{forced}: {keyAfterRequest}), " +
            $"SPS/PPS on keys: {headersOnKeys}, bitrate {actualKbps:F0} kbps (cap {bitrateKbps}), PSNR {meanPsnr:F1}/{steadyPsnr:F1} dB (all/last30), encode {warm.Average():F1}/{warm.Max():F1} ms, decode {warmDecode.Average():F1}/{warmDecode.Max():F1} ms {(passed ? "PASS" : "FAIL")}";
        return new(passed, summary, new
        {
            Size = $"{size.Width}x{size.Height}", Label = label, Arguments = arguments, Device = encoder.Description,
            Decoded = decoded.Count, Frames = frames, KeyFrames = keyFrames, ForcedKeyFrameHonoured = keyAfterRequest, ParameterSetsOnKeys = headersOnKeys,
            AnnexB = startCodes, ActualKbps = actualKbps, CapKbps = bitrateKbps, MeanPsnrDb = meanPsnr, SteadyPsnrDb = steadyPsnr, MinPsnrDb = psnr.Count > 0 ? psnr.Min() : 0,
            EncodeMsMean = warm.Average(), EncodeMsMax = warm.Max(), DecodeMsMean = warmDecode.Average(), DecodeMsMax = warmDecode.Max(), Passed = passed
        });
    }

    static readonly Dictionary<(int, int), byte[][]> FrameCache = new();

    /// <summary>
    /// Real desktop content: one GDI screenshot, then a document-like scroll of the central area and a moving cursor box,
    /// so the encoder sees UI chrome, text, and motion. Falls back to the synthetic generator if the screenshot fails.
    /// </summary>
    static byte[][] DesktopFrames(int width, int height, int count)
    {
        if (FrameCache.TryGetValue((width, height), out var cached)) return cached;
        byte[]? shot = null;
        try { using var gdi = new GdiDesktopCapture(width, height); shot = gdi.Capture(); }
        catch (Exception error) { Console.Error.WriteLine("[mf test] screenshot unavailable, using synthetic content: " + error.Message); }
        var frames = new byte[count][];
        if (shot == null || IsFlat(shot))
        {
            for (var i = 0; i < count; i++) frames[i] = SyntheticDesktop(width, height, i);
        }
        else
        {
            var bandTop = height / 6; var bandHeight = height * 2 / 3;
            for (var i = 0; i < count; i++)
            {
                var frame = (byte[])shot.Clone();
                var shift = (i * 3) % Math.Max(1, bandHeight / 2);          // 3 px per frame vertical scroll
                for (var y = 0; y < bandHeight; y++)
                {
                    var from = bandTop + (y + shift) % bandHeight;
                    Buffer.BlockCopy(shot, from * width * 4, frame, (bandTop + y) * width * 4, width * 4);
                }
                var boxLeft = (width / 8 + i * 11) % (width * 3 / 4); var boxTop = height / 4 + (i * 4) % (height / 2);
                for (var y = boxTop; y < Math.Min(height, boxTop + 24); y++)
                    for (var x = boxLeft; x < Math.Min(width, boxLeft + 24); x++)
                    {
                        var o = (y * width + x) * 4;
                        frame[o] = 40; frame[o + 1] = 120; frame[o + 2] = 255;
                    }
                frames[i] = frame;
            }
        }
        FrameCache[(width, height)] = frames;
        return frames;
    }

    static bool IsFlat(byte[] pixels)
    {
        var first = BitConverter.ToUInt32(pixels, 0);
        for (var i = 0; i < pixels.Length; i += 4 * 97) if (BitConverter.ToUInt32(pixels, i) != first) return false;
        return true;
    }

    /// <summary>Text-like blocks, a moving window, a gradient and a static region: roughly what a desktop looks like to an encoder.</summary>
    static byte[] SyntheticDesktop(int width, int height, int frame)
    {
        var pixels = new byte[checked(width * height * 4)];
        var random = new Random(12345);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var gradient = (byte)(200 + 40 * x / width);
                pixels[offset] = gradient; pixels[offset + 1] = gradient; pixels[offset + 2] = (byte)(gradient - 8); pixels[offset + 3] = 255;
            }
        // "text": 6x10 glyph cells with a fixed random pattern, lines every 16 px
        for (var line = 40; line + 12 < height - 40; line += 16)
            for (var cell = 40; cell + 8 < width * 3 / 5; cell += 7)
            {
                if (random.Next(5) == 0) continue;
                var pattern = random.Next(1 << 20);
                for (var gy = 0; gy < 10; gy++)
                    for (var gx = 0; gx < 5; gx++)
                        if (((pattern >> ((gy * 5 + gx) % 20)) & 1) != 0)
                        {
                            var offset = ((line + gy) * width + cell + gx) * 4;
                            pixels[offset] = 30; pixels[offset + 1] = 30; pixels[offset + 2] = 30;
                        }
            }
        // moving window with a title bar and colored content
        var windowWidth = width / 4; var windowHeight = height / 3;
        var left = width / 2 + (frame * 9) % (width / 3); var top = height / 5 + (frame * 5) % (height / 4);
        for (var y = top; y < Math.Min(height, top + windowHeight); y++)
            for (var x = left; x < Math.Min(width, left + windowWidth); x++)
            {
                var offset = (y * width + x) * 4;
                var title = y < top + 28;
                pixels[offset] = (byte)(title ? 150 : 255 - (x - left) * 255 / windowWidth);
                pixels[offset + 1] = (byte)(title ? 90 : (y - top) * 255 / windowHeight);
                pixels[offset + 2] = (byte)(title ? 40 : 100 + (x + y + frame * 3) % 120);
            }
        return pixels;
    }

    static double Psnr(byte[] reference, byte[] test)
    {
        double sum = 0;
        long count = 0;
        for (var i = 0; i < reference.Length; i += 4)
            for (var c = 0; c < 3; c++)
            {
                var d = reference[i + c] - test[i + c];
                sum += d * d; count++;
            }
        var mse = sum / count;
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
