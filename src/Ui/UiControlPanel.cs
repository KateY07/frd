using System.Drawing;
using System.Numerics;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>The controller's floating toolbar and settings card.</summary>
sealed class UiControlPanel : UiGlassPanel
{
    public readonly UiLabel StatusName = new("", 12) { MaxWidth = 90, Color = UiTheme.Subtle };
    public readonly UiLabel Latency = new("— ms", 12) { Color = UiTheme.Subtle };
    public readonly UiButton Input = new() { Label = "键鼠", Icon = "mouse", IsToggle = true, IsEnabled = false };
    public readonly UiButton Clipboard = new() { Label = "剪贴板", Icon = "clipboard", IsToggle = true, IsEnabled = false };
    public readonly UiButton FullScreen = new() { Icon = "expand" };
    public readonly UiButton More = new() { Icon = "sliders", IsToggle = true };
    public readonly UiButton Disconnect = new() { Icon = "power", IsDanger = true };
    public readonly UiButton Collapse = new() { Icon = "chevrons" };
    public readonly UiButton Annotate = new() { Icon = "pen", IsToggle = true, IsEnabled = false, ToolTip = "标注：在画面上画涂鸦、矩形、文字，被控端屏幕同步显示（Esc 退出，Ctrl+Z 撤销）" };
    public readonly UiSwatch[] Swatches =
    [
        new() { Fill = UiPaint.Hex(0xFFFF4D4F), IsSelected = true, ToolTip = "红色" }, new() { Fill = UiPaint.Hex(0xFFFFC53D), ToolTip = "黄色" },
        new() { Fill = UiPaint.Hex(0xFF3DD68C), ToolTip = "绿色" }, new() { Fill = UiPaint.Hex(0xFFFFFFFF), ToolTip = "白色" }
    ];
    public readonly UiButton ToolPen = new() { Icon = "pen", IsToggle = true, IsChecked = true, ToolTip = "涂鸦（自由画笔）" };
    public readonly UiButton ToolRect = new() { Icon = "rect", IsToggle = true, ToolTip = "矩形" };
    public readonly UiButton ToolText = new() { Icon = "text", IsToggle = true, ToolTip = "文字：点一下画面，输入后按 Enter 确认，Esc 取消" };
    public readonly UiButton Thick = new() { Label = "粗", IsToggle = true, FixedWidth = 34, ToolTip = "粗笔" };
    public readonly UiButton Undo = new() { Label = "撤销", ToolTip = "撤销上一笔（Ctrl+Z）" };
    public readonly UiButton ClearBoard = new() { Label = "清除", ToolTip = "清除所有标注" };
    readonly UiElement[] annotateElements;
    RectangleF annotateRow;
    public bool AnnotateOn => Annotate.IsChecked == true;
    const float AnnotateRowHeight = 36;

    public readonly UiLabel EncoderTitle = new("编码预设", 11) { Color = UiTheme.Subtle };
    public readonly UiLabel BitrateLabel = new("", 11) { Color = UiTheme.Subtle };
    public readonly UiChoice Presets = new() { IsEnabled = false };
    public readonly UiSlider Bitrate = new() { IsEnabled = false, Minimum = .1, Maximum = 100, Step = .1, Tag = "bitrate" };
    public readonly UiLabel FpsTitle = new("帧率", 12) { Color = UiTheme.Subtle };
    public readonly UiButton[] Fps = new[] { 10, 20, 25, 30 }.Select(value => new UiButton { Label = value.ToString(), Tag = value, IsEnabled = false, FixedWidth = 36 }).ToArray();
    public readonly UiButton PacketDiagnostics = new() { Label = "诊断包", Icon = "pulse", IsToggle = true, IsEnabled = false };
    public readonly UiButton ShowInfo = new() { Label = "诊断信息", Icon = "activity", IsToggle = true };
    public readonly UiButton ShowRemoteCursor = new() { Label = "远程光标", Icon = "cursor", IsToggle = true };
    public readonly UiLabel Operation = new("正在连接…", 11) { Color = UiTheme.Subtle };
    public readonly UiButton Reconnect = new() { Label = "重新建立会话", IsVisible = false };

    float dotX, dotY;
    public Color DotColor => StatusColor;
    public bool MoreOpen => More.IsChecked == true;
    readonly UiElement[] elements;
    readonly UiElement[] moreElements;

