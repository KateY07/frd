using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Frd;

static partial class RemoteLaunch
{
    /// <summary>Host-side glass status panel: draggable, collapses to an orb, expands when a controller connects.</summary>
    sealed class HostWindow : Window
    {
        const double PanelWidth = 320, OrbSize = 50, PanelRadius = 16, ScreenMargin = 12, AnimationMs = 240;
        static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

        readonly RemoteHost server;
        readonly string listening;
        readonly Avalonia.Controls.Shapes.Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        readonly Avalonia.Controls.Shapes.Ellipse orbDot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), IsHitTestVisible = false };
        readonly Avalonia.Controls.Shapes.Ellipse pillDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        readonly TextBlock pillText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        readonly Grid orbHost = new();
        readonly TextBlock status = new() { Text = "等待主控", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock listenValue = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock durationValue = new() { FontSize = 12, Text = "—" };
        readonly TextBlock helperValue = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly List<TextBlock> labels = new();
        readonly Button secureHelper = Glass.Chip(new Button { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left }, "安装安全桌面辅助服务");
        readonly ToggleButton input = Glass.Chip(new ToggleButton { IsChecked = true, IsVisible = false }, "键鼠", "HostInput", "mouse", false);
        readonly ToggleButton clipboard = Glass.Chip(new ToggleButton { IsChecked = true, IsVisible = false }, "剪贴板", "HostClipboard", "clipboard", false);
        readonly Button disconnect = Glass.Chip(new Button { IsVisible = false }, "断开", "HostDisconnect", "power", false);
        readonly ToggleButton more = Glass.Chip(new ToggleButton(), "更多", "HostMore", "sliders", false);
        readonly Button quit = Glass.Chip(new Button { HorizontalAlignment = HorizontalAlignment.Left }, "退出被控端", "HostQuit", "power");
        readonly Button collapse = Glass.Chip(new Button(), "收起", "HostCollapse", "chevrons", false);
        readonly Button orb = new() { Name = "HostOrb", Width = OrbSize, Height = OrbSize, CornerRadius = new CornerRadius(OrbSize / 2),
            Padding = new Thickness(0), BorderThickness = new Thickness(1), IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand) };
        readonly StackPanel moreSection = new() { IsVisible = false, Margin = new Thickness(6, 0, 6, 2) };
        readonly Border divider = new() { Height = 1, Margin = new Thickness(0, 8) };
        readonly Border card = new() { CornerRadius = new CornerRadius(PanelRadius), BorderThickness = new Thickness(1), Padding = new Thickness(5) };
        readonly Grid root = new();
        readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
        Glass.Palette palette = Glass.For(true);
        DateTime connectedAt;
        long lastActivity = Environment.TickCount64;
        DateTime closingSince;
        bool closing, finished, moved, controlled, expanded = true, animating;

        public HostWindow(AppConfiguration config, IPAddress[] addresses, int port, string token, CodecProbeSample[]? probeSamples)
        {
            Title = $"FRD 被控端 · TCP {port}"; Width = PanelWidth; SizeToContent = SizeToContent.Height;
            CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            Background = Brushes.Transparent; TransparencyLevelHint = Glass.Levels;
            listening = $"{string.Join("、", addresses.Select(a => a.ToString()))}:{port}";
            listenValue.Text = listening; ToolTip.SetTip(listenValue, listening);
            disconnect.Classes.Add("danger"); orb.Classes.Add("orb");
            ToolTip.SetTip(input, "键鼠：允许主控使用键盘和鼠标"); ToolTip.SetTip(clipboard, "剪贴板：允许与主控同步");
            ToolTip.SetTip(disconnect, "断开当前主控"); ToolTip.SetTip(more, "监听地址、会话时长与安全桌面服务");
            ToolTip.SetTip(collapse, "收起为悬浮球（空闲 10 秒自动收起）"); ToolTip.SetTip(orb, "点击展开；拖动移动");
            orb.Padding = new Thickness(0);
            var orbRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            orbRow.Children.Add(Glass.Icon("monitor", 24)); orbRow.Children.Add(pillDot); orbRow.Children.Add(pillText);
            orbHost.Children.Add(orbRow); orbHost.Children.Add(orbDot);
            orb.Content = orbHost; PrepareOrb();

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var chip in new Button[] { input, clipboard, disconnect, more, collapse }) actions.Children.Add(chip);
            var state = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.Margin = new Thickness(0, 0, 7, 0);
            state.Children.Add(dot); Grid.SetColumn(status, 1); state.Children.Add(status);
            var bar = new Grid { ColumnDefinitions = new("*,Auto") };
            bar.Children.Add(state); Grid.SetColumn(actions, 1); bar.Children.Add(actions);

            Grid Row(string label, Control value)
            {
                var name = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; labels.Add(name);
                var row = new Grid { ColumnDefinitions = new("84,*"), Margin = new Thickness(0, 3, 0, 0) };
                row.Children.Add(name); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
            }
            moreSection.Children.Add(divider);
            moreSection.Children.Add(Row("监听", listenValue));
            moreSection.Children.Add(Row("会话时长", durationValue));
            moreSection.Children.Add(Row("安全桌面服务", helperValue));
            secureHelper.Margin = new Thickness(-6, 4, 0, 0); moreSection.Children.Add(secureHelper);
            quit.Classes.Add("danger"); quit.Margin = new Thickness(-6, 4, 0, 0); moreSection.Children.Add(quit);
            ToolTip.SetTip(quit, "停止监听并退出被控端（正在被控制时会先断开）");
            var stack = new StackPanel(); stack.Children.Add(bar); stack.Children.Add(moreSection);
            card.Child = stack;
            root.ClipToBounds = true; root.Children.Add(card); root.Children.Add(orb);
            Content = root;
            ApplyColors();

            server = new(config, addresses, port, token, probeSamples);
            disconnect.Click += (_, _) => server.DisconnectActive();
            clipboard.IsCheckedChanged += (_, _) => server.SetClipboardAllowed(clipboard.IsChecked == true);
            input.IsCheckedChanged += (_, _) => server.SetInputAllowed(input.IsChecked == true);
            more.IsCheckedChanged += (_, _) => { moreSection.IsVisible = more.IsChecked == true; Touch(); };
            collapse.Click += (_, _) => Animate(true);
            quit.Click += (_, _) => Close();
            secureHelper.Click += async (_, _) =>
            {
                secureHelper.IsEnabled = false;
                secureHelper.Content = "等待管理员确认…";
                try { await SecureDesktopService.InstallViaUacAsync(); SessionTrace.Event("secure-helper", "refresh-ui"); RefreshSecureHelper(); SessionTrace.Event("secure-helper", "refresh-ui-done"); }
                catch (Exception error)
                {
                    SessionTrace.Error("secure-helper", error);
                    Console.Error.WriteLine("[secure helper] Installation failed: " + error);
                    secureHelper.Content = "安装失败，点击重试";
                    secureHelper.IsEnabled = true;
                    ToolTip.SetTip(secureHelper, error.Message);
                }
            };
            server.ControllerChanged += address => Dispatcher.UIThread.Post(() =>
            {
                controlled = address != null;
                if (controlled) connectedAt = DateTime.UtcNow;
                status.Text = controlled ? $"被 {address} 控制" : "等待主控";
                input.IsVisible = clipboard.IsVisible = disconnect.IsVisible = controlled;
                if (!controlled) durationValue.Text = "—";
                UpdateDot(); RefreshSecureHelper(); Touch();
                if (controlled && !expanded) Animate(false);
                else if (!expanded && !animating) ResizeCollapsed();
            });
            server.Status += text => Dispatcher.UIThread.Post(() => ToolTip.SetTip(status, text));
            server.Failed += error => Dispatcher.UIThread.Post(() => { Console.Error.WriteLine(error); hostExitCode = 1; Close(); });

            AddHandler(PointerPressedEvent, BeginDrag, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, (_, _) => Touch(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            tick.Tick += (_, _) => OnTick();
            PropertyChanged += (_, args) => { if (args.Property == ActualThemeVariantProperty) ApplyColors(); };
            SizeChanged += (_, _) =>
            {
                if (animating) return;
                Glass.ClipRounded(this, expanded ? PanelRadius : CollapsedRadius);
                if (!moved) Dock(); else KeepOnScreen();
            };
            Opened += (_, _) =>
            {
                try
                {
                    Dock();
                    var handle = TryGetPlatformHandle()?.Handle ?? 0;
                    if (handle == 0 || !SetWindowDisplayAffinity(handle, 0x11))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "被控端提示窗口无法排除出屏幕捕获");
                    // Never take keyboard focus: remote key events must not be able to operate this panel.
                    SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000);
                    server.Start(handle);
                    RefreshSecureHelper(); Touch(); tick.Start();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error); status.Text = error.Message; hostExitCode = 1;
                    Dispatcher.UIThread.Post(Close);
                }
            };
            Closing += async (_, e) =>
            {
                if (finished) return;
                e.Cancel = true;
                if (closing) { if (DateTime.UtcNow - closingSince > TimeSpan.FromSeconds(2)) ShutdownGuard.Kill("host"); return; }
                closing = true; closingSince = DateTime.UtcNow; tick.Stop();
                ShutdownGuard.Arm("host", TimeSpan.FromSeconds(10));
                try { await ShutdownGuard.Bounded(() => server.DisposeAsync().AsTask(), "host-server", TimeSpan.FromSeconds(6)); }
                catch (Exception error) { Console.Error.WriteLine(error); hostExitCode = 1; }
                finally { finished = true; Dispatcher.UIThread.Post(Close); }
            };
        }

        /// <summary>The collapsed form is a plain orb while idle and a status pill ("被 xxx 控制") while controlled.</summary>
        Size PrepareOrb()
        {
            pillDot.IsVisible = pillText.IsVisible = controlled; orbDot.IsVisible = !controlled;
            if (controlled)
            {
                pillText.Text = status.Text; orb.Width = double.NaN; orb.Height = 40; orb.CornerRadius = new CornerRadius(20);
                orb.Padding = new Thickness(14, 0); orbHost.ClearValue(WidthProperty); orbHost.ClearValue(HeightProperty);
            }
            else
            {
                orb.Width = orb.Height = OrbSize; orb.CornerRadius = new CornerRadius(OrbSize / 2); orb.Padding = new Thickness(0);
                orbHost.Width = orbHost.Height = OrbSize - 2;
            }
            orb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            collapsedWidth = orb.DesiredSize.Width;
            return orb.DesiredSize;
        }

        double collapsedWidth = OrbSize;
        double CollapsedRadius => orb.CornerRadius.TopLeft;

        void ResizeCollapsed()
        {
            var size = PrepareOrb(); var scale = RenderScaling;
            var right = Position.X + Bounds.Width * scale;
            Width = size.Width; Height = size.Height;
            Position = new PixelPoint((int)Math.Round(right - size.Width * scale), Position.Y);
            Glass.ClipRounded(this, CollapsedRadius); KeepOnScreen();
        }

        void Touch() => lastActivity = Environment.TickCount64;

        void OnTick()
        {
            if (controlled && more.IsChecked == true) durationValue.Text = (DateTime.UtcNow - connectedAt).ToString(@"hh\:mm\:ss");
            if (closing || animating || !expanded || IsPointerOver) return;
            if (Environment.TickCount64 - lastActivity >= IdleDelay.TotalMilliseconds) Animate(true);
        }

        void BeginDrag(object? sender, PointerPressedEventArgs e)
        {
            Touch();
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || animating) return;
            var ancestors = e.Source is Visual v ? v.GetSelfAndVisualAncestors().ToArray() : [];
            if (ancestors.Any(c => c is Button && c != orb)) return;
            var onOrb = ancestors.Contains(orb); var start = Position;
            moved = true; BeginMoveDrag(e);
            // BeginMoveDrag runs the system move loop; an unchanged position afterwards means the orb was clicked, not dragged.
            if (onOrb && Position == start) Animate(false);
        }

        PixelRect WorkArea() => (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        void KeepOnScreen()
        {
            if (Bounds.Height <= 0) return;
            var area = WorkArea(); var scale = RenderScaling;
            var w = (int)(Bounds.Width * scale); var h = (int)(Bounds.Height * scale);
            var x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - w));
            var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
            if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
        }

        void Dock()
        {
            if (Bounds.Height <= 0) return;
            var area = WorkArea(); var scale = RenderScaling;
            Position = new(area.Right - (int)(Bounds.Width * scale) - (int)(ScreenMargin * scale),
                area.Bottom - (int)(Bounds.Height * scale) - (int)(ScreenMargin * scale));
        }

        static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

        /// <summary>Morphs the window between the panel and the orb, keeping the right edge and top fixed.</summary>
        async void Animate(bool toOrb)
        {
            if (animating || closing || expanded == !toOrb) return;
            animating = true; moved = true;
            try
            {
                Touch();
                var scale = RenderScaling; var area = WorkArea();
                var right = Position.X + Bounds.Width * scale; var top = Position.Y;
                double panelHeight; Rect from, to;
                var current = Bounds.Size;
                orb.IsVisible = true; orb.HorizontalAlignment = HorizontalAlignment.Left; orb.VerticalAlignment = VerticalAlignment.Top;
                var orbSize = PrepareOrb();
                if (toOrb)
                {
                    panelHeight = Bounds.Height;
                    from = new Rect(Position.X, top, PanelWidth, panelHeight);
                    to = new Rect(right - orbSize.Width * scale, top, orbSize.Width, orbSize.Height);
                }
                else
                {
                    card.IsVisible = true; card.Width = PanelWidth;
                    card.Measure(new Size(PanelWidth, double.PositiveInfinity)); panelHeight = card.DesiredSize.Height;
                    var x = Math.Clamp(right - PanelWidth * scale, area.X, Math.Max(area.X, area.Right - PanelWidth * scale));
                    var y = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - panelHeight * scale));
                    from = new Rect(Position.X, top, current.Width, current.Height);
                    to = new Rect(x, y, PanelWidth, panelHeight);
                }
                card.Width = PanelWidth; card.HorizontalAlignment = HorizontalAlignment.Right; card.VerticalAlignment = VerticalAlignment.Top;
                orb.HorizontalAlignment = HorizontalAlignment.Left; orb.VerticalAlignment = VerticalAlignment.Top;
                orb.IsVisible = true; orb.Opacity = toOrb ? 0 : 1; card.Opacity = toOrb ? 1 : 0;
                SizeToContent = SizeToContent.Manual;
                var clock = Stopwatch.StartNew();
                while (!closing)
                {
                    var t = Math.Min(1, clock.ElapsedMilliseconds / AnimationMs); var e = Ease(t);
                    double Lerp(double a, double b) => a + (b - a) * e;
                    Width = Math.Max(1, Lerp(from.Width, to.Width)); Height = Math.Max(1, Lerp(from.Height, to.Height));
                    // Linear left/width interpolation keeps the right edge fixed (see from/to above).
                    Position = new PixelPoint((int)Math.Round(Lerp(from.X, to.X)), (int)Math.Round(Lerp(from.Y, to.Y)));
                    Glass.ClipRounded(this, toOrb ? Lerp(PanelRadius, CollapsedRadius) : Lerp(CollapsedRadius, PanelRadius));
                    card.Opacity = toOrb ? 1 - e : e; orb.Opacity = toOrb ? e : 1 - e;
                    if (t >= 1) break;
                    await Task.Delay(12);
                }
            }
            finally
            {
                expanded = !toOrb;
                card.ClearValue(WidthProperty); card.ClearValue(OpacityProperty);
                card.HorizontalAlignment = HorizontalAlignment.Stretch; card.VerticalAlignment = VerticalAlignment.Stretch;
                orb.ClearValue(OpacityProperty); orb.ClearValue(HorizontalAlignmentProperty); orb.ClearValue(VerticalAlignmentProperty);
                card.IsVisible = expanded; orb.IsVisible = !expanded;
                if (!closing)
                {
                    Width = expanded ? PanelWidth : collapsedWidth;
                    SizeToContent = SizeToContent.Height;
                }
                animating = false;
                if (!closing) { Glass.ClipRounded(this, expanded ? PanelRadius : CollapsedRadius); KeepOnScreen(); Touch(); }
            }
        }

        void RefreshSecureHelper()
        {
            var helper = SecureDesktopService.Status();
            helperValue.Text = helper.Running ? "运行中" : helper.Installed ? "已安装，未运行" : "未安装";
            helperValue.Foreground = Glass.Brush(helper.Running ? palette.Good : palette.Warn);
            ToolTip.SetTip(helperValue, helper.Message);
            secureHelper.IsVisible = !helper.Running;
            secureHelper.IsEnabled = !helper.Running;
            secureHelper.Content = helper.Installed ? "启动/修复安全桌面辅助服务" : "安装安全桌面辅助服务";
            ToolTip.SetTip(secureHelper, helper.Message);
        }

        void UpdateDot() => dot.Fill = orbDot.Fill = pillDot.Fill = Glass.Brush(controlled ? palette.Good : palette.Warn);

        void ApplyColors()
        {
            palette = Glass.For(true);
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Foreground = Glass.Brush(palette.Text);
            Styles.Clear(); Styles.AddRange(Glass.BuildStyles(palette));
            card.Background = Glass.Brush(palette.Tint); card.BorderBrush = Glass.Brush(palette.Border);
            orb.Background = Glass.Brush(palette.Tint); orb.BorderBrush = Glass.Brush(palette.Border); orb.Foreground = Glass.Brush(palette.Text);
            divider.Background = Glass.Brush(palette.Line);
            foreach (var label in labels) label.Foreground = Glass.Brush(palette.Subtle);
            listenValue.Foreground = durationValue.Foreground = Glass.Brush(palette.Text);
            UpdateDot(); pillText.Foreground = Glass.Brush(palette.Text);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    }
}
