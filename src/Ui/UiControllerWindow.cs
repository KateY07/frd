using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Text.Json;
using static Glasspane.UiNative;

namespace Frd;

/// <summary>The controller (viewer) window: native Win32 + Direct3D11/Direct2D, no UI framework.</summary>
sealed class UiControllerWindow : UiWindow
{
    internal static int RegressionExitCode;

    AppConfiguration config;
    readonly RemoteOptions? remoteOptions;
    readonly string? reportPath;
    readonly int autoCloseSeconds;
    readonly object sceneGate = new();
    readonly UiControlPanel panel;
    readonly UiVideoView preview;
    readonly UiText measureText;
    readonly UiDevices measureDevices;
    NativeInputSource? inputSource;
    readonly UiTimer refresh = new(100), debounce = new(150), autoClose = new(1000), idleTimer = new(500), animationTimer = new(16);
    readonly UiLabel clipboardMessage = new("关闭；开启后同步两端新复制的内容");
    UiChoice presets => panel.Presets;
    UiSlider bitrate => panel.Bitrate;
    UiButton inputEnabled => panel.Input;
    UiButton clipboardEnabled => panel.Clipboard;
    UiButton packetDiagnostics => panel.PacketDiagnostics;
    UiButton showInfo => panel.ShowInfo;
    UiLabel operation => panel.Operation;
    UiButton[] fpsButtons => panel.Fps;
    UiLabel bitrateLabel => panel.BitrateLabel;
    int selectedFps;
    string[] infoLines = ["正在启动真实捕获与编解码…"];
    string summary = "FRD · 真实屏幕", captureDetails = "", metricsText = "";
    readonly string[] timings = new string[6];
    readonly object receiveGate = new();
    readonly object inputGate = new();
    readonly LinkedList<RemoteInputEvent> inputQueue = new();
    readonly SemaphoreSlim inputReady = new(0, 1), inputTransition = new(1, 1);
    readonly CancellationTokenSource inputStop = new();
    CancellationTokenSource? inputChannelStop;
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
    DateTime stoppingSince;
    bool started, stopping, allowClose, applying, applyAgain, disconnected, restartRequested, scheduledClose;
    long receivedFrames, shownFrames;
    int successfulChanges;
    long lastActivity = Environment.TickCount64;
    bool inputForwarding, trackingMouse;
    Vector2 lastPointer;
    long hoverSince;
    UiElement? tooltipTarget;

