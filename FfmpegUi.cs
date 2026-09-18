using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Frd;

static partial class FfmpegUi
{
    static int regressionExitCode;
    static Func<Window>? createWindow;
    internal static string? InteractionScriptPath;

    public static void Run(AppConfiguration config, int autoCloseSeconds = 0, string? reportPath = null, RemoteOptions? remote = null)
    {
        regressionExitCode = 0;
        createWindow = () => new DesktopWindow(config, autoCloseSeconds, reportPath, remote);
        AppBuilder.Configure<DemoApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
        if (regressionExitCode != 0) Environment.ExitCode = regressionExitCode;
    }

    public static void RunPresentationRegression(AppConfiguration config, string reportPath)
    {
        regressionExitCode = 0;
        createWindow = () => new PresentationTestWindow(config, reportPath);
        AppBuilder.Configure<DemoApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
        if (regressionExitCode != 0) Environment.ExitCode = regressionExitCode;
    }

    sealed class DemoApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = createWindow!();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    sealed partial class DesktopWindow : Window
    {
        AppConfiguration config;
        readonly RemoteOptions? remoteOptions;
        readonly string? reportPath;
        readonly int autoCloseSeconds;
        readonly ComboBox presets = new() { Name = "EncoderPreset", HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly ComboBox transmissionScale = new() { Name = "TransmissionScale", Width = 165, IsEnabled = false };
        readonly Slider bitrate = new() { Name = "BitrateLimit", Minimum = .1, Maximum = 100, TickFrequency = .1, IsSnapToTickEnabled = true };
        readonly NumericUpDown bitrateLabel = new() { Name = "BitrateValueMbps", Minimum = .1m, Maximum = 100m, Increment = .1m, FormatString = "0.0##", Width = 115 };
        bool updatingBitrate;
        readonly TextBlock metrics = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        readonly TextBlock operation = new() { TextWrapping = TextWrapping.Wrap };
        readonly TextBlock captureDetails = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 4, 0, 6) };
        readonly TextBlock summary = new() { Text = "FRD · 真实屏幕 / FFmpeg / UDP", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock[] timings = Enumerable.Range(0, 6).Select(_ => new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 8, 3) }).ToArray();
        readonly Grid details = new() { RowDefinitions = new("Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        readonly DiagnosticWatermark watermark = new();
        readonly TextBlock controlTitle = new() { Text = "FRD 控制", VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox inputEnabled = new() { Name = "InputForwarding", Content = "键鼠转发 · Esc 退出", IsEnabled = false, Margin = new Thickness(12, 0) };
        readonly CheckBox packetDiagnostics = new() { Name = "PacketDiagnostics", Content = "诊断包", IsEnabled = false, Margin = new Thickness(8, 0) };
        readonly CheckBox clipboardEnabled = new() { Name = "ClipboardSync", Content = "剪贴板同步（文本、文件、目录）", IsEnabled = false };
        readonly TextBlock clipboardMessage = new() { Text = "关闭；开启后同步两端新复制的内容", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0) };
        readonly Button collapse = new() { Name = "CollapseDiagnostics", Content = "收起 ▴", MinWidth = 72 };
        readonly Window overlay = new()
        {
            Title = "FRD 控制栏", WindowDecorations = Avalonia.Controls.WindowDecorations.None, CanResize = false,
            ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.Height,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent]
        };
        readonly ConfirmedVideoView preview = new() { Name = "ReceivedVideo" };
        readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
        readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
        readonly DispatcherTimer autoClose = new();
        readonly object receiveGate = new();
        readonly object inputGate = new();
        readonly LinkedList<RemoteInputEvent> inputQueue = new();
        readonly SemaphoreSlim inputReady = new(0, 1), inputTransition = new(1, 1);
        readonly CancellationTokenSource inputStop = new();
        readonly List<string> errors = new();
        readonly DateTime startedUtc = DateTime.UtcNow;
        DesktopStreamSource? capture;
        DemoSession? session;
        Win32InputInjector? inputInjector;
        RemoteInputServer? inputServer;
        RemoteInputClient? inputClient;
        ClipboardSyncSession? clipboardSync;
        CursorClient? cursorClient;
        readonly SemaphoreSlim clipboardTransition = new(1, 1);
        bool updatingClipboard;
        Task? inputWorker;
        string? lastInputMessage, captureBackend;
        DesktopCaptureStatistics? captureStatistics;
        bool updatingInput, changingDiagnostics, updatingDiagnostics;
        long inputSent, inputRejected, coalescedMoves;
        SessionStatus? pendingStatus;
        bool started, stopping, allowClose, applying, applyAgain;
        long receivedFrames, shownFrames;
        int successfulChanges;
        object? overlayCheck;
        bool overlayPassed;

        public DesktopWindow(AppConfiguration config, int autoCloseSeconds, string? reportPath, RemoteOptions? remote = null)
        {
            remoteOptions = remote;
            this.config = config; this.autoCloseSeconds = autoCloseSeconds; this.reportPath = reportPath;
            ToolTip.SetTip(inputEnabled, "本机共用桌面，目标若是预览窗口会被拒绝；预览获得焦点时无法同时作为被控键盘目标。双机控制需独立桌面。");
            preview.Presented += (frameId, tick) => { Interlocked.Increment(ref shownFrames); session?.ReportPresented(frameId, tick); };
            preview.Failed += ex => { if (Dispatcher.UIThread.CheckAccess()) ReportError(ex); else Dispatcher.UIThread.Post(() => ReportError(ex)); };
            preview.Input += QueueInput;
            preview.InputExitRequested += () => inputEnabled.IsChecked = false;
            Title = autoCloseSeconds > 0 ? "FRD — 自动回归（完成后关闭）" : "FRD — FFmpeg 真实屏幕 / localhost UDP";
            if (remote != null) { Title = $"FRD 主控端 — {remote.Host}:{remote.Port}"; ToolTip.SetTip(inputEnabled, "转发到被控端；Esc 退出并释放按键。"); }
            Width = 1280; Height = 860; MinWidth = 780; MinHeight = 520; CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = new Border { Background = Brushes.Black, Child = preview };
            var header = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto") };
            header.Children.Add(controlTitle); Grid.SetColumn(inputEnabled, 1); header.Children.Add(inputEnabled);
            Grid.SetColumn(packetDiagnostics, 2); header.Children.Add(packetDiagnostics);
            Grid.SetColumn(collapse, 3); header.Children.Add(collapse);
            var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(details);
            overlay.Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#EE17202B")), BorderBrush = new SolidColorBrush(Color.Parse("#556B829B")),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = panel
            };
            overlay.Foreground = Brushes.Gainsboro;
            var controls = new Grid { ColumnDefinitions = new("260,16,*,125"), RowDefinitions = new("Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            controls.Children.Add(new TextBlock { Text = "主控端手动选择编码预设", Margin = new Thickness(0, 0, 0, 5) });
            Grid.SetRow(presets, 1); controls.Children.Add(presets);
            var capTitle = new TextBlock { Text = "编码码率上限（Mbps）· 可拖动或直接输入", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            Grid.SetColumn(capTitle, 2); Grid.SetColumnSpan(capTitle, 2); controls.Children.Add(capTitle);
            Grid.SetColumn(bitrate, 2); Grid.SetRow(bitrate, 1); controls.Children.Add(bitrate);
            bitrateLabel.VerticalAlignment = VerticalAlignment.Center; bitrateLabel.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(bitrateLabel, 3); Grid.SetRow(bitrateLabel, 1); controls.Children.Add(bitrateLabel);
            Add(details, controls, 0);
            var diagnostics = new StackPanel { IsHitTestVisible = false, Opacity = .8 };
            diagnostics.Children.Add(summary);
            watermark.FontSize = 12; watermark.Foreground = Brushes.White;
            watermark.Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#6017202B")),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = diagnostics,
                IsHitTestVisible = false, ClipToBounds = true
            };
            metrics.Margin = new Thickness(0, 4, 0, 5); metrics.Text = "正在启动真实捕获与 FFmpeg…";
            diagnostics.Children.Add(metrics);
            diagnostics.Children.Add(new TextBlock { Text = "分层耗时：最近一帧 / 近 1 秒平均（ms）；无新样本显示 —", Opacity = .8 });
            var timingGrid = new Grid { ColumnDefinitions = new("*,*,*"), RowDefinitions = new("Auto,Auto") };
            for (var i = 0; i < timings.Length; i++) { Grid.SetRow(timings[i], i / 3); Grid.SetColumn(timings[i], i % 3); timingGrid.Children.Add(timings[i]); }
            diagnostics.Children.Add(timingGrid); diagnostics.Children.Add(captureDetails); Add(details, operation, 1);
            var clipboardRow = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(0, 5, 0, 0) };
            clipboardRow.Children.Add(clipboardEnabled); Grid.SetColumn(clipboardMessage, 1); clipboardRow.Children.Add(clipboardMessage);
            Add(details, clipboardRow, 2);
            transmissionScale.ItemsSource = new[] { 1d, .75, .5 }.Select(scale => new ComboBoxItem { Content = scale == 1 ? "1× 原始分辨率" : $"{scale}× 宽高", Tag = scale }).ToArray();
            transmissionScale.SelectedIndex = config.TransmissionScale == 1 ? 0 : config.TransmissionScale == .75 ? 1 : 2;
            var scaleRow = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(0, 6, 0, 0) };
            scaleRow.Children.Add(transmissionScale);
            var scaleHelp = new TextBlock { Text = "传输比例 · 与主控窗口缩放无关", Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(scaleHelp, 1); scaleRow.Children.Add(scaleHelp); Add(details, scaleRow, 3);
            diagnostics.Children.Add(new TextBlock
            {
                Text = "带宽估计仅供参考。传输含限速与重组，渲染含等待绘制；GPU 完成不等于物理扫描。相同像素保持显示，静止绘制 FPS 可为 0。",
                TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 8, 0, 0)
            });

            var entries = config.Presets.Select(pair =>
            {
                var item = new ComboBoxItem { Content = pair.Value.Label, Tag = pair.Key, IsEnabled = pair.Value.Enabled };
                ToolTip.SetTip(item, pair.Value.UnavailableReason ?? pair.Value.EncoderArguments);
                return item;
            }).ToArray();
            presets.ItemsSource = entries;
            presets.SelectedItem = entries.FirstOrDefault(item => Equals(item.Tag, config.InitialPreset));
            bitrate.Maximum = config.MaximumBitrateMbps; bitrateLabel.Maximum = (decimal)config.MaximumBitrateMbps;
            bitrate.Value = Math.Clamp(config.InitialBitrateKbps / 1000d, .1, config.MaximumBitrateMbps); UpdateBitrateLabel();
            presets.IsEnabled = bitrate.IsEnabled = bitrateLabel.IsEnabled = false;
            presets.SelectionChanged += (_, _) => ScheduleApply();
            transmissionScale.SelectionChanged += (_, _) => ScheduleApply();
            bitrate.PropertyChanged += (_, args) =>
            {
                if (args.Property == Slider.ValueProperty) { UpdateBitrateLabel(); ScheduleApply(); }
            };
            bitrateLabel.ValueChanged += (_, _) =>
            {
                if (updatingBitrate || bitrateLabel.Value is not { } value) return;
                bitrate.Value = Math.Clamp((double)value, bitrate.Minimum, bitrate.Maximum);
            };
            refresh.Tick += (_, _) => RefreshStatus();
            debounce.Tick += (_, _) => ApplySelection();
            autoClose.Tick += (_, _) => { autoClose.Stop(); Close(); };
            collapse.Click += (_, _) => ToggleDetails();
            inputEnabled.IsCheckedChanged += (_, _) => ChangeInputToggle();
            clipboardEnabled.IsCheckedChanged += async (_, _) =>
            {
                if (updatingClipboard || stopping) return;
                try { await SetClipboardAsync(clipboardEnabled.IsChecked == true); }
                catch (Exception error)
                {
                    Console.Error.WriteLine("[clipboard] " + error); clipboardMessage.Text = error.Message;
                    updatingClipboard = true; clipboardEnabled.IsChecked = false; updatingClipboard = false;
                }
            };
            packetDiagnostics.IsCheckedChanged += (_, _) => ChangeDiagnostics();
            AddHandler(Avalonia.Input.InputElement.KeyDownEvent, ExitInputOnEscape, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            overlay.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, ExitInputOnEscape, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            overlay.Opened += (_, _) => { ExcludeFromCapture(overlay, "控制栏"); PositionOverlay(); };
            watermark.Opened += (_, _) => { ExcludeFromCapture(watermark, "诊断水印"); PositionOverlay(); };
            overlay.SizeChanged += (_, _) => PositionOverlay();
            watermark.SizeChanged += (_, _) => PositionOverlay();
            overlay.Closing += Shutdown;
            PositionChanged += (_, _) => PositionOverlay();
            PropertyChanged += (_, args) =>
            {
                if (args.Property == ClientSizeProperty || args.Property == BoundsProperty) PositionOverlay();
                if (args.Property == WindowStateProperty && WindowState == WindowState.Minimized) { overlay.Hide(); watermark.Hide(); }
                else if (args.Property == WindowStateProperty && started && !stopping && WindowState != WindowState.Minimized)
                { if (!watermark.IsVisible) watermark.Show(this); if (!overlay.IsVisible) overlay.Show(this); PositionOverlay(); }
            };
            Opened += Start;
            Closing += Shutdown;
        }

        static void Add(Grid grid, Control child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        void UpdateBitrateLabel() { updatingBitrate = true; bitrateLabel.Value = (decimal)bitrate.Value; updatingBitrate = false; }

        void ExitInputOnEscape(object? sender, Avalonia.Input.KeyEventArgs args)
        {
            if (args.Key != Avalonia.Input.Key.Escape || inputEnabled.IsChecked != true) return;
            preview.SetInputEnabled(false); inputEnabled.IsChecked = false; args.Handled = true;
        }

        void ToggleDetails()
        {
            details.IsVisible = !details.IsVisible; collapse.Content = details.IsVisible ? "收起 ▴" : "展开 ▾"; PositionOverlay();
        }

        async Task VerifyOverlayAsync()
        {
            await Task.Delay(100);
            if (stopping) return;
            var expanded = overlay.ClientSize.Height;
            var expandedBounds = CheckOverlayBounds(true);
            ToggleDetails(); await Task.Delay(100);
            if (stopping) return;
            var folded = overlay.ClientSize.Height;
            var foldedBounds = CheckOverlayBounds(false);
            var keptToggle = overlay.IsVisible && collapse.IsVisible && !details.IsVisible;
            ToggleDetails(); await Task.Delay(100);
            if (stopping) return;
            var nativeOwner = GetWindow(overlay.TryGetPlatformHandle()?.Handle ?? 0, 4) == (TryGetPlatformHandle()?.Handle ?? 0);
            var passThrough = watermark.VerifyPassThrough(preview.SourceWindow);
            overlayPassed = nativeOwner && passThrough && keptToggle && folded > 0 && folded < expanded && details.IsVisible && expandedBounds.Passed && foldedBounds.Passed;
            overlayCheck = new { Passed = overlayPassed, WatermarkMouseTransparent = passThrough, WatermarkCannotActivate = passThrough, WatermarkOpacity = .8, NativeOwnedWindow = nativeOwner, ExpandedHeight = expanded, CollapsedHeight = folded, CollapseButtonRemainsVisible = keptToggle, ExpandedBounds = expandedBounds, FoldedBounds = foldedBounds };
            if (!overlayPassed) throw new InvalidOperationException("真实浮层或折叠布局验证失败");
        }

        sealed record OverlayBounds(bool Passed, bool InsidePreview, bool ControlsInsideOverlay, double Width, double Height);

        OverlayBounds CheckOverlayBounds(bool expanded)
        {
            var origin = overlay.PointToScreen(new Point());
            var outerOrigin = this.PointToScreen(new Point());
            var width = overlay.ClientSize.Width * overlay.RenderScaling;
            var height = overlay.ClientSize.Height * overlay.RenderScaling;
            var inside = origin.X >= outerOrigin.X - 1 && origin.Y >= outerOrigin.Y - 1 &&
                origin.X + width <= outerOrigin.X + ClientSize.Width * RenderScaling + 1 &&
                origin.Y + height <= outerOrigin.Y + ClientSize.Height * RenderScaling + 1;
            Control[] controls = expanded ? [controlTitle, inputEnabled, packetDiagnostics, collapse, presets, bitrate, bitrateLabel, clipboardEnabled, transmissionScale] : [controlTitle, inputEnabled, packetDiagnostics, collapse];
            var contentInside = controls.All(control =>
            {
                var point = control.PointToScreen(new Point());
                return point.X >= origin.X - 1 && point.Y >= origin.Y - 1 &&
                    point.X + control.Bounds.Width * overlay.RenderScaling <= origin.X + width + 1 &&
                    point.Y + control.Bounds.Height * overlay.RenderScaling <= origin.Y + height + 1;
            });
            return new(inside && contentInside, inside, contentInside, overlay.ClientSize.Width, overlay.ClientSize.Height);
        }

        void PositionOverlay()
        {
            if (!overlay.IsVisible || ClientSize.Width <= 0) return;
            overlay.Width = Math.Max(340, Math.Min(details.IsVisible ? 1100 : 750, ClientSize.Width - 24));
            overlay.Position = this.PointToScreen(new Point(Math.Max(12, (ClientSize.Width - overlay.Width) / 2), 12));
            if (watermark.IsVisible)
            {
                watermark.Width = Math.Max(340, Math.Min(1100, ClientSize.Width - 24));
                watermark.MaxHeight = Math.Max(40, ClientSize.Height - overlay.ClientSize.Height - 36);
                watermark.Position = this.PointToScreen(new Point(12, Math.Max(12, ClientSize.Height - watermark.ClientSize.Height - 12)));
            }
        }

        void ExcludeFromCapture(Window window, string description)
        {
            if (!SetWindowDisplayAffinity(window.TryGetPlatformHandle()?.Handle ?? 0, 0x11))
                ReportError(new Win32Exception(Marshal.GetLastWin32Error(), $"无法将{description}排除出屏幕捕获"));
        }

        async void Start(object? sender, EventArgs args)
        {
            ExcludeFromCapture(this, "预览窗口");
            watermark.Show(this); overlay.Show(this); PositionOverlay();
            refresh.Start();
            try
            {
                if (remoteOptions == null)
                {
                    capture = new(config.TransmissionScale); config = config with { Width = capture.Width, Height = capture.Height };
                    session = new(config, capture.Capture, capture.Resize, capture.SourceWidth, capture.SourceHeight);
                }
                else session = new(config, remoteOptions);
                session.FrameReceived += frame => { Interlocked.Increment(ref receivedFrames); preview.Submit(frame); };
                session.StatusChanged += status => { lock (receiveGate) pendingStatus = status; };
                await session.StartAsync();
                if (remoteOptions != null)
                {
                    config = session.Configuration;
                    bitrate.Maximum = config.MaximumBitrateMbps; bitrateLabel.Maximum = (decimal)config.MaximumBitrateMbps;
                    bitrate.Value = config.InitialBitrateKbps / 1000d;
                    presets.ItemsSource = config.Presets.Select(entry => new ComboBoxItem
                        { Content = entry.Value.Label, Tag = entry.Key, IsEnabled = entry.Value.Enabled }).ToArray();
                    presets.SelectedItem = presets.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == config.InitialPreset);
                    transmissionScale.SelectedIndex = config.TransmissionScale == 1 ? 0 : config.TransmissionScale == .75 ? 1 : 2;
                }
                if (stopping) return;
                await StartInputAsync();
                if (session.IsRemote)
                    cursorClient = await session.ConnectRemoteCursorAsync(update => Dispatcher.UIThread.Post(() =>
                    {
                        if (stopping) return;
                        try { preview.SetRemoteCursor(update); if (InteractionScriptPath != null) interactionCursors.Add(update); }
                        catch (Exception error) { ReportError(error); }
                    }), error => Dispatcher.UIThread.Post(() => ReportError(error)), inputStop.Token);
                if (stopping) return;
                started = true; presets.IsEnabled = bitrate.IsEnabled = bitrateLabel.IsEnabled = transmissionScale.IsEnabled = true;
                inputEnabled.IsEnabled = packetDiagnostics.IsEnabled = true;
                clipboardEnabled.IsEnabled = session.IsRemote;
                if (!session.IsRemote) clipboardMessage.Text = "localhost 共用剪贴板；双机连接后可开启";
                if (InteractionScriptPath != null)
                {
                    await VerifyOverlayAsync();
                    await RunInteractionAsync(InteractionScriptPath); return;
                }
                if (autoCloseSeconds > 0)
                {
                    await VerifyOverlayAsync();
                    if (stopping) return;
                    autoClose.Interval = TimeSpan.FromSeconds(autoCloseSeconds); autoClose.Start();
                }
            }
            catch (OperationCanceledException) when (stopping) { Console.Error.WriteLine("Window startup cancelled during shutdown."); }
            catch (Exception ex)
            {
                ReportError(ex);
                if (autoCloseSeconds > 0) Close();
            }
        }

        void ScheduleApply()
        {
            if (!started || stopping) return;
            debounce.Stop(); debounce.Start();
        }

        async Task StartInputAsync()
        {
            if (session?.IsRemote == true)
            {
                inputClient = await session.ConnectRemoteInputAsync(inputStop.Token);
                inputWorker = Task.Run(SendInputLoopAsync); return;
            }
            inputInjector = new();
            inputInjector.RegisterControllerWindow(TryGetPlatformHandle()?.Handle ?? 0);
            inputInjector.RegisterControllerWindow(overlay.TryGetPlatformHandle()?.Handle ?? 0);
            inputInjector.RegisterControllerWindow(watermark.TryGetPlatformHandle()?.Handle ?? 0);
            inputInjector.RegisterControllerWindow(preview.SourceWindow);
            inputServer = new(inputInjector);
            inputServer.Failed += ex => Dispatcher.UIThread.Post(() => ReportError(ex));
            var client = await RemoteInputClient.ConnectAsync(inputServer.Endpoint, inputStop.Token);
            if (stopping || inputStop.IsCancellationRequested) { client.Dispose(); return; }
            inputClient = client;
            inputWorker = Task.Run(SendInputLoopAsync);
        }

        async Task SetClipboardAsync(bool enabled)
        {
            await clipboardTransition.WaitAsync();
            try
            {
                clipboardEnabled.IsEnabled = false;
                if (clipboardSync != null) { await clipboardSync.DisposeAsync(); clipboardSync = null; }
                if (enabled)
                {
                    clipboardSync = await session!.ConnectRemoteClipboardAsync(new WindowsClipboard(TryGetPlatformHandle()?.Handle ?? 0), inputStop.Token);
                    clipboardSync.Status += status => Dispatcher.UIThread.Post(() =>
                    {
                        clipboardMessage.Text = status.Message;
                        if (!status.Connected && !stopping) clipboardEnabled.IsChecked = false;
                    });
                }
                clipboardMessage.Text = enabled ? "已开启；同步两端新复制的内容" : "已关闭剪贴板同步";
            }
            finally { clipboardEnabled.IsEnabled = !stopping && session?.IsRemote == true; clipboardTransition.Release(); }
        }

        async void ChangeDiagnostics()
        {
            if (updatingDiagnostics || changingDiagnostics || !started || stopping || session == null) return;
            changingDiagnostics = true; packetDiagnostics.IsEnabled = false;
            try
            {
                var enabled = packetDiagnostics.IsChecked == true;
                var result = await session.SetPacketDiagnosticsAsync(enabled);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                operation.Text = enabled ? "独立诊断 UDP 包已开启，开销计入流量与码率上限。" : "诊断包已关闭；本机分层计时仍然可用。";
            }
            catch (Exception ex)
            {
                updatingDiagnostics = true;
                lock (receiveGate) packetDiagnostics.IsChecked = pendingStatus?.PacketDiagnosticsEnabled == true;
                updatingDiagnostics = false; ReportError(ex);
            }
            finally { changingDiagnostics = false; packetDiagnostics.IsEnabled = started && !stopping; }
        }

        async void ChangeInputToggle()
        {
            if (updatingInput || stopping) return;
            try { await SetInputAsync(inputEnabled.IsChecked == true); }
            catch (Exception ex) { ReportError(ex); }
        }

        async Task SetInputAsync(bool enabled)
        {
            if (!enabled) { preview.SetInputEnabled(false); lock (inputGate) inputQueue.Clear(); }
            await inputTransition.WaitAsync();
            inputEnabled.IsEnabled = false;
            try
            {
                enabled &= !stopping;
                if (inputClient == null) throw new InvalidOperationException("输入通道尚未连接");
                var result = await inputClient.SetEnabledAsync(enabled);
                if (!result.Accepted) throw new InvalidOperationException(result.Message);
                if (enabled && stopping) { await inputClient.SetEnabledAsync(false); enabled = false; }
                preview.SetInputEnabled(enabled);
                updatingInput = true; inputEnabled.IsChecked = enabled; updatingInput = false;
                operation.Text = enabled ? "键鼠转发已启用，按 Esc 退出。本机共用桌面，目标若是预览窗口会被拒绝；预览获得焦点时无法同时作为被控键盘目标。双机控制需独立桌面。" : "键鼠转发已关闭，按键已释放。";
            }
            catch
            {
                preview.SetInputEnabled(false);
                updatingInput = true; inputEnabled.IsChecked = false; updatingInput = false;
                throw;
            }
            finally { inputEnabled.IsEnabled = started && !stopping; inputTransition.Release(); }
        }

        void QueueInput(RemoteInputEvent input)
        {
            if (stopping || inputClient?.Enabled != true) return;
            var source = capture?.Statistics;
            var mapped = session?.IsRemote == true ? InputCoordinates.MapFromVideo(input, preview.VideoWidth, preview.VideoHeight, session.RemoteSourceWidth, session.RemoteSourceHeight) :
                source == null ? input : InputCoordinates.MapFromVideo(input, preview.VideoWidth, preview.VideoHeight, source.SourceWidth, source.SourceHeight);
            if (mapped == null) return;
            lock (inputGate)
            {
                if (mapped.Kind == RemoteInputKind.MouseMove && inputQueue.Last?.Value.Kind == RemoteInputKind.MouseMove)
                { inputQueue.Last.Value = mapped; coalescedMoves++; }
                else if (inputQueue.Count < 512) inputQueue.AddLast(mapped);
                else
                {
                    preview.SetInputEnabled(false);
                    inputEnabled.IsChecked = false;
                    ReportError(new InvalidOperationException("键鼠转发积压，已停止输入并释放按键；未继续累积事件。"));
                    return;
                }
                if (inputReady.CurrentCount == 0) inputReady.Release();
            }
        }

        async Task SendInputLoopAsync()
        {
            try
            {
                while (!inputStop.IsCancellationRequested)
                {
                    await inputReady.WaitAsync(inputStop.Token);
                    while (true)
                    {
                        RemoteInputEvent? next;
                        lock (inputGate) { next = inputQueue.First?.Value; if (next != null) inputQueue.RemoveFirst(); }
                        if (next == null) break;
                        var result = await inputClient!.SendAsync(next, inputStop.Token);
                        if (result.Accepted) Interlocked.Increment(ref inputSent);
                        else
                        {
                            Interlocked.Increment(ref inputRejected);
                            if (result.Message != lastInputMessage)
                            {
                                lastInputMessage = result.Message; Console.Error.WriteLine("[input rejected] " + result.Message);
                                Dispatcher.UIThread.Post(() => operation.Text = "输入未注入：" + result.Message);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (inputStop.IsCancellationRequested) { Console.Error.WriteLine("Input send queue stopped."); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Dispatcher.UIThread.Post(() => { preview.SetInputEnabled(false); inputEnabled.IsChecked = false; ReportError(ex); });
            }
        }

        async Task StopInputAsync()
        {
            preview.SetInputEnabled(false);
            try { if (inputClient != null) await SetInputAsync(false); }
            finally
            {
                inputStop.Cancel(); inputClient?.Dispose();
                if (inputWorker != null) await inputWorker;
                inputServer?.Dispose();
                inputInjector?.UnregisterControllerWindow(TryGetPlatformHandle()?.Handle ?? 0);
                inputInjector?.UnregisterControllerWindow(overlay.TryGetPlatformHandle()?.Handle ?? 0);
                inputInjector?.UnregisterControllerWindow(preview.SourceWindow);
                inputInjector?.Dispose();
            }
        }

        async void ApplySelection()
        {
            debounce.Stop();
            if (!started || stopping || session == null) return;
            if (applying) { applyAgain = true; return; }
            if (presets.SelectedItem is not ComboBoxItem { Tag: string presetId }) return;
            applying = true;
            var limit = (int)Math.Round(bitrate.Value * 1000);
            operation.Text = $"正在请求被控端应用 {presetId} / {limit / 1000d:0.###} Mbps…";
            try
            {
                var scale = transmissionScale.SelectedItem is ComboBoxItem { Tag: double selectedScale } ? selectedScale : config.TransmissionScale;
                var result = await session.ApplyAsync(presetId, limit, scale);
                config = session.Configuration;
                if (result.Success) successfulChanges++;
                operation.Text = result.Success ? $"已应用：{presetId} / {limit / 1000d:0.###} Mbps / {config.TransmissionScale}× {config.Width}×{config.Height}（会话 {result.Generation}）" : $"切换未生效，保留原会话：{result.Message}";
                if (!result.Success) Console.Error.WriteLine($"[UI control] {result.Message}");
            }
            catch (Exception ex) { ReportError(ex); }
            finally
            {
                applying = false;
                if (applyAgain && !stopping) { applyAgain = false; debounce.Start(); }
            }
        }

        void RefreshStatus()
        {
            SessionStatus? status;
            lock (receiveGate) status = pendingStatus;
            if (status is { } currentStatus)
            {
                string Mean(double value) => currentStatus.HasRenderTiming && currentStatus.HasRecentRenderTiming ? $"{value:F2}" : "—";
                var latency = currentStatus.HasRenderTiming ? $"最近 {currentStatus.CaptureToRenderMs:F1} / 近 1 秒平均 {Mean(currentStatus.MeanCaptureToRenderMs)} ms（{currentStatus.RenderTimingSamples} 帧）" : "等待首次 GPU 确认";
                summary.Text = $"{currentStatus.ActivePreset} · {currentStatus.AppliedLimitKbps / 1000d:0.###} Mbps · {currentStatus.RenderedFps:F1} FPS · 近 1 秒 {Mean(currentStatus.MeanCaptureToRenderMs)} ms";
                metrics.Text = $"发送 {currentStatus.SentMbps:F3} / 接收 {currentStatus.ReceivedMbps:F3} Mbps  |  带宽估计 {currentStatus.EstimatedMbps:F3} Mbps（参考）\n诊断包：{(currentStatus.PacketDiagnosticsEnabled ? "开" : "关")}，开销 {currentStatus.DiagnosticMbps * 1000:F2} kbps（已计入流量）\n捕获 → GPU 完成：{latency}  |  绘制 {currentStatus.RenderedFps:F1} / 解码 {currentStatus.DecodedFps:F1} FPS\n网络延迟趋势 {currentStatus.DelayTrendMs:+0.00;-0.00;0.00} ms  |  丢包 {currentStatus.LossRate:P1}  |  {currentStatus.Message}";
                if (!changingDiagnostics) { updatingDiagnostics = true; packetDiagnostics.IsChecked = currentStatus.PacketDiagnosticsEnabled; updatingDiagnostics = false; }
                metrics.Text += "\n" + currentStatus.TimingDescription;
                metrics.Text += status.SimulatedCapacityMbps > 0
                    ? $" · 专项链路模拟 {status.SimulatedCapacityMbps:0.##} Mbps"
                    : " · UDP 即时发送，滑条仅限制编码码率";
                timings[0].Text = $"捕获  {currentStatus.CaptureMs:F2} / {Mean(currentStatus.MeanCaptureMs)}";
                timings[1].Text = $"编码  {currentStatus.EncodeMs:F2} / {Mean(currentStatus.MeanEncodeMs)}";
                var transfer = currentStatus.TransferDetails;
                timings[2].Text = $"传输  {currentStatus.TransferMs:F2} / {Mean(currentStatus.MeanTransferMs)}" +
                    (transfer == null ? "" : $"\n最近 {transfer.PayloadBytes:N0} B / {transfer.Packets} 包\n发送侧 {transfer.SenderMs:F2}（计划等 {transfer.PlannedWaitMs:F2} / 额外等 {transfer.WakeupOverrunMs:F2}）\n末包发送→收齐 {transfer.LastSendToReassemblyMs:F2} / 接收排队 {transfer.ReceiveQueueMs:F2}");
                timings[3].Text = $"解码  {currentStatus.DecodeMs:F2} / {Mean(currentStatus.MeanDecodeMs)}";
                timings[4].Text = $"渲染  {currentStatus.RenderMs:F2} / {Mean(currentStatus.MeanRenderMs)}";
                timings[5].Text = $"合计  {currentStatus.CaptureToRenderMs:F2} / {Mean(currentStatus.MeanCaptureToRenderMs)}";
                if (session?.IsRemote == true && !currentStatus.HasRenderTiming)
                    foreach (var timing in timings) timing.Text = (timing.Text ?? "").Split(' ')[0] + "  — / —";
                var stages = capture?.Statistics;
                captureDetails.Text = session?.IsRemote == true ? $"被控桌面 {session.RemoteSourceWidth}×{session.RemoteSourceHeight} → {config.Width}×{config.Height}；本机仅接收、解码、渲染" : stages == null ? $"捕获后端：{capture?.Backend ?? "启动中"}" :
                    $"DXGI 捕获最近一次（ms）：取帧 {stages.AcquireMs:F2} / 缩放提交 {stages.GpuScaleSubmitMs:F2} / 回读等待 {stages.MapWaitAndReadbackMs:F2} / 像素复制 {stages.CpuRowCopyMs:F2}";
            }
        }

        async void Shutdown(object? sender, WindowClosingEventArgs args)
        {
            if (allowClose) return;
            args.Cancel = true;
            if (stopping) return;
            stopping = true; debounce.Stop(); autoClose.Stop();
            presets.IsEnabled = bitrate.IsEnabled = bitrateLabel.IsEnabled = transmissionScale.IsEnabled = packetDiagnostics.IsEnabled = inputEnabled.IsEnabled = clipboardEnabled.IsEnabled = false; operation.Text = "正在关闭捕获、FFmpeg 与 UDP…";
            try { await SetClipboardAsync(false); } catch (Exception ex) { ReportError(ex); }
            try { if (cursorClient != null) await cursorClient.DisposeAsync(); } catch (Exception ex) { ReportError(ex); }
            try { await StopInputAsync(); } catch (Exception ex) { ReportError(ex); }
            try { if (session != null) await session.StopAsync(); }
            catch (Exception ex) { ReportError(ex); }
            finally
            {
                refresh.Stop();
                preview.Stop();
                try { session?.Dispose(); } catch (Exception ex) { ReportError(ex); }
                captureBackend = capture?.Backend; captureStatistics = capture?.Statistics;
                try { capture?.Dispose(); } catch (Exception ex) { ReportError(ex); }
                WriteReport(); allowClose = true;
                Dispatcher.UIThread.Post(() => { watermark.Close(); overlay.Close(); Close(); });
            }
        }

        void ReportError(Exception ex)
        {
            Console.Error.WriteLine(ex); errors.Add(ex.ToString()); operation.Text = "错误：" + ex.Message;
        }

        void WriteReport()
        {
            if (reportPath == null) return;
            try
            {
                var path = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SessionStatus? status; long frames;
                lock (receiveGate) status = pendingStatus;
                frames = Interlocked.Read(ref receivedFrames);
                var presented = Interlocked.Read(ref shownFrames);
                var stageSum = status == null ? 0 : status.CaptureMs + status.EncodeMs + status.TransferMs + status.DecodeMs + status.RenderMs;
                var meanStageSum = status == null ? 0 : status.MeanCaptureMs + status.MeanEncodeMs + status.MeanTransferMs + status.MeanDecodeMs + status.MeanRenderMs;
                var timingPassed = status?.HasRenderTiming == true && Math.Abs(stageSum - status.CaptureToRenderMs) < .00001 && Math.Abs(meanStageSum - status.MeanCaptureToRenderMs) < .00001;
                var transport = status?.TransferDetails;
                var transportPassed = transport != null && Math.Abs(transport.SenderMs + transport.LastSendToReassemblyMs + transport.ReceiveQueueMs - status!.TransferMs) < .00001;
                var dxgi = session?.IsRemote == true || captureBackend?.StartsWith("DXGI", StringComparison.Ordinal) == true;
                var finalOverlayBounds = CheckOverlayBounds(details.IsVisible);
                var passed = started && frames > 0 && presented > 0 && timingPassed && transportPassed && dxgi && overlayPassed && finalOverlayBounds.Passed && errors.Count == 0;
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    Passed = passed, Technology = "Avalonia", RealDesktopCapture = true, RemoteController = session?.IsRemote == true, CanResize, Started = started,
                    DurationSeconds = (DateTime.UtcNow - startedUtc).TotalSeconds,
                    ReceivedFrames = frames, GpuConfirmedFrames = presented, SuccessfulManualChanges = successfulChanges,
                    ManualLimitMinimumMbps = bitrate.Minimum, ManualLimitMaximumMbps = bitrate.Maximum,
                    ManualLimitMinimumKbps = bitrate.Minimum * 1000, ManualLimitMaximumKbps = bitrate.Maximum * 1000,
                    Overlay = new { OwnedByPreview = overlay.Owner == this, Visible = overlay.IsVisible, Expanded = details.IsVisible, overlay.Width, Height = overlay.ClientSize.Height, Position = overlay.Position.ToString() },
                    OverlayRegression = overlayCheck, FinalOverlayBounds = finalOverlayBounds,
                    TimingRegression = new { Passed = timingPassed, TransferBreakdownPassed = transportPassed, LatestStageSumMs = stageSum, LatestTotalMs = status?.CaptureToRenderMs, MeanStageSumMs = meanStageSum, MeanTotalMs = status?.MeanCaptureToRenderMs },
                    Input = new { RequiresManualEnable = true, SentEvents = Interlocked.Read(ref inputSent), RejectedEvents = Interlocked.Read(ref inputRejected), CoalescedMouseMoves = coalescedMoves, PendingEvents = inputQueue.Count },
                    Status = status, Session = session?.GetReport(), Errors = errors,
                    Capture = captureBackend, CaptureStatistics = captureStatistics,
                    Render = "D3D11 upload → Present(0) → event query GPU completion; physical scan-out is not timed"
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (autoCloseSeconds > 0 && !passed) regressionExitCode = 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[UI report] {ex}"); regressionExitCode = 1; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
        [DllImport("user32.dll")] static extern nint GetWindow(nint window, uint relation);
    }

    sealed class PresentationTestWindow : Window
    {
        readonly ConfirmedVideoView view = new();
        readonly TextBlock progress = new() { Text = "GPU 呈现回归：A → 重复 A → 单像素变化 B → 重复 B", Margin = new Thickness(8) };
        readonly AppConfiguration config;
        readonly string reportPath;
        readonly CancellationTokenSource cancellation = new();
        readonly object gate = new();
        readonly List<long> presentedIds = new();
        readonly List<string> errors = new();
        readonly List<object> assertions = new();

        public PresentationTestWindow(AppConfiguration config, string reportPath)
        {
            this.config = config; this.reportPath = reportPath;
            Title = "FRD — GPU 呈现回归（完成后自动关闭）";
            Width = 960; Height = 640; CanResize = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var layout = new Grid { RowDefinitions = new("*,Auto"), Margin = new Thickness(8) };
            layout.Children.Add(new Border { Background = Brushes.Black, Child = view });
            Grid.SetRow(progress, 1); layout.Children.Add(progress); Content = layout;
            view.Presented += (id, _) => { lock (gate) presentedIds.Add(id); };
            view.Failed += ex => { Console.Error.WriteLine(ex); lock (gate) errors.Add(ex.ToString()); };
            Opened += RunTest;
            Closing += (_, _) => cancellation.Cancel();
            Closed += (_, _) => view.Stop();
        }

        int PresentedCount { get { lock (gate) return presentedIds.Count; } }

        async void RunTest(object? sender, EventArgs args)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await Task.Delay(80, cancellation.Token);
                CheckAspect("Initial window");
                var a = new byte[checked(config.Width * config.Height * 4)];
                for (var i = 0; i < a.Length; i += 4) { a[i] = 72; a[i + 1] = 48; a[i + 2] = 32; a[i + 3] = 255; }
                view.Submit(new(a, config.Width, config.Height, 100));
                await AwaitCount(1); CheckCount("A", 1);
                view.Submit(new((byte[])a.Clone(), config.Width, config.Height, 101));
                await Task.Delay(200, cancellation.Token); CheckCount("Repeated A", 1);
                var b = (byte[])a.Clone(); b[((config.Height / 2) * config.Width + config.Width / 2) * 4 + 2] = 255;
                view.Submit(new(b, config.Width, config.Height, 102));
                await AwaitCount(2); CheckCount("One pixel B", 2);
                view.Submit(new((byte[])b.Clone(), config.Width, config.Height, 103));
                await Task.Delay(200, cancellation.Token); CheckCount("Repeated B", 2);
                Width = 1200; Height = 800;
                await Task.Delay(100, cancellation.Token); CheckAspect("Resized window");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); lock (gate) errors.Add(ex.ToString()); }
            finally
            {
                view.Stop();
                try
                {
                    var passed = errors.Count == 0 && assertions.Count == 6 && presentedIds.SequenceEqual([100L, 102L]);
                    var path = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(new
                    {
                        Passed = passed, Scope = "Production ConfirmedVideoView; no codec, capture or transport. D3D11 event confirms GPU completion after Present submission; physical scanout is not timed.",
                        DurationSeconds = elapsed.Elapsed.TotalSeconds, ExpectedGpuCounts = new[] { 1, 1, 2, 2 },
                        PresentedFrameIds = presentedIds, Assertions = assertions, Errors = errors
                    }, AppConfiguration.JsonOptions));
                    regressionExitCode = passed ? 0 : 1;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); regressionExitCode = 1; }
                Close(); cancellation.Dispose();
            }
        }

        async Task AwaitCount(int count)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (PresentedCount < count)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException($"Expected {count} GPU completions, observed {PresentedCount}.");
                await Task.Delay(10, cancellation.Token);
            }
        }

        void CheckCount(string stage, int expected)
        {
            var actual = PresentedCount;
            assertions.Add(new { Stage = stage, ExpectedGpuCount = expected, ActualGpuCount = actual, Passed = actual == expected });
            if (actual != expected) throw new InvalidOperationException($"{stage}: expected {expected} GPU completions, got {actual}.");
        }

        void CheckAspect(string stage)
        {
            var expected = config.Width / (double)config.Height;
            var actual = view.Bounds.Width / view.Bounds.Height;
            var passed = view.Bounds.Width > 0 && view.Bounds.Height > 0 && Math.Abs(actual - expected) < .005;
            assertions.Add(new { Stage = stage, WindowWidth = Width, WindowHeight = Height, ViewWidth = view.Bounds.Width, ViewHeight = view.Bounds.Height, ExpectedAspect = expected, ActualAspect = actual, Passed = passed });
            if (!passed) throw new InvalidOperationException($"{stage}: rendered view has aspect {actual}, expected {expected}.");
        }
    }
}
