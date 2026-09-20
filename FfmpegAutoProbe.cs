using System.Diagnostics;
using System.Text.Json;

namespace Frd;

sealed record CodecProbeSample(string PresetId, string DecoderArguments, int Width, int Height,
    double CaptureMs, double EncodeMs, double LocalDecodeMs, byte[] Packet)
{
    public double SenderMs => CaptureMs + EncodeMs;
}

sealed record CodecProbeReport(List<CodecProbeSample> Samples);
sealed record CodecDecodeRequest(CodecProbeSample[] Samples);
sealed record CodecDecodeReport(Dictionary<string, double> DecoderMs);

static class AutoCodecProbe
{
    const int DeadlineMs = 5000, MaxPacketBytes = 128 * 1024, MaxSamples = 8;

    public static (AppConfiguration Configuration, CodecProbeSample[] Samples) SelectSender(AppConfiguration config)
    {
        if (!config.AutoSelectCodec) return (config, []);
        var path = Path.Combine(Path.GetTempPath(), "frd-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RunProcess("--auto-codec-probe", Path.Combine(AppContext.BaseDirectory, "codec-config.json"), path);
            var samples = Read<CodecProbeReport>(path)?.Samples.Where(sample =>
                config.Presets.TryGetValue(sample.PresetId, out var preset) && preset.Enabled &&
                preset.BitrateControlled && sample.Packet.Length > 0 && sample.Packet.Length <= MaxPacketBytes)
                .ToArray() ?? [];
            var fastest = samples.MinBy(sample => sample.SenderMs + sample.LocalDecodeMs);
            if (fastest == null)
            {
                Console.Error.WriteLine("[auto-codec] No valid result in 5 seconds; retaining configured preset " + config.InitialPreset);
                return (config, samples);
            }
            Console.Error.WriteLine($"[auto-codec] {fastest.PresetId}: capture {fastest.CaptureMs:F2} + encode {fastest.EncodeMs:F2} + decode {fastest.LocalDecodeMs:F2} ms; {samples.Length} valid candidates.");
            return (config with { InitialPreset = fastest.PresetId }, samples);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[auto-codec] Probe failed; retaining configured preset: " + error);
            return (config, []);
        }
        finally { Delete(path); }
    }

    public static AppConfiguration SelectReceiver(AppConfiguration config, CodecProbeSample[]? samples)
    {
        if (!config.AutoSelectCodec || samples is not { Length: > 0 }) return config;
        var request = Path.Combine(Path.GetTempPath(), "frd-decoder-" + Guid.NewGuid().ToString("N") + ".json");
        var output = request + ".result";
        try
        {
            File.WriteAllText(request, JsonSerializer.Serialize(new CodecDecodeRequest(samples), AppConfiguration.JsonOptions));
            RunProcess("--auto-decode-probe", Path.Combine(AppContext.BaseDirectory, "codec-config.json"), output, request);
            var results = Read<CodecDecodeReport>(output)?.DecoderMs;
            if (results == null || results.Count == 0) return config;
            var fastest = samples.Where(sample => results.ContainsKey(sample.PresetId) && config.Presets.ContainsKey(sample.PresetId))
                .MinBy(sample => sample.SenderMs + results[sample.PresetId]);
            if (fastest == null) return config;
            Console.Error.WriteLine($"[auto-codec] Remote pair {fastest.PresetId}: sender {fastest.SenderMs:F2} + receiver decode {results[fastest.PresetId]:F2} ms.");
            return config with { InitialPreset = fastest.PresetId };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[auto-codec] Decoder probe failed; retaining host preset: " + error);
            return config;
        }
        finally { Delete(request); Delete(output); }
    }

    public static int RunWorker(string[] args)
    {
        var config = AppConfiguration.Load(args[1]);
        if (args[0] == "--auto-codec-probe") RunSenderWorker(config, args[2]);
        else RunDecoderWorker(args[3], args[2]);
        return 0;
    }

