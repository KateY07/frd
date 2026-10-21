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
            if (args is ["--mouse-target", "--state", var mousePath, "--motion", var motion])
            { Target.Run(Path.GetFullPath(mousePath), true, bool.Parse(motion)); return 0; }
            Settings = Options.Parse(args);
            AppBuilder.Configure<TestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            return ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

sealed record Options(string Config, string StatePath, string Report, int Port, string Preset, int Bitrate, double Scale, int Count, int Interval, string Host = "127.0.0.1", bool Mouse = false, string? TargetEvents = null)
{
    public static Options Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Expected --config PATH --target-state PATH --report PATH --port PORT [--preset h264_fast --bitrate 5000 --scale 1 --count 24 --interval 170]. FRD_TEST_TOKEN supplies the localhost host password. Target mode: --target --state PATH.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--config" or "--target-state" or "--report" or "--port" or "--preset" or "--bitrate" or "--scale" or "--count" or "--interval" or "--host" or "--mouse" or "--target-events")) throw new ArgumentException("Unknown option: " + args[i]);
            values.Add(args[i], args[i + 1]);
        }
        string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required: " + key);
        var options = new Options(Path.GetFullPath(Required("--config")), Path.GetFullPath(Required("--target-state")), Path.GetFullPath(Required("--report")),
            int.Parse(Required("--port")), values.GetValueOrDefault("--preset", "h264_fast"), int.Parse(values.GetValueOrDefault("--bitrate", "5000")),
            double.Parse(values.GetValueOrDefault("--scale", "1"), System.Globalization.CultureInfo.InvariantCulture), int.Parse(values.GetValueOrDefault("--count", "24")), int.Parse(values.GetValueOrDefault("--interval", "170")), values.GetValueOrDefault("--host", "127.0.0.1"), bool.Parse(values.GetValueOrDefault("--mouse", "false")), values.GetValueOrDefault("--target-events"));
        if (options.Mouse && options.Count > 24) throw new ArgumentException("Mouse marker test supports at most 24 positions.");
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
    RemoteInputClient? input;
    SessionStatus? latestStatus;
    Sample? active;
    bool closing, completed;
    int decodedMarkers;

    public TestWindow(Options options)
    {
        this.options = options;
        target = JsonSerializer.Deserialize<TargetState>(File.ReadAllText(options.StatePath), AppConfiguration.JsonOptions) ?? throw new InvalidDataException("Target state is empty.");
        ValidateTarget();
        Title = options.Mouse ? "FRD 鼠标到位 → 返回画面测试（完成后关闭）" : "FRD 画面反馈延迟测试（不注入键鼠，完成后关闭）";
        Width = 460; Height = 290; CanResize = true; ShowActivated = false; Topmost = true;
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
            ValidateTarget();
            if (!options.Mouse) Target.Stimulate(target, 0);
            session = new DemoSession(AppConfiguration.Load(options.Config), new RemoteOptions(options.Host, options.Port, Environment.GetEnvironmentVariable("FRD_TEST_TOKEN")!));
            session.FrameReceived += Frame;
            session.Failed += Error;
            session.StatusChanged += value => Volatile.Write(ref latestStatus, value);
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            var applied = await session.ApplyAsync(options.Preset, options.Bitrate, options.Scale).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            if (!applied.Success) throw new InvalidOperationException(applied.Message);
            await initial.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            if (options.Mouse)
            {
                input = await session.ConnectRemoteInputAsync(stop.Token);
                if (!(await input.SetEnabledAsync(true, stop.Token)).Accepted) throw new IOException("Input enable rejected.");
            }
            await Task.Delay(350, stop.Token);
            for (var id = 1; id <= options.Count; id++)
            {
                ValidateTarget();
                var sample = new Sample(id);
                var deferredBefore = view.DeferredPresentations;
                lock (gate) { active = sample; sample.Started = Stopwatch.GetTimestamp(); }
                if (options.Mouse)
                {
                    var x = (target.Left - target.SourceLeft + 16 + id * 16d) / (target.SourceWidth - 1);
                    var y = (target.Top - target.SourceTop + 190d) / (target.SourceHeight - 1);
                    if (!(await input!.SendAsync(new(RemoteInputKind.MouseMove, x, y), stop.Token)).Accepted) throw new IOException("Mouse send rejected.");
                    sample.TargetCompleted = sample.Started;
                }
                else sample.TargetCompleted = Target.Stimulate(target, id);
                sample.Acknowledged = Stopwatch.GetTimestamp();
                await sample.Done.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
                double? arrival = null, paintMs = null, uncertainty = null;
                if (options.TargetEvents != null)
                {
                    var remote = typeof(DemoSession).GetField("remote", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
                    var clock = (ClockEstimate)remote.GetType().GetProperty("Clock")!.GetValue(remote)!;
                    foreach (var line in File.ReadAllLines(options.TargetEvents))
                    {
                        using var item = JsonDocument.Parse(line);
                        var data = item.RootElement;
                        if (data.GetProperty("Id").GetInt32() != id) continue;
                        var arrived = data.GetProperty("Arrived").GetInt64();
                        arrival = Ms(clock.ToLocal(arrived, target.Frequency) - sample.Started);
                        paintMs = (data.GetProperty("Painted").GetInt64() - arrived) * 1000d / target.Frequency;
                        uncertainty = clock.UncertaintyMs;
                    }
                }
                var result = new Measurement(id, options.Mouse ? null : Ms(sample.TargetCompleted - sample.Started), Ms(sample.Acknowledged - sample.Started),
                    Ms(sample.Decoded - sample.Started), Ms(sample.Rendered - sample.Started), sample.MatchingFrames, sample.DecodedPts, sample.RenderedPts, Volatile.Read(ref latestStatus), arrival, paintMs, uncertainty, view.DeferredPresentations - deferredBefore);
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
            input?.Dispose();
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
                    Passed = Program.ExitCode == 0, Scope = options.Mouse ? "Controller-generated movement → production fixed UDP input → Windows cursor confirmed at target by WM_MOUSEMOVE and GetCursorPos → marker paint → real capture/encode/UDP/decode → matched frame D3D11 GPU completion. Excludes physical mouse sampling, production UI collection queue and display scanout; send completion is NOT cursor confirmation." : "Local WM_APP stimulus → capture/encode/UDP/decode/GPU; no physical input.",
                    Clock = "Start and decoded/presented timestamps use only controller Stopwatch. No remote clock subtraction.", PreviewExcludedFromCapture = true,
                    options.Mouse, target.Motion, options.Host,
                    StageTimingNote = "Each measurement includes the latest production SessionStatus (updated independently; rolling 1-second capture/encode/transfer/decode/render means and FPS). These aid attribution but are not an exact per-marker decomposition; capture scheduling before CaptureTick is excluded from those stages.",
                    options.Preset, options.Bitrate, options.Scale, options.Count, options.Interval,
                    Source = new { target.SourceWidth, target.SourceHeight, target.Window },
                    decodedMarkers, Measurements = measurements,
                    MessageAckMeaning = options.Mouse ? "Local UDP send completion only; not a remote acknowledgement. TargetMessageMs is null because no remote timestamp is subtracted." : "Target message acknowledgement.",
                    Summary = new { TargetMessage = Summary(measurements.Where(x => x.TargetMessageMs.HasValue).Select(x => x.TargetMessageMs!.Value)), MessageAck = Summary(measurements.Select(x => x.MessageAckMs)),
                        Decode = Summary(measurements.Select(x => x.DecodeMs)), Gpu = Summary(measurements.Select(x => x.GpuMs)) }, Errors = errors.ToArray()
                }, AppConfiguration.JsonOptions));
            }
            completed = true;
            Close();
        }
    }

    void ValidateTarget()
    {
        if (!options.Mouse) { Target.Validate(target); return; }
        if (!target.Mouse || target.Closed || target.SourceWidth <= 0 || target.SourceHeight <= 0 || target.Width < 528 || target.Height < 208)
            throw new InvalidDataException("Live mouse target state required.");
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
        return new { Count = data.Length, MeanMs = data.Length == 0 ? 0 : data.Average(), P50Ms = Percentile(.5), P95Ms = Percentile(.95), P99Ms = Percentile(.99), MaximumMs = data.LastOrDefault() };
        double Percentile(double fraction) => data.Length == 0 ? 0 : data[Math.Clamp((int)Math.Ceiling(data.Length * fraction) - 1, 0, data.Length - 1)];
    }

    sealed class Sample(int id)
    {
        public int Id = id, MatchingFrames;
        public long Started, TargetCompleted, Acknowledged, Decoded, Rendered, DecodedPts, RenderedPts;
        public HashSet<long> Pts = new();
        public TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    sealed record Measurement(int Id, double? TargetMessageMs, double MessageAckMs, double DecodeMs, double GpuMs, int MatchingFrames, long DecodedPts, long RenderedPts, SessionStatus? Status, double? EstimatedMouseArrivalMs, double? TargetPaintMs, double? ClockUncertaintyMs, long DeferredPresentations);
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
