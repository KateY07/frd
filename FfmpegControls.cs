using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
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
        const double OrbSize = 50, ChromeMargin = 12;
        readonly Button fullScreen = new() { Name = "FullScreen", Content = "全屏", Padding = new Thickness(10, 4) };
        readonly CheckBox showWatermark = new() { Name = "ShowWatermark", Content = "诊断水印", IsChecked = true };
        readonly Button floatingOrb = new()
        {
            Name = "FloatingOrb", Content = "FRD", Width = OrbSize, Height = OrbSize,
            CornerRadius = new CornerRadius(OrbSize / 2), Padding = new Thickness(0), FontSize = 12,
            Background = new SolidColorBrush(Color.Parse("#BE263445")), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#99CAD7E6")), BorderThickness = new Thickness(1),
            IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand)
        };
        readonly Border controlPanel = new() { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10) };
        readonly Grid chromeRoot = new();
        Point orbFraction = new(1, 0), panelFraction = new(.5, 0), orbDragOrigin;
        PixelPoint orbPointerOrigin, restorePosition;
        Size restoreSize;
        WindowState restoreState = WindowState.Normal;
        IPointer? orbPointer;
        bool orbDragged, dragExpanded, chromeOpened, fullScreenKeyHeld;

        void BuildControls()
        {
            overlay.FontSize = 12;
            presets.MinHeight = transmissionScale.MinHeight = 28;
            presets.Height = transmissionScale.Height = double.NaN;
            presets.MinWidth = 130;
            transmissionScale.Width = 110;
            bitrate.VerticalAlignment = VerticalAlignment.Center;
            controlTitle.Text = "编码预设"; controlTitle.FontSize = 11; controlTitle.Opacity = .75;
            inputEnabled.Content = "键鼠"; inputEnabled.Margin = new Thickness(0);
            clipboardEnabled.Content = "剪贴板"; packetDiagnostics.Margin = new Thickness(0);
            collapse.Content = "收起"; collapse.MinWidth = 48; collapse.Padding = new Thickness(8, 4);
            fullScreen.Height = collapse.Height = 28;
            ToolTip.SetTip(fullScreen, "Ctrl+Alt+F11 切换全屏；Ctrl+Alt 退出键鼠控制");
            ToolTip.SetTip(collapse, "收起为可拖动的悬浮球");
            ToolTip.SetTip(floatingOrb, "点击展开控制栏；拖动移动，方向键微调");
            ToolTip.SetTip(showWatermark, "只隐藏显示，不改变诊断包或统计");
            ToolTip.SetTip(packetDiagnostics, "主控切换独立诊断包；关闭后跨机延迟不可测");
            ToolTip.SetTip(clipboardEnabled, clipboardMessage);

            var controls = new Grid { ColumnDefinitions = new("*,10,1.3*,10,110,10,Auto,6,Auto"), RowDefinitions = new("Auto,5,Auto") };
            controls.Children.Add(controlTitle);
            var scaleTitle = new TextBlock { Text = "传输比例", FontSize = 11, Opacity = .75 };
            Grid.SetColumn(bitrateLabel, 2); controls.Children.Add(bitrateLabel);
            Grid.SetColumn(scaleTitle, 4); controls.Children.Add(scaleTitle);
            Grid.SetRow(presets, 2); controls.Children.Add(presets);
            Grid.SetColumn(bitrate, 2); Grid.SetRow(bitrate, 2); controls.Children.Add(bitrate);
            Grid.SetColumn(transmissionScale, 4); Grid.SetRow(transmissionScale, 2); controls.Children.Add(transmissionScale);
            Grid.SetColumn(fullScreen, 6); Grid.SetRow(fullScreen, 2); controls.Children.Add(fullScreen);
            Grid.SetColumn(collapse, 8); Grid.SetRow(collapse, 2); controls.Children.Add(collapse);
            Add(details, controls, 0);
            transmissionScale.ItemsSource = new[] { 1d, .75, .5 }.Select(scale =>
                new ComboBoxItem { Content = scale == 1 ? "1× 原始" : $"{scale}×", Tag = scale }).ToArray();
            transmissionScale.SelectedIndex = config.TransmissionScale == 1 ? 0 : config.TransmissionScale == .75 ? 1 : 2;
            ToolTip.SetTip(transmissionScale, "传输分辨率比例，与窗口缩放无关");
            var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            foreach (var check in new[] { inputEnabled, clipboardEnabled, showWatermark, packetDiagnostics })
            {
                check.FontSize = 12; check.MinHeight = 24; check.Margin = new Thickness(0, 0, 16, 0);
                options.Children.Add(check);
            }
            Add(details, options, 1);
            operation.FontSize = 11; operation.Opacity = .8; operation.TextWrapping = TextWrapping.NoWrap;
            operation.TextTrimming = TextTrimming.CharacterEllipsis; operation.Text = "正在连接…";
            operation.Bind(ToolTip.TipProperty, new Binding("Text") { Source = operation });
            Add(details, operation, 2);
            controlPanel.Child = details;
            chromeRoot.Children.Add(controlPanel); chromeRoot.Children.Add(floatingOrb);
            overlay.Content = chromeRoot;
            ApplyChromeColors();

            var diagnosticBody = new StackPanel { IsHitTestVisible = false, Opacity = .8 };
            watermark.FontSize = 11; watermark.Foreground = Brushes.White;
            watermark.Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#8017202B")),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7), Child = diagnosticBody,
                IsHitTestVisible = false, ClipToBounds = true
            };
            summary.FontSize = 12; diagnosticBody.Children.Add(summary);
            metrics.FontSize = 11; metrics.Margin = new Thickness(0, 3, 0, 4);
            metrics.Text = "正在启动真实捕获与 FFmpeg…"; diagnosticBody.Children.Add(metrics);
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
            floatingOrb.Click += (_, _) => ToggleDetails();
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

        void ApplyChromeColors()
        {
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            overlay.RequestedThemeVariant = ActualThemeVariant;
            overlay.Foreground = new SolidColorBrush(Color.Parse(dark ? "#EDF0F3" : "#20252B"));
            controlPanel.Background = new SolidColorBrush(Color.Parse(dark ? "#222529" : "#FAFAFA"));
            controlPanel.BorderBrush = new SolidColorBrush(Color.Parse(dark ? "#484E56" : "#D7DCE1"));
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
                WindowState = WindowState.FullScreen; fullScreen.Content = "退出全屏";
            }
            else
            {
                WindowState = restoreState; fullScreen.Content = "全屏";
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
            if (!moved && !dragExpanded) ToggleDetails();
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
            if (!overlay.IsVisible || ClientSize.Width <= 0) return;
            overlay.Width = details.IsVisible ? Math.Max(280, Math.Min(980, ClientSize.Width - ChromeMargin * 2)) : OrbSize;
            overlay.Position = this.PointToScreen(details.IsVisible ? PanelPosition() : OrbPosition());
            if (!watermark.IsVisible) return;
            watermark.Width = Math.Max(280, Math.Min(920, ClientSize.Width - ChromeMargin * 2));
            watermark.MaxHeight = Math.Max(40, ClientSize.Height - (details.IsVisible ? overlay.ClientSize.Height : 0) - ChromeMargin * 3);
            watermark.Position = this.PointToScreen(new Point(ChromeMargin, Math.Max(ChromeMargin, ClientSize.Height - watermark.ClientSize.Height - ChromeMargin)));
        }
    }
}
