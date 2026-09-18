using System.Diagnostics;
using System.Text.Json;

namespace Frd;

static class FfmpegRegression
{
    public static void RunStaticLowBandwidth(AppConfiguration config, string output)
    {
        Directory.CreateDirectory(output);
        var source = new PixelSequence(1280, 720);
        var results = new List<object>(); var passed = true;
        foreach (var name in new[] { "libx264", "libvpx-vp9", "libaom-av1" })
        {
            try
            {
                var entry = FindPreset(config, name);
                var result = CodecTest(entry.Key, entry.Value, source, output, 500, 12);
                results.Add(result); passed &= result.Passed;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); passed = false; results.Add(new { Encoder = name, Passed = false, Error = ex.ToString() }); }
        }
        File.WriteAllText(Path.Combine(output, "static-500kbps.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, ConfiguredCapKbps = 500, MotionSeconds = 2, StaticSeconds = 12,
            Scope = "Synthetic pixels through real FFmpeg encoder/decoder only, at 30 media FPS; no capture, transport or rendering. Repeated frozen pixels are submitted normally. Tail byte count includes coded frame headers and actual periodic keyframes; no application idle suppression or forced refinement is added.",
            Results = results
        }, AppConfiguration.JsonOptions));
        Environment.ExitCode = passed ? 0 : 1;
    }

    public static void RunControl(AppConfiguration config, string output)
    {
        Directory.CreateDirectory(output);
        var report = IntegrationTest(config).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "control-regression.json"), JsonSerializer.Serialize(report, AppConfiguration.JsonOptions));
        Environment.ExitCode = report.Passed ? 0 : 1;
        Console.WriteLine($"Manual control regression {(report.Passed ? "PASS" : "FAIL")}: {Path.GetFullPath(output)}");
    }

    public static void Run(AppConfiguration config, string output)
    {
        Directory.CreateDirectory(output);
        var started = DateTime.UtcNow;
        var source = new PixelSequence(1280, 720);
        var candidates = new[] { "libx264", "libvpx-vp9", "libaom-av1" };
        var results = new List<object>();
        var pass = true;
        SaveBitmap(Path.Combine(output, "frozen-source.bmp"), source.GetFrame(59), 1280, 720);
        foreach (var encoderName in candidates)
        {
            try
            {
                var entry = FindPreset(config, encoderName);
                var result = CodecTest(entry.Key, entry.Value, source, output);
                results.Add(result); pass &= result.Passed;
                File.WriteAllText(Path.Combine(output, encoderName + ".json"), JsonSerializer.Serialize(result, AppConfiguration.JsonOptions));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex); pass = false;
                results.Add(new { Encoder = encoderName, Passed = false, Error = ex.ToString() });
            }
        }
        object integration;
        try
        {
            var result = IntegrationTest(config).GetAwaiter().GetResult();
            integration = result; pass &= result.Passed;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); pass = false; integration = new { Passed = false, Error = ex.ToString() }; }
        var report = new
        {
            Passed = pass, StartedUtc = started, CompletedUtc = DateTime.UtcNow, Ffmpeg = FfmpegRuntime.Version,
            CodecIsolationScope = "Synthetic generic pixels only; 1280x720 at 30 media FPS, 1 Mbps codec cap, 2 seconds motion then 4 seconds frozen last motion frame. Repeated frames are submitted without custom motion detection or enhancement. Runs as fast as the codec permits; no capture, UDP or display, and no end-to-end latency claim.",
            QualityInterpretation = "Luma PSNR compares reconstructed full-range BT.709 luma to the frozen source. Quality improvement is reported, not guaranteed and not used to turn codec functionality into a pass. Native encoder frame skipping is allowed: samples use the nearest decoded static frame and report its actual PTS. A final skipped frame retains the last decoded surface. Decoded frame count must equal encoded packet count for these one-frame-per-packet presets. Periodic keyframes follow each JSON preset. Identical pixels report 100 dB.",
            RateAcceptance = "Encoded total must fit 1 Mbps over media duration plus at most 100 kbit (12500 bytes) of initial VBV burst. This is a codec bitstream check; transport separately enforces actual wire pacing including headers.",
            CodecResults = results, Integration = integration
        };
        File.WriteAllText(Path.Combine(output, "ffmpeg-regression.json"), JsonSerializer.Serialize(report, AppConfiguration.JsonOptions));
        Environment.ExitCode = pass ? 0 : 1;
        Console.WriteLine($"FFmpeg regression {(pass ? "PASS" : "FAIL")}: {Path.GetFullPath(output)}");
    }

    static KeyValuePair<string, CodecPreset> FindPreset(AppConfiguration config, string encoderName)
    {
        foreach (var entry in config.Presets)
            if (entry.Value.Enabled && new FfmpegArguments(entry.Value.EncoderArguments).CodecName == encoderName) return entry;
        throw new InvalidOperationException($"No enabled JSON preset for required regression encoder {encoderName}.");
    }

    sealed record QualityPoint(double StaticSeconds, long Pts, double PsnrDb, bool KeyFrame);
    sealed record CodecResult(string Preset, string Encoder, string Decoder, bool Passed, int InputFrames, int DecodedFrames,
        int PacketCount, long EncodedBytes, double StreamMbps, double MotionMbps, double StaticMbps,
        int ConfiguredCapKbps, bool CodecRateWithinCapBudget, long RateBudgetBytes, long TailOneSecondBytes,
        double TailOneSecondMbps, long[] StaticKeyframePts, double MeanEncodeMs, double P95EncodeMs,
        double MeanDecodeMs, double P95DecodeMs, double WallSeconds, List<QualityPoint> StaticQuality,
        double? StaticGainDb, string StaticObservation, int StaticKeyframes, string TimingBoundary,
        string EncoderArguments, string DecoderArguments);

    static CodecResult CodecTest(string presetId, CodecPreset preset, PixelSequence source, string output, int bitrateKbps = 1000, int staticSeconds = 4)
    {
        const int fps = 30, firstStatic = fps * 2;
        var lastIndex = firstStatic + fps * staticSeconds;
        var sourcePixels = source.GetFrame(firstStatic - 1);
        var referenceLuma = Luma(sourcePixels);
        using var encoder = new FfmpegEncoder(preset.EncoderArguments, 1280, 720, fps, bitrateKbps);
        using var decoder = new FfmpegDecoder(preset.DecoderArguments);
        var encodeMs = new List<double>(); var decodeMs = new List<double>();
        var qualities = new List<QualityPoint>();
        var sampleTargets = new[] { 0, 1, 2, 4, 8, staticSeconds }.Where(second => second <= staticSeconds).Distinct().Order().Select(second => firstStatic + fps * second).ToArray();
        var requested = new Queue<int>(sampleTargets);
        var keyframes = new HashSet<long>();
        var decodedFrames = 0; var packets = 0; var staticKeyframes = 0; long bytes = 0, motionBytes = 0, staticBytes = 0, tailBytes = 0;
        byte[]? firstPixels = null, lastPixels = null;
        DecodedPixels? previousStaticFrame = null;
        var watch = Stopwatch.StartNew();

        void Observe(DecodedPixels frame)
        {
            if (frame.Width != 1280 || frame.Height != 720) throw new InvalidDataException("Codec changed regression dimensions.");
            decodedFrames++;
            if (frame.Pts < firstStatic) return;
            firstPixels ??= frame.Bgra; lastPixels = frame.Bgra;
            while (requested.TryPeek(out var target) && frame.Pts >= target)
            {
                requested.Dequeue();
                var sample = previousStaticFrame != null && target - previousStaticFrame.Pts < frame.Pts - target ? previousStaticFrame : frame;
                qualities.Add(new((target - firstStatic) / (double)fps, sample.Pts, Psnr(referenceLuma, sample.Bgra), keyframes.Contains(sample.Pts)));
            }
            previousStaticFrame = frame;
        }
        void DecodePackets(IReadOnlyList<CodecPacket> encoded)
        {
            foreach (var packet in encoded)
            {
                packets++; bytes += packet.Data.Length;
                if (packet.Pts < firstStatic) motionBytes += packet.Data.Length; else staticBytes += packet.Data.Length;
                if (packet.Pts > lastIndex - fps && packet.Pts <= lastIndex) tailBytes += packet.Data.Length;
                if (packet.KeyFrame) { keyframes.Add(packet.Pts); if (packet.Pts >= firstStatic) staticKeyframes++; }
                var start = Stopwatch.GetTimestamp();
                var frames = decoder.Decode(packet.Data, packet.Pts);
                decodeMs.Add(ElapsedMs(start));
                foreach (var frame in frames) Observe(frame);
            }
        }
        for (var index = 0; index <= lastIndex; index++)
        {
            var pixels = index < firstStatic ? source.GetFrame(index) : sourcePixels;
            var start = Stopwatch.GetTimestamp();
            var encoded = encoder.Encode(pixels, index, index == 0);
            encodeMs.Add(ElapsedMs(start)); DecodePackets(encoded);
        }
        DecodePackets(encoder.Flush());
        foreach (var frame in decoder.Flush()) Observe(frame);
        while (requested.TryDequeue(out var target) && previousStaticFrame != null)
            qualities.Add(new((target - firstStatic) / (double)fps, previousStaticFrame.Pts,
                Psnr(referenceLuma, previousStaticFrame.Bgra), keyframes.Contains(previousStaticFrame.Pts)));
        watch.Stop();
        if (firstPixels != null) SaveBitmap(Path.Combine(output, encoder.Name + "-static-first.bmp"), firstPixels, 1280, 720);
        if (lastPixels != null) SaveBitmap(Path.Combine(output, encoder.Name + "-static-final.bmp"), lastPixels, 1280, 720);
        var gain = qualities.Count >= 2 ? (double?)(qualities[^1].PsnrDb - qualities[0].PsnrDb) : null;
        var rateBudget = (long)Math.Ceiling((lastIndex + 1.0) / fps * bitrateKbps * 1000 / 8) + 12500;
        var result = new CodecResult(presetId, encoder.Name, decoder.Name,
            decodedFrames > 0 && decodedFrames == packets && qualities.Count == sampleTargets.Length && bytes <= rateBudget, lastIndex + 1, decodedFrames, packets, bytes,
            bytes * 8.0 / ((lastIndex + 1.0) / fps) / 1_000_000, motionBytes * 8.0 / 2 / 1_000_000,
            staticBytes * 8.0 / ((lastIndex + 1.0 - firstStatic) / fps) / 1_000_000,
            bitrateKbps, bytes <= rateBudget, rateBudget, tailBytes, tailBytes * 8.0 / 1_000_000,
            keyframes.Where(pts => pts >= firstStatic).Order().ToArray(), Mean(encodeMs), Percentile(encodeMs, .95),
            Mean(decodeMs), Percentile(decodeMs, .95), watch.Elapsed.TotalSeconds, qualities, gain,
            gain == null ? "Insufficient static decoded samples" : gain > .1 ? "Static quality improved by more than 0.1 dB" : gain < -.1 ? "Static quality decreased by more than 0.1 dB" : "Static quality remained within 0.1 dB",
            staticKeyframes, "Encode includes BGRA conversion, codec call and packet copy; decode includes packet copy, codec call and BGRA conversion. Initialization, source generation, PSNR and flush are excluded from per-call timing.",
            preset.EncoderArguments, preset.DecoderArguments);
        Console.WriteLine($"{encoder.Name}: decoded {decodedFrames}, encode {result.MeanEncodeMs:F2} ms, static PSNR gain {gain:F2} dB");
        return result;
    }

    sealed record IntegrationStep(string Name, bool Passed, string Preset, int LimitKbps, int Generation, long ReceivedFrames, string Message);
    sealed record IntegrationResult(bool Passed, List<IntegrationStep> Steps, object Session, string Scope);

    static async Task<IntegrationResult> IntegrationTest(AppConfiguration config)
    {
        var h264 = FindPreset(config, "libx264").Key;
        var vp9 = FindPreset(config, "libvpx-vp9").Key;
        var av1 = FindPreset(config, "libaom-av1").Key;
        var testConfig = config with { InitialPreset = h264, InitialBitrateKbps = 1000 };
        var source = new PixelSequence(config.Width, config.Height);
        var sourceIndex = 0; long frameCount = 0;
        var statuses = new List<SessionStatus>(); var statusGate = new object();
        using var session = new DemoSession(testConfig, () => source.GetFrame(Interlocked.Increment(ref sourceIndex) % 120));
        session.FrameReceived += _ => Interlocked.Increment(ref frameCount);
        session.StatusChanged += status => { lock (statusGate) statuses.Add(status); };
        var steps = new List<IntegrationStep>();
        try
        {
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await AwaitFrames(session.AppliedGeneration, 0);
            await AwaitStatus(h264, 1000);
            steps.Add(new("Initial H.264", true, h264, 1000, session.AppliedGeneration, Interlocked.Read(ref frameCount), "Initial decoded UDP frame received"));
            await Apply("Switch to VP9", vp9, 1000);
            await Apply("Switch to AV1", av1, 1000);
            await Apply("Return to H.264", h264, 1000);
            foreach (var cap in new[] { 500, 1000, 2000 }) await Apply("Manual bitrate " + cap, h264, cap);
            var beforeGeneration = session.AppliedGeneration;
            var beforeFrames = Interlocked.Read(ref frameCount);
            var av2 = config.Presets.FirstOrDefault(entry => entry.Key.Contains("av2", StringComparison.OrdinalIgnoreCase));
            if (av2.Key == null) throw new InvalidOperationException("AV2 preset missing; unsupported backend rejection cannot be verified.");
            var rejected = await session.ApplyAsync(av2.Key, 1000).WaitAsync(TimeSpan.FromSeconds(10));
            await AwaitFrames(beforeGeneration, beforeFrames);
            steps.Add(new("Unavailable AV2 preserves running stream", !rejected.Success && session.AppliedGeneration == beforeGeneration,
                av2.Key, 1000, session.AppliedGeneration, Interlocked.Read(ref frameCount), rejected.Message));
            var diagnosticsBefore = DiagnosticFrames();
            var enabledDiagnostics = await session.SetPacketDiagnosticsAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
            if (!enabledDiagnostics.Success) throw new InvalidOperationException(enabledDiagnostics.Message);
            var diagnosticWait = Stopwatch.StartNew();
            while (DiagnosticFrames() <= diagnosticsBefore)
            {
                if (diagnosticWait.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Enabled diagnostic UDP stream did not deliver any frame.");
                await Task.Delay(30);
            }
            await AwaitStatus(h264, 2000);
            steps.Add(new("Enable separate diagnostic UDP", session.AppliedGeneration == beforeGeneration,
                h264, 2000, session.AppliedGeneration, Interlocked.Read(ref frameCount), $"Received diagnostic frames: {DiagnosticFrames()}"));
            var beforeDisableFrames = Interlocked.Read(ref frameCount);
            var disabledDiagnostics = await session.SetPacketDiagnosticsAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
            if (!disabledDiagnostics.Success) throw new InvalidOperationException(disabledDiagnostics.Message);
            await AwaitFrames(beforeGeneration, beforeDisableFrames); await Task.Delay(100);
            var afterDisable = DiagnosticFrames();
            await AwaitFrames(beforeGeneration, Interlocked.Read(ref frameCount)); await Task.Delay(250);
            await AwaitStatus(h264, 2000);
            steps.Add(new("Disable diagnostics preserves video", DiagnosticFrames() == afterDisable && session.AppliedGeneration == beforeGeneration,
                h264, 2000, session.AppliedGeneration, Interlocked.Read(ref frameCount), $"Diagnostic count remained {afterDisable} while video decoded further frames"));
            int statusStart;
            lock (statusGate) statusStart = statuses.Count;
            await Task.Delay(1600);
            SessionStatus[] stable;
            lock (statusGate) stable = statuses.Skip(statusStart).ToArray();
            var unchanged = session.AppliedGeneration == beforeGeneration && stable.Length >= 3 && stable.All(status => status.ActivePreset == h264 && status.AppliedLimitKbps == 2000);
            steps.Add(new("Estimation does not modify manual selection", unchanged, h264, 2000, session.AppliedGeneration,
                Interlocked.Read(ref frameCount), $"Observed {stable.Length} status reports without another control request"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            steps.Add(new("Integration failure", false, "", 0, session.AppliedGeneration, Interlocked.Read(ref frameCount), ex.ToString()));
        }
        finally { await session.StopAsync(); }
        return new(steps.Count >= 11 && steps.All(step => step.Passed), steps, session.GetReport(),
            "Synthetic source through real FFmpeg, localhost UDP video and TCP manual control; decoded frame receipt confirms each new generation. No screen capture or UI render is claimed by this test.");

        long DiagnosticFrames() => JsonSerializer.SerializeToElement(session.GetReport()).GetProperty("ReceivedDiagnosticFrames").GetInt64();

        async Task Apply(string name, string presetId, int cap)
        {
            var before = Interlocked.Read(ref frameCount);
            var result = await session.ApplyAsync(presetId, cap).WaitAsync(TimeSpan.FromSeconds(15));
            if (!result.Success) throw new InvalidOperationException(name + ": " + result.Message);
            await AwaitFrames(result.Generation, before);
            await AwaitStatus(presetId, cap);
            steps.Add(new(name, session.AppliedGeneration == result.Generation, presetId, cap, result.Generation,
                Interlocked.Read(ref frameCount), "Sender status confirmed exact preset/cap and receiver continued decoding; same-session bitrate changes may preserve generation"));
        }
        async Task AwaitFrames(int generation, long before)
        {
            var watch = Stopwatch.StartNew();
            while (session.DecodedGeneration != generation || Interlocked.Read(ref frameCount) <= before)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException($"Generation {generation} has no decoded frame; current decoded generation {session.DecodedGeneration}.");
                await Task.Delay(30);
            }
        }
        async Task AwaitStatus(string presetId, int cap)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                lock (statusGate)
                    if (statuses.Count > 0 && statuses[^1].ActivePreset == presetId && statuses[^1].AppliedLimitKbps == cap) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException($"Status did not confirm {presetId} at {cap} kbps.");
                await Task.Delay(30);
            }
        }
    }

    static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    static double Mean(List<double> values) => values.Count == 0 ? 0 : values.Average();
    static double Percentile(List<double> values, double fraction) => values.Count == 0 ? 0 : values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Count * fraction) - 1, 0, values.Count - 1));
    static byte[] Luma(byte[] pixels)
    {
        var result = new byte[pixels.Length / 4];
        for (int i = 0, p = 0; i < result.Length; i++, p += 4) result[i] = (byte)((19 * pixels[p] + 183 * pixels[p + 1] + 54 * pixels[p + 2] + 128) >> 8);
        return result;
    }
    static double Psnr(byte[] reference, byte[] actual)
    {
        if (actual.Length != reference.Length * 4) throw new InvalidDataException("PSNR frame dimensions mismatch.");
        long error = 0;
        for (int i = 0, p = 0; i < reference.Length; i++, p += 4)
        {
            int luma = (19 * actual[p] + 183 * actual[p + 1] + 54 * actual[p + 2] + 128) >> 8;
            int delta = reference[i] - luma; error += delta * delta;
        }
        return error == 0 ? 100 : 10 * Math.Log10(255.0 * 255 * reference.Length / error);
    }

    static void SaveBitmap(string path, byte[] bgra, int width, int height)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4D42); writer.Write(54 + bgra.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(bgra.Length); writer.Write(3780); writer.Write(3780); writer.Write(0); writer.Write(0); writer.Write(bgra);
    }

    sealed class PixelSequence
    {
        readonly int width, height;
        readonly byte[] background;
        static readonly string[] Glyphs = ["01110100011000110001100011000101110", "00100011000010000100001000010001110", "01110100010000100010001000100011111", "11110000010000101110000010000111110", "00010001100101010010111110001000010", "11111100001000011110000010000111110", "01110100001000011110100011000101110", "11111000010001000100010000100001000", "01110100011000101110100011000101110", "01110100011000101111000010000101110"];

        public PixelSequence(int width, int height)
        {
            this.width = width; this.height = height; background = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var shade = (byte)(45 + x * 160 / width + ((x / 16 + y / 16) % 2) * 24);
                Set(background, x, y, shade, (byte)Math.Min(255, shade + y * 20 / height), shade);
            }
            for (var y = 15; y + 14 < height; y += 37)
                for (var x = 15; x + 10 < width; x += 21) DrawGlyph(background, x, y, (x / 21 + y / 37) % 10, 2);
        }

        public byte[] GetFrame(int index)
        {
            var result = (byte[])background.Clone();
            int left = width / 10, top = height / 5, patchWidth = width * 4 / 5, patchHeight = height * 3 / 5;
            for (var y = 0; y < patchHeight; y++) for (var x = 0; x < patchWidth; x++)
            {
                int u = x + index * 7, v = y + index * 3;
                int checker = ((u / 7 + v / 7) & 1) * 75;
                byte shade = (byte)(50 + checker + ((u + v) % 120));
                Set(result, left + x, top + y, shade, (byte)(40 + (u * 3 + v) % 170), (byte)(60 + checker));
            }
            for (var y = top + 8; y + 14 < top + patchHeight; y += 29)
                for (var x = left + 8; x + 10 < left + patchWidth; x += 31) DrawGlyph(result, x, y, (x / 31 + y / 29 + index / 4) % 10, 2);
            return result;
        }

        void DrawGlyph(byte[] pixels, int x, int y, int digit, int scale)
        {
            var glyph = Glyphs[digit];
            for (var row = 0; row < 7; row++) for (var col = 0; col < 5; col++)
                if (glyph[row * 5 + col] == '1')
                    for (var dy = 0; dy < scale; dy++) for (var dx = 0; dx < scale; dx++) Set(pixels, x + col * scale + dx, y + row * scale + dy, 15, 15, 15);
        }
        void Set(byte[] pixels, int x, int y, byte red, byte green, byte blue)
        {
            int p = (y * width + x) * 4; pixels[p] = blue; pixels[p + 1] = green; pixels[p + 2] = red; pixels[p + 3] = 255;
        }
    }
}