    public UiControlPanel(object gate) : base(gate)
    {
        moreElements = [EncoderTitle, BitrateLabel, Presets, Bitrate, FpsTitle, .. Fps, PacketDiagnostics, ShowInfo, ShowRemoteCursor, Operation, Reconnect];
        annotateElements = [.. Swatches, ToolPen, ToolRect, ToolText, Thick, Undo, ClearBoard];
        elements = [StatusName, Latency, Input, Clipboard, Annotate, FullScreen, More, Disconnect, Collapse, .. annotateElements, .. moreElements];
        ToolTipDefaults();
    }

    void ToolTipDefaults()
    {
        Input.ToolTip = "键鼠转发（Ctrl+Alt 退出并释放按键）";
        Clipboard.ToolTip = "剪贴板同步（文本、文件、目录）";
        FullScreen.ToolTip = "全屏（Ctrl+Alt+F11）";
        More.ToolTip = "编码预设、码率、帧率与诊断";
        Disconnect.ToolTip = "断开并关闭窗口";
        Collapse.ToolTip = "收起为悬浮球（空闲 10 秒自动收起）";
        PacketDiagnostics.ToolTip = "主控切换独立诊断包；关闭后跨机延迟不可测";
        ShowRemoteCursor.ToolTip = "在画面上叠加显示被控端的鼠标指针（位置和形状），不需要开启键鼠转发";
        ShowInfo.ToolTip = "在画面左下角显示延迟与流量；只隐藏显示，不改变诊断包或统计";
    }

    public IEnumerable<UiElement> AllElements => elements;
    protected override IEnumerable<UiElement> Elements => elements;

    public void Wire(Action invalidate)
    {
        Invalidate = invalidate;
        foreach (var element in elements) element.Invalidate = invalidate;
    }

    static float RowWidth(IEnumerable<(UiElement Element, Vector2 Size)> items, float spacing) => items.Sum(item => item.Size.X) + spacing * Math.Max(0, items.Count() - 1);

    protected override float MeasureContent(UiText text, float width) => MeasureBase() + (AnnotateOn ? AnnotateRowHeight : 0);

    float MeasureBase()
    {
        var height = 28f;
        if (!MoreOpen) return height;
        height += 11;                                            // divider
        height += 16 + 4 + 30;                                   // titles + dropdown/slider row
        if (Presets.IsOpen) height += 4 + Presets.ListHeight;
        height += 8 + 28;                                        // fps
        height += 6 + 28;                                        // diagnostics toggles
        height += 6 + 22;                                        // operation line
        return height;
    }

