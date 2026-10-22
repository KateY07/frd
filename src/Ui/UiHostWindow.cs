using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using static Glasspane.UiNative;

namespace Frd;

/// <summary>The host's floating status card (permissions, who is connected, helper service, quit).</summary>
sealed class UiHostPanel : UiGlassPanel
{
    public readonly UiLabel Status = new(Loc.T("等待主控"), 13) { MaxWidth = 150 };
    public readonly UiButton Input = new() { Label = "", Icon = "mouse", IsToggle = true, IsVisible = false, ToolTip = Loc.T("键鼠：允许主控使用键盘和鼠标") };
    public readonly UiButton Clipboard = new() { Label = "", Icon = "clipboard", IsToggle = true, IsVisible = false, ToolTip = Loc.T("剪贴板：允许与主控同步") };
    public readonly UiButton Annotate = new() { Label = "", Icon = "pen", IsToggle = true, IsVisible = false, ToolTip = Loc.T("标注：允许主控在本机屏幕上画笔") };
    public readonly UiButton Shot = new() { Label = "", Icon = "camera", ToolTip = Loc.T("截屏：截取本机屏幕，在图片上标注后保存或复制") };
    public readonly UiButton Disconnect = new() { Icon = "power", IsDanger = true, IsVisible = false, ToolTip = Loc.T("断开当前主控") };
    public readonly UiButton More = new() { Icon = "sliders", IsToggle = true, ToolTip = Loc.T("监听地址、会话时长与安全桌面服务") };
    public readonly UiButton Collapse = new() { Icon = "chevrons", ToolTip = Loc.T("收起为悬浮球（空闲 10 秒自动收起）") };
    public readonly UiLabel ListenTitle = new(Loc.T("监听"), 11) { Color = UiTheme.Subtle }, DurationTitle = new(Loc.T("会话时长"), 11) { Color = UiTheme.Subtle }, ServiceTitle = new(Loc.T("安全桌面服务"), 11) { Color = UiTheme.Subtle };
    public readonly UiLabel ListenValue = new("", 12) { MaxWidth = 200 }, DurationValue = new("—", 12), ServiceValue = new("", 12);
    public readonly UiButton Install = new() { Label = Loc.T("安装安全桌面辅助服务"), IsVisible = false };
    public readonly UiButton Quit = new() { Label = Loc.T("退出被控端"), Icon = "power", IsDanger = true, ToolTip = Loc.T("停止监听并退出被控端（正在被控制时会先断开）") };
    readonly UiElement[] elements;
    readonly UiElement[] moreElements;
    float dotX, dotY;
    bool installVisible;
    RectangleF transferRect;

    /// <summary>The running file transfer, shown as a text line and a thin bar under the card's first row.</summary>
    public ClipboardTransferProgress? Transfer { get; set; }

    public UiHostPanel(object gate) : base(gate)
    {
        MaxWidth = 320;
        moreElements = [ListenTitle, DurationTitle, ServiceTitle, ListenValue, DurationValue, ServiceValue, Install, Quit];
        elements = [Status, Input, Clipboard, Annotate, Shot, Disconnect, More, Collapse, .. moreElements];
        Input.IsChecked = true; Clipboard.IsChecked = true; Annotate.IsChecked = true;
    }

    public bool MoreOpen => More.IsChecked == true;
    public bool Controlled { get; set; }
    public IEnumerable<UiElement> AllElements => elements;
    protected override IEnumerable<UiElement> Elements => elements;
    public void SetInstallVisible(bool visible) { installVisible = visible; Install.IsVisible = visible; Invalidate?.Invoke(); }
    public void Wire(Action invalidate) { Invalidate = invalidate; foreach (var element in elements) element.Invalidate = invalidate; }

    const float TransferRowHeight = 34;

    float MeasureBase()
    {
        var height = 28f;
        if (!MoreOpen) return height;
        height += 11 + 3 * 22;
        if (installVisible) height += 4 + 28;
        return height + 4 + 28;
    }

    protected override float MeasureContent(UiText text, float width) => MeasureBase() + (Transfer != null ? TransferRowHeight : 0);

