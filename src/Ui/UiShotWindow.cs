using System.Drawing;
using System.Numerics;
using System.Text;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DXGI;
using static Glasspane.UiNative;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>Toolbar of the screenshot editor: colours, pen/rectangle/text, undo/clear, and save/copy/close.</summary>
sealed class UiShotPanel : UiGlassPanel
{
    public readonly UiSwatch[] Swatches =
    [
        new() { Fill = UiPaint.Hex(0xFFFF4D4F), IsSelected = true, ToolTip = Loc.T("红色") }, new() { Fill = UiPaint.Hex(0xFFFFC53D), ToolTip = Loc.T("黄色") },
        new() { Fill = UiPaint.Hex(0xFF3DD68C), ToolTip = Loc.T("绿色") }, new() { Fill = UiPaint.Hex(0xFFFFFFFF), ToolTip = Loc.T("白色") }
    ];
    public readonly UiButton ToolPen = new() { Icon = "pen", IsToggle = true, IsChecked = true, ToolTip = Loc.T("涂鸦（自由画笔）") };
    public readonly UiButton ToolRect = new() { Icon = "rect", IsToggle = true, ToolTip = Loc.T("矩形") };
    public readonly UiButton ToolText = new() { Icon = "text", IsToggle = true, ToolTip = Loc.T("文字：点一下画面，输入后按 Enter 确认，Esc 取消") };
    public readonly UiButton Thick = new() { Label = Loc.T("粗"), IsToggle = true, FixedWidth = 34, ToolTip = Loc.T("粗笔") };
    public readonly UiButton Undo = new() { Label = Loc.T("撤销"), ToolTip = Loc.T("撤销上一笔（Ctrl+Z）") };
    public readonly UiButton ClearBoard = new() { Label = Loc.T("清除"), ToolTip = Loc.T("清除所有标注") };
    public readonly UiLabel Message = new("", 11) { Color = UiTheme.Subtle, MaxWidth = 200 };
    public readonly UiButton Save = new() { Label = Loc.T("保存"), Icon = "camera", ToolTip = Loc.T("保存为 PNG（图片\\FRD 文件夹，Ctrl+S）") };
    public readonly UiButton Copy = new() { Label = Loc.T("复制"), Icon = "clipboard", ToolTip = Loc.T("复制到剪贴板（Ctrl+C）") };
    public readonly UiButton Close = new() { Label = Loc.T("关闭"), Icon = "power", IsDanger = true, ToolTip = Loc.T("关闭截屏标注") };
    readonly UiElement[] elements;

    public UiShotPanel(object gate) : base(gate)
    {
        MaxWidth = 560;
        elements = [.. Swatches, ToolPen, ToolRect, ToolText, Thick, Undo, ClearBoard, Message, Save, Copy, Close];
    }

    public IEnumerable<UiElement> AllElements => elements;
    protected override IEnumerable<UiElement> Elements => elements;
    public void Wire(Action invalidate) { Invalidate = invalidate; foreach (var element in elements) element.Invalidate = invalidate; }

    protected override float MeasureContent(UiText text, float width) => 28 + 6 + 28;

    protected override void LayoutContent(UiText text, RectangleF content)
    {
        var x = content.X; var y = content.Y; var width = content.Width;
        var cx = x + 4;
        foreach (var swatch in Swatches) { swatch.Bounds = new RectangleF(cx, y, 26, 28); cx += 28; }
        cx += 6;
        foreach (var button in new[] { ToolPen, ToolRect, ToolText, Thick, Undo, ClearBoard }) { var size = button.Measure(text, 0); button.Bounds = new RectangleF(cx, y, size.X, 28); cx += size.X + 4; }
        y += 28 + 6;
        // Right-aligned actions; the message takes what is left of the row.
        var actions = new[] { Save, Copy, Close };
        var sizes = actions.Select(button => button.Measure(text, 0)).ToArray();
        var total = sizes.Sum(size => size.X) + 4 * (actions.Length - 1);
        var ax = x + width - total;
        for (var i = 0; i < actions.Length; i++) { actions[i].Bounds = new RectangleF(ax, y, sizes[i].X, 28); ax += sizes[i].X + 4; }
        Message.MaxWidth = Math.Max(30, x + width - total - 8 - (x + 8));
        var message = Message.Measure(text, 0);
        Message.Bounds = new RectangleF(x + 8, y + 14 - message.Y / 2, Math.Min(message.X, Message.MaxWidth), message.Y);
    }