    protected override void LayoutContent(UiText text, RectangleF content)
    {
        var x = content.X; var y = content.Y; var width = content.Width;
        // ---- bar: status on the left, buttons right aligned
        var buttons = new UiButton[] { Input, Clipboard, Annotate, FullScreen, More, Disconnect, Collapse };
        var latencySize = Latency.Measure(text, 0);
        var latencyWidth = Math.Max(40, latencySize.X);
        // The two labelled toggles drop their text (icon only, tooltip stays) when the bar would otherwise run out of room.
        const float MinimumName = 56;
        Input.Label = "键鼠"; Clipboard.Label = "剪贴板";
        var sizes = buttons.Select(button => button.Measure(text, 0)).ToArray();
        var buttonsWidth = sizes.Sum(size => size.X) + 2 * (buttons.Length - 1);
        if (buttonsWidth + 23 + MinimumName + 8 + latencyWidth + 8 > width)
        {
            Input.Label = ""; Clipboard.Label = "";
            sizes = buttons.Select(button => button.Measure(text, 0)).ToArray();
            buttonsWidth = sizes.Sum(size => size.X) + 2 * (buttons.Length - 1);
        }
        var buttonsLeft = x + width - buttonsWidth;
        var cursor = buttonsLeft;
        for (var i = 0; i < buttons.Length; i++) { buttons[i].Bounds = new RectangleF(cursor, y, sizes[i].X, 28); cursor += sizes[i].X + 2; }
        dotX = x + 12; dotY = y + 14;
        StatusName.MaxWidth = Math.Clamp(buttonsLeft - (x + 23) - 8 - latencyWidth - 8, 30, 140);
        var nameSize = StatusName.Measure(text, 0);
        StatusName.Bounds = new RectangleF(x + 23, y + 14 - nameSize.Y / 2, Math.Min(nameSize.X, StatusName.MaxWidth), nameSize.Y);
        Latency.Bounds = new RectangleF(StatusName.Bounds.Right + 8, y + 14 - latencySize.Y / 2, latencyWidth, latencySize.Y);

        // ---- annotation tools: a row at the very bottom of the card, only while annotating
        foreach (var element in annotateElements) element.IsVisible = AnnotateOn;
        if (AnnotateOn)
        {
            annotateRow = new RectangleF(x, content.Y + MeasureBase() + 6, width, 28);
            var cx = annotateRow.X + 4;
            foreach (var swatch in Swatches) { swatch.Bounds = new RectangleF(cx, annotateRow.Y, 26, 28); cx += 28; }
            cx += 6;
            foreach (var button in new[] { ToolPen, ToolRect, ToolText, Thick, Undo, ClearBoard }) { var size = button.Measure(text, 0); button.Bounds = new RectangleF(cx, annotateRow.Y, size.X, 28); cx += size.X + 4; }
        }
        foreach (var element in moreElements) element.IsVisible = MoreOpen && (element != Reconnect || reconnectVisible);
        if (!MoreOpen) return;

        y += 28 + 11;
        var left = x + 6; var inner = width - 12; var column = (inner - 12) / 2;
        EncoderTitle.Bounds = new RectangleF(left, y, column, 16);
        BitrateLabel.Bounds = new RectangleF(left + column + 12, y, column, 16);
        y += 16 + 4;
        Presets.Bounds = new RectangleF(left, y, column, 30);
        Bitrate.Bounds = new RectangleF(left + column + 12, y + 4, column, 22);
        y += 30;
        if (Presets.IsOpen) y += 4 + Presets.ListHeight;
        y += 8;
        var frameLabel = FpsTitle.Measure(text, 0);
        FpsTitle.Bounds = new RectangleF(left, y + 14 - frameLabel.Y / 2, frameLabel.X, frameLabel.Y);
        var fx = left + frameLabel.X + 8;
        foreach (var button in Fps) { button.Bounds = new RectangleF(fx, y, 36, 28); fx += 39; }
        y += 28 + 6;
        var packetSize = PacketDiagnostics.Measure(text, 0); var infoSize = ShowInfo.Measure(text, 0);
        PacketDiagnostics.Bounds = new RectangleF(left - 4, y, packetSize.X, 28);
        ShowInfo.Bounds = new RectangleF(left - 4 + packetSize.X + 4, y, infoSize.X, 28);
        var cursorSize = ShowRemoteCursor.Measure(text, 0);
        ShowRemoteCursor.Bounds = new RectangleF(ShowInfo.Bounds.Right + 4, y, cursorSize.X, 28);
        y += 28 + 6;
        var reconnectSize = Reconnect.Measure(text, 0);
        Reconnect.Bounds = new RectangleF(left + inner - reconnectSize.X, y - 3, reconnectSize.X, 28);
        Operation.MaxWidth = Math.Max(40, inner - (reconnectVisible ? reconnectSize.X + 8 : 0));
        var operationSize = Operation.Measure(text, 0);
        Operation.Bounds = new RectangleF(left, y + 11 - operationSize.Y / 2, Operation.MaxWidth, operationSize.Y);
    }

    bool reconnectVisible;
    public void SetReconnectVisible(bool visible) { reconnectVisible = visible; Reconnect.IsVisible = visible; Invalidate?.Invoke(); }

    protected override void DrawContent(UiPaint paint, float opacity)
    {
        paint.FillCircle(new Vector2(dotX, dotY), 4, UiPaint.WithAlpha(StatusColor, opacity));
        foreach (var element in elements)
        {
            if (!element.IsVisible || element == Presets) continue;
            element.Draw(paint, opacity);
        }
        if (MoreOpen)
        {
            var rect = CurrentRect;
            var dividerY = Math.Min(Latency.Bounds.Y, StatusName.Bounds.Y) + 28 - 6 + 0;
            paint.FillRounded(new RectangleF(rect.X + 11, rect.Y + 5 + 28 + 5, rect.Width - 22, 1), 0, UiPaint.WithAlpha(UiTheme.Line, opacity));
            Presets.Draw(paint, opacity);
            Presets.DrawList(paint, opacity);
        }
    }

    public string BitrateTooltip => "";
}
