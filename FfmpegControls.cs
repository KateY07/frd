using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Frd;

static partial class FfmpegUi
{
    sealed partial class DesktopWindow
    {
        const double OrbSize = 50, ChromeMargin = 12, PanelRadius = 16, PanelMaxWidth = 420;
        readonly Button fullScreen = Glass.Chip(new Button(), "全屏", "FullScreen", "expand", false);
        readonly Button reconnect = Glass.Chip(new Button { IsVisible = false }, "重新建立会话", "Reconnect");
        readonly Button disconnectButton = Glass.Chip(new Button(), "断开", "Disconnect", "power", false);
        readonly ToggleButton moreButton = Glass.Chip(new ToggleButton(), "更多", "MoreSettings", "sliders", false);
        readonly ToggleButton showWatermark = Glass.Chip(new ToggleButton { IsChecked = false }, "诊断水印", "ShowWatermark", "activity");
        readonly Button floatingOrb = new()
        {
            Name = "FloatingOrb", Content = "FRD", Width = OrbSize, Height = OrbSize,
            CornerRadius = new CornerRadius(OrbSize / 2), Padding = new Thickness(0), FontSize = 12,
            BorderThickness = new Thickness(1), IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand)
        };
        readonly Border controlPanel = new() { CornerRadius = new CornerRadius(PanelRadius), BorderThickness = new Thickness(1), Padding = new Thickness(5) };
        readonly Grid chromeRoot = new();
        readonly Avalonia.Controls.Shapes.Ellipse statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock statusName = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis };
        readonly StackPanel debugRow = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
        readonly StackPanel moreSection = new() { IsVisible = false, Margin = new Thickness(6, 0, 6, 2) };
        readonly Avalonia.Controls.Shapes.Ellipse orbDot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), IsHitTestVisible = false };
        Glass.Palette palette = Glass.For(true);
        Point orbFraction = new(1, 0), panelFraction = new(.5, 0), orbDragOrigin;
        PixelPoint orbPointerOrigin, restorePosition;
        Size restoreSize;
        WindowState restoreState = WindowState.Normal;
        IPointer? orbPointer;
        bool orbDragged, dragExpanded, chromeOpened, fullScreenKeyHeld;

        void BuildControls()
        {
            overlay.FontSize = 12;
            presets.MinHeight = 28; presets.Height = double.NaN; presets.MinWidth = 130;
            bitrate.VerticalAlignment = VerticalAlignment.Center;
            controlTitle.Text = "编码预设"; controlTitle.FontSize = 11;
            bitrateLabel.FontSize = 11;
            statusName.Text = remoteOptions?.Host ?? "本机演示";
            ToolTip.SetTip(fullScreen, "Ctrl+Alt+F11 切换全屏");
            ToolTip.SetTip(collapse, "收起为悬浮球（空闲 10 秒自动收起）");
            ToolTip.SetTip(moreButton, "编码预设、码率、帧率与诊断");
            ToolTip.SetTip(disconnectButton, "断开并关闭窗口");
            ToolTip.SetTip(floatingOrb, "点击展开控制栏；拖动移动，方向键微调");
            ToolTip.SetTip(showWatermark, "在画面左下角显示延迟与流量；只隐藏显示，不改变诊断包或统计");
            ToolTip.SetTip(packetDiagnostics, "主控切换独立诊断包；关闭后跨机延迟不可测");
            ToolTip.SetTip(clipboardEnabled, clipboardMessage);
            disconnectButton.Classes.Add("danger");
            floatingOrb.Classes.Add("orb");
            Glass.SetIcon(floatingOrb, "menu", null, 24); floatingOrb.Width = OrbSize; floatingOrb.Padding = new Thickness(0);
            var orbIcon = (Control)floatingOrb.Content!; floatingOrb.Content = null;
            floatingOrb.Content = new Grid { Width = OrbSize - 2, Height = OrbSize - 2, Children = { orbIcon, orbDot } };

            details.RowDefinitions = new("Auto,Auto");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var chip in new Button[] { inputEnabled, clipboardEnabled, fullScreen, moreButton, disconnectButton, collapse })
                actions.Children.Add(chip);
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(8, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(statusDot); status.Children.Add(statusName);
            var bar = new Grid { ColumnDefinitions = new("*,Auto") };
            bar.Children.Add(status);
            Grid.SetColumn(actions, 1); bar.Children.Add(actions);
            Add(details, bar, 0);

            moreSection.Children.Add(Glass.Divider(palette));
            var encoder = new Grid { ColumnDefinitions = new("*,12,*"), RowDefinitions = new("Auto,4,Auto") };
            encoder.Children.Add(controlTitle);
            Grid.SetColumn(bitrateLabel, 2); encoder.Children.Add(bitrateLabel);
            Grid.SetRow(presets, 2); encoder.Children.Add(presets);
            Grid.SetColumn(bitrate, 2); Grid.SetRow(bitrate, 2); encoder.Children.Add(bitrate);
            moreSection.Children.Add(encoder);
            presetMenu.CornerRadius = new CornerRadius(8); presetMenu.Margin = new Thickness(0, 6, 0, 0);
            moreSection.Children.Add(presetMenu);
            var frames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 6, 0, 0) };
            frames.Children.Add(new TextBlock { Text = "帧率", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Opacity = .75 });
            foreach (var button in fpsButtons)
            {
                Glass.Chip(button, button.Content?.ToString() ?? "", button.Name);
                button.Padding = new Thickness(0); button.MinWidth = 36; button.Width = 36;
                frames.Children.Add(button);
            }
            moreSection.Children.Add(frames);
            debugRow.Children.Add(packetDiagnostics); debugRow.Children.Add(showWatermark);
            moreSection.Children.Add(debugRow);
            operation.FontSize = 11; operation.Opacity = .8; operation.TextWrapping = TextWrapping.NoWrap;
            operation.TextTrimming = TextTrimming.CharacterEllipsis; operation.Text = "正在连接…";
            operation.Bind(ToolTip.TipProperty, new Binding("Text") { Source = operation });
            var connectionRow = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
            operation.VerticalAlignment = VerticalAlignment.Center;
            connectionRow.Children.Add(operation);
            Grid.SetColumn(reconnect, 1); connectionRow.Children.Add(reconnect);
            moreSection.Children.Add(connectionRow);
            Add(details, moreSection, 1);
            operation.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) UpdateStatusDot(); };
            moreButton.IsCheckedChanged += (_, _) => { moreSection.IsVisible = moreButton.IsChecked == true; PositionOverlay(); };
            disconnectButton.Click += (_, _) => Close();
            collapse.Click += (_, _) => AnimateChrome(true);
            chromeRoot.AddHandler(PointerMovedEvent, (_, _) => Touch(), RoutingStrategies.Tunnel);
            chromeRoot.AddHandler(PointerPressedEvent, (_, _) => Touch(), RoutingStrategies.Tunnel);
            chromeRoot.AddHandler(KeyDownEvent, (_, _) => Touch(), RoutingStrategies.Tunnel);
            idleTimer.Tick += (_, _) => CheckIdle();

            controlPanel.Child = details;
            chromeRoot.Children.Add(controlPanel); chromeRoot.Children.Add(floatingOrb);
            overlay.Content = chromeRoot;
            ApplyChromeColors();

            var diagnosticBody = new StackPanel { IsHitTestVisible = false, Opacity = .8 };
            watermark.FontSize = 11; watermark.Foreground = Brushes.White;
            watermark.Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#8017202B")),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7), Child = diagnosticBody,
                IsHitTestVisible = false, ClipToBounds = true
            };
            summary.FontSize = 12; diagnosticBody.Children.Add(summary);
            metrics.FontSize = 11; metrics.Margin = new Thickness(0, 3, 0, 4);
            metrics.Text = "正在启动真实捕获与编解码…"; diagnosticBody.Children.Add(metrics);
            diagnosticBody.Children.Add(new TextBlock { Text = "分层耗时：最近 / 近 1 秒平均（ms）", Opacity = .8 });
            var timingGrid = new Grid { ColumnDefinitions = new("*,*,*"), RowDefinitions = new("Auto,Auto") };
            for (var i = 0; i < timings.Length; i++)
            { Grid.SetRow(timings[i], i / 3); Grid.SetColumn(timings[i], i % 3); timingGrid.Children.Add(timings[i]); }
            diagnosticBody.Children.Add(timingGrid); diagnosticBody.Children.Add(captureDetails);
            diagnosticBody.Children.Add(new TextBlock
            {
                Text = "带宽估计仅供参考；传输含等待与重组。GPU 完成不等于物理扫描；不变像素可保持显示。",
                TextWrapping = TextWrapping.Wrap, Opacity = .7
            });

            fullScreen.Click += (_, _) => ToggleFullScreen();
            void ReleaseShortcut(object? sender, KeyEventArgs args) { if (args.Key == Key.F11) fullScreenKeyHeld = false; }
            AddHandler(KeyUpEvent, ReleaseShortcut, RoutingStrategies.Tunnel);
            overlay.AddHandler(KeyUpEvent, ReleaseShortcut, RoutingStrategies.Tunnel);
            Deactivated += (_, _) => fullScreenKeyHeld = false;
            overlay.Deactivated += (_, _) => fullScreenKeyHeld = false;
            showWatermark.IsCheckedChanged += (_, _) => SyncChromeVisibility();
            floatingOrb.Click += (_, _) => AnimateChrome(false);
            ToolTip.SetTip(presets, "单击展开控制栏内的预设列表；也可用键盘选择");
            presets.AddHandler(PointerReleasedEvent, (_, args) =>
            {
                if (args.GetCurrentPoint(presets).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
                presets.IsDropDownOpen = false;
                presetMenu.IsVisible = !presetMenu.IsVisible;
                PositionOverlay();
                args.Handled = true;
            }, RoutingStrategies.Tunnel);
            chromeRoot.AddHandler(PointerPressedEvent, BeginOrbDrag, RoutingStrategies.Tunnel);
            chromeRoot.AddHandler(PointerMovedEvent, ContinueOrbDrag, RoutingStrategies.Tunnel);
            chromeRoot.AddHandler(PointerReleasedEvent, EndOrbDrag, RoutingStrategies.Tunnel);
            chromeRoot.PointerCaptureLost += (_, _) => orbPointer = null;
            floatingOrb.KeyDown += (_, args) =>
            {
                var delta = args.Key switch { Key.Left => new Point(-10, 0), Key.Right => new Point(10, 0), Key.Up => new Point(0, -10), Key.Down => new Point(0, 10), _ => default };
                if (delta == default) return;
                var current = OrbPosition(); MoveOrb(new(current.X + delta.X, current.Y + delta.Y)); args.Handled = true;
            };
        }

        static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);
        const int AnimationMs = 240;
        readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        long lastActivity = Environment.TickCount64;
        bool animating;

        void Touch() => lastActivity = Environment.TickCount64;

        void CheckIdle()
        {
            if (stopping || animating || !details.IsVisible || orbPointer != null || chromeRoot.IsPointerOver) return;
            if (autoCloseSeconds > 0 || InteractionScriptPath != null) return;
            if (Environment.TickCount64 - lastActivity >= IdleDelay.TotalMilliseconds) AnimateChrome(true);
        }

        static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

        /// <summary>Animates the overlay window between the bar and the orb; the plain ToggleDetails stays instant.</summary>
        async void AnimateChrome(bool toOrb)
        {
            if (animating || stopping || details.IsVisible == !toOrb) return;
            animating = true;
            try
            {
                Touch();
                var panelWidth = Math.Max(280, Math.Min(PanelMaxWidth, ClientSize.Width - ChromeMargin * 2));
                double panelHeight;
                Rect from, to;
                if (toOrb)
                {
                    panelHeight = overlay.ClientSize.Height;
                    from = new Rect(PanelPosition(), new Size(panelWidth, panelHeight));
                    to = new Rect(OrbPosition(), new Size(OrbSize, OrbSize));
                }
                else
                {
                    var orbRect = new Rect(OrbPosition(), new Size(OrbSize, OrbSize));
                    details.IsVisible = true; controlPanel.IsVisible = true; floatingOrb.IsVisible = false;
                    controlPanel.Width = panelWidth; controlPanel.Measure(new Size(panelWidth, double.PositiveInfinity));
                    panelHeight = controlPanel.DesiredSize.Height;
                    from = orbRect;
                    var topLeft = new Point(ChromeMargin + panelFraction.X * Math.Max(0, ClientSize.Width - panelWidth - ChromeMargin * 2),
                        ChromeMargin + panelFraction.Y * Math.Max(0, ClientSize.Height - panelHeight - ChromeMargin * 2));
                    to = new Rect(topLeft, new Size(panelWidth, panelHeight));
                }
                controlPanel.Width = panelWidth; controlPanel.HorizontalAlignment = HorizontalAlignment.Right;
                controlPanel.VerticalAlignment = VerticalAlignment.Top;
                floatingOrb.HorizontalAlignment = HorizontalAlignment.Left; floatingOrb.VerticalAlignment = VerticalAlignment.Top;
                floatingOrb.IsVisible = true; floatingOrb.Opacity = toOrb ? 0 : 1;
                overlay.SizeToContent = SizeToContent.Manual;
                var clock = Stopwatch.StartNew();
                while (!stopping)
                {
                    var t = Math.Min(1, clock.ElapsedMilliseconds / (double)AnimationMs);
                    var e = Ease(t);
                    double Lerp(double a, double b) => a + (b - a) * e;
                    var frame = new Rect(Lerp(from.X, to.X), Lerp(from.Y, to.Y), Lerp(from.Width, to.Width), Lerp(from.Height, to.Height));
                    overlay.Width = Math.Max(1, frame.Width); overlay.Height = Math.Max(1, frame.Height);
                    overlay.Position = this.PointToScreen(frame.TopLeft);
                    var radius = toOrb ? Lerp(PanelRadius, OrbSize / 2) : Lerp(OrbSize / 2, PanelRadius);
                    Glass.ClipRounded(overlay, radius);
                    controlPanel.Opacity = toOrb ? 1 - e : e;
                    floatingOrb.Opacity = toOrb ? e : 1 - e;
                    if (t >= 1) break;
                    await Task.Delay(12);
                }
            }
            finally
            {
                controlPanel.ClearValue(WidthProperty); controlPanel.ClearValue(OpacityProperty);
                controlPanel.HorizontalAlignment = HorizontalAlignment.Stretch; controlPanel.VerticalAlignment = VerticalAlignment.Stretch;
                floatingOrb.ClearValue(OpacityProperty); floatingOrb.ClearValue(HorizontalAlignmentProperty); floatingOrb.ClearValue(VerticalAlignmentProperty);
                floatingOrb.IsVisible = toOrb;
                overlay.SizeToContent = SizeToContent.Height;
                animating = false;
                if (!stopping)
                {
                    if (details.IsVisible == toOrb) ToggleDetails();
                    else PositionOverlay();
                    Touch();
                }
            }
        }

        /// <summary>Debug controls (packet diagnostics, diagnostic watermark) stay hidden until Ctrl+Alt+F12.</summary>
        void SetDebug(bool visible) { debugRow.IsVisible = visible; if (visible) SetMore(true); else PositionOverlay(); }

        void SetMore(bool open) { moreButton.IsChecked = open; moreSection.IsVisible = open; PositionOverlay(); }

        void UpdateStatusDot()
        {
            var text = operation.Text ?? "";
            var color = text.Contains("中断") || text.StartsWith("错误") || text.Contains("失败") ? palette.Bad
                : text.StartsWith("已连接") || text.StartsWith("已应用") || text.Contains("已启用") || text.Contains("已开启") ? palette.Good : palette.Warn;
            if (text.StartsWith("正在", StringComparison.Ordinal) && statusDot.Fill != null) return;
            statusDot.Fill = orbDot.Fill = Glass.Brush(color);
        }

        void RebuildPresetMenu()
        {
            var choices = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            foreach (var item in presets.Items.OfType<ComboBoxItem>())
            {
                var choice = Glass.Chip(new Button { Tag = item.Tag, IsEnabled = item.IsEnabled }, item.Content?.ToString() ?? "");
                choice.HorizontalContentAlignment = HorizontalAlignment.Left; choice.HorizontalAlignment = HorizontalAlignment.Stretch;
                choice.Click += (_, _) =>
                {
                    presets.SelectedItem = item;
                    presetMenu.IsVisible = false;
                    PositionOverlay();
                };
                choices.Children.Add(choice);
            }
            presetMenu.Child = new ScrollViewer { Content = choices, MaxHeight = 200, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }

        void ApplyChromeColors()
        {
            palette = Glass.For(true);
            overlay.RequestedThemeVariant = ThemeVariant.Dark;
            overlay.Foreground = Glass.Brush(palette.Text);
            overlay.Styles.Clear(); overlay.Styles.AddRange(Glass.BuildStyles(palette));
            controlPanel.Background = Glass.Brush(palette.Tint);
            controlPanel.BorderBrush = Glass.Brush(palette.Border);
            floatingOrb.Background = Glass.Brush(palette.Tint);
            floatingOrb.BorderBrush = Glass.Brush(palette.Border);
            floatingOrb.Foreground = Glass.Brush(palette.Text);
            floatingOrb.BorderBrush = Glass.Brush(palette.Border);
            presets.Background = Glass.Brush(palette.Hover); presets.BorderThickness = new Thickness(0); presets.CornerRadius = new CornerRadius(8);
            presetMenu.Background = Glass.Brush(palette.Hover);
            presetMenu.BorderBrush = Brushes.Transparent;
            statusName.Foreground = Glass.Brush(palette.Subtle);
            controlTitle.Foreground = bitrateLabel.Foreground = Glass.Brush(palette.Subtle);
            foreach (var divider in moreSection.Children.OfType<Border>().Where(child => child.Height == 1)) divider.Background = Glass.Brush(palette.Line);
            statusDot.Fill = null; UpdateStatusDot();
            UpdateFpsButtons();
        }

        void HandleLocalKeyDown(object? sender, KeyEventArgs args)
        {
            var firstFullScreenKey = args.Key == Key.F11 && !fullScreenKeyHeld;
            if (args.Key == Key.F11) fullScreenKeyHeld = true;
            if ((args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != (KeyModifiers.Control | KeyModifiers.Alt)) return;
            if (inputEnabled.IsChecked == true) HandleLocalShortcut(0x11);
            if (args.Key == Key.F11)
            {
                if (firstFullScreenKey) HandleLocalShortcut(0x7A);
                args.Handled = true;
            }
            else if (args.Key == Key.F12) { SetDebug(!debugRow.IsVisible); args.Handled = true; }
        }

        void HandleLocalShortcut(int key)
        {
            if (key == 0x7A) ToggleFullScreen();
            else if (key == 0x11)
            {
                if (inputEnabled.IsChecked == true) { preview.SetInputEnabled(false); inputEnabled.IsChecked = false; }
            }
        }

        void ToggleFullScreen()
        {
            if (stopping) return;
            if (WindowState != WindowState.FullScreen)
            {
                restoreState = WindowState; restoreSize = new(Width, Height); restorePosition = Position;
                WindowState = WindowState.FullScreen; Glass.SetIcon(fullScreen, "shrink"); ToolTip.SetTip(fullScreen, "退出全屏（Ctrl+Alt+F11）");
            }
            else
            {
                WindowState = restoreState; Glass.SetIcon(fullScreen, "expand"); ToolTip.SetTip(fullScreen, "全屏（Ctrl+Alt+F11）");
                if (restoreState == WindowState.Normal)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (stopping || WindowState != WindowState.Normal) return;
                        Width = restoreSize.Width; Height = restoreSize.Height; Position = restorePosition; PositionOverlay();
                    });
            }
            PositionOverlay();
        }

        void ToggleDetails()
        {
            details.IsVisible = !details.IsVisible;
            controlPanel.IsVisible = details.IsVisible;
            floatingOrb.IsVisible = !details.IsVisible;
            PositionOverlay();
        }

        Point OrbPosition() => new(ChromeMargin + orbFraction.X * Math.Max(0, ClientSize.Width - OrbSize - ChromeMargin * 2),
            ChromeMargin + orbFraction.Y * Math.Max(0, ClientSize.Height - OrbSize - ChromeMargin * 2));

        Point PanelPosition() => new(ChromeMargin + panelFraction.X * Math.Max(0, ClientSize.Width - overlay.Width - ChromeMargin * 2),
            ChromeMargin + panelFraction.Y * Math.Max(0, ClientSize.Height - overlay.ClientSize.Height - ChromeMargin * 2));

        void MoveControlPanel(Point position)
        {
            panelFraction = new(Math.Clamp((position.X - ChromeMargin) / Math.Max(1, ClientSize.Width - overlay.Width - ChromeMargin * 2), 0, 1),
                Math.Clamp((position.Y - ChromeMargin) / Math.Max(1, ClientSize.Height - overlay.ClientSize.Height - ChromeMargin * 2), 0, 1));
            PositionOverlay();
        }

        void MoveOrb(Point position)
        {
            orbFraction = new(Math.Clamp((position.X - ChromeMargin) / Math.Max(1, ClientSize.Width - OrbSize - ChromeMargin * 2), 0, 1),
                Math.Clamp((position.Y - ChromeMargin) / Math.Max(1, ClientSize.Height - OrbSize - ChromeMargin * 2), 0, 1));
            PositionOverlay();
        }

        void BeginOrbDrag(object? sender, PointerPressedEventArgs args)
        {
            if (orbPointer != null || args.GetCurrentPoint(chromeRoot).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
            if (details.IsVisible && args.Source is Visual source && source.GetSelfAndVisualAncestors()
                .Any(control => control is Button or CheckBox or ComboBox or Slider or TextBox)) return;
            dragExpanded = details.IsVisible;
            orbPointerOrigin = overlay.PointToScreen(args.GetPosition(overlay)); orbDragOrigin = dragExpanded ? PanelPosition() : OrbPosition();
            orbDragged = false; orbPointer = args.Pointer; args.Pointer.Capture(chromeRoot); args.Handled = true;
        }

        void ContinueOrbDrag(object? sender, PointerEventArgs args)
        {
            if (orbPointer != args.Pointer) return;
            var current = overlay.PointToScreen(args.GetPosition(overlay));
            var dx = (current.X - orbPointerOrigin.X) / RenderScaling;
            var dy = (current.Y - orbPointerOrigin.Y) / RenderScaling;
            if (dx * dx + dy * dy > 16) orbDragged = true;
            if (orbDragged)
            {
                var next = new Point(orbDragOrigin.X + dx, orbDragOrigin.Y + dy);
                if (dragExpanded) MoveControlPanel(next); else MoveOrb(next);
            }
            args.Handled = true;
        }

        void EndOrbDrag(object? sender, PointerReleasedEventArgs args)
        {
            if (orbPointer != args.Pointer || args.InitialPressMouseButton != MouseButton.Left) return;
            var moved = orbDragged; orbPointer = null; args.Pointer.Capture(null);
            args.Handled = true;
            if (!moved && !dragExpanded) AnimateChrome(false);
        }

        void SyncChromeVisibility()
        {
            if (!chromeOpened || stopping) return;
            if (WindowState == WindowState.Minimized) { overlay.Hide(); watermark.Hide(); return; }
            if (!overlay.IsVisible) overlay.Show(this);
            if (showWatermark.IsChecked == true) { if (!watermark.IsVisible) watermark.Show(this); }
            else watermark.Hide();
            PositionOverlay();
        }

        void PositionOverlay()
        {
            if (animating) return;
            if (!overlay.IsVisible || ClientSize.Width <= 0) return;
            overlay.Width = details.IsVisible ? Math.Max(280, Math.Min(PanelMaxWidth, ClientSize.Width - ChromeMargin * 2)) : OrbSize;
            overlay.Position = this.PointToScreen(details.IsVisible ? PanelPosition() : OrbPosition());
            Glass.ClipRounded(overlay, details.IsVisible ? PanelRadius : OrbSize / 2);
            if (!watermark.IsVisible) return;
            watermark.Width = Math.Max(280, Math.Min(920, ClientSize.Width - ChromeMargin * 2));
            watermark.MaxHeight = Math.Max(40, ClientSize.Height - (details.IsVisible ? overlay.ClientSize.Height : 0) - ChromeMargin * 3);
            watermark.Position = this.PointToScreen(new Point(ChromeMargin, Math.Max(ChromeMargin, ClientSize.Height - watermark.ClientSize.Height - ChromeMargin)));
        }
    }
}
