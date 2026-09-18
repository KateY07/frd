using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Frd;

static partial class FfmpegUi
{
    sealed partial class DesktopWindow
    {
        async Task VerifyOverlayAsync()
        {
            await Task.Delay(100);
            if (stopping) return;
            if (operation.Text?.StartsWith("已连接：", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("连接成功后控制栏仍显示启动状态。");
            var originalState = WindowState;
            var originalSize = new Size(Width, Height);
            var originalPosition = Position;
            var originalExpanded = details.IsVisible;
            var originalOrbFraction = orbFraction;
            var originalPanelFraction = panelFraction;
            var originalWatermark = showWatermark.IsChecked;
            var originalDiagnostics = packetDiagnostics.IsChecked;
            var originalConfiguration = JsonSerializer.Serialize(session?.Configuration, AppConfiguration.JsonOptions);
            var layouts = new List<object>();
            OverlayBounds? expandedBounds = null, foldedBounds = null;
            var expandedHeight = 0d;
            var foldedHeight = 0d;
            var nativeOwner = false;
            var passThrough = false;
            var keptOrb = false;
            var edgeClamping = false;
            var resizeRetainedOrb = false;
            var panelEdgeClamping = false;
            var resizeRetainedPanel = false;
            var collapseRetainedPanel = false;
            var watermarkHideIndependent = false;
            var watermarkHideSurvivesRestore = false;
            var normalFullScreenRestored = false;
            var maximizedFullScreenRestored = false;
            overlayPassed = false;

            object Report(string? failure = null) => new
            {
                Passed = overlayPassed, Failure = failure,
                WatermarkMouseTransparent = passThrough, WatermarkCannotActivate = passThrough,
                WatermarkOpacity = .8, NativeOwnedWindow = nativeOwner,
                ExpandedHeight = expandedHeight, CollapsedHeight = foldedHeight,
                CollapseButtonRemainsVisible = keptOrb, FloatingOrbRemainsVisible = keptOrb,
                OrbEdgeClamping = edgeClamping, OrbAccessibleAfterResize = resizeRetainedOrb,
                PanelEdgeClamping = panelEdgeClamping, PanelAccessibleAfterResize = resizeRetainedPanel,
                PanelPositionSurvivesCollapse = collapseRetainedPanel,
                WatermarkHideIndependent = watermarkHideIndependent,
                WatermarkHideSurvivesRestore = watermarkHideSurvivesRestore,
                NormalFullScreenRestored = normalFullScreenRestored,
                MaximizedFullScreenRestored = maximizedFullScreenRestored,
                ExpandedBounds = expandedBounds, FoldedBounds = foldedBounds,
                LayoutChecks = layouts
            };

            void Require(bool passed, string message)
            {
                if (!passed) throw new InvalidOperationException("真实控制浮层回归失败：" + message);
            }

            async Task SettleAsync(Func<bool> ready, string message)
            {
                var deadline = DateTime.UtcNow.AddSeconds(2);
                do
                {
                    if (stopping) throw new OperationCanceledException("控制浮层回归期间窗口已关闭。");
                    PositionOverlay();
                    await Task.Delay(200);
                    if (ready()) return;
                }
                while (DateTime.UtcNow < deadline);
                Require(false, message);
            }

            OverlayBounds CheckLayout(string scenario, bool expanded)
            {
                var bounds = CheckOverlayBounds(expanded);
                layouts.Add(new { Scenario = scenario, RequestedWidth = Width, RequestedHeight = Height, ClientSize.Width, ClientSize.Height, Bounds = bounds });
                Require(bounds.Passed, $"{scenario}：浮层、可见控件或悬浮球超出可操作范围。{JsonSerializer.Serialize(bounds)}");
                return bounds;
            }

            bool ConfigurationUnchanged() => packetDiagnostics.IsChecked == originalDiagnostics &&
                JsonSerializer.Serialize(session?.Configuration, AppConfiguration.JsonOptions) == originalConfiguration;

            bool WatermarkVisible() => watermark.IsVisible && IsRegressionWindowVisible(watermark.TryGetPlatformHandle()?.Handle ?? 0);
            bool WatermarkHidden() => !watermark.IsVisible && !IsRegressionWindowVisible(watermark.TryGetPlatformHandle()?.Handle ?? 0);

            bool PanelAtFraction(Point fraction)
            {
                var expected = this.PointToScreen(new Point(
                    ChromeMargin + fraction.X * Math.Max(0, ClientSize.Width - overlay.ClientSize.Width - ChromeMargin * 2),
                    ChromeMargin + fraction.Y * Math.Max(0, ClientSize.Height - overlay.ClientSize.Height - ChromeMargin * 2)));
                var actual = overlay.PointToScreen(new Point());
                return Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2;
            }

            async Task<bool> CheckFullScreenAsync(WindowState priorState)
            {
                var size = ClientSize;
                var width = Width;
                var height = Height;
                var position = Position;
                ToggleFullScreen();
                await SettleAsync(() => WindowState == WindowState.FullScreen && CheckOverlayBounds(true).Passed,
                    $"{priorState} 状态未进入可操作的全屏布局。");
                CheckLayout($"fullscreen-from-{priorState}", true);
                ToggleFullScreen();
                await SettleAsync(() => WindowState == priorState &&
                    Math.Abs(ClientSize.Width - size.Width) <= 2 && Math.Abs(ClientSize.Height - size.Height) <= 2 &&
                    Math.Abs(Width - width) <= 2 && Math.Abs(Height - height) <= 2 &&
                    Math.Abs(Position.X - position.X) <= 2 && Math.Abs(Position.Y - position.Y) <= 2 && CheckOverlayBounds(true).Passed,
                    $"退出全屏后未还原 {priorState} 的状态、尺寸和位置。");
                CheckLayout($"fullscreen-restored-{priorState}", true);
                return true;
            }

            try
            {
                if (WindowState == WindowState.FullScreen) ToggleFullScreen();
                WindowState = WindowState.Normal;
                if (!details.IsVisible) ToggleDetails();
                showWatermark.IsChecked = true;
                foreach (var size in new[] { new Size(1280, 860), new Size(780, 520), new Size(980, 600), new Size(680, 460) })
                {
                    Width = size.Width; Height = size.Height;
                    await SettleAsync(() => overlay.IsVisible && WatermarkVisible() && CheckOverlayBounds(true).Passed,
                        $"{size.Width}×{size.Height} 展开布局中的控件被裁切。");
                    var bounds = CheckLayout($"expanded-{size.Width}x{size.Height}", true);
                    SaveChromePreview($"controls-{size.Width}");
                    if (expandedHeight == 0) { expandedHeight = bounds.Height; expandedBounds = bounds; }
                }

                var ownerHandle = TryGetPlatformHandle()?.Handle ?? 0;
                var overlayHandle = overlay.TryGetPlatformHandle()?.Handle ?? 0;
                nativeOwner = ownerHandle != 0 && overlayHandle != 0 && GetWindow(overlayHandle, 4) == ownerHandle;
                Require(nativeOwner, "控制栏不是预览窗口的原生从属窗口。");
                passThrough = watermark.VerifyPassThrough(preview.SourceWindow);
                Require(passThrough, "诊断水印必须鼠标穿透且不能激活。");

                Width = 1280; Height = 860;
                await SettleAsync(() => CheckOverlayBounds(true).Passed, "工具栏拖动测试前布局无效。");
                MoveControlPanel(new Point(-100000, -100000));
                await SettleAsync(() => CheckOverlayBounds(true).Passed && PanelAtFraction(new Point()),
                    "工具栏拖至左上方后未限制到窗口内边界。");
                CheckLayout("panel-clamped-top-left", true);
                Require(panelFraction == new Point(), "工具栏左上越界位置未正确限制。");
                MoveControlPanel(new Point(100000, 100000));
                await SettleAsync(() => CheckOverlayBounds(true).Passed && PanelAtFraction(new Point(1, 1)),
                    "工具栏拖至右下方后未限制到窗口内边界。");
                CheckLayout("panel-clamped-bottom-right", true);
                panelEdgeClamping = panelFraction == new Point(1, 1);
                Require(panelEdgeClamping, "工具栏右下越界位置未正确限制。");
                var movedPanelFraction = panelFraction;
                Width = 680; Height = 460;
                await SettleAsync(() => CheckOverlayBounds(true).Passed && PanelAtFraction(movedPanelFraction),
                    "缩小到最小窗口后工具栏无法操作或丢失拖动位置。");
                CheckLayout("panel-after-minimum-resize", true);
                resizeRetainedPanel = panelFraction == movedPanelFraction;
                Require(resizeRetainedPanel, "窗口缩放未保留工具栏位置。");
                Width = 980; Height = 600;
                await SettleAsync(() => CheckOverlayBounds(true).Passed && PanelAtFraction(movedPanelFraction),
                    "重新放大窗口后工具栏丢失拖动位置。");
                CheckLayout("panel-after-grow", true);

                ToggleDetails();
                await SettleAsync(() => CheckOverlayBounds(false).Passed, "收起后未形成独立的 50×50 悬浮球。");
                var folded = CheckLayout("collapsed", false);
                SaveChromePreview("orb");
                foldedHeight = folded.Height; foldedBounds = folded;
                keptOrb = floatingOrb.IsEffectivelyVisible && overlay.IsVisible && !details.IsVisible && !controlPanel.IsVisible;
                Require(keptOrb && foldedHeight < expandedHeight, "收起后仍存在工具栏，或缺少可点击的悬浮球。");

                MoveOrb(new Point(-100000, -100000));
                await SettleAsync(() => CheckOverlayBounds(false).Passed, "悬浮球移至左上方后未限制到窗口内。");
                CheckLayout("orb-clamped-top-left", false);
                Require(orbFraction.X == 0 && orbFraction.Y == 0, "悬浮球左上越界位置未正确限制。");
                MoveOrb(new Point(100000, 100000));
                await SettleAsync(() => CheckOverlayBounds(false).Passed, "悬浮球移至右下方后未限制到窗口内。");
                CheckLayout("orb-clamped-bottom-right", false);
                edgeClamping = orbFraction.X == 1 && orbFraction.Y == 1;
                Require(edgeClamping, "悬浮球右下越界位置未正确限制。");
                var movedFraction = orbFraction;
                Width = 680; Height = 460;
                await SettleAsync(() => CheckOverlayBounds(false).Passed, "缩小窗口后悬浮球无法操作。");
                CheckLayout("orb-after-resize", false);
                resizeRetainedOrb = orbFraction == movedFraction && floatingOrb.IsEffectivelyVisible;
                Require(resizeRetainedOrb, "窗口缩放未保留悬浮球位置。");
                ToggleDetails();
                await SettleAsync(() => CheckOverlayBounds(true).Passed && PanelAtFraction(movedPanelFraction),
                    "悬浮球展开后控件被裁切或工具栏丢失拖动位置。");
                CheckLayout("expanded-after-orb-move", true);
                collapseRetainedPanel = panelFraction == movedPanelFraction;
                Require(collapseRetainedPanel, "收起、移动悬浮球并展开后工具栏未保留拖动位置。");

                showWatermark.IsChecked = false;
                await SettleAsync(() => WatermarkHidden(), "关闭诊断水印后窗口仍可见。");
                watermarkHideIndependent = ConfigurationUnchanged();
                Require(watermarkHideIndependent, "隐藏水印改变了诊断包开关或会话配置。");
                WindowState = WindowState.Minimized;
                await SettleAsync(() => WindowState == WindowState.Minimized, "窗口无法最小化。");
                WindowState = WindowState.Normal;
                await SettleAsync(() => WindowState == WindowState.Normal && overlay.IsVisible && CheckOverlayBounds(true).Passed,
                    "最小化恢复后控制浮层未恢复。");
                watermarkHideSurvivesRestore = showWatermark.IsChecked == false && WatermarkHidden() && ConfigurationUnchanged();
                Require(watermarkHideSurvivesRestore, "最小化恢复后已隐藏的水印重新显示，或改变了会话配置。");
                showWatermark.IsChecked = true;
                await SettleAsync(() => WatermarkVisible(), "诊断水印无法重新显示。");
                Require(watermark.VerifyPassThrough(preview.SourceWindow), "重新显示后水印不再鼠标穿透。");

                Width = 980; Height = 600;
                await SettleAsync(() => CheckOverlayBounds(true).Passed, "全屏测试前控制浮层布局无效。");
                normalFullScreenRestored = await CheckFullScreenAsync(WindowState.Normal);
                WindowState = WindowState.Maximized;
                await SettleAsync(() => WindowState == WindowState.Maximized && CheckOverlayBounds(true).Passed,
                    "窗口无法最大化。");
                maximizedFullScreenRestored = await CheckFullScreenAsync(WindowState.Maximized);
                Require(ConfigurationUnchanged(), "显示操作改变了诊断包开关或会话配置。");
                overlayPassed = true;
                overlayCheck = Report();
            }
            catch (Exception error)
            {
                overlayCheck = Report(error.Message);
                throw;
            }
            finally
            {
                if (!stopping)
                {
                    if (WindowState == WindowState.FullScreen) { ToggleFullScreen(); await Task.Delay(200); }
                    WindowState = WindowState.Normal;
                    Width = originalSize.Width; Height = originalSize.Height; Position = originalPosition;
                    orbFraction = originalOrbFraction;
                    panelFraction = originalPanelFraction;
                    if (details.IsVisible != originalExpanded) ToggleDetails();
                    showWatermark.IsChecked = originalWatermark;
                    WindowState = originalState;
                    PositionOverlay();
                    await Task.Delay(100);
                    PositionOverlay();
                }
            }
        }

        sealed record OverlayBounds(bool Passed, bool InsidePreview, bool ControlsInsideOverlay, double Width, double Height,
            bool ControlsVisible, bool CorrectPresentation, bool ControlsDoNotOverlap);

        void SaveChromePreview(string name)
        {
            if (reportPath == null) return;
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            Directory.CreateDirectory(directory);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(overlay.ClientSize.Width * overlay.RenderScaling),
                    (int)Math.Ceiling(overlay.ClientSize.Height * overlay.RenderScaling)),
                new Vector(96 * overlay.RenderScaling, 96 * overlay.RenderScaling));
            bitmap.Render(chromeRoot);
            bitmap.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsRegressionWindowVisible(nint window);

        OverlayBounds CheckOverlayBounds(bool expanded)
        {
            var origin = overlay.PointToScreen(new Point());
            var outerOrigin = this.PointToScreen(new Point());
            var width = overlay.ClientSize.Width * overlay.RenderScaling;
            var height = overlay.ClientSize.Height * overlay.RenderScaling;
            var inside = overlay.IsVisible && origin.X >= outerOrigin.X - 1 && origin.Y >= outerOrigin.Y - 1 &&
                origin.X + width <= outerOrigin.X + ClientSize.Width * RenderScaling + 1 &&
                origin.Y + height <= outerOrigin.Y + ClientSize.Height * RenderScaling + 1;
            Control[] expandedControls = [controlTitle, inputEnabled, packetDiagnostics, collapse, presets, bitrate, bitrateLabel,
                clipboardEnabled, transmissionScale, fullScreen, showWatermark];
            Control[] controls = expanded ? expandedControls : [floatingOrb];
            var visible = controls.All(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0);
            var rectangles = controls.Select(control =>
            {
                var point = control.PointToScreen(new Point());
                return new Rect(point.X, point.Y, control.Bounds.Width * overlay.RenderScaling, control.Bounds.Height * overlay.RenderScaling);
            }).ToArray();
            var contentInside = rectangles.All(rect => rect.Left >= origin.X - 1 && rect.Top >= origin.Y - 1 &&
                rect.Right <= origin.X + width + 1 && rect.Bottom <= origin.Y + height + 1);
            var noOverlap = !rectangles.Where((rect, index) => rectangles.Skip(index + 1).Any(other =>
                Math.Min(rect.Right, other.Right) - Math.Max(rect.Left, other.Left) > 1 &&
                Math.Min(rect.Bottom, other.Bottom) - Math.Max(rect.Top, other.Top) > 1)).Any();
            var presentation = expanded
                ? details.IsVisible && controlPanel.IsVisible && !floatingOrb.IsVisible && overlay.ClientSize.Width <= Math.Min(980, ClientSize.Width - 24) + 1
                : !details.IsVisible && !controlPanel.IsVisible && floatingOrb.IsHitTestVisible &&
                    expandedControls.All(control => !control.IsEffectivelyVisible) &&
                    Math.Abs(overlay.ClientSize.Width - OrbSize) <= 1 && Math.Abs(overlay.ClientSize.Height - OrbSize) <= 1;
            return new(inside && contentInside && visible && presentation && noOverlap, inside, contentInside,
                overlay.ClientSize.Width, overlay.ClientSize.Height, visible, presentation, noOverlap);
        }
    }
}
