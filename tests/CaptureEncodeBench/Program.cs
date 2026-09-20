using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Frd.CaptureEncodeBench;

sealed record Route(string Id, string Codec, string Input, string Description);
sealed record Settings(string NativeDirectory, string Output, string? Display, int Frames, int Warmup, int Fps, int BitrateKbps, string RateControl, int CpuThreads, int MaxSeconds, bool Fence, string[] Routes);
sealed record Sample(long Pts, bool Warmup, double AcquireCallMs, double ReadbackSubmitMs, double MapWaitMs,
    double PrepareCpuOrSubmitMs, double GpuFenceMs, double HardwareMapMs, double EncodeCallToPacketMs,
    double CaptureToPacketMs, double ReleaseMs, double SourceAgeAtAcquireMs, double SourcePresentToPacketMs,
    long SourcePresentTicks, long AcquireTicks, uint AccumulatedDesktopFrames, int Bytes, string ReadbackKind, EncodeTiming EncoderStages);

sealed class NativeTimerResolution : IDisposable
{
    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeEndPeriod(uint period);

    public NativeTimerResolution()
    {
        if (timeBeginPeriod(1) != 0) throw new InvalidOperationException("Could not request 1 ms benchmark timer resolution.");
    }

    public void Dispose()
    {
        var result = timeEndPeriod(1);
        if (result != 0) Console.Error.WriteLine($"timeEndPeriod failed: {result}");
    }
}