    protected override void DrawContent(UiPaint paint, float opacity)
    {
        foreach (var element in elements) if (element.IsVisible) element.Draw(paint, opacity);
    }
}

/// <summary>
/// The host's screenshot tool: an ordinary window showing a grab of the host screen, where the host user can draw (pen, rectangle, text) on the
/// picture itself and then save it as a PNG or copy it. Nothing is drawn on the live screen and nothing is sent to the controller.
/// </summary>
sealed class UiShotWindow : UiWindow
{
    readonly object sceneGate = new();
    readonly byte[] pixels;
    readonly int imageWidth, imageHeight;
    readonly UiDevices measureDevices;
    readonly UiText measureText;
    readonly UiShotPanel panel;
    readonly UiVideoView preview;
    readonly AnnotationBoard board = new();
    readonly StringBuilder textBuffer = new();
    AnnotationKind tool = AnnotationKind.Pen;
    Color penColor = UiPaint.Hex(0xFFFF4D4F);
    bool textEditing, updatingTool, trackingMouse;
    Vector2 textAt, lastPointer;
    UiElement? tooltipTarget;
    long hoverSince;

    public UiShotWindow(byte[] bgra, int width, int height)
    {
        pixels = bgra; imageWidth = width; imageHeight = height;
        MinWidth = 640; MinHeight = 420;
        CreateNative(Loc.T("FRD 截屏标注"), WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, 0, unchecked((int)0x80000000), unchecked((int)0x80000000), 1180, 760);
        SetClientSize((int)(1180 * Scale), (int)(760 * Scale));
        CenterOnScreen();
        measureDevices = new UiDevices();
        measureText = new UiText(measureDevices.DWrite);
        panel = new UiShotPanel(sceneGate);
        panel.Wire(() => preview?.Invalidate());

        preview = new UiVideoView(this);
        preview.DrawOverlay = DrawOverlay;
        panel.Backdrop = (paint, rect, radius, _) => preview.DrawGlass(paint, rect, radius);

        foreach (var swatch in panel.Swatches)
        {
            var chosen = swatch;
            swatch.Click += () => { foreach (var other in panel.Swatches) other.IsSelected = ReferenceEquals(other, chosen); penColor = chosen.Fill; preview.Invalidate(); };
        }
        void SelectTool(UiButton chosen, AnnotationKind kind)
        {
            if (updatingTool) return;
            updatingTool = true;
            foreach (var button in new[] { panel.ToolPen, panel.ToolRect, panel.ToolText }) button.IsChecked = ReferenceEquals(button, chosen);
            updatingTool = false;
            CommitText();
            tool = kind;
        }
        panel.ToolPen.CheckedChanged += () => { if (panel.ToolPen.IsChecked == true) SelectTool(panel.ToolPen, AnnotationKind.Pen); else if (!updatingTool) panel.ToolPen.IsChecked = true; };
        panel.ToolRect.CheckedChanged += () => { if (panel.ToolRect.IsChecked == true) SelectTool(panel.ToolRect, AnnotationKind.Rectangle); else if (!updatingTool) panel.ToolRect.IsChecked = true; };
        panel.ToolText.CheckedChanged += () => { if (panel.ToolText.IsChecked == true) SelectTool(panel.ToolText, AnnotationKind.Text); else if (!updatingTool) panel.ToolText.IsChecked = true; };
        panel.Undo.Click += () => { CommitText(); board.Undo(); preview.Invalidate(); };
        panel.ClearBoard.Click += () => { CommitText(); board.Clear(); preview.Invalidate(); };
        panel.Save.Click += Save;
        panel.Copy.Click += CopyToClipboard;
        panel.Close.Click += () => Close();

        preview.Submit(new VideoFrame(pixels, width, height, 0));
        preview.Start();
        lock (sceneGate) { panel.Layout(measureText, ClientWidth, ClientHeight); }
        Show(SW_SHOWNORMAL);
        if (IsIconic(Handle)) Show(SW_RESTORE);
        SetForegroundWindow(Handle);
        RaiseOpened();
    }

    // ---- export ------------------------------------------------------------------------------------------------------

    void SetMessage(string value)
    {
        lock (sceneGate) { panel.Message.Text = value; panel.Relayout(measureText); }
        preview.Invalidate();
    }

    /// <summary>The picture with the marks burnt in, at the full resolution of the grab.</summary>
    byte[] Compose()
    {
        CommitText();
        return ShotComposer.Compose(pixels, imageWidth, imageHeight, board);
    }

