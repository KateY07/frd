using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Frd;

namespace Frd.InputVisualLatency;

static class Program
{
    public static Options Settings = null!;
    public static int ExitCode = 1;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args is ["--target", "--state", var path]) { Target.Run(Path.GetFullPath(path)); return 0; }
            Settings = Options.Parse(args);
            AppBuilder.Configure<TestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            return ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

sealed record Options(string Config, string StatePath, string Report, int Port, string Preset, int Bitrate, double Scale, int Count, int Interval)
{
    public static Options Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Expected --config PATH --target-state PATH --report PATH --port PORT [--preset h264_fast --bitrate 5000 --scale 1 --count 24 --interval 170]. FRD_TEST_TOKEN supplies the localhost host password. Target mode: --target --state PATH.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--config" or "--target-state" or "--report" or "--port" or "--preset" or "--bitrate" or "--scale" or "--count" or "--interval")) throw new ArgumentException("Unknown option: " + args[i]);
            values.Add(args[i], args[i + 1]);
        }
        string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required: " + key);
        var options = new Options(Path.GetFullPath(Required("--config")), Path.GetFullPath(Required("--target-state")), Path.GetFullPath(Required("--report")),
            int.Parse(Required("--port")), values.GetValueOrDefault("--preset", "h264_fast"), int.Parse(values.GetValueOrDefault("--bitrate", "5000")),
            double.Parse(values.GetValueOrDefault("--scale", "1"), System.Globalization.CultureInfo.InvariantCulture), int.Parse(values.GetValueOrDefault("--count", "24")), int.Parse(values.GetValueOrDefault("--interval", "170")));
        if (options.Port is < 1 or > 65535 || options.Count is < 2 or > 240 || options.Interval is < 40 or > 5000) throw new ArgumentOutOfRangeException(nameof(args));
        TransmissionGeometry.ValidateScale(options.Scale);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FRD_TEST_TOKEN"))) throw new ArgumentException("FRD_TEST_TOKEN is required; the password is never written to the report.");
        return options;
    }
}

sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.MainWindow = new TestWindow(Program.Settings);
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class TestWindow : Window
{
    readonly Options options;
    readonly TargetState target;
    readonly ConfirmedVideoView view = new();
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<Measurement> measurements = new();
    readonly List<string> errors = new();
    DemoSession? session;
    SessionStatus? latestStatus;
    Sample? active;
    bool closing, completed;
    int decodedMarkers;

    public TestWindow(Options options)
    {
        this.options = options;
        target = JsonSerializer.Deserialize<TargetState>(File.ReadAllText(options.StatePath), AppConfiguration.JsonOptions) ?? throw new InvalidDataException("Target state is empty.");
        Target.Validate(target);
        Title = "FRD 画面反馈延迟测试（不注入键鼠，完成后关闭）";
        Width = 460; Height = 290; CanResize = true; ShowActivated = false;
        Position = new PixelPoint(target.SourceLeft + target.SourceWidth - 560, target.SourceTop + target.SourceHeight - 380);
        Content = view;
        view.Presented += Presented;
        view.Failed += Error;
        Opened += async (_, _) => await Run();
        Closing += (_, e) =>
        {
            if (completed) return;
            e.Cancel = true;
            stop.Cancel();
        };
    }

    async Task Run()
    {
        try
        {
            Target.ExcludePreview(TryGetPlatformHandle()?.Handle ?? 0);
            Target.Validate(target);
            Target.Stimulate(target, 0);
            session = new DemoSession(AppConfiguration.Load(options.Config), new RemoteOptions("127.0.0.1", options.Port, Environment.GetEnvironmentVariable("FRD_TEST_TOKEN")!));
            session.FrameReceived += Frame;
            session.Failed += Error;
            session.StatusChanged += value => Volatile.Write(ref latestStatus, value);
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            var applied = await session.ApplyAsync(options.Preset, options.Bitrate, options.Scale).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            if (!applied.Success) throw new InvalidOperationException(applied.Message);
            await initial.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            await Task.Delay(350, stop.Token);
            for (var id = 1; id <= options.Count; id++)
            {
                Target.Validate(target);
                var sample = new Sample(id);
                lock (gate) { active = sample; sample.Started = Stopwatch.GetTimestamp(); }
                sample.TargetCompleted = Target.Stimulate(target, id);
                sample.Acknowledged = Stopwatch.GetTimestamp();
                await sample.Done.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
                var result = new Measurement(id, Ms(sample.TargetCompleted - sample.Started), Ms(sample.Acknowledged - sample.Started),
                    Ms(sample.Decoded - sample.Started), Ms(sample.Rendered - sample.Started), sample.MatchingFrames, sample.DecodedPts, sample.RenderedPts, Volatile.Read(ref latestStatus));
                if (result.TargetMessageMs < 0 || result.DecodeMs < result.TargetMessageMs || result.GpuMs < result.DecodeMs) throw new InvalidDataException("Timing or marker association is invalid.");
                measurements.Add(result);
                Console.WriteLine($"{id:D2}: message={result.MessageAckMs:F2} ms, decode={result.DecodeMs:F2} ms, GPU={result.GpuMs:F2} ms");
                lock (gate) active = null;
                await Task.Delay(options.Interval + id * 13 % 83, stop.Token);
            }
            Program.ExitCode = 0;
        }
        catch (Exception error) { Error(error); }
        finally
        {
            closing = true;
            if (session != null)
            {
                session.FrameReceived -= Frame;
                try { await session.StopAsync(); session.Dispose(); }
                catch (Exception error) { Error(error); }
            }
            try { view.Stop(); }
            catch (Exception error) { Error(error); }
            lock (gate)
            {
                if (errors.Count != 0 || measurements.Count != options.Count) Program.ExitCode = 1;
                Directory.CreateDirectory(Path.GetDirectoryName(options.Report)!);
                File.WriteAllText(options.Report, JsonSerializer.Serialize(new
                {
                    Passed = Program.ExitCode == 0, Scope = "Localhost targeted WM_APP stimulus → target GDI paint → real desktop capture → FFmpeg encode → UDP → decode → D3D11 GPU completion. No SendInput, physical mouse/keyboard, cursor movement, focus changes, production UI input collection/queue, or physical scan-out measurement.",
                    Clock = "All stimulus, target message, decoder and GPU timestamps use this machine's Stopwatch clock.", PreviewExcludedFromCapture = true,
                    StageTimingNote = "Each measurement includes the latest production SessionStatus (updated independently; rolling 1-second capture/encode/transfer/decode/render means and FPS). These aid attribution but are not an exact per-marker decomposition; capture scheduling before CaptureTick is excluded from those stages.",
                    options.Preset, options.Bitrate, options.Scale, options.Count, options.Interval,
                    Source = new { target.SourceWidth, target.SourceHeight, target.Window },
                    decodedMarkers, Measurements = measurements,
                    Summary = new { TargetMessage = Summary(measurements.Select(x => x.TargetMessageMs)), MessageAck = Summary(measurements.Select(x => x.MessageAckMs)),
                        Decode = Summary(measurements.Select(x => x.DecodeMs)), Gpu = Summary(measurements.Select(x => x.GpuMs)) }, Errors = errors.ToArray()
                }, AppConfiguration.JsonOptions));
            }
            completed = true;
            Close();
        }
    }

    void Frame(DecodedPixels frame)
    {
        if (closing) return;
        var decoded = Stopwatch.GetTimestamp();
        var id = Marker.Read(frame, target);
        if (id >= 0)
        {
            Interlocked.Increment(ref decodedMarkers);
            if (id == 0) initial.TrySetResult();
            lock (gate)
            {
                if (active is { } sample && sample.Started > 0 && id == sample.Id && decoded >= sample.Started)
                {
                    if (sample.Decoded == 0) { sample.Decoded = decoded; sample.DecodedPts = frame.Pts; }
                    sample.Pts.Add(frame.Pts); sample.MatchingFrames++;
                }
            }
        }
        view.Submit(frame);
    }

    void Presented(long pts, long tick)
    {
        session?.ReportPresented(pts, tick);
        lock (gate)
        {
            if (active is not { } sample || !sample.Pts.Contains(pts) || sample.Rendered != 0) return;
            sample.Rendered = tick; sample.RenderedPts = pts;
            sample.Done.TrySetResult();
        }
    }

    void Error(Exception error)
    {
        Console.Error.WriteLine(error);
        lock (gate) errors.Add(error.ToString());
        Program.ExitCode = 1;
        stop.Cancel();
    }

    static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    static object Summary(IEnumerable<double> source)
    {
        var data = source.Order().ToArray();
        return new { Count = data.Length, MeanMs = data.Length == 0 ? 0 : data.Average(), P50Ms = Percentile(.5), P95Ms = Percentile(.95), MaximumMs = data.LastOrDefault() };
        double Percentile(double fraction) => data.Length == 0 ? 0 : data[Math.Clamp((int)Math.Ceiling(data.Length * fraction) - 1, 0, data.Length - 1)];
    }

    sealed class Sample(int id)
    {
        public int Id = id, MatchingFrames;
        public long Started, TargetCompleted, Acknowledged, Decoded, Rendered, DecodedPts, RenderedPts;
        public HashSet<long> Pts = new();
        public TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    sealed record Measurement(int Id, double TargetMessageMs, double MessageAckMs, double DecodeMs, double GpuMs, int MatchingFrames, long DecodedPts, long RenderedPts, SessionStatus? Status);
}

static class Marker
{
    public const int Left = 24, Top = 56, Cell = 48, Cells = 10, Height = 128;

    public static int Read(DecodedPixels image, TargetState target)
    {
        double CellMean(int cell)
        {
            var sourceX = target.Left - target.SourceLeft + Left + Cell * (cell + .5);
            var sourceY = target.Top - target.SourceTop + Top + Height * .5;
            var x = (int)Math.Round(sourceX * image.Width / target.SourceWidth);
            var y = (int)Math.Round(sourceY * image.Height / target.SourceHeight);
            var radius = Math.Max(1, (int)(Cell * image.Width / (double)target.SourceWidth / 7));
            if (x - radius < 0 || x + radius >= image.Width || y - radius < 0 || y + radius >= image.Height) return double.NaN;
            long total = 0; var count = 0;
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var p = ((y + dy) * image.Width + x + dx) * 4;
                    total += image.Bgra[p] + image.Bgra[p + 1] + image.Bgra[p + 2]; count += 3;
                }
            return total / (double)count;
        }
        var white = CellMean(0); var black = CellMean(1);
        if (!double.IsFinite(white) || !double.IsFinite(black) || white - black < 100) return -1;
        var result = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var level = (CellMean(bit + 2) - black) / (white - black);
            if (!double.IsFinite(level) || level is > .30 and < .70) return -1;
            if (level >= .70) result |= 1 << bit;
        }
        return result;
    }
}
