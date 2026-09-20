using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFmpeg.AutoGen;
using Frd;

namespace Frd.CodecLatency;

static unsafe class LowColorProbe
{
    const int Fps = 30, Warmup = 10, Measured = 90;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly int[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
    static readonly Lazy<int> QuantizerSelfCheck = new(CheckQuantizerEquivalence);
    internal sealed record Candidate(string Name, string Encoder, int GrayBits = 0, int RedBits = 0, int GreenBits = 0, int BlueBits = 0, bool Dither = false);

    public static int Run(string fixtureDirectory, string outputDirectory, string mode)
    {
        if (mode == "selftest")
        {
            Directory.CreateDirectory(outputDirectory);
            var report = new { Passed = true, Cases = QuantizerSelfCheck.Value, ByteValues = 256, Bits = new[] { 1, 2, 4, 6 },
                VectorByteCount = Vector<byte>.Count, Vector.IsHardwareAccelerated, Scope = "Exact LUT equivalence, SIMD/scalar output, all byte values, unaligned rows, varied stride/width/height, untouched padding. No codec or performance benchmark." };
            File.WriteAllText(Path.Combine(outputDirectory, "quantizer-selftest.json"), JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        if (mode is not ("quick" or "quick1" or "full")) throw new ArgumentException("Low-color mode must be quick (5 Mbps), quick1 (1 Mbps), full (1 and 5 Mbps), or selftest.");
        Directory.CreateDirectory(outputDirectory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "manifest.json")));
        var width = manifest.RootElement.GetProperty("Width").GetInt32();
        var height = manifest.RootElement.GetProperty("Height").GetInt32();
        if (width <= 0 || height <= 0 || (width | height) % 2 != 0) throw new InvalidDataException("Fixture dimensions must be positive and even.");
        var files = Directory.GetFiles(fixtureDirectory, "*.bgra").Order(StringComparer.Ordinal).ToArray();
        if (files.Length < 2) throw new InvalidDataException("At least two real-desktop BGRA fixtures are required.");
        var frames = files.Select(File.ReadAllBytes).ToArray();
        if (frames.Any(frame => frame.Length != checked(width * height * 4))) throw new InvalidDataException("A fixture does not match the manifest dimensions.");
        var uniqueFixtures = frames.Select(frame => Convert.ToHexString(SHA256.HashData(frame))).Distinct().Count();
        Candidate[] candidates = [
            new("color-yuv420", "libx264"), new("gray8", "libx264", 8), new("gray6", "libx264", 6),
            new("gray4", "libx264", 4), new("gray2", "libx264", 2), new("gray1", "libx264", 1),
            new("gray1-ordered", "libx264", 1, Dither: true),
            new("rgb565", "libx264", RedBits: 5, GreenBits: 6, BlueBits: 5),
            new("rgb332", "libx264", RedBits: 3, GreenBits: 3, BlueBits: 2),
            new("qsv-color-nv12", "h264_qsv"), new("qsv-gray8", "h264_qsv", 8),
            new("qsv-gray4", "h264_qsv", 4), new("qsv-gray1", "h264_qsv", 1), new("color-yuv420-repeat", "libx264")
        ];
        var filter = Environment.GetEnvironmentVariable("FRD_LOW_COLOR_CANDIDATES");
        if (!string.IsNullOrWhiteSpace(filter)) candidates = candidates.Where(candidate => filter.Split(',').Contains(candidate.Name)).ToArray();
        if (candidates.Length == 0) throw new ArgumentException("No matching low-color candidates");
        List<object> results = new();
        var failed = 0;
        foreach (var bitrate in mode == "full" ? new[] { 1000, 5000 } : mode == "quick1" ? new[] { 1000 } : new[] { 5000 })
        foreach (var candidate in candidates)
        {
            Console.Error.WriteLine($"Low-color probe: {candidate.Name}, {bitrate} kbps, {width}x{height}.");
            try { results.Add(RunCandidate(candidate, bitrate, frames, width, height, outputDirectory)); }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"Low-color candidate failed: {candidate.Name}, {bitrate} kbps: {error}");
                results.Add(new { candidate.Name, candidate.Encoder, BitrateKbps = bitrate, Passed = false, Error = error.ToString() });
            }
            Save();
        }
        return failed == 0 ? 0 : 2;

