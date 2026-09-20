using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;
using Frd;

namespace Frd.DesktopProbe;

record Candidate(string Id, string Encoder, string Decoder);
record ProbeResult(string Id, bool Passed, double MeanMs, double P95Ms, double DecodeMeanMs, double PsnrDb,
    int Frames, double Mbps, string? Error = null);
record RunSettings(string Directory, string NativeDirectory, Candidate Candidate, int VideoPort, int DiagnosticPort,
    int OwnerPid, long OwnerStartedUtcTicks, int SessionId, int Seconds = 1200, int Width = 1280, int Height = 720,
    int Fps = 30, int BitrateKbps = 5000);
record FrameStamp(long Id, int Generation, string Desktop, bool NewImage, long Started, long Captured, long Encoded, string Trial);

static class Program
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Output { get; set; } = "";
    public static int AutoCloseSeconds { get; set; }
    public static bool SecureOnStart { get; set; }
    public static bool SenderOnly { get; set; }
    public static bool GpuSenderOnly { get; set; }
    public static bool RecordFixture { get; set; }
    public static bool NativeSender { get; set; }
    public static string NativeDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "DesktopProbe.exe");

    [STAThread]
    static int Main(string[] args)
    {
        SetErrorMode(0x8003);
        try
        {
            if (args is ["--service", var directory]) { Privilege.RunService(directory); return 0; }
            if (args is ["--elevate", var elevateDirectory]) { Privilege.InstallAndStart(elevateDirectory); return 0; }
            if (args is ["--broker", var brokerDirectory]) { Privilege.RunBroker(brokerDirectory); return 0; }
            if (args is ["--worker", var workerDirectory]) { CaptureWorker.Run(workerDirectory); return 0; }
            if (args is ["--case", var caseDirectory, var candidateId]) { CodecProbe.RunCase(caseDirectory, candidateId); return 0; }
            if (args is ["--uac-check"]) { Thread.Sleep(1000); return 0; }
            Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            AutoCloseSeconds = args.Length > 1 && int.TryParse(args[1], out var seconds) ? seconds : 0;
            SecureOnStart = args.Contains("--secure");
            SenderOnly = args.Contains("--sender");
            GpuSenderOnly = args.Contains("--gpu-sender");
            RecordFixture = args.Contains("--record-fixture");
            NativeSender = args.Contains("--native-sender");
            SenderOnly |= GpuSenderOnly || RecordFixture || NativeSender;
            Directory.CreateDirectory(Output);
            AppBuilder.Configure<ProbeApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
            return Environment.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static void Save<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static bool OwnerAlive(RunSettings settings)
    {
        try { using var process = Process.GetProcessById(settings.OwnerPid); return process.StartTime.ToUniversalTime().Ticks == settings.OwnerStartedUtcTicks && !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception error) { Console.Error.WriteLine("Owner state: " + error); return false; }
    }
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
}

sealed class ProbeApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = Program.SenderOnly ? new SenderProbe() : new ProbeWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

static class CodecProbe
{
    public static Candidate[] Candidates =
    [
        new("h264_fast", "-c:v libx264 -preset ultrafast -tune zerolatency -bf 0 -g 300 -threads 4 -bufsize 167k", "-c:v h264 -threads 1"),
        new("h264_qsv", "-c:v h264_qsv -preset veryfast -async_depth 1 -bf 0 -g 300 -pix_fmt nv12 -bufsize 167k -look_ahead 0 -forced_idr 1", "-c:v h264 -threads 1"),
        new("h264_qsv_d3d11", "-c:v h264_qsv -preset veryfast -async_depth 1 -bf 0 -g 300 -pix_fmt nv12 -bufsize 167k -look_ahead 0 -forced_idr 1", "-c:v h264 -hwaccel d3d11va -hwaccel_output_format d3d11 -threads 1"),
        new("hevc_qsv", "-c:v hevc_qsv -preset veryfast -async_depth 1 -bf 0 -g 300 -pix_fmt nv12 -bufsize 167k -forced_idr 1", "-c:v hevc -threads 1"),
        new("av1_qsv", "-c:v av1_qsv -preset veryfast -async_depth 1 -g 300 -pix_fmt nv12 -bufsize 167k", "-c:v libdav1d -threads 1"),
        new("h264_nvenc", "-c:v h264_nvenc -preset p1 -tune ull -rc vbr -bf 0 -rc-lookahead 0 -zerolatency 1 -delay 0 -g 300 -bufsize 167k", "-c:v h264 -threads 1"),
        new("hevc_nvenc", "-c:v hevc_nvenc -preset p1 -tune ull -rc vbr -bf 0 -rc-lookahead 0 -zerolatency 1 -delay 0 -g 300 -bufsize 167k", "-c:v hevc -threads 1"),
        new("av1_nvenc", "-c:v av1_nvenc -preset p1 -tune ull -rc vbr -rc-lookahead 0 -zerolatency 1 -delay 0 -g 300 -bufsize 167k", "-c:v libdav1d -threads 1"),
        new("h264_amf", "-c:v h264_amf -usage ultralowlatency -quality speed -bf 0 -g 300 -bufsize 167k", "-c:v h264 -threads 1")
    ];

    public static async Task<Candidate> FindFastest(string directory, Action<string> progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using (var capture = new DxgiDesktopCapture(1280, 720))
            File.WriteAllBytes(Path.Combine(directory, "source.bgra"), capture.Capture());
        List<ProbeResult> results = new();
        foreach (var candidate in Candidates)
        {
            token.ThrowIfCancellationRequested();
            progress("正在实测 " + candidate.Id);
            var info = Program.StartInfo("--case", directory, candidate.Id);
            info.RedirectStandardError = true; info.RedirectStandardOutput = true;
            using var child = Process.Start(info) ?? throw new IOException("Cannot start isolated codec probe");
            var errors = child.StandardError.ReadToEndAsync();
            var output = child.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(25000);
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                child.Kill(true); await child.WaitForExitAsync();
                Console.Error.WriteLine(candidate.Id + " timed out or was cancelled");
                if (token.IsCancellationRequested) throw;
            }
            File.WriteAllText(Path.Combine(directory, candidate.Id + ".stderr.log"), await errors);
            File.WriteAllText(Path.Combine(directory, candidate.Id + ".stdout.log"), await output);
            var path = Path.Combine(directory, candidate.Id + ".json");
            var result = File.Exists(path) ? Program.Read<ProbeResult>(path) : new(candidate.Id, false, 0, 0, 0, 0, 0, 0, "Native initialization failed or timed out; see stderr");
            results.Add(result); Program.Save(Path.Combine(directory, "ranking.json"), results);
        }
        var best = results.Where(result => result.Passed).OrderBy(result => result.MeanMs).FirstOrDefault()
            ?? throw new InvalidOperationException("No codec passed the real encode/decode probe; inspect logs.");
        return Candidates.Single(candidate => candidate.Id == best.Id);
    }

    public static void RunCase(string directory, string id)
    {
        var candidate = Candidates.Single(candidate => candidate.Id == id);
        ProbeResult result;
        try
        {
            FfmpegRuntime.Initialize(Program.NativeDirectory);
            const int width = 1280, height = 720, warmup = 8, count = 36;
            var source = File.ReadAllBytes(Path.Combine(directory, "source.bgra"));
            if (source.Length != width * height * 4) throw new InvalidDataException("Capture fixture size");
            var images = Enumerable.Range(0, 8).Select(phase => Shift(source, width, height, phase * 5)).ToArray();
            using var encoder = new FfmpegEncoder(candidate.Encoder, width, height, 30, 5000);
            using var decoder = new FfmpegDecoder(candidate.Decoder);
            Dictionary<long, long> starts = new();
            HashSet<long> decodedIds = new();
            List<double> times = new(), decodeTimes = new();
            double error = 0;
            long bytes = 0;
            var decodedCount = 0;
            for (var idFrame = 0; idFrame < warmup + count; idFrame++)
            {
                var started = Stopwatch.GetTimestamp(); starts[idFrame] = started;
                var packets = encoder.Encode(images[idFrame % 8], idFrame, idFrame == 0);
                var encoded = Stopwatch.GetTimestamp();
                foreach (var packet in packets)
                {
                    if (packet.Pts >= warmup)
                    {
                        times.Add(Stopwatch.GetElapsedTime(starts[packet.Pts], encoded).TotalMilliseconds); bytes += packet.Data.Length;
                    }
                    var decodeStart = Stopwatch.GetTimestamp();
                    var decoded = decoder.Decode(packet.Data, packet.Pts);
                    var decodeMs = Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
                    foreach (var pixels in decoded)
                    {
                        if (!decodedIds.Add(pixels.Pts) || pixels.Width != width || pixels.Height != height) throw new InvalidDataException("Decoded frame identity/dimensions");
                        if (pixels.Pts < warmup) continue;
                        decodedCount++; decodeTimes.Add(decodeMs);
                        var original = images[(int)pixels.Pts % 8];
                        for (var pixel = 0; pixel < pixels.Bgra.Length; pixel += 64)
                        {
                            var delta = (.0722 * pixels.Bgra[pixel] + .7152 * pixels.Bgra[pixel + 1] + .2126 * pixels.Bgra[pixel + 2]) -
                                (.0722 * original[pixel] + .7152 * original[pixel + 1] + .2126 * original[pixel + 2]);
                            error += delta * delta;
                        }
                    }
                }
                var remaining = 1000d / 30 - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining > 1) Thread.Sleep((int)remaining);
            }
            if (times.Count != count || decodedCount != count) throw new InvalidDataException($"Delayed/missing outputs: {times.Count}/{decodedCount}/{count}");
            var psnr = error == 0 ? 100 : 10 * Math.Log10(255d * 255 * count * (source.Length / 64) / error);
            var rate = bytes * 8d / (count / 30d) / 1e6;
            var passed = psnr >= 20 && bytes * 8 <= 5_000_000 * (count / 30d) + 334_000;
            result = new(id, passed, times.Average(), times.Order().ElementAt((int)Math.Ceiling(count * .95) - 1),
                decodeTimes.Average(), psnr, count, rate, passed ? null : "Pixel quality or bitrate sanity check failed");
        }
        catch (Exception error) { Console.Error.WriteLine(error); result = new(id, false, 0, 0, 0, 0, 0, 0, error.Message); }
        Program.Save(Path.Combine(directory, id + ".json"), result);
    }

    static byte[] Shift(byte[] source, int width, int height, int rows)
    {
        var target = new byte[source.Length];
        var split = rows % height * width * 4;
        source.AsSpan(split).CopyTo(target); source.AsSpan(0, split).CopyTo(target.AsSpan(source.Length - split));
        return target;
    }
}