    void Save()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures ? pictures : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"), "FRD");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"FRD-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            for (var index = 2; File.Exists(path); index++) path = Path.Combine(folder, $"FRD-{DateTime.Now:yyyyMMdd-HHmmss}-{index}.png");
            PngWriter.Write(path, Compose(), imageWidth, imageHeight);
            SetMessage(Loc.F("已保存：{0}", path));
        }
        catch (Exception error) { Console.Error.WriteLine("[screenshot] " + error); SetMessage(Loc.T("保存失败：") + error.Message); }
    }

    void CopyToClipboard()
    {
        try { ImageClipboard.Copy(Handle, Compose(), imageWidth, imageHeight); SetMessage(Loc.T("已复制到剪贴板")); }
        catch (Exception error) { Console.Error.WriteLine("[screenshot] " + error); SetMessage(Loc.T("复制失败：") + error.Message); }
    }

    // ---- drawing -----------------------------------------------------------------------------------------------------

    float TextFraction(float fitWidth) => (panel.Thick.IsChecked == true ? 30f : 20f) / Math.Max(1, fitWidth);
    static uint ToArgb(Color c) => ((uint)Math.Round(c.A * 255) << 24) | ((uint)Math.Round(c.R * 255) << 16) | ((uint)Math.Round(c.G * 255) << 8) | (uint)Math.Round(c.B * 255);
    Vector2 ToDip(nint lParam) => new(LoWord(lParam) / Scale, HiWord(lParam) / Scale);

    (float X, float Y, float Width, float Height) Fit => UiVideoView.FitRect(ClientWidth / Scale, ClientHeight / Scale, imageWidth, imageHeight);

    Vector2 ToRelative(Vector2 point)
    {
        var fit = Fit;
        return new((point.X - fit.X) / Math.Max(1, fit.Width), (point.Y - fit.Y) / Math.Max(1, fit.Height));
    }

    void CommitText()
    {
        if (!textEditing) return;
        textEditing = false;
        var value = textBuffer.ToString().Trim(); textBuffer.Clear();
        if (value.Length > 0) board.AddText(textAt, value, penColor, TextFraction(Fit.Width));
        preview?.Invalidate();
    }

    void DrawOverlay(UiPaint paint, float width, float height)
    {
        var area = UiVideoView.FitRect(width, height, imageWidth, imageHeight);
        board.Draw(paint, new RectangleF(area.X, area.Y, area.Width, area.Height));
        if (textEditing)
        {
            var fontSize = Math.Max(8f, TextFraction(area.Width) * area.Width);
            var x = area.X + textAt.X * area.Width; var y = area.Y + textAt.Y * area.Height;
            paint.DrawText(textBuffer + "|", x + 1, y + 1, fontSize, new Color(0, 0, 0, .55f), 600);
            paint.DrawText(textBuffer + "|", x, y, fontSize, penColor, 600);
        }
        lock (sceneGate)
        {
            panel.Draw(paint);
            if (tooltipTarget?.ToolTip is { Length: > 0 } tip && Environment.TickCount64 - hoverSince >= 500)
            {
                var size = paint.Text.Measure(tip, 11);
                var x = Math.Clamp(lastPointer.X + 12, 6, Math.Max(6, width - size.X - 22)); var y = Math.Clamp(lastPointer.Y + 20, 6, Math.Max(6, height - size.Y - 14));
                var box = new RectangleF(x, y, size.X + 16, size.Y + 10);
                paint.FillRounded(box, 6, UiPaint.Hex(0xF01B2027));
                paint.StrokeRounded(box, 6, UiTheme.Border);
                paint.DrawText(tip, box.X + 8, box.Y + 5, 11, UiTheme.Text);
            }
        }
    }

    // ---- window messages ---------------------------------------------------------------------------------------------

    protected override void OnResized()
    {
        if (panel == null || measureText == null) return;
        lock (sceneGate) panel.Layout(measureText, (int)(ClientWidth / Scale), (int)(ClientHeight / Scale));
        preview?.Invalidate();
    }

    protected override void OnDpiChanged() => OnResized();

    protected override void OnDestroying()
    {
        preview.Dispose();
        measureDevices.Dispose();
    }

    UiElement? HoverElement()
    {
        foreach (var element in panel.AllElements) if (element.IsVisible && element.IsHovered && element.ToolTip != null) return element;
        return null;
    }

    protected override nint OnMessage(uint message, nuint wParam, nint lParam, out bool handled)
    {
        handled = false;
        switch (message)
        {
            case WM_MOUSEMOVE:
            {
                if (!trackingMouse) { var track = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle }; TrackMouseEvent(ref track); trackingMouse = true; }
                var point = ToDip(lParam);
                lastPointer = point;
                if (board.IsDrawing) board.Add(ToRelative(point));
                lock (sceneGate)
                {
                    panel.PointerMove(point);
                    var hot = panel.Contains(point) ? HoverElement() : null;
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
                else
                {
                    CommitText();
                    var relative = ToRelative(point);
                    if (tool == AnnotationKind.Text) { textEditing = true; textAt = relative; textBuffer.Clear(); handled = true; }
                    else
                    {
                        board.Begin(tool, relative, penColor, (panel.Thick.IsChecked == true ? 7f : 3f) / Math.Max(1, Fit.Width));
                        SetCapture(Handle); handled = true;
                    }
                }
                SetFocus(Handle);
                preview.Invalidate();
                return 0;
            }
            case WM_LBUTTONUP:
            {
                var point = ToDip(lParam);
                bool consumed;
                lock (sceneGate) panel.PointerUp(point, out consumed);
                if (consumed) { if (GetCapture() == Handle) ReleaseCapture(); handled = true; }
                if (board.IsDrawing) { board.End(); if (GetCapture() == Handle) ReleaseCapture(); handled = true; }
                preview.Invalidate();
                return 0;
            }
            case WM_SETCURSOR when LoWord(lParam) == 1:
            {
                GetCursorPos(out var position);
                ScreenToClient(Handle, ref position);
                var point = new Vector2(position.X / Scale, position.Y / Scale);
                bool over; bool hot;
                lock (sceneGate) { over = panel.Contains(point); hot = over && HoverElement() is { IsEnabled: true }; }
                SetCursor(LoadCursor(0, hot ? IDC_HAND : !over ? 32515 /* IDC_CROSS */ : IDC_ARROW));
                handled = true; return 1;
            }
            case 0x0102 /* WM_CHAR */ when textEditing:
                if (wParam == 13) CommitText();
                else if (wParam == 8) { if (textBuffer.Length > 0) textBuffer.Length--; }
                else if (wParam >= 32 && wParam != 127 && textBuffer.Length < AnnotationBoard.MaximumTextLength) textBuffer.Append((char)wParam);
                preview.Invalidate(); handled = true; return 0;
            case WM_KEYDOWN:
            {
                var control = (GetKeyState(0x11) & 0x8000) != 0;
                if (wParam == 0x1B && textEditing) { textEditing = false; textBuffer.Clear(); preview.Invalidate(); handled = true; }
                else if (control && wParam == 0x5A) { CommitText(); board.Undo(); preview.Invalidate(); handled = true; }
                else if (control && wParam == 0x53) { Save(); handled = true; }
                else if (control && wParam == 0x43) { CopyToClipboard(); handled = true; }
                return 0;
            }
        }
        return 0;
    }
}