    static void RunSenderWorker(AppConfiguration config, string output)
    {
        CodecProbeReport report = new([]);
        using var capture = new DesktopStreamSource(config.TransmissionScale);
        capture.Capture();
        var candidates = config.Presets.Where(entry => entry.Value.Enabled && entry.Value.BitrateControlled &&
            (entry.Value.AutoProbe || entry.Key == config.InitialPreset) &&
            entry.Value.MinimumAutoBitrateKbps <= config.InitialBitrateKbps)
            .OrderBy(entry => entry.Key == config.InitialPreset ? 0 : 1).Take(MaxSamples);
        foreach (var (id, preset) in candidates)
        {
            try
            {
                using var encoder = new VideoEncoder(preset.EncoderArguments, capture.Width, capture.Height,
                    config.FramesPerSecond, config.InitialBitrateKbps);
                using var decoder = new VideoDecoder(preset.DecoderArguments);
                CodecProbeSample? sample = null;
                for (var frame = 0; frame < 3; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    MappedBgraFrame? mapped = null;
                    byte[]? pixels = null;
                    if (encoder.SupportsMappedBgra) mapped = capture.CaptureMapped(true);
                    else pixels = capture.Capture();
                    var captured = Stopwatch.GetTimestamp();
                    IReadOnlyList<CodecPacket> packets;
                    try { packets = mapped != null ? encoder.EncodeMapped(mapped, frame, true) : encoder.Encode(pixels!, frame, true); }
                    finally { mapped?.Dispose(); }
                    var encoded = Stopwatch.GetTimestamp();
                    foreach (var packet in packets)
                    {
                        if (packet.Data.Length is < 1 or > MaxPacketBytes) continue;
                        var decoded = decoder.Decode(packet.Data, packet.Pts);
                        var finished = Stopwatch.GetTimestamp();
                        if (decoded.Count == 0 || decoded[^1].Width != capture.Width || decoded[^1].Height != capture.Height) continue;
                        sample = new(id, preset.DecoderArguments, capture.Width, capture.Height,
                            Ms(started, captured), Ms(captured, encoded), Ms(encoded, finished), packet.Data);
                    }
                }
                if (sample == null) throw new InvalidDataException("No complete decoded frame from a bounded packet.");
                report.Samples.Add(sample);
                Write(output, report);
                Console.Error.WriteLine($"[auto-codec] {id}: {sample.CaptureMs:F2} + {sample.EncodeMs:F2} + {sample.LocalDecodeMs:F2} ms, {sample.Packet.Length} bytes");
            }
            catch (Exception error) { Console.Error.WriteLine($"[auto-codec] {id} skipped: {error}"); }
        }
    }

    static void RunDecoderWorker(string requestPath, string output)
    {
        var request = Read<CodecDecodeRequest>(requestPath) ?? throw new InvalidDataException("Missing decoder request.");
        CodecDecodeReport report = new(new());
        foreach (var sample in request.Samples.Take(MaxSamples))
        {
            try
            {
                if (sample.Packet.Length is < 1 or > MaxPacketBytes) continue;
                using var decoder = new VideoDecoder(sample.DecoderArguments);
                var started = Stopwatch.GetTimestamp();
                var frames = decoder.Decode(sample.Packet, 0);
                var elapsed = Ms(started, Stopwatch.GetTimestamp());
                if (frames.Any(frame => frame.Width == sample.Width && frame.Height == sample.Height))
                {
                    report.DecoderMs[sample.PresetId] = elapsed;
                    Write(output, report);
                }
            }
            catch (Exception error) { Console.Error.WriteLine($"[auto-codec] Receiver rejected {sample.PresetId}: {error}"); }
        }
    }

    static void RunProcess(string mode, string configPath, string outputPath, string? requestPath = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate FRD executable.");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add(outputPath);
        if (requestPath != null) start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch bounded codec probe.");
        if (process.WaitForExit(DeadlineMs)) return;
        Console.Error.WriteLine("[auto-codec] 5-second probe limit reached; using completed candidates.");
        process.Kill(entireProcessTree: true);
        process.WaitForExit(1000);
    }

    static T? Read<T>(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppConfiguration.JsonOptions) : default;

    static void Write<T>(string path, T value)
    {
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, AppConfiguration.JsonOptions));
        File.Move(temporary, path, true);
    }

    static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) { Console.Error.WriteLine("[auto-codec] Cannot remove temporary probe file: " + error); }
    }

    static double Ms(long start, long end) => (end - start) * 1000d / Stopwatch.Frequency;
}
