using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Frd;

namespace Frd.DesktopProbe;

sealed class ProbeWindow : Window
{
    readonly ConfirmedVideoView video = new();
    readonly TextBlock status = new() { Text = "准备自动探测…", TextWrapping = TextWrapping.Wrap };
    readonly TextBlock metrics = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    readonly Button secure = new() { Content = "启用锁屏 / UAC 捕获", IsEnabled = false };
    readonly Button uac = new() { Content = "测试 UAC", IsEnabled = false };
    readonly Button lockScreen = new() { Content = "锁屏测试", IsEnabled = false };
    readonly Button evidence = new() { Content = "查看安全桌面证据", IsEnabled = false };
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<long, FrameStamp> stamps = new();
    readonly ConcurrentDictionary<long, (long Received, long Decoded)> decodedStamps = new();
    readonly ConcurrentQueue<(long Tick, double Capture, double Encode, double Transfer, double Decode, double Render)> timings = new();
    readonly ConcurrentDictionary<string, long> secureTrials = new();
    readonly object decodeGate = new();
    readonly object aggregateGate = new();
    long timingCount;
    double totalCapture, totalEncode, totalTransfer, totalDecode, totalRender;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    UdpVideoReceiver? receiver;
    UdpClient? diagnostic;
    CancellationTokenSource? sessionStop;
    FfmpegDecoder? decoder;
    Process? worker;
    Candidate? chosen;
    string? run;
    int decoderGeneration = -1;
    long presented, decoded, secureDecoded, secureNewImages;
    long firstReceived, lastReceived;
    DecodedPixels? securePixels;
    string lastSecureDesktop = "";
    bool showingEvidence, closed, manualPassed, transitioning;
    DateTime? autoCloseAt;
    DateTime nextReport;
    string? failure;