    protected override void LayoutContent(UiText text, RectangleF content)
    {
        var x = content.X; var y = content.Y; var width = content.Width;
        var buttons = new List<UiButton>();
        if (Controlled) buttons.AddRange([Input, Clipboard, Annotate, Disconnect]);
        buttons.AddRange([Shot, More, Collapse]);
        foreach (var hidden in new[] { Input, Clipboard, Annotate, Disconnect }) hidden.IsVisible = Controlled;
        var sizes = buttons.Select(button => button.Measure(text, 0)).ToArray();
        var buttonsWidth = sizes.Sum(size => size.X) + 2 * (buttons.Count - 1);
        var buttonsLeft = x + width - buttonsWidth;
        var cursor = buttonsLeft;
        for (var i = 0; i < buttons.Count; i++) { buttons[i].Bounds = new RectangleF(cursor, y, sizes[i].X, 28); cursor += sizes[i].X + 2; }
        dotX = x + 12; dotY = y + 14;
        Status.MaxWidth = Math.Max(30, buttonsLeft - (x + 23) - 8);
        var statusSize = Status.Measure(text, 0);
        Status.Bounds = new RectangleF(x + 23, y + 14 - statusSize.Y / 2, Math.Min(statusSize.X, Status.MaxWidth), statusSize.Y);
        foreach (var element in moreElements) element.IsVisible = MoreOpen && (element != Install || installVisible);
        transferRect = Transfer != null ? new RectangleF(x + 6, content.Y + MeasureBase() + 2, width - 12, TransferRowHeight - 4) : default;
        if (!MoreOpen) return;
        y += 28 + 11;
        var left = x + 6; var valueX = left + 92;
        void Row(UiLabel title, UiLabel value)
        {
            var t = title.Measure(text, 0); var v = value.Measure(text, 0);
            title.Bounds = new RectangleF(left, y + 11 - t.Y / 2, 90, t.Y);
            value.Bounds = new RectangleF(valueX, y + 11 - v.Y / 2, Math.Max(10, width - 12 - 92), v.Y);
            y += 22;
        }
        ListenValue.MaxWidth = Math.Max(40, width - 104);
        Row(ListenTitle, ListenValue); Row(DurationTitle, DurationValue); Row(ServiceTitle, ServiceValue);
        if (installVisible) { var s = Install.Measure(text, 0); Install.Bounds = new RectangleF(left - 6, y + 4, s.X, 28); y += 32; }
        var q = Quit.Measure(text, 0);
        Quit.Bounds = new RectangleF(left - 6, y + 4, q.X, 28);
    }

    protected override void DrawContent(UiPaint paint, float opacity)
    {
        paint.FillCircle(new Vector2(dotX, dotY), 4, UiPaint.WithAlpha(StatusColor, opacity));
        foreach (var element in elements) if (element.IsVisible) element.Draw(paint, opacity);
        if (MoreOpen) paint.FillRounded(new RectangleF(CurrentRect.X + 11, CurrentRect.Y + 5 + 28 + 5, CurrentRect.Width - 22, 1), 0, UiPaint.WithAlpha(UiTheme.Line, opacity));
        if (Transfer is { } transfer && transferRect.Width > 0)
        {
            paint.DrawText(transfer.Describe(), transferRect.X, transferRect.Y, 11, UiPaint.WithAlpha(UiTheme.Subtle, opacity), 400, false, transferRect.Width);
            var track = new RectangleF(transferRect.X, transferRect.Y + 20, transferRect.Width, 4);
            paint.FillRounded(track, 2, UiPaint.WithAlpha(UiTheme.Track, opacity));
            paint.FillRounded(new RectangleF(track.X, track.Y, Math.Max(4, track.Width * (float)transfer.Fraction), track.Height), 2, UiPaint.WithAlpha(UiTheme.Thumb, opacity));
        }
    }
}