    public UiControllerWindow(AppConfiguration config, int autoCloseSeconds, string? reportPath, RemoteOptions? remote = null)
    {
        remoteOptions = remote;
        this.config = config; this.autoCloseSeconds = autoCloseSeconds; this.reportPath = reportPath;
        selectedFps = config.FramesPerSecond;
        IsMainWindow = true; MinWidth = 680; MinHeight = 460;
        var title = autoCloseSeconds > 0 ? "FRD — 自动回归（完成后关闭）" : "FRD — 真实屏幕 / localhost UDP";
        if (remote != null) title = $"FRD 主控端 — {remote.Host}:{remote.Port}";
        CreateNative(title, WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, 0, unchecked((int)0x80000000), unchecked((int)0x80000000), 1280, 860);
        SetClientSize((int)(1280 * Scale), (int)(860 * Scale));
        CenterOnScreen();

        measureDevices = new UiDevices();
        measureText = new UiText(measureDevices.DWrite);
        panel = new UiControlPanel(sceneGate);
        panel.Wire(() => preview?.Invalidate());
        panel.StatusName.Text = remote?.Host ?? "本机演示";
        panel.Backdrop = (paint, rect, radius, _) => preview!.DrawGlass(paint, rect, radius);   // preview is assigned right below, before the first draw
        panel.AnimationFinished += _ => { UiThread.Post(() => { UpdateOrbShape(); }); };
        panel.InteractionStarted = () => lastActivity = Environment.TickCount64;

        preview = new UiVideoView(this);
        preview.Presented += (frameId, tick) => { Interlocked.Increment(ref shownFrames); session?.ReportPresented(frameId, tick); };
        preview.Failed += ex => { if (UiThread.CheckAccess()) ReportError(ex); else UiThread.Post(() => ReportError(ex)); };
        preview.DrawOverlay = DrawOverlay;
        preview.NeedsContinuousRender = () => panel.Animating;

        inputSource = new NativeInputSource(Handle, subclass: false)
        {
            VideoArea = () => preview.VideoAreaPixels,
            PointerIsLocal = (x, y) => { lock (sceneGate) return panel.IsCapturing || panel.Contains(new Vector2(x / Scale, y / Scale)); }
        };
        inputSource.Input += QueueInput;
        inputSource.ExitRequested += () => { inputForwarding = false; inputEnabled.IsChecked = false; };
        inputSource.LocalShortcutRequested += HandleLocalShortcut;
        inputSource.Failed += ex => UiThread.Post(() => ReportError(ex));
        inputSource.SetEnabled(false);

        // ---- presets, slider, fps
        foreach (var pair in config.Presets)
            presets.Items.Add(new UiChoiceItem(pair.Value.Label, pair.Key, pair.Value.Enabled, pair.Value.UnavailableReason ?? pair.Value.EncoderArguments));
        presets.SelectedIndex = Math.Max(-1, presets.Items.FindIndex(item => item.Tag == config.InitialPreset));
        bitrate.Maximum = config.MaximumBitrateMbps;
        bitrate.Value = Math.Clamp(config.InitialBitrateKbps / 1000d, .1, config.MaximumBitrateMbps); UpdateBitrateLabel();
        presets.IsEnabled = bitrate.IsEnabled = false;
        foreach (var button in fpsButtons)
        {
            button.IsEnabled = false;
            button.Click += () => { selectedFps = (int)button.Tag!; UpdateFpsButtons(); ScheduleApply(); };
        }
        UpdateFpsButtons();
        presets.SelectionChanged += () => { UpdateBitrateAvailability(); ScheduleApply(); };
        presets.OpenChanged += () => { lock (sceneGate) panel.Relayout(measureText); };
        bitrate.ValueChanged += () => { UpdateBitrateLabel(); ScheduleApply(); };
        panel.More.CheckedChanged += () => { lock (sceneGate) panel.Relayout(measureText); preview.Invalidate(); };
        panel.More.Click += () => { };
        refresh.Tick += RefreshStatus;
        debounce.Tick += ApplySelection;
        autoClose.Tick += () => { scheduledClose = true; autoClose.Stop(); Close(); };
        idleTimer.Tick += CheckIdle;
        animationTimer.Tick += AnimationTick;
        panel.Reconnect.Click += () => { restartRequested = true; Close(); };
        inputEnabled.CheckedChanged += () => ChangeInputToggle();
        clipboardEnabled.CheckedChanged += async () =>
        {
            if (updatingClipboard || stopping) return;
            try { await SetClipboardAsync(clipboardEnabled.IsChecked == true); }
            catch (Exception error)
            {
                Console.Error.WriteLine("[clipboard] " + error); clipboardMessage.Text = error.Message;
                updatingClipboard = true; clipboardEnabled.IsChecked = false; updatingClipboard = false;
            }
        };
        packetDiagnostics.CheckedChanged += () => ChangeDiagnostics();
        showInfo.IsChecked = true;
        showInfo.CheckedChanged += () => preview.Invalidate();
        panel.FullScreen.Click += () => ToggleFullScreenWithState();
        panel.Disconnect.Click += () => Close();
        panel.Collapse.Click += () => AnimateChrome(true);
        panel.Input.ToolTip = remote != null ? "转发到被控端；Ctrl+Alt 退出并释放按键。" : "转发鼠标移动、点击、拖动、滚轮和键盘；Ctrl+Alt 退出并释放按键。";
        panel.Clipboard.ToolTip = "剪贴板同步（文本、文件、目录）";

        Closing += Shutdown;
        Closed += () =>
        {
            SessionTrace.Event("window", restartRequested ? "closed-for-manual-new-session" : disconnected ?
                "closed-after-disconnect" : scheduledClose ? "scheduled-close" : "user-or-test-close");
            if (restartRequested) LaunchNewSession();
        };
        ExcludeFromCapture();
        preview.Start();
        lock (sceneGate) { panel.Layout(measureText, ClientWidth, ClientHeight); }
        Show(SW_SHOWNORMAL);
        // The first ShowWindow call is overridden by the process start-up hint (scripts may start us minimized); the second one applies.
        if (IsIconic(Handle)) Show(SW_RESTORE);
        RaiseOpened();
        Start();
    }

    // ---- state helpers ---------------------------------------------------------------------------------------------

    bool HasSelectedBitrateCap() => presets.SelectedTag is string id && config.Presets.TryGetValue(id, out var preset) && preset.BitrateControlled;

    void UpdateBitrateAvailability()
    {
        bitrate.IsEnabled = started && !stopping && HasSelectedBitrateCap();
        UpdateBitrateLabel();
    }

    void UpdateBitrateLabel() => bitrateLabel.Text = HasSelectedBitrateCap() ? $"码率上限 · {bitrate.Value:0.0##} Mbps" : "无损模式 · 不限制码率";

    void ScheduleApply()
    {
        if (!started || stopping) return;
        debounce.Stop(); debounce.Start();
    }

    void UpdateFpsButtons()
    {
        foreach (var button in fpsButtons) { button.IsToggle = true; button.IsChecked = (int)button.Tag! == selectedFps; }
    }

    static string FormatMs(double value) => value < 10 ? $"{value:0.0} ms" : $"{value:0} ms";

    void UpdateStatusDot()
    {
        var text = operation.Text ?? "";
        var color = text.Contains("中断") || text.StartsWith("错误") || text.Contains("失败") ? UiTheme.Bad
            : text.StartsWith("已连接") || text.StartsWith("已应用") || text.Contains("已启用") || text.Contains("已开启") ? UiTheme.Good : UiTheme.Warn;
        if (text.StartsWith("正在", StringComparison.Ordinal) && panel.StatusColor != UiTheme.Warn) return;
        panel.StatusColor = color;
    }