    public ProbeWindow()
    {
        Title = "FRD 专用测试 · 自动选编码 / 锁屏 / UAC";
        Width = 1120; Height = 780; MinWidth = 720; MinHeight = 520;
        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        foreach (var button in new[] { secure, uac, lockScreen, evidence }) controls.Children.Add(button);
        var confirm = new Button { Content = "人工确认通过" }; controls.Children.Add(confirm);
        var folder = new Button { Content = "打开测试记录" }; controls.Children.Add(folder);
        var layout = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), Margin = new Thickness(12), RowSpacing = 8 };
        layout.Children.Add(controls); Grid.SetRow(status, 1); layout.Children.Add(status);
        Grid.SetRow(metrics, 2); layout.Children.Add(metrics); Grid.SetRow(video, 3); layout.Children.Add(video); Content = layout;
        video.Presented += Presented; video.Failed += Fail;
        Opened += async (_, _) =>
        {
            if (!SetWindowDisplayAffinity(TryGetPlatformHandle()?.Handle ?? 0, 0x11)) Console.Error.WriteLine("Exclude preview: " + new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                FfmpegRuntime.Initialize(Program.NativeDirectory);
                chosen = await Task.Run(() => CodecProbe.FindFastest(Path.Combine(Program.Output, "probe"), text => Dispatcher.UIThread.Post(() => status.Text = text), lifetime.Token));
                var ranking = Program.Read<List<ProbeResult>>(Path.Combine(Program.Output, "probe", "ranking.json"));
                var best = ranking.Single(value => value.Id == chosen.Id);
                status.Text = $"实测候选中编码最低：{chosen.Id}，平均 {best.MeanMs:F2} ms / P95 {best.P95Ms:F2} ms；解码 {best.DecodeMeanMs:F2} ms。\n固定 1280×720 · 30 FPS · 5 Mbps。当前为普通权限；锁屏与 UAC 尚未验证。";
                await StartSession(Program.SecureOnStart); secure.IsEnabled = !Program.SecureOnStart;
                if (Program.AutoCloseSeconds > 0) autoCloseAt = DateTime.UtcNow.AddSeconds(Program.AutoCloseSeconds);
            }
            catch (OperationCanceledException) when (closed) { Console.Error.WriteLine("Probe window closed during scan"); }
            catch (Exception error) { Fail(error); }
        };
        secure.Click += async (_, _) =>
        {
            if (transitioning) return;
            transitioning = true; secure.IsEnabled = false;
            try { await StartSession(true); }
            catch (Exception error) { Fail(error); secure.IsEnabled = true; }
            finally { transitioning = false; }
        };
        uac.Click += (_, _) =>
        {
            try { File.WriteAllText(Path.Combine(run!, "trial"), "uac"); using var check = Process.Start(new ProcessStartInfo(Program.Executable, "--uac-check") { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { status.Text = "UAC 已取消；仍可检查提示期间是否捕获成功。"; Console.Error.WriteLine(error.Message); }
            catch (Exception error) { Fail(error); }
        };
        lockScreen.Click += (_, _) => { File.WriteAllText(Path.Combine(run!, "trial"), "lock"); if (!LockWorkStation()) Fail(new Win32Exception(Marshal.GetLastWin32Error(), "LockWorkStation")); };
        evidence.Click += (_, _) =>
        {
            showingEvidence = !showingEvidence;
            evidence.Content = showingEvidence ? "返回实时画面" : "查看安全桌面证据";
            if (showingEvidence && securePixels != null) video.Submit(securePixels);
        };
        confirm.Click += (_, _) =>
        {
            if (!secureTrials.ContainsKey("uac") || !secureTrials.ContainsKey("lock")) { status.Text = "请分别用按钮完成 UAC、锁屏测试，并检查证据图像后再确认；两项都须收到真实安全桌面新图像。"; return; }
            manualPassed = true; SaveReport(); status.Text = "已记录人工确认；锁屏与 UAC 的逐项结论请同时查看测试记录。";
        };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", Program.Output) { UseShellExecute = true });
        timer.Tick += (_, _) => Update(); timer.Start();
        Closing += (_, _) => { closed = true; lifetime.Cancel(); timer.Stop(); StopSession(); SaveReport(); };
    }

    async Task StartSession(bool system)
    {
        StopSession();
        run = Path.Combine(Program.Output, (system ? "system-" : "normal-") + DateTime.Now.ToString("HHmmssfff"));
        Directory.CreateDirectory(run); sessionStop = new();
        stamps.Clear(); decodedStamps.Clear(); timings.Clear(); decoderGeneration = -1;
        receiver = new UdpVideoReceiver();
        diagnostic = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.VideoReceived += Decode; receiver.Failed += Fail;
        var settings = new RunSettings(run, Program.NativeDirectory, chosen!, receiver.Port,
            ((IPEndPoint)diagnostic.Client.LocalEndPoint!).Port, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Process.GetCurrentProcess().SessionId);
        Program.Save(Path.Combine(run, "settings.json"), settings);
        var client = diagnostic; var stop = sessionStop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await client.ReceiveAsync(stop);
                    if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;
                    var stamp = JsonSerializer.Deserialize<FrameStamp>(packet.Buffer) ?? throw new InvalidDataException("Empty diagnostic packet");
                    stamps[stamp.Id] = stamp;
                    stamps.TryRemove(stamp.Id - 256, out _);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Diagnostic reception stopped"); }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Diagnostic socket closed"); }
            catch (Exception error) { Fail(error); }
        }, stop);
        if (system)
        {
            status.Text = "即将请求 Windows UAC：临时 SYSTEM 服务用于安全桌面捕获；关闭 Demo 后自动停止。";
            var info = new ProcessStartInfo(Program.Executable, $"--elevate \"{run}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var elevated = Process.Start(info) ?? throw new IOException("Elevation was not started");
            await elevated.WaitForExitAsync(lifetime.Token);
            if (elevated.ExitCode != 0) throw new InvalidOperationException("SYSTEM helper setup failed; inspect privilege-error.json");
            status.Text = $"{chosen!.Id} · 等待 SYSTEM 捕获进程。就绪后可点击“测试 UAC”和“锁屏测试”；恢复普通桌面后查看证据。";
        }
        else worker = Process.Start(Program.StartInfo("--worker", run)) ?? throw new IOException("Cannot start capture worker");
        var portsPath = Path.Combine(run, "ports.json");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(portsPath))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Capture worker did not announce its UDP endpoints; inspect worker/broker logs");
            await Task.Delay(50, lifetime.Token);
        }
        using var ports = JsonDocument.Parse(File.ReadAllText(portsPath));
        receiver.SetExpectedSource(new IPEndPoint(IPAddress.Loopback, ports.RootElement.GetProperty("Video").GetInt32()));
        diagnostic.Send(new byte[1], new IPEndPoint(IPAddress.Loopback, ports.RootElement.GetProperty("Diagnostic").GetInt32()));
        File.WriteAllText(Path.Combine(run, "registered"), "Loopback endpoints registered");
    }

    void Decode(ReceivedVideo packet)
    {
        try
        {
            lock (decodeGate)
            {
                if (closed || chosen == null) return;
                if (packet.Generation != decoderGeneration)
                {
                    if (!packet.KeyFrame) return;
                    decoder?.Dispose(); decoder = new FfmpegDecoder(chosen.Decoder); decoderGeneration = packet.Generation;
                }
                var received = Stopwatch.GetTimestamp();
                if (firstReceived == 0) firstReceived = received; lastReceived = received;
                var frames = decoder!.Decode(packet.Data, packet.FrameId);
                var decodedTick = Stopwatch.GetTimestamp();
                foreach (var pixels in frames)
                {
                    Interlocked.Increment(ref decoded); decodedStamps[pixels.Pts] = (received, decodedTick);
                    decodedStamps.TryRemove(pixels.Pts - 256, out _);
                    if (stamps.TryGetValue(pixels.Pts, out var stamp) && !stamp.Desktop.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref secureDecoded);
                        if (stamp.NewImage) { Interlocked.Increment(ref secureNewImages); secureTrials.AddOrUpdate(stamp.Trial, 1, (_, total) => total + 1); }
                        if (securePixels == null || lastSecureDesktop != stamp.Desktop + stamp.Generation)
                        {
                            securePixels = pixels; lastSecureDesktop = stamp.Desktop + stamp.Generation;
                            SaveBitmap(Path.Combine(run!, $"secure-decoded-{stamp.Generation}.bmp"), pixels);
                            Program.Save(Path.Combine(run!, $"secure-evidence-{stamp.Generation}.json"), new { Stamp = stamp, DecodedAt = decodedTick, Pixels = pixels.Bgra.Length, Note = "Decoded through real localhost UDP; manual visual confirmation required" });
                        }
                    }
                    if (!showingEvidence) video.Submit(pixels);
                }
            }
        }
        catch (Exception error) { Fail(error); }
    }

    void Presented(long id, long tick)
    {
        if (showingEvidence) return;
        Interlocked.Increment(ref presented);
        if (!stamps.TryRemove(id, out var stamp) || !decodedStamps.TryRemove(id, out var receive)) return;
        if (!stamp.NewImage) return;
        var scale = 1000d / Stopwatch.Frequency;
        var capture = (stamp.Captured - stamp.Started) * scale; var encode = (stamp.Encoded - stamp.Captured) * scale;
        var transfer = (receive.Received - stamp.Encoded) * scale; var decode = (receive.Decoded - receive.Received) * scale;
        var render = (tick - receive.Decoded) * scale;
        timings.Enqueue((tick, capture, encode, transfer, decode, render));
        lock (aggregateGate) { timingCount++; totalCapture += capture; totalEncode += encode; totalTransfer += transfer; totalDecode += decode; totalRender += render; }
    }

    void Update()
    {
        var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (timings.TryPeek(out var head) && head.Tick < cutoff) timings.TryDequeue(out _);
        var samples = timings.ToArray();
        string desktop = "等待捕获", identity = "普通权限";
        if (run != null)
        {
            var path = Path.Combine(run, "worker-status.json");
            if (File.Exists(path))
            {
                try
                {
                    using var state = JsonDocument.Parse(File.ReadAllText(path));
                    desktop = state.RootElement.GetProperty("Desktop").GetString()!;
                    identity = state.RootElement.GetProperty("System").GetBoolean() ? "SYSTEM" : "普通权限";
                    var fresh = DateTime.UtcNow - state.RootElement.GetProperty("UpdatedUtc").GetDateTime() < TimeSpan.FromSeconds(3);
                    uac.IsEnabled = lockScreen.IsEnabled = identity == "SYSTEM" && fresh;
                }
                catch (Exception error) { Console.Error.WriteLine("Read worker status: " + error); }
            }
        }
        var timing = samples.Length == 0 ? "等待已呈现帧" :
            $"最近 1 秒真实变化：捕获 {samples.Average(v => v.Capture):F2} / 编码 {samples.Average(v => v.Encode):F2} / UDP {samples.Average(v => v.Transfer):F2} / 解码 {samples.Average(v => v.Decode):F2} / 呈现 {samples.Average(v => v.Render):F2} ms\n捕获→GPU 完成 {samples.Average(v => v.Capture + v.Encode + v.Transfer + v.Decode + v.Render):F2} ms（不含物理屏幕扫描）";
        metrics.Text = $"{identity} · 桌面 {desktop} · 已解码 {decoded} / GPU 确认 {presented}\n{timing}\n安全桌面已解码 {secureDecoded} 帧，新图像 {secureNewImages}；UAC {secureTrials.GetValueOrDefault("uac")} / 锁屏 {secureTrials.GetValueOrDefault("lock")}；人工确认：{(manualPassed ? "已确认" : "未确认")}。";
        evidence.IsEnabled = securePixels != null;
        if (DateTime.UtcNow >= nextReport) { SaveReport(); nextReport = DateTime.UtcNow.AddSeconds(1); }
        if (worker is { HasExited: true } && worker.ExitCode != 0 && failure == null) Fail(new InvalidOperationException("捕获辅助进程异常退出；查看 worker.log。"));
        if (autoCloseAt is { } deadline && DateTime.UtcNow >= deadline) { if (presented < 3 || failure != null) Environment.ExitCode = 1; Close(); }
    }

    void StopSession()
    {
        if (run != null) File.WriteAllText(Path.Combine(run, "stop"), "Viewer stopped");
        if (worker != null)
        {
            if (!worker.WaitForExit(5000)) { Console.Error.WriteLine("Ordinary worker did not stop; terminating owned test process"); worker.Kill(true); worker.WaitForExit(); }
            worker.Dispose(); worker = null;
        }
        sessionStop?.Cancel(); diagnostic?.Dispose(); receiver?.Dispose();
        lock (decodeGate) { decoder?.Dispose(); decoder = null; }
        sessionStop?.Dispose(); sessionStop = null; diagnostic = null; receiver = null;
    }
    void Fail(Exception error)
    {
        Console.Error.WriteLine(error); failure = error.ToString();
        Dispatcher.UIThread.Post(() => status.Text = "测试未通过：" + error.Message + "\n完整异常已保留。");
        Environment.ExitCode = 1;
    }
    void SaveReport() => Program.Save(Path.Combine(Program.Output, "demo-report.json"), new
    {
        Selected = chosen, DecodedFrames = decoded, GpuConfirmedFrames = presented, SecureDecodedFrames = secureDecoded,
        SecureNewImages = secureNewImages, ManualConfirmed = manualPassed, Failure = failure,
        ChangedFrameMeans = TimingSummary(),
        SecureTrials = secureTrials.ToArray(), RecentTimings = timings.Select(value => new { value.Capture, value.Encode, value.Transfer, value.Decode, value.Render }).ToArray(),
        NormalSmokePassed = presented >= 3 && failure == null, SecureDesktopPassed = secureNewImages > 0 && manualPassed,
        Scope = "Capture and codec use existing FRD CPU-frame backend, real localhost UDP and GPU presentation. Lowest measured candidate, not an absolute hardware minimum. Secure desktop must be manually checked. No keyboard or mouse forwarding.",
        UpdatedUtc = DateTime.UtcNow
    });
    object TimingSummary()
    {
        lock (aggregateGate)
        {
            var count = Math.Max(1, timingCount);
            return new { Count = timingCount, Capture = totalCapture / count, Encode = totalEncode / count,
                Transfer = totalTransfer / count, Decode = totalDecode / count, Render = totalRender / count,
                Total = (totalCapture + totalEncode + totalTransfer + totalDecode + totalRender) / count };
        }
    }
    static void SaveBitmap(string path, DecodedPixels pixels)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write((ushort)0x4d42); file.Write(54 + pixels.Bgra.Length); file.Write(0); file.Write(54);
        file.Write(40); file.Write(pixels.Width); file.Write(-pixels.Height); file.Write((ushort)1); file.Write((ushort)32);
        file.Write(0); file.Write(pixels.Bgra.Length); file.Write(0); file.Write(0); file.Write(0); file.Write(0); file.Write(pixels.Bgra);
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
}