/// <summary>Host-side status window: translucent, click-through outside the card, never takes focus, invisible to capture.</summary>
sealed class UiHostWindow : UiWindow
{
    const float WindowWidthDip = 360 + UiGlassPanel.Margin * 2, WindowHeightDip = 300;
    readonly RemoteHost server;
    readonly string listening;
    readonly UiHostPanel panel;
    readonly object sceneGate = new();
    readonly UiDevices devices;
    readonly UiText text;
    readonly UiBackdrop backdrop = new();
    UiAnnotationOverlay? overlay;
    UiSurface? surface;
    UiPaint? paint;
    readonly UiTimer tick = new(500), animationTimer = new(16);
    DateTime connectedAt, closingSince;
    long lastActivity = Environment.TickCount64;
    bool closing, finished, controlled, renderQueued, trackingMouse, nativeMovePending;
    const uint WM_RENDER = WM_APP + 2;

    public UiHostWindow(AppConfiguration config, IPAddress[] addresses, int port, string token, CodecProbeSample[]? probeSamples)
    {
        IsMainWindow = true;
        listening = Loc.F("{0}:{1}", string.Join(Loc.T("、"), addresses.Select(a => a.ToString())), port);
        var dpi = (int)UiNative.GetDpiForSystem();
        var scale = dpi / 96f;
        CreateNative(Loc.F("FRD 被控端 · TCP {0}", port), WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
            0, 0, (int)(WindowWidthDip * scale), (int)(WindowHeightDip * scale));
        devices = new UiDevices();
        text = new UiText(devices.DWrite);
        panel = new UiHostPanel(sceneGate) { PanelFraction = new(1, 1), OrbFraction = new(1, 1) };
        panel.ListenValue.Text = listening; panel.ListenValue.ToolTip = listening;
        panel.Wire(RequestRender);
        panel.Backdrop = (p, rect, radius, _) => backdrop.Draw(p, rect, radius);
        panel.InteractionStarted = () => lastActivity = Environment.TickCount64;
        panel.AnimationFinished += _ => UiThread.Post(() => { ApplyRegion(); });
        panel.BackgroundPressed = _ => { nativeMovePending = true; return true; };
        panel.More.CheckedChanged += () => { lock (sceneGate) panel.Relayout(text); ApplyRegion(); RequestRender(); };
        panel.Collapse.Click += () => Animate(true);
        panel.Quit.Click += () => Close();
        panel.Disconnect.Click += () => server!.DisconnectActive();
        panel.Clipboard.CheckedChanged += () => server!.SetClipboardAllowed(panel.Clipboard.IsChecked == true);
        panel.Input.CheckedChanged += () => server!.SetInputAllowed(panel.Input.IsChecked == true);
        panel.Annotate.CheckedChanged += () =>
        {
            var allowed = panel.Annotate.IsChecked == true;
            server!.SetAnnotationAllowed(allowed);
            if (!allowed) overlay?.Reset();
        };
        panel.Install.Click += InstallHelper;
        panel.Shot.Click += TakeScreenshot;

        server = new(config, addresses, port, token, probeSamples);
        server.ClipboardProgress += value => UiThread.Post(() =>
        {
            var changedShape = (panel.Transfer == null) != (value == null);
            lock (sceneGate) { panel.Transfer = value; if (changedShape) panel.Relayout(text); }
            if (changedShape) ApplyRegion();
            RequestRender();
        });
        server.AnnotationReceived += message =>
        {
            var current = overlay;
            if (current != null) { current.Enqueue(message); return; }
            UiThread.Post(() =>
            {
                try { (overlay ??= new UiAnnotationOverlay()).Enqueue(message); }
                catch (Exception error) { Console.Error.WriteLine("[annotation] " + error.Message); }
            });
        };
        LocalInputMonitor.Start();
        server.LocalInputBusy = () => LocalInputMonitor.IsActive;
        server.ControllerChanged += address => UiThread.Post(() => OnControllerChanged(address));
        server.Status += message => UiThread.Post(() => panel.Status.ToolTip = message);
        server.Failed += error => UiThread.Post(() => { Console.Error.WriteLine(error); RemoteLaunch.hostExitCode = 1; Close(); });
        tick.Tick += OnTick; animationTimer.Tick += AnimationTick;
        Closing += OnClosing;

        try
        {
            Dock();
            if (!ExcludeFromCapture()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), Loc.T("被控端提示窗口无法排除出屏幕捕获"));
            // Never take keyboard focus: remote key events must not be able to operate this panel.
            SetWindowLongPtr(Handle, GWL_EXSTYLE, (nint)((ulong)GetWindowLongPtr(Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE));
            surface = new UiSurface(devices, Handle, true);
            paint = new UiPaint(devices, surface.Context, text);
            Show(SW_SHOWNOACTIVATE);
            if (IsIconic(Handle)) Show(SW_SHOWNOACTIVATE);
            SetTopmost(true);
            lock (sceneGate) { panel.UsePill = false; UpdatePill(); panel.Layout(text, (int)(ClientWidth / Scale), (int)(ClientHeight / Scale)); }
            ApplyRegion();
            server.Start(Handle);
            RefreshSecureHelper(); Touch(); tick.Start();
            RequestRender();
            RaiseOpened();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error); panel.Status.Text = error.Message; RemoteLaunch.hostExitCode = 1;
            UiThread.Post(Close);
        }
    }

    void Touch() => lastActivity = Environment.TickCount64;

    /// <summary>Grabs the screen (the FRD cards exclude themselves from capture) and opens the annotation editor on the picture.</summary>
    void TakeScreenshot()
    {
        try
        {
            var (bgra, width, height) = ScreenGrab.CapturePrimary();
            new UiShotWindow(bgra, width, height);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[screenshot] " + error);
            panel.Status.ToolTip = Loc.T("截屏失败：") + error.Message;
        }
    }

    void Dock()
    {
        var area = MonitorWorkArea();
        var bounds = OuterBounds;
        SetPosition(area.X + area.Width - bounds.Width, area.Y + area.Height - bounds.Height);
    }

    void ApplyRegion() { }       // the window is click-through outside the card (WM_NCHITTEST) and transparent there, so no clip region is needed

    void UpdatePill()
    {
        var label = controlled ? panel.Status.Text : "";
        panel.UsePill = controlled;
        panel.PillText = label;
        panel.PillWidth = controlled ? Math.Min(280, text.Measure(label, 12).X + 28 + 14 + 18 + 10) : 116;
    }

    // ---- state -----------------------------------------------------------------------------------------------------

    void OnControllerChanged(IPAddress? address)
    {
        controlled = address != null;
        if (controlled) connectedAt = DateTime.UtcNow; else overlay?.Reset();
        lock (sceneGate)
        {
            panel.Controlled = controlled;
            panel.Status.Text = controlled ? Loc.F("被 {0} 控制", address) : Loc.T("等待主控");
            panel.StatusColor = controlled ? UiTheme.Good : UiTheme.Warn;
            if (!controlled) panel.DurationValue.Text = "—";
            UpdatePill();
            panel.Relayout(text);
        }
        RefreshSecureHelper(); Touch();
        if (controlled && panel.Collapsed) Animate(false);
        ApplyRegion(); RequestRender();
    }

    bool localBusyReported;

    void OnTick()
    {
        var busy = LocalInputMonitor.IsActive;
        if (busy != localBusyReported)
        {
            localBusyReported = busy;
            if (controlled) Console.Error.WriteLine(busy ? "[input] Local keyboard/mouse in use: remote input is paused." : "[input] Local keyboard/mouse idle: remote input resumes.");
        }
        if (controlled && panel.MoreOpen) { lock (sceneGate) panel.DurationValue.Text = (DateTime.UtcNow - connectedAt).ToString(@"hh\:mm\:ss"); }
        if (closing || panel.Animating || panel.Collapsed || panel.IsPointerOver) return;
        if (Environment.TickCount64 - lastActivity >= 10_000) Animate(true);
    }

    void Animate(bool toCollapsed)
    {
        if (panel.Animating || closing) return;
        Touch();
        lock (sceneGate) { UpdatePill(); panel.StartAnimation(text, toCollapsed); }
        animationTimer.Start();
    }

    void AnimationTick()
    {
        bool animating;
        lock (sceneGate) animating = panel.Tick(text);
        ApplyRegion();
        if (!animating) { animationTimer.Stop(); ApplyRegion(); }
    }

    void RefreshSecureHelper()
    {
        var helper = SecureDesktopService.Status();
        lock (sceneGate)
        {
            panel.ServiceValue.Text = helper.Running ? Loc.T("运行中") : helper.Installed ? Loc.T("已安装，未运行") : Loc.T("未安装");
            panel.ServiceValue.Color = helper.Running ? UiTheme.Good : UiTheme.Warn;
            panel.ServiceValue.ToolTip = helper.Message;
            // Always available: a running service can still be out of date or broken, so it can be reinstalled (the installer offers install/overwrite or uninstall).
            panel.SetInstallVisible(true);
            panel.Install.IsEnabled = true;
            panel.Install.Label = helper.Running ? Loc.T("修复/重新安装安全桌面辅助服务") : helper.Installed ? Loc.T("启动/修复安全桌面辅助服务") : Loc.T("安装安全桌面辅助服务");
            panel.Install.ToolTip = helper.Message;
            panel.Relayout(text);
        }
        ApplyRegion(); RequestRender();
    }

    async void InstallHelper()
    {
        panel.Install.IsEnabled = false; panel.Install.Label = Loc.T("等待管理员确认…");
        try { await SecureDesktopService.InstallViaUacAsync(); SessionTrace.Event("secure-helper", "refresh-ui"); RefreshSecureHelper(); SessionTrace.Event("secure-helper", "refresh-ui-done"); }
        catch (Exception error)
        {
            SessionTrace.Error("secure-helper", error);
            Console.Error.WriteLine("[secure helper] Installation failed: " + error);
            panel.Install.Label = Loc.T("安装失败，点击重试");
            panel.Install.IsEnabled = true;
            panel.Install.ToolTip = error.Message;
            lock (sceneGate) panel.Relayout(text);
            ApplyRegion(); RequestRender();
        }
    }

    async void OnClosing(UiClosingEventArgs args)
    {
        if (finished) return;
        args.Cancel = true;
        if (closing) { if (DateTime.UtcNow - closingSince > TimeSpan.FromSeconds(2)) ShutdownGuard.Kill("host"); return; }
        closing = true; closingSince = DateTime.UtcNow; tick.Stop();
        ShutdownGuard.Arm("host", TimeSpan.FromSeconds(10));
        try { await ShutdownGuard.Bounded(() => server.DisposeAsync().AsTask(), "host-server", TimeSpan.FromSeconds(6)); }
        catch (Exception error) { Console.Error.WriteLine(error); RemoteLaunch.hostExitCode = 1; }
        finally { finished = true; UiThread.Post(Close); }
    }

    // ---- rendering (UI thread) -------------------------------------------------------------------------------------

    void RequestRender()
    {
        if (renderQueued || IsDestroyed) return;
        renderQueued = true;
        PostMessage(Handle, WM_RENDER, 0, 0);
    }

    void Render()
    {
        renderQueued = false;
        if (surface == null || paint == null || IsDestroyed || ClientWidth <= 0 || State == UiWindowState.Minimized) return;
        try
        {
            surface.Resize(ClientWidth, ClientHeight, Dpi);
            surface.Begin();
            surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
            var bounds = OuterBounds;
            lock (sceneGate)
            {
                backdrop.Capture(paint, panel.CurrentRect, new Point(bounds.X, bounds.Y), Scale);
                panel.Draw(paint);
            }
            DrawTooltip();
            surface.End();
            surface.Present(1).CheckError();
        }
        catch (Exception error) { Console.Error.WriteLine($"[UI draw] {error.Message}"); }
    }

    void DrawTooltip()
    {
        var hot = panel.HoveredElement;
        if (hot?.ToolTip is not { Length: > 0 } tip || Environment.TickCount64 - hoverSince < 500) return;
        var size = paint!.Text.Measure(tip, 11);
        var anchor = hot.Bounds;
        var x = Math.Clamp(anchor.X + anchor.Width / 2 - size.X / 2 - 8, 4, ClientWidth / Scale - size.X - 20);
        var y = Math.Max(4, anchor.Y - size.Y - 18);
        var box = new RectangleF(x, y, size.X + 16, size.Y + 10);
        paint.FillRounded(box, 6, UiPaint.Hex(0xF01B2027));
        paint.StrokeRounded(box, 6, UiTheme.Border);
        paint.DrawText(tip, box.X + 8, box.Y + 5, 11, UiTheme.Text);
    }

    UiElement? hoverTarget;
    long hoverSince;

    // ---- messages ------------------------------------------------------------------------------------------------

    protected override void OnResized() { if (panel == null) return; lock (sceneGate) panel.Layout(text, (int)(ClientWidth / Scale), (int)(ClientHeight / Scale)); ApplyRegion(); RequestRender(); }
    protected override void OnDpiChanged() => OnResized();

    protected override void OnDestroying()
    {
        LocalInputMonitor.Stop();
        overlay?.Dispose(); overlay = null;
        tick.Dispose(); animationTimer.Dispose();
        backdrop.Dispose(); paint?.Dispose(); surface?.Dispose(); devices.Dispose();
    }

    Vector2 ToDip(nint lParam) => new(LoWord(lParam) / Scale, HiWord(lParam) / Scale);

    protected override nint OnMessage(uint message, nuint wParam, nint lParam, out bool handled)
    {
        handled = true;
        switch (message)
        {
            case WM_RENDER: Render(); return 0;
            case WM_NCHITTEST:
            {
                var screen = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                ScreenToClient(Handle, ref screen);
                bool inside;
                lock (sceneGate) inside = panel != null && panel.Contains(new Vector2(screen.X / Scale, screen.Y / Scale));
                return inside ? 1 /* HTCLIENT */ : -1 /* HTTRANSPARENT */;
            }
            case WM_MOUSEACTIVATE: return 3;   // MA_NOACTIVATE
            case WM_SETCURSOR:
            {
                GetCursorPos(out var position); ScreenToClient(Handle, ref position);
                bool hot;
                lock (sceneGate) hot = panel.HoveredElement is { IsEnabled: true };
                SetCursor(LoadCursor(0, hot ? IDC_HAND : IDC_ARROW));
                return 1;
            }
            case WM_MOUSEMOVE:
            {
                if (!trackingMouse) { var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle }; TrackMouseEvent(ref track); trackingMouse = true; }
                lock (sceneGate)
                {
                    panel.PointerMove(ToDip(lParam));
                    var hot = panel.HoveredElement;
                    if (!ReferenceEquals(hot, hoverTarget)) { hoverTarget = hot; hoverSince = Environment.TickCount64; }
                }
                RequestRender();
                return 0;
            }
            case WM_MOUSELEAVE:
                trackingMouse = false;
                lock (sceneGate) { panel.PointerLeave(); hoverTarget = null; }
                RequestRender();
                return 0;
            case WM_LBUTTONDOWN:
            {
                bool consumed;
                lock (sceneGate) consumed = panel.PointerDown(ToDip(lParam));
                if (nativeMovePending)
                {
                    nativeMovePending = false;
                    var before = OuterBounds;
                    ReleaseCapture();
                    DefWindowProc(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, lParam);   // runs the system move loop until the button is released
                    var after = OuterBounds;
                    var moved = before.X != after.X || before.Y != after.Y;
                    if (!moved && panel.Collapsed) Animate(false);
                    return 0;
                }
                if (consumed) SetCapture(Handle);
                return 0;
            }
            case WM_LBUTTONUP:
            {
                bool consumed;
                lock (sceneGate) { panel.PointerUp(ToDip(lParam), out consumed); }
                if (GetCapture() == Handle) ReleaseCapture();
                RequestRender();
                return 0;
            }
            case WM_MOUSEWHEEL: return 0;
        }
        handled = false;
        return 0;
    }
}