/// <summary>Burns an <see cref="AnnotationBoard"/> into a BGRA picture with Direct2D, off screen.</summary>
static class ShotComposer
{
    public static byte[] Compose(byte[] pixels, int imageWidth, int imageHeight, AnnotationBoard board)
    {
        using var devices = new UiDevices();
        var text = new UiText(devices.DWrite);
        using var context = devices.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        var size = new Vortice.Mathematics.SizeI(imageWidth, imageHeight);
        var format = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = context.CreateBitmap(size, 0, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        ID2D1Bitmap source;
        unsafe { fixed (byte* data = pixels) source = context.CreateBitmap(size, (nint)data, (uint)(imageWidth * 4), new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.None)); }
        using (source)
        {
            context.Target = target;
            context.BeginDraw();
            context.DrawBitmap(source, new Vortice.RawRectF(0, 0, imageWidth, imageHeight), 1f, InterpolationMode.Linear, null, null);
            using (var paint = new UiPaint(devices, context, text)) board.Draw(paint, new RectangleF(0, 0, imageWidth, imageHeight));
            context.EndDraw();
            context.Target = null;
        }
        using var staging = context.CreateBitmap(size, 0, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(target);
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            var result = new byte[imageWidth * imageHeight * 4];
            for (var y = 0; y < imageHeight; y++) System.Runtime.InteropServices.Marshal.Copy(mapped.Bits + y * (int)mapped.Pitch, result, y * imageWidth * 4, imageWidth * 4);
            for (var i = 3; i < result.Length; i += 4) result[i] = 255;
            return result;
        }
        finally { staging.Unmap(); }
    }
}