    /// <summary>Collapsed form: a plain orb until connected, then a pill that keeps the network latency visible.</summary>
    void UpdateOrbShape()
    {
        var pill = autoCloseSeconds == 0 && started && !disconnected;
        lock (sceneGate) { panel.UsePill = pill; panel.Relayout(measureText); }
        preview.Invalidate();
    }

    void Touch() => lastActivity = Environment.TickCount64;

    void CheckIdle()
    {
        UpdateStatusDot();
        if (stopping || panel.Animating || panel.Collapsed || panel.IsPointerOver) return;
        if (autoCloseSeconds > 0) return;
        if (Environment.TickCount64 - lastActivity >= 10_000) AnimateChrome(true);
    }

    void AnimateChrome(bool toCollapsed)
    {
        if (panel.Animating) return;
        Touch();
        lock (sceneGate) panel.StartAnimation(measureText, toCollapsed);
        animationTimer.Start();
    }

    void AnimationTick()
    {
        bool animating;
        lock (sceneGate) animating = panel.Tick(measureText);
        if (!animating) animationTimer.Stop();
    }

    void ToggleFullScreenWithState()
    {
        if (stopping) return;
        ToggleFullScreen();
        panel.FullScreen.Icon = State == UiWindowState.FullScreen ? "shrink" : "expand";
        panel.FullScreen.ToolTip = State == UiWindowState.FullScreen ? "退出全屏（Ctrl+Alt+F11）" : "全屏（Ctrl+Alt+F11）";
        lock (sceneGate) panel.Relayout(measureText);
        preview.Invalidate();
    }

    void HandleLocalShortcut(int key)
    {
        if (key == 0x7A) UiThread.Post(ToggleFullScreenWithState);
        else if (key == 0x11) { if (inputEnabled.IsChecked == true) { inputForwarding = false; inputEnabled.IsChecked = false; } }
    }

    // ---- session startup -------------------------------------------------------------------------------------------