static unsafe class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly Route[] Routes =
    [
        new("cpu-rgb", "libx264rgb", "cpu-rgb", "DDA -> CPU map -> borrowed BGR0 -> x264rgb; no application conversion or row copy"),
        new("cpu-yuv", "libx264", "cpu-yuv", "DDA -> CPU map -> threaded BGRA/YUV420P conversion -> x264"),
        new("gpu-nv12-cpu", "libx264", "gpu-download", "DDA -> GPU NV12 conversion -> CPU map -> borrowed NV12 -> x264"),
        new("cpu-nv12-qsv", "h264_qsv", "cpu-nv12", "DDA -> CPU map -> threaded BGRA/NV12 conversion -> QSV software input/upload"),
        new("gpu-nv12-qsv", "h264_qsv", "gpu-nv12", "DDA -> GPU NV12 conversion -> direct QSV hardware mapping -> QSV"),
        new("gpu-nv12-qsv-half", "h264_qsv", "gpu-nv12-half", "DDA -> GPU resize and NV12 conversion to half dimensions -> direct QSV hardware mapping -> QSV"),
        new("gpu-rgb-qsv", "h264_qsv", "gpu-rgb", "DDA original BGRA texture -> QSV; report rejection, no conversion fallback"),
        new("gpu-nv12-nvenc", "h264_nvenc", "gpu-nv12", "DDA -> GPU NV12 conversion -> NVENC on capture device"),
        new("gpu-rgb-nvenc", "h264_nvenc", "gpu-rgb", "DDA original BGRA texture -> NVENC; driver-internal conversion is inside encode timing")
    ];

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.SequenceEqual(new[] { "--help" })) { Help(); return 0; }
            if (args.SequenceEqual(new[] { "--list" }))
            {
                foreach (var route in Routes) Console.WriteLine($"{route.Id,-20} {route.Description}");
                return 0;
            }
            var values = Parse(args);
            var settings = ReadSettings(values);
            if (values.TryGetValue("--worker", out var id)) return Worker(settings, Routes.Single(r => r.Id == id));
            if (!values.ContainsKey("--run")) throw new ArgumentException("Use --run explicitly. --list does not capture or encode.");
            return Run(settings);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Help() => Console.WriteLine("""
        CaptureEncodeBench (.NET 10, Windows x64)
          --list
          --run --ffmpeg <DLL directory> --output <new directory>
            [--routes all|cpu-rgb,cpu-yuv,...] [--display \\.\DISPLAY1]
            [--frames 60] [--warmup 10] [--fps 30] [--bitrate-mbps 5]
            [--max-seconds 15] [--gpu-fence] [--rate-control cbr|quality] [--cpu-threads 4]
        No automatic run without --run. Real native desktop only, no resize or fallback.
        Keep desktop content changing throughout the run; static/locked desktop is reported.
        All routes use H.264, including x264rgb 4:4:4. No network/decode/render inside timing.
        --gpu-fence changes the experiment: conversion waits explicitly before encoding.
        """);

    static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string> { "--run", "--worker", "--ffmpeg", "--output", "--routes", "--display", "--frames", "--warmup", "--fps", "--bitrate-mbps", "--max-seconds", "--gpu-fence", "--rate-control", "--cpu-threads" };
        Dictionary<string, string> result = new();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!allowed.Contains(key) || result.ContainsKey(key)) throw new ArgumentException("Unknown or duplicate argument: " + key);
            if (key is "--run" or "--gpu-fence") result.Add(key, "true");
            else if (++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) result.Add(key, args[i]);
            else throw new ArgumentException("Missing value for " + key);
        }
        if (result.ContainsKey("--run") && result.ContainsKey("--worker")) throw new ArgumentException("--run and --worker are mutually exclusive.");
        return result;
    }

    static Settings ReadSettings(Dictionary<string, string> values)
    {
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Required: " + name);
        int Integer(string key, int fallback, int min, int max)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                throw new ArgumentException($"{key} must be {min}..{max}.");
            return value;
        }
        var native = Path.GetFullPath(Required("--ffmpeg"));
        if (!File.Exists(Path.Combine(native, "avcodec-62.dll"))) throw new FileNotFoundException("FFmpeg 8 libavcodec 62 required.", native);
        var bitrateText = values.GetValueOrDefault("--bitrate-mbps", "5");
        if (!decimal.TryParse(bitrateText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var bitrate) || bitrate is < 0.01m or > 1000m)
            throw new ArgumentException("--bitrate-mbps must be 0.01..1000 using '.' as decimal separator.");
        var selected = values.GetValueOrDefault("--routes", "all");
        var rateControl = values.GetValueOrDefault("--rate-control", "cbr");
        if (rateControl is not ("cbr" or "quality")) throw new ArgumentException("--rate-control must be cbr or quality.");
        var routes = selected == "all" ? Routes.Select(r => r.Id).ToArray() : selected.Split(',');
        if (routes.Length == 0 || routes.Distinct().Count() != routes.Length || routes.Any(id => !Routes.Any(r => r.Id == id)))
            throw new ArgumentException("Invalid or duplicate route. Use --list.");
        if (values.TryGetValue("--worker", out var worker) && !Routes.Any(r => r.Id == worker)) throw new ArgumentException("Unknown worker route.");
        return new(native, Path.GetFullPath(Required("--output")), values.GetValueOrDefault("--display"),
            Integer("--frames", 60, 2, 600), Integer("--warmup", 10, 1, 120), Integer("--fps", 30, 1, 30),
            checked((int)Math.Round(bitrate * 1000)), rateControl, Integer("--cpu-threads", 4, 1, 64),
            Integer("--max-seconds", 15, 1, 120), values.ContainsKey("--gpu-fence"), routes);
    }

    static int Run(Settings settings)
    {
        if (Directory.Exists(settings.Output) && Directory.EnumerateFileSystemEntries(settings.Output).Any())
            throw new IOException("Output directory must be new or empty; previous evidence will not be overwritten.");
        Directory.CreateDirectory(settings.Output);
        var identity = new
        {
            Schema = 2, CreatedUtc = DateTimeOffset.UtcNow, Settings = settings,
            TestPlanSha256 = HashFile(Path.Combine(AppContext.BaseDirectory, "TEST-PLAN.md")),
            ProgramSha256 = HashFile(Assembly.GetExecutingAssembly().Location),
            NativeLibraries = Directory.GetFiles(settings.NativeDirectory, "*.dll").Select(path => new { File = Path.GetFileName(path), Sha256 = HashFile(path) }).ToArray(),
            Matrix = Routes.Where(r => settings.Routes.Contains(r.Id)).ToArray(),
            Scope = "Live native desktop; capture call to complete encoded packet. No capture wait, network, decoding or presentation in this metric.",
            ComparisonLimit = "Routes run sequentially against a changing desktop, not identical source frames. No automatic fastest/hardware-limit conclusion.",
            Excluded = new[] { "grayscale", "low bit depth", "resizing except the dedicated half-size QSV route", "partial-frame output/network overlap", "cross-adapter texture copies" },
            QsvCompatibility = "GPU QSV workers use a documented public-loader initialization workaround, limited to one open per disposable process. Not a production lifetime fix."
        };
        Save(Path.Combine(settings.Output, "manifest.json"), identity);
        List<object> results = new();
        var failed = false;
        foreach (var route in Routes.Where(r => settings.Routes.Contains(r.Id)))
        {
            Console.WriteLine($"[{route.Id}] {route.Description}");
            var directory = Path.Combine(settings.Output, route.Id); Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            var arguments = new[] { "--worker", route.Id, "--ffmpeg", settings.NativeDirectory, "--output", directory,
                "--frames", settings.Frames.ToString(), "--warmup", settings.Warmup.ToString(), "--fps", settings.Fps.ToString(),
                "--bitrate-mbps", (settings.BitrateKbps / 1000m).ToString(CultureInfo.InvariantCulture),
                "--rate-control", settings.RateControl, "--cpu-threads", settings.CpuThreads.ToString(),
                "--max-seconds", settings.MaxSeconds.ToString() };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (settings.Display != null) { start.ArgumentList.Add("--display"); start.ArgumentList.Add(settings.Display); }
            if (settings.Fence) start.ArgumentList.Add("--gpu-fence");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated route worker.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var timeout = false;
            Console.CancelKeyPress += onCancel;
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!process.WaitForExit(200))
                {
                    timeout = deadline.Elapsed.TotalSeconds > settings.MaxSeconds + 60;
                    if (timeout || cancellation.IsCancellationRequested) break;
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
            }
            File.WriteAllText(Path.Combine(directory, "stdout.txt"), stdout.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(directory, "stderr.txt"), stderr.GetAwaiter().GetResult());
            var path = Path.Combine(directory, "report.json");
            if (!File.Exists(path)) Save(path, new { Schema = 1, Route = route, Status = cancellation.IsCancellationRequested ? "cancelled" : timeout ? "worker-timeout" : "worker-crashed", ExitCode = process.ExitCode, Samples = Array.Empty<Sample>() });
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var status = document.RootElement.GetProperty("Status").GetString();
            results.Add(document.RootElement.Clone());
            failed |= status != "completed" && status != "codec-not-in-build";
            Console.WriteLine($"[{route.Id}] {status}; {path}");
            Save(Path.Combine(settings.Output, "results.json"), results);
            if (cancellation.IsCancellationRequested) { failed = true; break; }
        }
        File.WriteAllText(Path.Combine(settings.Output, "summary.md"), Summary(results));
        Console.WriteLine("Report: " + Path.Combine(settings.Output, "summary.md"));
        return failed ? 2 : 0;
    }

    static int Worker(Settings settings, Route route)
    {
        Directory.CreateDirectory(settings.Output);
        var report = new Dictionary<string, object?>
        {
            ["Schema"] = 2, ["Route"] = route, ["Settings"] = settings, ["Status"] = "initializing",
            ["LatencyFloorAssessment"] = "not-assessed",
            ["CompletionMeaning"] = "completed means this sample run decoded correctly, not that a latency floor has been established.",
            ["TestPlanSha256"] = HashFile(Path.Combine(AppContext.BaseDirectory, "TEST-PLAN.md"))
        };
        List<Sample> samples = new(); List<CodecPacket> packets = new(); Dictionary<long, byte[]> references = new();
        report["Samples"] = samples;
        DesktopSource? desktop = null; GpuInput? gpu = null; CpuInput? cpu = null; BenchEncoder? encoder = null;
        var initialized = false;
        try
        {
            using var timerResolution = new NativeTimerResolution();
            FfmpegRuntime.Initialize(settings.NativeDirectory);
            report["Ffmpeg"] = FfmpegRuntime.Version;
            if (!FfmpegRuntime.HasEncoder(route.Codec)) { report["Status"] = "codec-not-in-build"; return 0; }
            var initialization = Stopwatch.GetTimestamp();
            desktop = new(settings.Display);
            report["Desktop"] = new { desktop.AdapterName, desktop.DisplayName, desktop.Width, desktop.Height, desktop.SystemMemory };
            var scaled = route.Input == "gpu-nv12-half";
            var encodedWidth = scaled ? desktop.Width / 2 & ~1 : desktop.Width;
            var encodedHeight = scaled ? desktop.Height / 2 & ~1 : desktop.Height;
            var gpuRoute = route.Input.StartsWith("gpu-", StringComparison.Ordinal);
            var gpuHardware = gpuRoute && route.Input != "gpu-download";
            if (gpuRoute) gpu = new(desktop, route.Input == "gpu-rgb", gpuHardware && route.Codec == "h264_qsv", settings.Fence, encodedWidth, encodedHeight);
            var format = route.Input == "cpu-rgb" ? AVPixelFormat.AV_PIX_FMT_BGR0 : route.Input == "cpu-yuv" ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
            if (!gpuHardware) cpu = new(desktop.Width, desktop.Height, format);
            encoder = new(route.Codec, gpuHardware ? gpu!.EncoderFormat : format, encodedWidth, encodedHeight, settings.Fps, settings.BitrateKbps,
                settings.RateControl, settings.CpuThreads, gpuHardware ? gpu!.EncoderPool : null);
            report["EncoderArguments"] = encoder.Arguments;
            report["EncoderSessionMode"] = encoder.SessionMode;
            report["ConversionThreads"] = cpu?.ConversionThreads;
            report["InitializationMs"] = Stopwatch.GetElapsedTime(initialization).TotalMilliseconds;
            report["TimingMeaning"] = settings.Fence
                ? "GPU fence explicitly waits before encode; this diagnostic run is distinct from the no-fence latency path."
                : "GPU prepare is CPU submission time. EncodeCallToPacket includes pending GPU work, driver waits and packet copy; it is not pure hardware encode time.";
            initialized = true;
            var watch = Stopwatch.StartNew(); var next = Stopwatch.GetTimestamp(); var interval = Stopwatch.Frequency / (double)settings.Fps;
            var empty = 0; var captureSeconds = 0d;
            var allocated = GC.GetTotalAllocatedBytes(true); var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            while (samples.Count < settings.Warmup + settings.Frames && watch.Elapsed.TotalSeconds < settings.MaxSeconds)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < next) { Thread.Sleep(Math.Max(1, (int)((next - now) * 1000 / Stopwatch.Frequency))); continue; }
                var start = Stopwatch.GetTimestamp();
                if (!desktop.Acquire()) { empty++; Thread.Sleep(1); continue; }
                var acquired = Stopwatch.GetTimestamp(); var prepare = acquired;
                next += (long)interval;
                if (next <= acquired) next = acquired + (long)interval;
                AVFrame* input;
                if (gpu != null)
                {
                    input = gpu.Prepare();
                    if (cpu != null)
                    {
                        using var texture = gpu.GetTexture(); var mapped = desktop.MapNv12(texture);
                        input = cpu.PrepareNv12(mapped.Pixels, mapped.Stride, gpu.SurfaceHeight);
                    }
                }
                else
                {
                    var mapped = desktop.MapBgra(); input = cpu!.Prepare(mapped.Pixels, mapped.Stride);
                }
                var encodeStarted = Stopwatch.GetTimestamp(); var packet = encoder.Encode(input, samples.Count);
                var finished = Stopwatch.GetTimestamp();
                captureSeconds = watch.Elapsed.TotalSeconds;
                var acquireMs = Stopwatch.GetElapsedTime(start, acquired).TotalMilliseconds;
                var prepareMs = Stopwatch.GetElapsedTime(prepare, encodeStarted).TotalMilliseconds;
                var readbackSubmit = desktop.ReadbackSubmitMs; var mapWait = desktop.MapWaitMs; var readback = desktop.ReadbackKind;
                var cleanup = Stopwatch.GetTimestamp(); cpu?.ReleaseBorrowed(); desktop.Unmap();
                var releaseMs = Stopwatch.GetElapsedTime(cleanup).TotalMilliseconds;
                if (!scaled && samples.Count == settings.Warmup + settings.Frames - 1)
                    references.Add(packet.Pts, desktop.CopyReference());
                cleanup = Stopwatch.GetTimestamp(); desktop.Release();
                releaseMs += Stopwatch.GetElapsedTime(cleanup).TotalMilliseconds;
                samples.Add(new(packet.Pts, samples.Count < settings.Warmup, acquireMs, readbackSubmit, mapWait,
                    Math.Max(0, prepareMs - readbackSubmit - mapWait - (gpu?.FenceMs ?? 0) - (gpu?.MapMs ?? 0)),
                    gpu?.FenceMs ?? 0, gpu?.MapMs ?? 0, Stopwatch.GetElapsedTime(encodeStarted, finished).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(start, finished).TotalMilliseconds, releaseMs,
                    Stopwatch.GetElapsedTime(desktop.LastPresentTime, acquired).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(desktop.LastPresentTime, finished).TotalMilliseconds,
                    desktop.LastPresentTime, acquired, desktop.AccumulatedFrames, packet.Data.Length, readback, encoder.LastTiming));
                packets.Add(packet);
            }
            watch.Stop();
            var width = encodedWidth; var height = encodedHeight;
            report["NoFreshImageAttempts"] = empty;
            report["CaptureWindowSeconds"] = captureSeconds;
            report["LoopWallSecondsIncludingFinalReference"] = watch.Elapsed.TotalSeconds;
            report["ManagedAllocatedBytesIncludingFinalReference"] = GC.GetTotalAllocatedBytes(true) - allocated;
            report["GcCollectionsIncludingFinalReference"] = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
            // Stop all encoder/GPU activity before CPU decoding and quality analysis.
            encoder.Dispose(); encoder = null; cpu?.Dispose(); cpu = null; gpu?.Dispose(); gpu = null; desktop.Dispose(); desktop = null;
            var measured = samples.Where(s => !s.Warmup).ToArray();
            report["MeasuredFrames"] = measured.Length;
            report["MeasuredCaptureFps"] = measured.Length > 1
                ? (measured.Length - 1d) * Stopwatch.Frequency / (measured[^1].AcquireTicks - measured[0].AcquireTicks) : null;
            report["MeasuredSourcePresentFps"] = measured.Length > 1
                ? (measured.Length - 1d) * Stopwatch.Frequency / (measured[^1].SourcePresentTicks - measured[0].SourcePresentTicks) : null;
            report["CaptureToPacketMs"] = Statistics(measured.Select(s => s.CaptureToPacketMs));
            report["EncodeCallToPacketMs"] = Statistics(measured.Select(s => s.EncodeCallToPacketMs));
            report["EncoderStages"] = new
            {
                SendFrameMs = Statistics(measured.Select(s => s.EncoderStages.SendFrameMs)),
                ReceivePacketMs = Statistics(measured.Select(s => s.EncoderStages.ReceivePacketMs)),
                PacketCopyMs = Statistics(measured.Select(s => s.EncoderStages.PacketCopyMs)),
                QueueCheckMs = Statistics(measured.Select(s => s.EncoderStages.QueueCheckMs)),
                OtherMeasuredCallMs = Statistics(measured.Select(s => s.EncodeCallToPacketMs - s.EncoderStages.SendFrameMs -
                    s.EncoderStages.ReceivePacketMs - s.EncoderStages.PacketCopyMs - s.EncoderStages.QueueCheckMs)),
                Meaning = "Host API boundaries only: send_frame may encode synchronously; receive_packet is not necessarily the hardware encoding duration."
            };
            report["SourcePresentToPacketMs"] = Statistics(measured.Select(s => s.SourcePresentToPacketMs));
            report["FirstFrameCaptureToPacketMs"] = samples.FirstOrDefault()?.CaptureToPacketMs;
            report["PayloadMbpsAtNominalFps"] = measured.Length == 0 ? null : measured.Sum(s => (long)s.Bytes) * 8d * settings.Fps / measured.Length / 1_000_000;
            report["PayloadMbpsDuringCaptureWindow"] = captureSeconds > 0 ? packets.Sum(p => (long)p.Data.Length) * 8d / captureSeconds / 1_000_000 : (double?)null;
            report["Status"] = "validating";
            using (var bitstream = File.Create(Path.Combine(settings.Output, "encoded.h264")))
                foreach (var encoded in packets) bitstream.Write(encoded.Data);
            report["Validation"] = Validate(packets, references, width, height, settings.Output);
            report["Status"] = measured.Length == settings.Frames ? "completed" : "insufficient-fresh-frames";
            return measured.Length == settings.Frames ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            report["Status"] = Equals(report["Status"], "validating") ? "validation-failed" : initialized ? "failed" : "initialization-failed";
            report["Error"] = error.ToString(); return 2;
        }
        finally
        {
            try { encoder?.Dispose(); cpu?.Dispose(); gpu?.Dispose(); desktop?.Dispose(); }
            catch (Exception error) { Console.Error.WriteLine("Cleanup failed: " + error); report["Status"] = "cleanup-failed"; report["CleanupError"] = error.ToString(); }
            Save(Path.Combine(settings.Output, "report.json"), report);
        }
    }

    static object Validate(List<CodecPacket> packets, Dictionary<long, byte[]> references, int width, int height, string directory)
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLog(string message)
        {
            if (message.Contains("[h264 @", StringComparison.OrdinalIgnoreCase) &&
                new[] { "error", "conceal", "corrupt", "invalid", "damaged" }.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase)))
                errors.Enqueue(message.Trim());
        }
        FfmpegRuntime.Log += OnLog;
        try { return ValidateCore(packets, references, width, height, directory, errors); }
        finally { FfmpegRuntime.Log -= OnLog; }
    }

    static object ValidateCore(List<CodecPacket> packets, Dictionary<long, byte[]> references, int width, int height, string directory,
        System.Collections.Concurrent.ConcurrentQueue<string> errors)
    {
        using var decoder = new FfmpegDecoder("-c:v h264 -err_detect explode");
        List<object> comparisons = new(); HashSet<long> seen = new(); HashSet<ulong> decodedPatterns = new();
        foreach (var packet in packets)
        {
            var frames = decoder.Decode(packet.Data, packet.Pts);
            if (!errors.IsEmpty) throw new InvalidDataException("Decoder reported corruption or concealment: " + string.Join(" | ", errors));
            if (frames.Count != 1 || frames[0].Pts != packet.Pts || !seen.Add(packet.Pts))
                throw new InvalidDataException("Decoded output must match each input PTS exactly once without buffering.");
            var frame = frames[0];
            if (frame.Width != width || frame.Height != height || frame.Bgra.Length != checked(width * height * 4))
                throw new InvalidDataException("Decoded dimensions differ from the selected encoded output.");
            decodedPatterns.Add(Fingerprint(frame.Bgra));
            if (!references.TryGetValue(packet.Pts, out var original)) continue;
            if (frame.Bgra.Length != original.Length || frame.Width * frame.Height * 4 != original.Length)
                throw new InvalidDataException("Decoded dimensions differ from the native source.");
            double squared = 0, absolute = 0;
            for (var i = 0; i < original.Length; i += 4)
                for (var c = 0; c < 3; c++) { var delta = (double)frame.Bgra[i + c] - original[i + c]; squared += delta * delta; absolute += Math.Abs(delta); }
            var components = original.Length / 4 * 3d; var mse = squared / components;
            comparisons.Add(new { packet.Pts, frame.Width, frame.Height, MeanAbsoluteRgbError = absolute / components,
                RgbPsnrDb = mse == 0 ? (double?)null : 10 * Math.Log10(255d * 255 / mse), ExactRgbMatch = mse == 0,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(original)), DecodedSha256 = Convert.ToHexString(SHA256.HashData(frame.Bgra)) });
            WriteBmp(Path.Combine(directory, $"source-{packet.Pts}.bmp"), original, frame.Width, frame.Height);
            WriteBmp(Path.Combine(directory, $"decoded-{packet.Pts}.bmp"), frame.Bgra, frame.Width, frame.Height);
        }
        if (references.Count == 0 && decodedPatterns.Count < 2)
            throw new InvalidDataException("Decoded scaled video did not change under the full-motion stimulus.");
        return new { Frames = seen.Count, PacketCount = packets.Count, DecodedPatterns = decodedPatterns.Count, ComparedSamples = comparisons,
            Meaning = "All packets decoded after sampling; sample RGB error and BMPs assess content/quality, not an automatic acceptable-quality verdict." };
    }

    static ulong Fingerprint(byte[] bytes)
    {
        var hash = 14695981039346656037ul;
        var step = Math.Max(1, bytes.Length / 4096);
        for (var i = 0; i < bytes.Length; i += step) hash = (hash ^ bytes[i]) * 1099511628211ul;
        return hash;
    }

    static object? Statistics(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : new { Mean = sorted.Average(), P50 = sorted[(sorted.Length - 1) / 2],
            P95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)], Max = sorted[^1], Count = sorted.Length };
    }

    static string Summary(List<object> results)
    {
        var text = new StringBuilder("# Capture + encode benchmark\n\nLive native desktop; rows do not necessarily contain identical input content. No hardware-limit or fastest-path claim.\n\n| Route | Status | Mean ms | P95 ms |\n|---|---|---:|---:|\n");
        foreach (JsonElement r in results)
        {
            var mean = "—"; var p95 = "—";
            if (r.TryGetProperty("CaptureToPacketMs", out var stats) && stats.ValueKind == JsonValueKind.Object)
            { mean = stats.GetProperty("Mean").GetDouble().ToString("F3", CultureInfo.InvariantCulture); p95 = stats.GetProperty("P95").GetDouble().ToString("F3", CultureInfo.InvariantCulture); }
            text.AppendLine($"| {r.GetProperty("Route").GetProperty("Id").GetString()} | {r.GetProperty("Status").GetString()} | {mean} | {p95} |");
        }
        text.AppendLine("\nSee each report.json for per-frame stages, warmup, source age, queue accumulation, codec arguments, bitrate, validation and errors. stderr.txt contains original FFmpeg/driver diagnostics. BMPs contain desktop content.\n");
        text.AppendLine("completed = one validated sampling run. LatencyFloorAssessment remains not-assessed until the documented engineering acceptance criteria are separately evaluated.");
        return text.ToString();
    }

    static void WriteBmp(string path, byte[] pixels, int width, int height)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0); writer.Write(pixels);
    }

    static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