        void Save()
        {
            var report = new
            {
                Computer = Environment.MachineName, Runtime = Environment.Version.ToString(), Ffmpeg = FfmpegRuntime.Version,
                CpuFlags = ffmpeg.av_get_cpu_flags(), Avx2Supported = Avx2.IsSupported, Sse2Supported = Sse2.IsSupported,
                ScalarQuantizationForced = Environment.GetEnvironmentVariable("FRD_LOW_COLOR_SCALAR") == "1", VectorByteCount = Vector<byte>.Count, Vector.IsHardwareAccelerated,
                Mode = mode, Width = width, Height = height, FixtureCount = frames.Length, UniqueFixtureImages = uniqueFixtures,
                FixtureManifest = manifest.RootElement, FramesPerSecond = Fps, WarmupFrames = Warmup, MeasuredFrames = Measured,
                Scope = "Preloaded real-desktop pixels; preprocessing plus complete encoded packet, then software decode to BGRA. No capture, network or render. Sub-8-bit candidates quantize pixels into standard 8-bit codec input; they are not 1/2/4/6-bit H.264 streams. QSV includes software-frame upload and synchronization. Warmup repeats fixture 0; measured fixture index = (submission index - warmup) modulo fixture count. Quality calculation and BMP output are outside timing; BMP/quality use the last measured frame.",
                Results = results
            };
            File.WriteAllText(Path.Combine(outputDirectory, "low-color-results.json"), JsonSerializer.Serialize(report, JsonOptions));
        }
    }

    static object RunCandidate(Candidate candidate, int bitrate, byte[][] fixtures, int width, int height, string outputDirectory)
    {
        var initialized = Stopwatch.GetTimestamp();
        using var encoder = new PreparedEncoder(candidate, bitrate, width, height);
        using var decoder = new FfmpegDecoder("-c:v h264 -threads 1 -flags low_delay");
        var initializationMs = Stopwatch.GetElapsedTime(initialized).TotalMilliseconds;
        List<double> prepare = new(), encode = new(), decode = new(), sender = new(), total = new(), inputCopy = new();
        List<object> frameResults = new();
        HashSet<string> decodedHashes = new();
        long payloadBytes = 0;
        long warmupPayloadBytes = 0, initialIdrBytes = 0;
        var keyFrames = 0;
        var changedInputs = 0;
        byte[]? decodedLast = null;
        var gc0 = 0; var gc1 = 0; var gc2 = 0; long allocated = 0;
        var measuredStart = 0L;
        for (var index = 0; index < Warmup + Measured; index++)
        {
            if (index == Warmup)
            {
                gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                allocated = GC.GetAllocatedBytesForCurrentThread(); measuredStart = Stopwatch.GetTimestamp();
            }
            var fixtureIndex = index < Warmup ? 0 : (index - Warmup) % fixtures.Length;
            var input = fixtures[fixtureIndex];
            var start = Stopwatch.GetTimestamp();
            encoder.Prepare(input);
            var prepared = Stopwatch.GetTimestamp();
            var packet = encoder.Encode(index);
            var encoded = Stopwatch.GetTimestamp();
            var pixels = decoder.Decode(packet.Data, packet.Pts);
            var decoded = Stopwatch.GetTimestamp();
            if (packet.Pts != index || pixels.Count != 1 || pixels[0].Pts != index)
                throw new InvalidDataException($"Immediate frame/PTS contract failed at input {index}: packet={packet.Pts}, decoded={pixels.Count}.");
            var image = pixels[0];
            if (image.Width != width || image.Height != height || image.Bgra.Length != input.Length)
                throw new InvalidDataException("Decoded dimensions or storage size do not match input.");
            if (index < Warmup) warmupPayloadBytes += packet.Data.Length;
            if (index == 0)
            {
                if (!packet.KeyFrame) throw new InvalidDataException("The first encoded frame was not a key frame.");
                initialIdrBytes = packet.Data.Length;
            }
            if (index >= Warmup)
            {
                var p = Stopwatch.GetElapsedTime(start, prepared).TotalMilliseconds;
                var e = Stopwatch.GetElapsedTime(prepared, encoded).TotalMilliseconds;
                var d = Stopwatch.GetElapsedTime(encoded, decoded).TotalMilliseconds;
                prepare.Add(p); encode.Add(e); decode.Add(d); sender.Add(p + e); total.Add(p + e + d); inputCopy.Add(encoder.LastInputCopyMs);
                payloadBytes += packet.Data.Length; if (packet.KeyFrame) keyFrames++;
                if (index > Warmup && !input.AsSpan().SequenceEqual(fixtures[(fixtureIndex + fixtures.Length - 1) % fixtures.Length])) changedInputs++;
                decodedHashes.Add(Convert.ToHexString(SHA256.HashData(image.Bgra)));
                frameResults.Add(new { InputIndex = index, FixtureIndex = fixtureIndex, PrepareMs = p, InputCopyMs = encoder.LastInputCopyMs, EncodeMs = e, DecodeMs = d, PacketBytes = packet.Data.Length });
            }
            decodedLast = image.Bgra;
            var remaining = 1000d / Fps - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining > 1) Thread.Sleep((int)remaining);
        }
        var elapsedSeconds = Stopwatch.GetElapsedTime(measuredStart).TotalSeconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var collections = new { Gen0 = GC.CollectionCount(0) - gc0, Gen1 = GC.CollectionCount(1) - gc1, Gen2 = GC.CollectionCount(2) - gc2 };
        var reference = encoder.ReferenceBgra();
        var levels = encoder.ValidateLevels();
        var lastInput = fixtures[(Measured - 1) % fixtures.Length];
        var stem = candidate.Name + "-" + bitrate;
        SaveBitmap(Path.Combine(outputDirectory, stem + "-reference.bmp"), reference, width, height);
        SaveBitmap(Path.Combine(outputDirectory, stem + "-decoded.bmp"), decodedLast!, width, height);
        var quality = Quality(reference, decodedLast!, width, height);
        var preprocessingDifference = Quality(lastInput, reference, width, height);
        Console.Error.WriteLine($"Low-color {stem}: prepare {prepare.Average():F3} ms, encode {encode.Average():F3} ms, sender {sender.Average():F3} ms, {payloadBytes * 8d * Fps / Measured / 1_000_000:F3} Mbps.");
        return new
        {
            candidate.Name, candidate.Encoder, candidate.GrayBits, candidate.RedBits, candidate.GreenBits, candidate.BlueBits, candidate.Dither,
            BitrateKbps = bitrate, Passed = true, InitializationMs = initializationMs, PixelFormat = encoder.PixelFormat,
            Range = encoder.FullRange ? "full 0..255" : "limited Y 16..235", EncoderArguments = encoder.Arguments,
            encoder.NumericRangeCheckPassed,
            encoder.ScalarQuantizationForced, encoder.VectorQuantizationEnabled, VectorByteCount = Vector<byte>.Count, Vector.IsHardwareAccelerated,
            encoder.SwsThreadsRequested, encoder.SwsThreads, encoder.ConversionApi, InputCopyMsIncludedInPrepare = Stats(inputCopy),
            PrepareMs = Stats(prepare), EncodeMs = Stats(encode), PrepareEncodeMs = Stats(sender), DecodeMs = Stats(decode), PrepareToDecodedMs = Stats(total),
            DecodedMeasuredFrames = total.Count, ChangedInputFrames = changedInputs, UniqueDecodedImages = decodedHashes.Count,
            ImmediateOutputComplete = true, MeasuredKeyframes = keyFrames, PayloadBytes = payloadBytes,
            WarmupPayloadBytes = warmupPayloadBytes, InitialIdrBytes = initialIdrBytes,
            NominalPayloadMbps = payloadBytes * 8d * Fps / Measured / 1_000_000, ObservedPayloadMbps = payloadBytes * 8d / elapsedSeconds / 1_000_000,
            AllocatedManagedBytesIncludingDecodeValidation = allocatedBytes, Collections = collections,
            ObservedPreparedLumaLevels = levels, CodecErrorAgainstPreparedInput = quality,
            PreprocessingDifferenceAgainstOriginal = preprocessingDifference,
            ReferenceBitmap = stem + "-reference.bmp", DecodedBitmap = stem + "-decoded.bmp", Frames = frameResults
        };
    }

    static object Stats(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new { Count = sorted.Length, Mean = sorted.Average(), Median = sorted[sorted.Length / 2], P95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], Max = sorted[^1] };
    }

    static object Quality(byte[] reference, byte[] decoded, int width, int height)
    {
        double abs = 0, squared = 0, rgbAbs = 0, chroma = 0, referenceEdges = 0, preservedEdges = 0;
        var count = width * height;
        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var delta = Luma(reference, offset) - Luma(decoded, offset);
            abs += Math.Abs(delta); squared += delta * delta;
            rgbAbs += Math.Abs(reference[offset] - decoded[offset]) + Math.Abs(reference[offset + 1] - decoded[offset + 1]) + Math.Abs(reference[offset + 2] - decoded[offset + 2]);
            chroma += Math.Max(decoded[offset], Math.Max(decoded[offset + 1], decoded[offset + 2])) - Math.Min(decoded[offset], Math.Min(decoded[offset + 1], decoded[offset + 2]));
            if (i % width == 0) continue;
            var edge = Math.Abs(Luma(reference, offset) - Luma(reference, offset - 4));
            if (edge < 24) continue;
            referenceEdges += edge;
            preservedEdges += Math.Min(edge, Math.Abs(Luma(decoded, offset) - Luma(decoded, offset - 4)));
        }
        return new { LumaMae = abs / count, LumaRmse = Math.Sqrt(squared / count), LumaPsnrDb = squared == 0 ? (double?)null : 10 * Math.Log10(255d * 255 * count / squared),
            RgbMae = rgbAbs / (count * 3), MeanChannelSpread = chroma / count,
            HorizontalEdgeAmplitudeRetention = referenceEdges == 0 ? (double?)null : preservedEdges / referenceEdges,
            Meaning = "Pixel metrics against supplied reference; edge amplitude retention does not measure text readability. Null PSNR means exact match." };
    }

    static double Luma(byte[] pixels, int offset) => pixels[offset + 2] * .2126 + pixels[offset + 1] * .7152 + pixels[offset] * .0722;

    static byte[] Quantizer(int bits, int minimum, int maximum)
    {
        var table = new byte[256];
        var levels = bits == 0 ? 255 : (1 << bits) - 1;
        for (var i = 0; i < table.Length; i++)
        {
            var unit = Math.Clamp((i - minimum) / (double)(maximum - minimum), 0, 1);
            table[i] = (byte)(minimum + Math.Round(Math.Round(unit * levels, MidpointRounding.AwayFromZero) * (maximum - minimum) / levels, MidpointRounding.AwayFromZero));
        }
        return table;
    }

    static void QuantizeGrayRows(byte* pixels, int stride, int width, int height, int bits, byte[] table, bool forceScalar)
    {
        var vectorWidth = Vector<byte>.Count;
        var vectorized = !forceScalar && Vector.IsHardwareAccelerated;
        var levels = new Vector<ushort>((ushort)((1 << bits) - 1));
        var factor = new Vector<ushort>((ushort)(255 / ((1 << bits) - 1)));
        for (var y = 0; y < height; y++)
        {
            var row = new Span<byte>(pixels + y * stride, width);
            var x = 0;
            if (vectorized)
                for (; x <= width - vectorWidth; x += vectorWidth)
                {
                    var packed = new Vector<byte>(row.Slice(x, vectorWidth));
                    Vector.Widen(packed, out Vector<ushort> lower, out Vector<ushort> upper);
                    lower = QuantizeWide(lower, levels, factor, bits == 6);
                    upper = QuantizeWide(upper, levels, factor, bits == 6);
                    Vector.Narrow(lower, upper).CopyTo(row.Slice(x, vectorWidth));
                }
            for (; x < width; x++) row[x] = table[row[x]];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<ushort> QuantizeWide(Vector<ushort> values, Vector<ushort> levels, Vector<ushort> factor, bool sixBits)
    {
        // Exact round(v * levels / 255); the largest intermediate is below ushort.MaxValue.
        var biased = values * levels + new Vector<ushort>(127);
        var quantized = (biased + Vector<ushort>.One + (biased >> 8)) >> 8;
        if (!sixBits) return quantized * factor;
        // For 63 levels, round(q * 255 / 63) = 4q plus three exact threshold increments.
        return quantized * new Vector<ushort>(4)
            + (Vector.GreaterThanOrEqual(quantized, new Vector<ushort>(11)) & Vector<ushort>.One)
            + (Vector.GreaterThanOrEqual(quantized, new Vector<ushort>(32)) & Vector<ushort>.One)
            + (Vector.GreaterThanOrEqual(quantized, new Vector<ushort>(53)) & Vector<ushort>.One);
    }

    static int CheckQuantizerEquivalence()
    {
        var random = new Random(0x465244);
        var cases = 0;
        foreach (var bits in new[] { 1, 2, 4, 6 })
        {
            var table = Quantizer(bits, 0, 255);
            foreach (var width in new[] { 0, 1, Vector<byte>.Count - 1, Vector<byte>.Count, Vector<byte>.Count + 1, 255, 256, 257, 1280, 2880 })
            foreach (var padding in new[] { 0, 1, 7, 31 })
                Verify(width, 3, padding, padding & 7, true);
            for (var test = 0; test < 32; test++) Verify(random.Next(1, 513), random.Next(1, 8), random.Next(0, 32), random.Next(0, 32), false);

            void Verify(int width, int height, int padding, int offset, bool allValues)
            {
                var stride = width + padding;
                var actual = new byte[offset + stride * height + 33];
                random.NextBytes(actual);
                if (allValues)
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++) actual[offset + y * stride + x] = (byte)(x + y * 17);
                var expected = actual.ToArray();
                var scalar = actual.ToArray();
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++) expected[offset + y * stride + x] = table[expected[offset + y * stride + x]];
                fixed (byte* data = actual) QuantizeGrayRows(data + offset, stride, width, height, bits, table, false);
                fixed (byte* data = scalar) QuantizeGrayRows(data + offset, stride, width, height, bits, table, true);
                if (!actual.AsSpan().SequenceEqual(expected) || !scalar.AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException($"Gray SIMD equivalence failed: bits={bits}, width={width}, height={height}, stride={stride}, offset={offset}.");
                cases++;
            }
        }
        Console.Error.WriteLine($"Gray quantizer exact self-check passed: {cases} cases; SIMD={Vector.IsHardwareAccelerated}, vector bytes={Vector<byte>.Count}.");
        return cases;
    }

    static void Check(int result, string operation)
    {
        if (result >= 0) return;
        var message = stackalloc byte[1024];
        ffmpeg.av_strerror(result, message, 1024);
        throw new InvalidOperationException($"{operation}: {Marshal.PtrToStringUTF8((nint)message)} ({result}).");
    }

    static void SaveBitmap(string path, byte[] pixels, int width, int height)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0); writer.Write(pixels);
    }

    internal sealed class PreparedEncoder : IDisposable
    {
        readonly Candidate candidate;
        readonly int width, height;
        readonly byte[]? quantizedRgb;
        readonly byte[] red, green, blue, gray;
        readonly bool[] legalGray = new bool[256];
        readonly byte*[] input = new byte*[4], output = new byte*[4];
        readonly int[] inputStride = new int[4], outputStride = new int[4];
        AVCodecContext* context;
        AVFrame* frame;
        AVFrame* sourceFrame;
        AVPacket* packet;
        SwsContext* converter;
        byte firstConvertedLuma;
        public bool FullRange => candidate.GrayBits > 0 && candidate.Encoder == "libx264";
        public string PixelFormat => ffmpeg.av_get_pix_fmt_name((AVPixelFormat)frame->format);
        public string Arguments { get; }
        public bool NumericRangeCheckPassed { get; }
        public bool ScalarQuantizationForced { get; } = Environment.GetEnvironmentVariable("FRD_LOW_COLOR_SCALAR") == "1";
        public bool VectorQuantizationEnabled => FullRange && !candidate.Dither && candidate.GrayBits is 1 or 2 or 4 or 6 && !ScalarQuantizationForced && Vector.IsHardwareAccelerated;
        public int SwsThreadsRequested { get; }
        public int SwsThreads { get; }
        public string ConversionApi => sourceFrame != null ? "sws_scale_frame with copied refcounted BGRA input" : "sws_scale with borrowed BGRA input";
        public double LastInputCopyMs { get; internal set; }

        public PreparedEncoder(Candidate candidate, int bitrate, int width, int height)
        {
            _ = QuantizerSelfCheck.Value;
            this.candidate = candidate; this.width = width; this.height = height;
            var requestedThreads = Environment.GetEnvironmentVariable("FRD_SWS_THREADS");
            SwsThreadsRequested = string.IsNullOrWhiteSpace(requestedThreads) ? 1 :
                int.TryParse(requestedThreads, out var parsedThreads) && parsedThreads is >= 1 and <= 64 ? parsedThreads :
                throw new ArgumentException("FRD_SWS_THREADS must be an integer from 1 to 64.");
            SwsThreads = candidate.Encoder == "libx264" ? SwsThreadsRequested : 1;
            red = Quantizer(candidate.RedBits, 0, 255); green = Quantizer(candidate.GreenBits, 0, 255); blue = Quantizer(candidate.BlueBits, 0, 255);
            gray = Quantizer(candidate.GrayBits, FullRange ? 0 : 16, FullRange ? 255 : 235);
            foreach (var value in gray) legalGray[value] = true;
            if (candidate.RedBits > 0) quantizedRgb = new byte[checked(width * height * 4)];
            var format = candidate.Encoder == "h264_qsv" ? AVPixelFormat.AV_PIX_FMT_NV12 : candidate.GrayBits > 0 ? AVPixelFormat.AV_PIX_FMT_GRAY8 : AVPixelFormat.AV_PIX_FMT_YUV420P;
            Arguments = candidate.Encoder == "libx264" ? "-c:v libx264 -preset ultrafast -tune zerolatency -threads 4 -bf 0 -g 300" : "-c:v h264_qsv -preset veryfast -async_depth 1 -look_ahead 0 -low_power 1 -bf 0 -g 300";
            Arguments += $" -b:v {bitrate}k -maxrate {bitrate}k -bufsize {(int)Math.Ceiling(bitrate * 1000d / Fps)} -pix_fmt {ffmpeg.av_get_pix_fmt_name(format)}";
            try
            {
                var codec = ffmpeg.avcodec_find_encoder_by_name(candidate.Encoder);
                if (codec == null) throw new NotSupportedException($"Encoder {candidate.Encoder} is unavailable.");
                context = ffmpeg.avcodec_alloc_context3(codec); frame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
                if (context == null || frame == null || packet == null) throw new OutOfMemoryException("FFmpeg allocation failed.");
                context->width = width; context->height = height; context->pix_fmt = format;
                context->time_base = new() { num = 1, den = Fps }; context->framerate = new() { num = Fps, den = 1 };
                context->gop_size = 300; context->max_b_frames = 0; context->thread_count = candidate.Encoder == "libx264" ? 4 : 1;
                context->bit_rate = bitrate * 1000L; context->rc_max_rate = context->bit_rate; context->rc_buffer_size = (int)Math.Ceiling(context->bit_rate / (double)Fps);
                context->color_range = FullRange ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
                context->colorspace = AVColorSpace.AVCOL_SPC_BT709; context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
                Set("preset", candidate.Encoder == "libx264" ? "ultrafast" : "veryfast");
                if (candidate.Encoder == "libx264") Set("tune", "zerolatency");
                else { Set("async_depth", "1"); Set("look_ahead", "0"); Set("low_power", "1"); }
                Check(ffmpeg.avcodec_open2(context, codec, null), "Open low-color " + candidate.Name);
                frame->format = (int)format; frame->width = width; frame->height = height;
                frame->color_range = context->color_range; frame->colorspace = context->colorspace;
                frame->color_primaries = context->color_primaries; frame->color_trc = context->color_trc;
                Check(ffmpeg.av_frame_get_buffer(frame, 32), "Allocate reusable low-color surface");
                // QSV grayscale writes directly to the NV12 Y plane; it does not compute discarded color planes.
                var conversionFormat = candidate.GrayBits > 0 ? AVPixelFormat.AV_PIX_FMT_GRAY8 : format;
                if (SwsThreads > 1)
                {
                    converter = ffmpeg.sws_alloc_context();
                    if (converter == null) throw new OutOfMemoryException("Allocate threaded color converter failed.");
                    SetScale("srcw", width); SetScale("srch", height); SetScale("dstw", width); SetScale("dsth", height);
                    SetScale("src_format", (int)AVPixelFormat.AV_PIX_FMT_BGRA); SetScale("dst_format", (int)conversionFormat);
                    SetScale("threads", SwsThreads); SetScale("sws_flags", (int)SwsFlags.SWS_FAST_BILINEAR);
                    Check(ffmpeg.sws_init_context(converter, null, null), "Initialize threaded color converter");
                    long actualThreads;
                    Check(ffmpeg.av_opt_get_int(converter, "threads", 0, &actualThreads), "Read effective swscale threads");
                    SwsThreads = checked((int)actualThreads);
                    sourceFrame = ffmpeg.av_frame_alloc();
                    if (sourceFrame == null) throw new OutOfMemoryException("Allocate reusable conversion source frame failed.");
                    sourceFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA; sourceFrame->width = width; sourceFrame->height = height;
                    sourceFrame->color_range = AVColorRange.AVCOL_RANGE_JPEG; sourceFrame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
                    sourceFrame->color_primaries = context->color_primaries; sourceFrame->color_trc = context->color_trc;
                    Check(ffmpeg.av_frame_get_buffer(sourceFrame, 32), "Allocate reusable refcounted conversion source");
                }
                else converter = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, conversionFormat, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (converter == null) throw new NotSupportedException("Low-color pixel conversion unavailable.");
                var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
                Check(ffmpeg.sws_setColorspaceDetails(converter, coefficients, 1, coefficients, FullRange ? 1 : 0, 0, 1 << 16, 1 << 16), "Set explicit low-color BT.709 range");
                inputStride[0] = width * 4;
                CheckNumericRange();
                NumericRangeCheckPassed = true;
                Console.Error.WriteLine($"Low-color conversion {candidate.Name}: {ConversionApi}, requested threads={SwsThreadsRequested}, effective threads={SwsThreads}; source copy is included in PrepareMs and InputCopyMsIncludedInPrepare.");
                if (candidate.Encoder != "libx264" && SwsThreadsRequested > 1)
                    Console.Error.WriteLine("Threaded conversion is disabled for QSV candidates; the grayscale converter writes only the NV12 Y plane.");
            }
            catch { Dispose(); throw; }
        }

        void Set(string key, string value) => Check(ffmpeg.av_opt_set(context, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"Set {candidate.Encoder} {key}");
        void SetScale(string key, long value) => Check(ffmpeg.av_opt_set_int(converter, key, value, 0), $"Set color converter {key}");

        void CheckNumericRange()
        {
            // Endpoints and middle gray detect a limited/full-range mismatch before any benchmark samples.
            var pixels = new byte[checked(width * height * 4)];
            foreach (var tone in new[] { 0, 128, 255 })
            {
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)tone; pixels[i + 1] = (byte)tone; pixels[i + 2] = (byte)tone; pixels[i + 3] = 255; }
                Prepare(pixels);
                var sourceLuma = candidate.RedBits > 0 ? red[tone] * .2126 + green[tone] * .7152 + blue[tone] * .0722 : tone;
                var expected = FullRange ? tone : 16 + (int)Math.Round(sourceLuma * 219d / 255);
                if (Math.Abs(firstConvertedLuma - expected) > 2)
                    throw new InvalidDataException($"{candidate.Name} range self-check failed for RGB={tone}: Y={firstConvertedLuma}, expected {expected} +/- 2.");
            }
        }

        public void Prepare(byte[] pixels)
        {
            LastInputCopyMs = 0;
            Check(ffmpeg.av_frame_make_writable(frame), "Reuse encoder input");
            if (quantizedRgb != null)
            {
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    quantizedRgb[i] = blue[pixels[i]]; quantizedRgb[i + 1] = green[pixels[i + 1]];
                    quantizedRgb[i + 2] = red[pixels[i + 2]]; quantizedRgb[i + 3] = 255;
                }
                pixels = quantizedRgb;
            }
            fixed (byte* source = pixels)
            {
                if (sourceFrame != null)
                {
                    var copyStart = Stopwatch.GetTimestamp();
                    Check(ffmpeg.av_frame_make_writable(sourceFrame), "Reuse refcounted conversion source");
                    var rowBytes = checked(width * 4);
                    for (var y = 0; y < height; y++)
                        new ReadOnlySpan<byte>(source + y * rowBytes, rowBytes).CopyTo(new Span<byte>(sourceFrame->data[0] + y * sourceFrame->linesize[0], rowBytes));
                    LastInputCopyMs = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                    Check(ffmpeg.sws_scale_frame(converter, frame, sourceFrame), "Threaded low-color conversion");
                }
                else
                {
                    input[0] = source;
                    for (uint i = 0; i < 4; i++) { output[i] = frame->data[i]; outputStride[i] = frame->linesize[i]; }
                    if (ffmpeg.sws_scale(converter, input, inputStride, 0, height, output, outputStride) != height) throw new InvalidDataException("Incomplete low-color conversion.");
                }
            }
            firstConvertedLuma = frame->data[0][0];
            if (candidate.GrayBits == 0) return;
            if (candidate.GrayBits < 8)
            {
                if (FullRange && !candidate.Dither && candidate.GrayBits is 1 or 2 or 4 or 6)
                    QuantizeGrayRows(frame->data[0], frame->linesize[0], width, height, candidate.GrayBits, gray, ScalarQuantizationForced);
                else
                    for (var y = 0; y < height; y++)
                    {
                        var row = frame->data[0] + y * frame->linesize[0];
                        for (var x = 0; x < width; x++)
                            row[x] = candidate.Dither ? (byte)(row[x] >= Bayer4[(y & 3) * 4 + (x & 3)] * 16 + 8 ? 255 : 0) : gray[row[x]];
                    }
            }
            if (candidate.Encoder == "h264_qsv")
                for (var y = 0; y < height / 2; y++) new Span<byte>(frame->data[1] + y * frame->linesize[1], width).Fill(128);
        }

        public CodecPacket Encode(long pts)
        {
            frame->pts = pts; frame->pict_type = pts == 0 ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
            Check(ffmpeg.avcodec_send_frame(context, frame), "Submit low-color frame");
            Check(ffmpeg.avcodec_receive_packet(context, packet), "Receive immediate low-color packet");
            try
            {
                if (packet->pts != pts) throw new InvalidDataException($"Encoder buffered input {pts}; received {packet->pts}.");
                var data = new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
                var result = new CodecPacket(data, (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, packet->pts);
                ffmpeg.av_packet_unref(packet);
                var extra = ffmpeg.avcodec_receive_packet(context, packet);
                if (extra != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    Check(extra, "Check low-color encoder queue");
                    throw new InvalidDataException("More than one packet per input; cannot use one-packet timing contract.");
                }
                return result;
            }
            finally { ffmpeg.av_packet_unref(packet); }
        }

        public int ValidateLevels()
        {
            Span<bool> observed = stackalloc bool[256];
            observed.Clear();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = frame->data[0][y * frame->linesize[0] + x]; observed[value] = true;
                if (candidate.GrayBits is > 0 and < 8 && !legalGray[value]) throw new InvalidDataException("Quantized input contains an invalid gray level.");
            }
            var count = 0; foreach (var found in observed) if (found) count++;
            return count;
        }

        public byte[] ReferenceBgra()
        {
            var result = new byte[checked(width * height * 4)];
            var convert = ffmpeg.sws_getContext(width, height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (convert == null) throw new NotSupportedException("Reference BGRA conversion unavailable.");
            try
            {
                var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
                Check(ffmpeg.sws_setColorspaceDetails(convert, coefficients, FullRange ? 1 : 0, coefficients, 1, 0, 1 << 16, 1 << 16), "Reference full-range BGRA");
                byte*[] source = new byte*[4]; int[] sourceStride = new int[4];
                for (uint i = 0; i < 4; i++) { source[i] = frame->data[i]; sourceStride[i] = frame->linesize[i]; }
                fixed (byte* target = result)
                {
                    byte*[] destination = [target, null, null, null]; int[] destinationStride = [width * 4, 0, 0, 0];
                    if (ffmpeg.sws_scale(convert, source, sourceStride, 0, height, destination, destinationStride) != height) throw new InvalidDataException("Incomplete reference conversion.");
                }
                return result;
            }
            finally { ffmpeg.sws_freeContext(convert); }
        }

        public void Dispose()
        {
            if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; }
            var releasedPacket = packet; packet = null; if (releasedPacket != null) ffmpeg.av_packet_free(&releasedPacket);
            var releasedFrame = frame; frame = null; if (releasedFrame != null) ffmpeg.av_frame_free(&releasedFrame);
            var releasedSource = sourceFrame; sourceFrame = null; if (releasedSource != null) ffmpeg.av_frame_free(&releasedSource);
            var releasedContext = context; context = null; if (releasedContext != null) ffmpeg.avcodec_free_context(&releasedContext);
        }
    }
}