    async void Start()
    {
        refresh.Start(); Touch(); idleTimer.Start();
        try
        {
            if (remoteOptions == null)
            {
                capture = new(config.TransmissionScale); config = config with { Width = capture.Width, Height = capture.Height };
                session = new(config, capture.Capture, capture.Resize, capture.SourceWidth, capture.SourceHeight,
                    capture.CaptureMapped, capture.SetPixelMode);
            }
            else session = new(config, remoteOptions);
            session.Failed += error => UiThread.Post(() => ReportError(error));
            session.FrameReceived += frame => { Interlocked.Increment(ref receivedFrames); preview.Submit(new VideoFrame(frame.Bgra, frame.Width, frame.Height, frame.Pts)); };
            session.StatusChanged += status => { lock (receiveGate) pendingStatus = status; };
            await session.StartAsync();
            if (remoteOptions != null)
            {
                config = session.Configuration;
                selectedFps = config.FramesPerSecond; UpdateFpsButtons();
                bitrate.Maximum = config.MaximumBitrateMbps;
                bitrate.Value = config.InitialBitrateKbps / 1000d;
                presets.Items.Clear();
                foreach (var entry in config.Presets) presets.Items.Add(new UiChoiceItem(entry.Value.Label, entry.Key, entry.Value.Enabled, entry.Value.UnavailableReason ?? entry.Value.EncoderArguments));
                presets.SelectedIndex = Math.Max(-1, presets.Items.FindIndex(item => item.Tag == config.InitialPreset));
            }
            if (stopping) return;
            await StartInputAsync();
            if (session.IsRemote)
                cursorClient = await session.ConnectRemoteCursorAsync(update => UiThread.Post(() =>
                {
                    if (stopping) return;
                    try { inputSource?.SetRemoteCursor(update); }
                    catch (Exception error) { ReportError(error); }
                }), error => UiThread.Post(() =>
                {
                    Console.Error.WriteLine("[cursor] Auxiliary channel failed; video session remains active: " + error);
                    operation.Text = "远端光标同步中断；视频会话仍在运行。";
                }), inputStop.Token);
            if (stopping) return;
            started = true; UpdateOrbShape(); presets.IsEnabled = true; UpdateBitrateAvailability();
            foreach (var button in fpsButtons) button.IsEnabled = true;
            inputEnabled.IsEnabled = packetDiagnostics.IsEnabled = true;
            clipboardEnabled.IsEnabled = session.IsRemote;
            operation.Text = $"已连接：{config.InitialPreset} / {(HasSelectedBitrateCap() ? $"{config.InitialBitrateKbps / 1000d:0.###} Mbps" : "无损不限码率")} / 原始分辨率 {config.Width}×{config.Height}";
            if (!session.IsRemote) clipboardMessage.Text = "localhost 共用剪贴板；双机连接后可开启";
            if (session.IsRemote && autoCloseSeconds == 0)
            {
                // Keyboard/mouse and clipboard sharing default to on wherever the connection supports them.
                if (inputEnabled.IsEnabled) inputEnabled.IsChecked = true;
                if (clipboardEnabled.IsEnabled) clipboardEnabled.IsChecked = true;
                // Latency needs the lightweight diagnostic packets; keep them on so it is always readable.
                if (packetDiagnostics.IsEnabled) packetDiagnostics.IsChecked = true;
            }
            if (autoCloseSeconds > 0)
            {
                if (stopping) return;
                autoClose.IntervalMilliseconds = autoCloseSeconds * 1000; autoClose.Start();
            }
        }
        catch (OperationCanceledException) when (stopping) { Console.Error.WriteLine("Window startup cancelled during shutdown."); }
        catch (Exception ex)
        {
            ReportError(ex);
            RegressionExitCode = 1;
            Close();
        }
    }

    // ---- business logic carried over from the previous window (verbatim apart from control types) ------------------

        async Task StartInputAsync()
        {
            if (session?.IsRemote == true)
            {
                if (inputWorker != null) await inputWorker;
                inputClient = await session.ConnectRemoteInputAsync(inputStop.Token);
                inputChannelStop?.Dispose();
                inputChannelStop = CancellationTokenSource.CreateLinkedTokenSource(inputStop.Token);
                var connected = inputClient;
                var channelToken = inputChannelStop.Token;
                inputWorker = Task.Run(() => SendInputLoopAsync(connected, channelToken)); return;
            }
            inputInjector = new();
            inputInjector.RegisterControllerWindow(Handle);
            inputInjector.RegisterControllerWindow(Handle);
            inputServer = new(inputInjector);
            inputServer.Failed += ex => UiThread.Post(() => ReportError(ex));
            var client = await RemoteInputClient.ConnectAsync(inputServer.Endpoint, inputStop.Token);
            if (stopping || inputStop.IsCancellationRequested) { client.Dispose(); return; }
            inputClient = client;
            inputChannelStop = CancellationTokenSource.CreateLinkedTokenSource(inputStop.Token);
            var localToken = inputChannelStop.Token;
            inputWorker = Task.Run(() => SendInputLoopAsync(client, localToken));
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
                    clipboardSync = await session!.ConnectRemoteClipboardAsync(new WindowsClipboard(Handle), inputStop.Token);
                    clipboardSync.Status += status => UiThread.Post(() =>
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
                operation.Text = enabled ? "独立诊断 UDP 包已开启，开销计入实际流量；滑条只限制编码码率。" : "诊断包已关闭；本机分层计时仍然可用。";
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

        async Task SetInputAsync(bool enabled, CancellationToken token = default)
        {
            if (!enabled) { SetPreviewInputEnabled(false); lock (inputGate) inputQueue.Clear(); }
            await inputTransition.WaitAsync();
            inputEnabled.IsEnabled = false;
            try
            {
                enabled &= !stopping;
                if (enabled && inputClient == null) await StartInputAsync();
                if (inputClient == null) throw new InvalidOperationException("输入通道尚未连接");
                var result = await inputClient.SetEnabledAsync(enabled, token);
                if (!result.Accepted) throw new InvalidOperationException(result.Message);
                if (enabled && stopping) { await inputClient.SetEnabledAsync(false); enabled = false; }
                SetPreviewInputEnabled(enabled);
                updatingInput = true; inputEnabled.IsChecked = enabled; updatingInput = false;
                operation.Text = enabled ? "键鼠转发已启用，按 Ctrl+Alt 退出并释放按键。"
                    : "键鼠转发已关闭，按键已释放。";
            }
            catch
            {
                SetPreviewInputEnabled(false);
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
                    SetPreviewInputEnabled(false);
                    var overflow = new InvalidOperationException("键鼠转发积压，已停止输入并释放按键；未继续累积事件。");
                    if (session?.IsRemote == true) HandleInputFailure(inputClient!, overflow);
                    else ReportError(overflow);
                    return;
                }
                if (inputReady.CurrentCount == 0) inputReady.Release();
            }
        }

        async Task SendInputLoopAsync(RemoteInputClient connected, CancellationToken channelToken)
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(channelToken);
            List<Task> confirmations = new();
            Exception? failure = null;
            try
            {
                while (!sending.IsCancellationRequested)
                {
                    await inputReady.WaitAsync(sending.Token);
                    while (true)
                    {
                        RemoteInputEvent? next;
                        lock (inputGate) { next = inputQueue.First?.Value; if (next != null) inputQueue.RemoveFirst(); }
                        if (next == null) break;
                        // Preserve wire order, but let the next input leave before this event's remote confirmation.
                        var confirmation = await connected.QueueAsync(next, sending.Token);
                        confirmations.RemoveAll(task => task.IsCompleted);
                        confirmations.Add(ObserveAsync(confirmation));
                    }
                }
            }
            catch (OperationCanceledException) when (sending.IsCancellationRequested) { Console.Error.WriteLine("Input send queue stopped."); }
            catch (Exception error) { Fail(error); }
            finally { await Task.WhenAll(confirmations); }

            async Task ObserveAsync(Task<RemoteInputResult> confirmation)
            {
                try
                {
                    var result = await confirmation;
                    if (result.Accepted) Interlocked.Increment(ref inputSent);
                    else
                    {
                        Interlocked.Increment(ref inputRejected);
                        lock (inputGate)
                        {
                            if (result.Message == lastInputMessage) return;
                            lastInputMessage = result.Message;
                        }
                        Console.Error.WriteLine("[input rejected] " + result.Message);
                        UiThread.Post(() => operation.Text = "输入未注入：" + result.Message);
                    }
                }
                catch (OperationCanceledException) when (inputStop.IsCancellationRequested) { Console.Error.WriteLine("Input confirmation stopped."); }
                catch (Exception error) { Fail(error); }
            }

            void Fail(Exception error)
            {
                if (Interlocked.CompareExchange(ref failure, error, null) != null) return;
                Console.Error.WriteLine(error);
                connected.Dispose();
                sending.Cancel();
                UiThread.Post(() => HandleInputFailure(connected, error));
            }
        }

        void HandleInputFailure(RemoteInputClient connected, Exception error)
        {
            if (stopping || !ReferenceEquals(inputClient, connected)) return;
            SessionTrace.Error("input-auxiliary", error);
            inputChannelStop?.Cancel(); connected.Dispose(); inputClient = null;
            SetPreviewInputEnabled(false);
            lock (inputGate) inputQueue.Clear();
            updatingInput = true; inputEnabled.IsChecked = false; updatingInput = false;
            operation.Text = "键鼠通道已中断；视频仍在运行。重新勾选“键鼠”可建立新的输入通道。";
        }

        async Task StopInputAsync()
        {
            SetPreviewInputEnabled(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { if (inputClient != null) await SetInputAsync(false, timeout.Token); }
            finally
            {
                inputStop.Cancel(); inputChannelStop?.Cancel(); inputClient?.Dispose();
                if (inputWorker != null) await inputWorker;
                inputChannelStop?.Dispose();
                inputServer?.Dispose();
                inputInjector?.Dispose();
            }
        }

        async void ApplySelection()
        {
            debounce.Stop();
            if (!started || stopping || session == null) return;
            if (applying) { applyAgain = true; return; }
            if (presets.SelectedTag is not string presetId) return;
            applying = true;
            var limit = (int)Math.Round(bitrate.Value * 1000);
            var rateText = HasSelectedBitrateCap() ? $"{limit / 1000d:0.###} Mbps" : "无损不限码率";
            operation.Text = $"正在请求被控端应用 {presetId} / {rateText} / {selectedFps} FPS…";
            try
            {
                var result = await session.ApplyAsync(presetId, limit, framesPerSecond: selectedFps);
                config = session.Configuration;
                if (result.Success) successfulChanges++;
                if (!result.Success) { selectedFps = config.FramesPerSecond; UpdateFpsButtons(); }
                operation.Text = result.Success ? $"已应用：{presetId} / {rateText} / {config.FramesPerSecond} FPS / {config.Width}×{config.Height}（会话 {result.Generation}）" : $"切换未生效，保留原会话：{result.Message}";
                if (!result.Success) Console.Error.WriteLine($"[UI control] {result.Message}");
            }
            catch (Exception ex) { ReportError(ex); }
            finally
            {
                applying = false;
                if (applyAgain && !stopping) { applyAgain = false; debounce.Start(); }
            }
        }

        void LaunchNewSession()
        {
            if (remoteOptions == null) return;
            try
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "FRD.exe");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                foreach (var argument in new[] { "--connect", remoteOptions.Host, "--port", remoteOptions.Port.ToString(),
                    "--token", remoteOptions.Token }) start.ArgumentList.Add(argument);
                using var launched = Process.Start(start) ?? throw new InvalidOperationException("未能启动新的主控进程。");
                SessionTrace.Event("window", "user-requested-new-process-and-session");
            }
            catch (Exception error)
            {
                SessionTrace.Error("manual-new-session", error);
                Console.Error.WriteLine(error);
            }
        }


