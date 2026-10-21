using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Frd;
using Frd.CodecLatency;

namespace Frd.DesktopProbe;

sealed class SenderProbe : Window
{
    readonly TextBlock status = new() { Text = "发送端短测：真实桌面 → 捕获 → 编码", FontSize = 22, Margin = new Thickness(20) };
    readonly Motion motion = new();
    readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly CancellationTokenSource stop = new();
    public SenderProbe()
    {
        Title = "FRD 捕获＋编码短测 · 完成自动关闭"; Width = 850; Height = 500;
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        grid.Children.Add(status); Grid.SetRow(motion, 1); grid.Children.Add(motion); Content = grid;
        animation.Tick += (_, _) => { if (!Program.RecordFixture) { motion.Phase++; motion.InvalidateVisual(); } }; animation.Start();
        Closing += (_, _) => { stop.Cancel(); animation.Stop(); };
        Opened += async (_, _) =>
        {
            try { await Task.Run(Run); }
            catch (OperationCanceledException) { Console.Error.WriteLine("Sender probe cancelled"); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { Close(); }
        };
    }

    void Run()
    {
        FfmpegRuntime.Initialize(Program.NativeDirectory);
        if (Program.RecordFixture) { RecordFixture(); return; }
        if (Program.GpuSenderOnly) { RunGpu(); return; }
        if (Program.NativeSender) { RunNative(); return; }
        List<object> reports = new();
        foreach (var variant in new[] { ("baseline", DesktopCapturePath.PixelShader, false), ("reuse", DesktopCapturePath.PixelShader, true),
            ("direct-reuse", DesktopCapturePath.DirectPixelShader, true), ("baseline-repeat", DesktopCapturePath.PixelShader, false) })
        {
            stop.Token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.Post(() => status.Text = "捕获＋编码：" + variant.Item1);
            try
            {
                using var capture = new DxgiDesktopCapture(1280, 720, variant.Item2, reusePixelBuffer: variant.Item3);
                using var encoder = new FfmpegEncoder("-c:v libx264 -preset ultrafast -tune zerolatency -bf 0 -g 300 -threads 4 -bufsize 167k", 1280, 720, 30, 5000);
                using var decoder = new FfmpegDecoder("-c:v h264 -threads 1");
                List<DesktopCaptureStatistics> captures = new();
                List<double> encode = new(), total = new();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var gen2 = GC.CollectionCount(2);
                var deadline = Stopwatch.StartNew(); var id = 0; var outputs = 0;
                while (captures.Count < 60 && deadline.Elapsed.TotalSeconds < 6)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var start = Stopwatch.GetTimestamp();
                    var pixels = capture.Capture(); var captured = Stopwatch.GetTimestamp();
                    if (capture.Statistics?.NewDesktopImage == true)
                    {
                        var packets = encoder.Encode(pixels, id, id == 0); var encoded = Stopwatch.GetTimestamp();
                        var frames = 0;
                        foreach (var packet in packets)
                        {
                            if (packet.Pts != id) throw new InvalidDataException("Encoder delayed frame output");
                            foreach (var decoded in decoder.Decode(packet.Data, packet.Pts))
                            {
                                if (decoded.Pts != id || decoded.Width != 1280 || decoded.Height != 720) throw new InvalidDataException("Decode validation failed");
                                frames++;
                            }
                        }
                        if (frames != 1) throw new InvalidDataException("Expected one immediate decoded frame");
                        outputs++;
                        if (id++ >= 10) { captures.Add(capture.Statistics); encode.Add(Stopwatch.GetElapsedTime(captured, encoded).TotalMilliseconds); total.Add(Stopwatch.GetElapsedTime(start, encoded).TotalMilliseconds); }
                    }
                    var remaining = 1000d / 30 - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (remaining > 1) Thread.Sleep((int)remaining);
                }
                if (captures.Count < 30) throw new InvalidDataException("Insufficient actual desktop changes");
                reports.Add(new { Variant = variant.Item1, Frames = captures.Count, Outputs = outputs,
                    Capture = Stats(captures.Select(x => x.TotalCaptureMs)), Encode = Stats(encode), CaptureEncode = Stats(total),
                    Acquire = Stats(captures.Select(x => x.AcquireMs)), MapWait = Stats(captures.Select(x => x.MapWaitAndReadbackMs)),
                    Allocation = Stats(captures.Select(x => x.PixelBufferAllocationMs)), RowCopy = Stats(captures.Select(x => x.CpuRowCopyMs)),
                    Release = Stats(captures.Select(x => x.FrameReleaseMs)), ScaleSubmit = Stats(captures.Select(x => x.GpuScaleSubmitMs)),
                    ThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, Gen2Collections = GC.CollectionCount(2) - gen2,
                    Scope = "Real primary desktop containing animated test window; 720p30 5Mbps. Decode validates output but is excluded from capture+encode time. CPU submission times are not GPU durations." });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Console.Error.WriteLine(error); reports.Add(new { Variant = variant.Item1, Error = error.ToString() }); }
            Program.Save(Path.Combine(Program.Output, "sender-results.json"), reports);
        }
    }
    static object Stats(IEnumerable<double> values)
    {
        var samples = values.Order().ToArray();
        return new { Mean = samples.Average(), P95 = samples[(int)Math.Ceiling(samples.Length * .95) - 1] };
    }
    void RecordFixture()
    {
        Dispatcher.UIThread.Post(() => status.Text = "记录真实桌面样本：文字滚动 → 局部运动 → 静止；完成自动关闭");
        using var capture = new DxgiDesktopCapture(1280, 720, DesktopCapturePath.NativeNoScale, reusePixelBuffer: true);
        capture.Capture();
        var width = capture.Statistics!.SourceWidth; var height = capture.Statistics.SourceHeight;
        List<object> frames = new();
        var hashes = new HashSet<string>();
        byte[]? staticPixels = null;
        for (var i = 0; i < 90; i++)
        {
            stop.Token.ThrowIfCancellationRequested();
            var index = i;
            Dispatcher.UIThread.InvokeAsync(() => { motion.Phase = index; motion.Fixture = true; motion.InvalidateVisual(); }).GetAwaiter().GetResult();
            Thread.Sleep(40);
            var pixels = i >= 60 && staticPixels != null ? staticPixels : capture.Capture();
            if (i == 60) staticPixels = pixels.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
            hashes.Add(hash);
            var file = $"frame-{i:D3}.bgra";
            File.WriteAllBytes(Path.Combine(Program.Output, file), pixels);
            frames.Add(new { File = file, Sha256 = hash, CaptureMs = capture.Statistics?.TotalCaptureMs, Scene = i < 30 ? "scroll" : i < 60 ? "local-motion" : "static" });
        }
        if (hashes.Count < 20) throw new InvalidDataException("Too few changed images in desktop fixture");
        Program.Save(Path.Combine(Program.Output, "manifest.json"), new { Width = width, Height = height, NativeResolution = true, Frames = frames, UniqueImages = hashes.Count,
            Source = "Actual primary desktop captured with DXGI NativeNoScale at physical texture resolution; visible text/motion test window. Frames 60-89 repeat captured frame 60 to isolate static replay from unrelated desktop activity. Local files only. Codec timing excludes capture and disk IO.",
            Segments = new[] { new { Scene = "scroll", Start = 0, Count = 30 }, new { Scene = "local-motion", Start = 30, Count = 30 }, new { Scene = "static", Start = 60, Count = 30 } } });
    }
    void RunNative()
    {
        using var capture = new DxgiDesktopCapture(1280, 720, DesktopCapturePath.NativeNoScale, reusePixelBuffer: true);
        capture.Capture();
        var width = capture.Statistics!.SourceWidth; var height = capture.Statistics.SourceHeight;
        List<object> reports = new();
        LowColorProbe.Candidate[] candidates = [new("color", "libx264"), new("gray8", "libx264", 8), new("gray4", "libx264", 4),
            new("gray1", "libx264", 1), new("qsv-gray8", "h264_qsv", 8), new("color-repeat", "libx264")];
        var filter = Environment.GetEnvironmentVariable("FRD_NATIVE_CANDIDATES");
        if (!string.IsNullOrWhiteSpace(filter)) candidates = candidates.Where(candidate => filter.Split(',').Contains(candidate.Name)).ToArray();
        var bitrate = int.TryParse(Environment.GetEnvironmentVariable("FRD_NATIVE_KBPS"), out var value) ? value : 5000;
        foreach (var candidate in candidates)
        {
            stop.Token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.Post(() => { status.Text = $"原始 {width}×{height}：{candidate.Name} · {bitrate / 1000d:0.##} Mbps"; motion.Fixture = true; motion.Phase = 0; });
            try
            {
                using var encoder = new LowColorProbe.PreparedEncoder(candidate, bitrate, width, height);
                using var decoder = new FfmpegDecoder("-c:v h264 -threads 1");
                List<DesktopCaptureStatistics> captures = new();
                List<double> prepare = new(), encode = new(), total = new(), copy = new();
                List<object> frames = new();
                var deadline = Stopwatch.StartNew(); var id = 0; var attempts = 0; var noChange = 0; long bytes = 0;
                while (total.Count < 60 && deadline.Elapsed.TotalSeconds < 12)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var iteration = Stopwatch.GetTimestamp(); attempts++;
                    var pixels = capture.Capture(); var captured = Stopwatch.GetTimestamp();
                    var stats = capture.Statistics!;
                    if (stats.SourceWidth != width || stats.SourceHeight != height || stats.OutputWidth != width || stats.OutputHeight != height)
                        throw new InvalidDataException("Native dimensions changed or pixels were scaled");
                    if (!stats.NewDesktopImage) { noChange++; Thread.Sleep(1); continue; }
                    encoder.Prepare(pixels); var prepared = Stopwatch.GetTimestamp();
                    var packet = encoder.Encode(id); var encoded = Stopwatch.GetTimestamp();
                    var decoded = decoder.Decode(packet.Data, packet.Pts);
                    if (decoded.Count != 1 || decoded[0].Pts != id || decoded[0].Width != width || decoded[0].Height != height)
                        throw new InvalidDataException("Native round-trip dimensions/PTS mismatch");
                    if (id++ >= 10)
                    {
                        captures.Add(stats); bytes += packet.Data.Length;
                        var p = Stopwatch.GetElapsedTime(captured, prepared).TotalMilliseconds; var e = Stopwatch.GetElapsedTime(prepared, encoded).TotalMilliseconds;
                        var t = Stopwatch.GetElapsedTime(iteration, encoded).TotalMilliseconds;
                        prepare.Add(p); encode.Add(e); total.Add(t); copy.Add(encoder.LastInputCopyMs);
                        frames.Add(new { CaptureMs = Stopwatch.GetElapsedTime(iteration, captured).TotalMilliseconds, PrepareMs = p, EncodeMs = e, TotalMs = t, stats.SourcePresentAgeMs, stats.AccumulatedFrames });
                    }
                    var remaining = 1000d / 30 - Stopwatch.GetElapsedTime(iteration).TotalMilliseconds;
                    if (remaining > 1) Thread.Sleep((int)remaining);
                }
                if (total.Count != 60) throw new InvalidDataException($"Only {total.Count} of 60 native samples completed");
                reports.Add(new { candidate.Name, Width = width, Height = height, BitrateKbps = bitrate, Frames = total.Count, Attempts = attempts, NoNewImageAttempts = noChange,
                    Capture = Stats(captures.Select(item => item.TotalCaptureMs)), MapWait = Stats(captures.Select(item => item.MapWaitAndReadbackMs)),
                    RowCopy = Stats(captures.Select(item => item.CpuRowCopyMs)), Prepare = Stats(prepare), InputCopyIncludedInPrepare = Stats(copy), Encode = Stats(encode), CaptureEncode = Stats(total),
                    SourcePresentAge = Stats(captures.Select(item => item.SourcePresentAgeMs)), AccumulatedDesktopFrames = captures.Sum(item => item.AccumulatedFrames),
                    PayloadMbpsAt30Fps = bytes * 8d / 2 / 1_000_000, FramesDetail = frames,
                    Scope = "Live full physical desktop, no scaling. Capture call -> complete encoded packet, including CPU readback/prepare/quantization. Decode validates every frame outside sender timing. Fresh desktop frames only; no-new-image attempts and accumulated desktop frames reported separately. Changing desktop content across variants; confirm close rankings with fixed-fixture tests." });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; reports.Add(new { candidate.Name, Error = error.ToString() }); }
            Program.Save(Path.Combine(Program.Output, "native-sender-results.json"), reports);
        }
    }
    void RunGpu()
    {
        using var sender = new GpuSender(nativeResolution: Program.NativeSender);
        Dispatcher.UIThread.Post(() => status.Text = $"GPU 发送端：D3D11 → NV12 → {sender.EncoderName}");
        using var decoder = new FfmpegDecoder("-c:v h264 -threads 1");
        List<double> acquire = new(), convert = new(), encode = new(), total = new(), readback = new();
        var watch = Stopwatch.StartNew(); var id = 0; long bytes = 0;
        var hashes = new HashSet<string>();
        while (total.Count < 90 && watch.Elapsed.TotalSeconds < 10)
        {
            stop.Token.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            var packet = sender.CaptureEncode(id);
            if (packet != null)
            {
                var frames = decoder.Decode(packet.Data, packet.Pts);
                if (frames.Count != 1 || frames[0].Pts != id || frames[0].Width != sender.Width || frames[0].Height != sender.Height)
                    throw new InvalidDataException("GPU encoded frame did not decode immediately and completely");
                hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frames[0].Bgra)));
                if (id++ >= 10) { acquire.Add(sender.AcquireMs); convert.Add(sender.ConvertReadyMs); encode.Add(sender.EncodeMs); total.Add(sender.TotalMs); readback.Add(sender.ReadbackMs); bytes += packet.Data.Length; }
            }
            var remaining = 1000d / 30 - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining > 1) Thread.Sleep((int)remaining);
        }
        if (total.Count < 30 || hashes.Count < 10) throw new InvalidDataException("Insufficient fresh decoded GPU frames");
        Program.Save(Path.Combine(Program.Output, "gpu-sender-results.json"), new { Encoder = sender.EncoderName, sender.Width, sender.Height, NativeResolution = Program.NativeSender, ExplicitGpuWait = sender.ExplicitGpuWait,
            ConversionTiming = sender.ExplicitGpuWait ? "GPU completion confirmed" : "CPU submission only; GPU wait included in encoding/output completion",
            Frames = total.Count, UniqueDecodedImages = hashes.Count,
            Acquire = Stats(acquire), ConversionGpuReady = Stats(convert), Encode = Stats(encode), CaptureEncode = Stats(total),
            Readback = Stats(readback), PayloadMbps = bytes * 8d / (total.Count / 30d) / 1_000_000,
            Scope = "Actual desktop -> same D3D11 device NV12 video processor. QSV uses direct hardware mapping; libx264 downloads NV12. Total ends after encoded packet output and capture release; ConversionTiming specifies synchronization. Decode validates outside timing. 30fps 5Mbps; native resolution when NativeResolution=true." });
    }
    sealed class Motion : Control
    {
        public int Phase;
        public bool Fixture;
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, Bounds.WithX(0).WithY(0));
            if (Fixture)
            {
                var motionPhase = Program.NativeSender ? Phase % 60 : Phase;
                var textShift = motionPhase < 30 ? motionPhase * 2 : 58;
                using (context.PushClip(new Rect(8, 8, 565, Math.Max(1, Bounds.Height - 16))))
                {
                    for (var row = 0; row < 22; row++)
                    {
                        var value = row % 3 == 0 ? "远程办公：中文小字、标点、表格与代码" : row % 3 == 1 ? "const latency = capture + encode; 0123456789" : "文件目录 / Localhost  192.168.0.1  AaBb Il1O0";
                        context.DrawText(new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei"), row % 3 == 0 ? 21 : 17, row % 4 == 0 ? Brushes.Gray : Brushes.Black), new Point(12, row * 28 - textShift));
                    }
                }
                var phase = Math.Min(motionPhase, 59);
                for (var row = 0; row < 10; row++)
                {
                    var level = (byte)(row * 255 / 9);
                    context.FillRectangle(new SolidColorBrush(Color.FromRgb(level, level, level)), new Rect(590, 12 + row * 17, 200, 17));
                }
                for (var row = 0; row < 6; row++)
                    context.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(row * 45), (byte)(255 - row * 40), (byte)(row * 32))), new Rect(590, 190 + row * 18, 200, 16));
                context.FillRectangle(Brushes.Red, new Rect(590, 300, 100, 15));
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0, 76, 0)), new Rect(690, 300, 100, 15));
                context.FillRectangle(Brushes.SteelBlue, new Rect(590 + phase * 7 % 120, 320, 65, 40));
                context.FillRectangle(Brushes.DarkOrange, new Rect(590 + phase * 11 % 150, 380, 40, 25));
                return;
            }
            for (var i = 0; i < 12; i++) context.FillRectangle(i % 2 == 0 ? Brushes.SteelBlue : Brushes.DarkOrange,
                new Rect((i * 73 + Phase * 11) % Math.Max(1, Bounds.Width), i * 27, 70, 20));
        }
    }
}