    // ---- status ------------------------------------------------------------------------------------------------------

    void RefreshStatus()
    {
        if (!stopping && session?.IsRemote == true && inputClient is { Failure: { } inputError } failedInput)
            HandleInputFailure(failedInput, inputError);
        if (disconnected)
        {
            summary = "原会话已断开 · 等待手动建立新会话";
            metricsText = "查看控制栏中的断连原因；原 TCP 连接不会自动重新握手。";
            UpdateInfoLines();
            return;
        }
        SessionStatus? status;
        lock (receiveGate) status = pendingStatus;
        var remoteWait = session?.RemoteStatusWaitSeconds ?? 0;
        if (status is { } currentStatus)
        {
            string Mean(double value) => currentStatus.HasRenderTiming && currentStatus.HasRecentRenderTiming ? $"{value:F2}" : "—";
            var latency = currentStatus.HasRenderTiming ? $"最近 {currentStatus.CaptureToRenderMs:F1} / 近 1 秒平均 {Mean(currentStatus.MeanCaptureToRenderMs)} ms（{currentStatus.RenderTimingSamples} 帧）" : "等待首次 GPU 确认";
            var cap = currentStatus.AppliedLimitKbps > 0 ? $"{currentStatus.AppliedLimitKbps / 1000d:0.###} Mbps" : "无损不限码率";
            summary = $"{currentStatus.ActivePreset} · {cap} · {currentStatus.RenderedFps:F1} FPS · 近 1 秒 {Mean(currentStatus.MeanCaptureToRenderMs)} ms";
            metricsText = $"发送 {currentStatus.SentMbps:F3} / 接收 {currentStatus.ReceivedMbps:F3} Mbps  |  带宽估计 {currentStatus.EstimatedMbps:F3} Mbps（参考）\n诊断包：{(currentStatus.PacketDiagnosticsEnabled ? "开" : "关")}，开销 {currentStatus.DiagnosticMbps * 1000:F2} kbps（已计入流量）\n捕获 → GPU 完成：{latency}  |  绘制 {currentStatus.RenderedFps:F1} / 解码 {currentStatus.DecodedFps:F1} FPS\n网络延迟趋势 {currentStatus.DelayTrendMs:+0.00;-0.00;0.00} ms  |  丢包 {currentStatus.LossRate:P1}  |  {currentStatus.Message}";
            var haveLatency = currentStatus.HasRenderTiming && currentStatus.HasRecentRenderTiming;
            var latencyText = haveLatency ? FormatMs(currentStatus.MeanTransferMs) : "— ms";
            panel.Latency.Text = latencyText; lock (sceneGate) panel.PillText = latencyText;
            panel.Latency.ToolTip = haveLatency
                ? $"网络延迟（传输）{FormatMs(currentStatus.MeanTransferMs)} · 端到端 {FormatMs(currentStatus.MeanCaptureToRenderMs)} · 丢包 {currentStatus.LossRate:P1}"
                : "等待诊断包计时；关闭“诊断包”后跨机延迟不可测";
            if (!changingDiagnostics) { updatingDiagnostics = true; packetDiagnostics.IsChecked = currentStatus.PacketDiagnosticsEnabled; updatingDiagnostics = false; }
            metricsText += "\n" + currentStatus.TimingDescription;
            metricsText += status.SimulatedCapacityMbps > 0
                ? $" · 专项链路模拟 {status.SimulatedCapacityMbps:0.##} Mbps"
                : " · UDP 即时发送，滑条仅限制编码码率";
            if (remoteWait >= 3)
            {
                summary = $"网络路径暂停 {remoteWait:F0} 秒 · 等待原会话恢复";
                metricsText = $"控制连接仍未被明确关闭；已等待 {remoteWait:F1} 秒。画面与统计可能暂时停留在上一状态。\n" + metricsText;
            }
            timings[0] = $"捕获  {currentStatus.CaptureMs:F2} / {Mean(currentStatus.MeanCaptureMs)}";
            timings[1] = $"编码  {currentStatus.EncodeMs:F2} / {Mean(currentStatus.MeanEncodeMs)}";
            timings[2] = $"传输  {currentStatus.TransferMs:F2} / {Mean(currentStatus.MeanTransferMs)}";
            timings[3] = $"解码  {currentStatus.DecodeMs:F2} / {Mean(currentStatus.MeanDecodeMs)}";
            timings[4] = $"渲染  {currentStatus.RenderMs:F2} / {Mean(currentStatus.MeanRenderMs)}";
            timings[5] = $"合计  {currentStatus.CaptureToRenderMs:F2} / {Mean(currentStatus.MeanCaptureToRenderMs)}";
            if (session?.IsRemote == true && !currentStatus.HasRenderTiming)
                for (var i = 0; i < timings.Length; i++) timings[i] = (timings[i] ?? "").Split(' ')[0] + "  — / —";
            var stages = capture?.Statistics;
            captureDetails = session?.IsRemote == true ? $"被控桌面 {session.RemoteSourceWidth}×{session.RemoteSourceHeight} → {session.Configuration.Width}×{session.Configuration.Height}；本机仅接收、解码、渲染" : stages == null ? $"捕获后端：{capture?.Backend ?? "启动中"}" :
                $"DXGI 捕获最近一次（ms）：取帧 {stages.AcquireMs:F2} / 缩放提交 {stages.GpuScaleSubmitMs:F2} / 回读等待 {stages.MapWaitAndReadbackMs:F2} / 像素复制 {stages.CpuRowCopyMs:F2}";
        }
        panel.Clipboard.ToolTip = clipboardMessage.Text;
        UpdateInfoLines();
    }

    void UpdateInfoLines()
    {
        if (showInfo.IsChecked != true) return;
        var lines = new List<string> { summary };
        lines.AddRange(metricsText.Split('\n'));
        lines.Add("分层耗时：最近 / 近 1 秒平均（ms）");
        lines.Add(string.Join("   ", timings.Where(t => t != null).Select(t => t.Split('\n')[0])));
        if (captureDetails.Length > 0) lines.Add(captureDetails);
        Volatile.Write(ref infoLines, lines.ToArray());
        preview.Invalidate();
    }

    // ---- shutdown ----------------------------------------------------------------------------------------------------

    async void Shutdown(UiClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (stopping)
        {
            // A second close request after the graceful path has been stuck for a while means "just close it".
            if (DateTime.UtcNow - stoppingSince > TimeSpan.FromSeconds(2)) ShutdownGuard.Kill("controller");
            return;
        }
        stopping = true; stoppingSince = DateTime.UtcNow; debounce.Stop(); autoClose.Stop();
        ShutdownGuard.Arm("controller", TimeSpan.FromSeconds(10));
        SessionTrace.Event("window", disconnected ? "closing-after-disconnect" : scheduledClose ? "closing-scheduled" : "closing-by-user-or-test");
        presets.IsEnabled = bitrate.IsEnabled = packetDiagnostics.IsEnabled = inputEnabled.IsEnabled = clipboardEnabled.IsEnabled = false; operation.Text = "正在关闭…（再次点击关闭可强制退出）";
        var sessionStopped = true;
        try { await ShutdownGuard.Bounded(() => SetClipboardAsync(false), "clipboard"); } catch (Exception ex) { ReportError(ex); }
        try { if (cursorClient != null) await ShutdownGuard.Bounded(() => cursorClient.DisposeAsync().AsTask(), "cursor"); } catch (Exception ex) { ReportError(ex); }
        try { await ShutdownGuard.Bounded(StopInputAsync, "input"); } catch (Exception ex) { ReportError(ex); }
        try { if (session != null) sessionStopped = await ShutdownGuard.Bounded(() => session.StopAsync(), "session", TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            refresh.Stop(); idleTimer.Stop(); animationTimer.Stop();
            preview.Stop();
            // Dispose() waits for the same stop task, so skip it if that task is still stuck; the process exit reclaims it.
            try { if (sessionStopped) session?.Dispose(); } catch (Exception ex) { ReportError(ex); }
            captureBackend = capture?.Backend; captureStatistics = capture?.Statistics;
            try { capture?.Dispose(); } catch (Exception ex) { ReportError(ex); }
            WriteReport(); allowClose = true;
            UiThread.Post(Close);
        }
    }

    void ReportError(Exception ex)
    {
        SessionTrace.Error("controller-window", ex);
        Console.Error.WriteLine(ex); errors.Add(ex.ToString()); operation.Text = "错误：" + ex.Message;
        RegressionExitCode = 1;
        if (stopping) return;
        if (remoteOptions == null) { UiThread.Post(Close); return; }
        disconnected = true;
        inputForwarding = false; inputSource?.SetEnabled(false);
        presets.IsEnabled = bitrate.IsEnabled = packetDiagnostics.IsEnabled = inputEnabled.IsEnabled = clipboardEnabled.IsEnabled = false;
        foreach (var button in fpsButtons) button.IsEnabled = false;
        panel.SetReconnectVisible(true); panel.More.IsChecked = true; UpdateOrbShape();
        operation.Text = $"原连接已中断：{ex.Message}；点击“重新建立会话”将创建新连接。";
        SessionTrace.Event("window", "disconnected-awaiting-user");
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
            var passed = started && frames > 0 && presented > 0 && timingPassed && transportPassed && dxgi && errors.Count == 0;
            File.WriteAllText(path, ReportJson.Serialize(new
            {
                Passed = passed, Technology = "Direct3D11 + Direct2D", RealDesktopCapture = true, RemoteController = session?.IsRemote == true, Started = started,
                DurationSeconds = (DateTime.UtcNow - startedUtc).TotalSeconds,
                ReceivedFrames = frames, GpuConfirmedFrames = presented, SuccessfulManualChanges = successfulChanges,
                ManualLimitMinimumMbps = bitrate.Minimum, ManualLimitMaximumMbps = bitrate.Maximum,
                ManualLimitMinimumKbps = bitrate.Minimum * 1000, ManualLimitMaximumKbps = bitrate.Maximum * 1000,
                Panel = new { Collapsed = panel.Collapsed, Expanded = !panel.Collapsed, Rect = panel.CurrentRect.ToString() },
                TimingRegression = new { Passed = timingPassed, TransferBreakdownPassed = transportPassed, LatestStageSumMs = stageSum, LatestTotalMs = status?.CaptureToRenderMs, MeanStageSumMs = meanStageSum, MeanTotalMs = status?.MeanCaptureToRenderMs },
                Input = new { RequiresManualEnable = true, SystemInputInjection = true, SentEvents = Interlocked.Read(ref inputSent), RejectedEvents = Interlocked.Read(ref inputRejected), CoalescedMouseMoves = coalescedMoves, PendingEvents = inputQueue.Count },
                Status = status, Session = session?.GetReport(), Errors = errors,
                Capture = captureBackend, CaptureStatistics = captureStatistics,
                Render = "D3D11 upload → Direct2D → Present(0) → event query GPU completion; physical scan-out is not timed"
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (autoCloseSeconds > 0 && !passed) RegressionExitCode = 1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[UI report] {ex}"); RegressionExitCode = 1; }
    }

    // ---- drawing (render thread) ----------------------------------------------------------------------------------

    void DrawOverlay(UiPaint paint, float width, float height)
    {
        var lines = Volatile.Read(ref infoLines);
        if (showInfo.IsChecked == true && lines.Length > 0 && width > 200)
        {
            var boxWidth = Math.Min(920, width - 24);
            var lineHeight = 15f;
            var boxHeight = 12 + lines.Length * lineHeight;
            var box = new RectangleF(12, Math.Max(12, height - boxHeight - 12), boxWidth, boxHeight);
            paint.FillRounded(box, 8, UiPaint.Hex(0xB817202B));
            for (var i = 0; i < lines.Length; i++) paint.DrawText(lines[i], box.X + 10, box.Y + 6 + i * lineHeight, 11, UiPaint.WithAlpha(UiPaint.Hex(0xFFFFFFFF), .85f), 400, true, box.Width - 20);
        }
        lock (sceneGate)
        {
            panel.Draw(paint);
            DrawTooltip(paint, width, height);
        }
    }

    void DrawTooltip(UiPaint paint, float width, float height)
    {
        var target = tooltipTarget;
        if (target?.ToolTip is not { Length: > 0 } tip || Environment.TickCount64 - hoverSince < 500) return;
        var size = paint.Text.Measure(tip, 11);
        var x = Math.Clamp(lastPointer.X + 12, 6, width - size.X - 22); var y = Math.Clamp(lastPointer.Y + 20, 6, height - size.Y - 14);
        var box = new RectangleF(x, y, size.X + 16, size.Y + 10);
        paint.FillRounded(box, 6, UiPaint.Hex(0xF01B2027));
        paint.StrokeRounded(box, 6, UiTheme.Border);
        paint.DrawText(tip, box.X + 8, box.Y + 5, 11, UiTheme.Text);
    }

    // ---- window messages -----------------------------------------------------------------------------------------

    protected override void OnResized()
    {
        if (panel == null || measureText == null) return;     // size messages arrive while the window is still being built
        lock (sceneGate) panel.Layout(measureText, (int)(ClientWidth / Scale), (int)(ClientHeight / Scale));
        preview?.Invalidate();
    }

    protected override void OnDpiChanged() => OnResized();

    protected override void OnDestroying()
    {
        inputSource?.Dispose(); inputSource = null;
        preview.Dispose();
        foreach (var timer in new[] { refresh, debounce, autoClose, idleTimer, animationTimer }) timer.Dispose();
        measureDevices.Dispose();
    }

    protected override nint OnMessage(uint message, nuint wParam, nint lParam, out bool handled)
    {
        handled = false;
        if (inputSource != null && inputSource.TryProcess(message, wParam, lParam, out var forwarded)) { handled = true; return forwarded; }
        switch (message)
        {
            case WM_MOUSEMOVE:
            {
                if (!trackingMouse) { var track = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle }; TrackMouseEvent(ref track); trackingMouse = true; }
                var point = ToDip(lParam);
                lastPointer = point;
                lock (sceneGate)
                {
                    panel.PointerMove(point);
                    var hot = panel.Contains(point) ? HoverElement(point) : null;
                    if (!ReferenceEquals(hot, tooltipTarget)) { tooltipTarget = hot; hoverSince = Environment.TickCount64; }
                }
                preview.Invalidate();
                return 0;
            }
            case WM_MOUSELEAVE:
                trackingMouse = false;
                lock (sceneGate) { panel.PointerLeave(); tooltipTarget = null; }
                preview.Invalidate();
                return 0;
            case WM_LBUTTONDOWN:
            {
                var point = ToDip(lParam);
                bool consumed;
                lock (sceneGate) consumed = panel.PointerDown(point);
                if (consumed) { SetCapture(Handle); handled = true; }
                SetFocus(Handle);
                return 0;
            }
            case WM_LBUTTONUP:
            {
                var point = ToDip(lParam);
                var orbClick = false; var consumed = false;
                lock (sceneGate) orbClick = panel.PointerUp(point, out consumed);
                if (consumed) { if (GetCapture() == Handle) ReleaseCapture(); handled = true; }
                if (orbClick) AnimateChrome(false);
                preview.Invalidate();
                return 0;
            }
            case WM_MOUSEWHEEL:
            {
                var screen = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                ScreenToClient(Handle, ref screen);
                var point = new Vector2(screen.X / Scale, screen.Y / Scale);
                bool consumed;
                lock (sceneGate) consumed = panel.Wheel(point, HiWord(wParam) / 120f);
                if (consumed) { handled = true; preview.Invalidate(); }
                return 0;
            }
            case WM_SETCURSOR when LoWord(lParam) == 1:
            {
                GetCursorPos(out var position);
                ScreenToClient(Handle, ref position);
                var point = new Vector2(position.X / Scale, position.Y / Scale);
                bool over; bool hot;
                lock (sceneGate) { over = panel.Contains(point); hot = over && HoverElement(point) is { IsEnabled: true }; }
                if (over || !(inputForwarding)) { SetCursor(LoadCursor(0, hot ? IDC_HAND : IDC_ARROW)); handled = true; return 1; }
                return 0;
            }
            case WM_KEYDOWN when wParam == 0x7B:   // F12 toggles the diagnostic overlay
                showInfo.IsChecked = showInfo.IsChecked != true; handled = true; return 0;
            case WM_SIZE:
                return 0;
        }
        return 0;
    }

    UiElement? HoverElement(Vector2 point)
    {
        foreach (var element in panel.AllElements) if (element.IsVisible && element.IsHovered && element.ToolTip != null) return element;
        return null;
    }

    Vector2 ToDip(nint lParam) => new(LoWord(lParam) / Scale, HiWord(lParam) / Scale);

    // ---- input forwarding bridge ---------------------------------------------------------------------------------------

    void SetPreviewInputEnabled(bool enabled)
    {
        inputForwarding = enabled;
        inputSource?.SetEnabled(enabled);
    }
}
