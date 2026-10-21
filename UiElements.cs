using System.Drawing;
using System.Numerics;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>Colors of the dark frosted-glass theme (same values the Avalonia version used).</summary>
static class UiTheme
{
    public static readonly Color Tint = UiPaint.Hex(0xA6161A22), Border = UiPaint.Hex(0x2EFFFFFF), Text = UiPaint.Hex(0xFFF2F5F9),
        Subtle = UiPaint.Hex(0xFFCCD3DC), Hover = UiPaint.Hex(0x26FFFFFF), Checked = UiPaint.Hex(0x66528CFF), CheckedText = UiPaint.Hex(0xFFE6EEFF),
        Line = UiPaint.Hex(0x26FFFFFF), Good = UiPaint.Hex(0xFF3DD68C), Warn = UiPaint.Hex(0xFFF5B84A), Bad = UiPaint.Hex(0xFFF06A66),
        Track = UiPaint.Hex(0x33FFFFFF), Thumb = UiPaint.Hex(0xFF8DB4FF);
}

/// <summary>Base of everything drawn in a panel. Logical state lives here; drawing happens on the render thread.</summary>
abstract class UiElement
{
    public RectangleF Bounds;
    public bool IsVisible = true;
    public bool IsEnabled = true;
    public string? ToolTip;
    public bool IsHovered, IsPressed;
    public object? Tag;
    public Action? Invalidate;           // set by the owner; wakes the render loop

    public virtual Vector2 Measure(UiText text, float availableWidth) => default;
    public virtual void Draw(UiPaint paint, float opacity) { }
    public virtual bool HitTest(Vector2 point) => IsVisible && Bounds.Contains(point.X, point.Y);
    public virtual void PointerDown(Vector2 point) { }
    public virtual void PointerMove(Vector2 point) { }
    public virtual void PointerUp(Vector2 point, bool inside) { }
    public virtual void Wheel(Vector2 point, float delta) { }
    protected void Changed() => Invalidate?.Invoke();
    public virtual bool WantsPointerCapture => false;
}

/// <summary>Static text. <see cref="Text"/> may change at any time from any thread.</summary>
sealed class UiLabel : UiElement
{
    string text = "";
    public float Size = 12;
    public int Weight = 400;
    public bool Mono;
    public float MaxWidth = 4096;
    public Color? Color;
    public string Text { get => text; set { if (text != value) { text = value; Changed(); } } }
    public float Opacity = 1;

    public UiLabel(string value = "", float size = 12) { text = value; Size = size; }

    public override Vector2 Measure(UiText metrics, float availableWidth)
    {
        var measured = metrics.Measure(text, Size, Weight, Mono);
        return new(Math.Min(measured.X, MaxWidth), measured.Y);
    }

    public override void Draw(UiPaint paint, float opacity)
    {
        if (!IsVisible) return;
        paint.DrawText(text, Bounds.X, Bounds.Y, Size, UiPaint.WithAlpha(Color ?? UiTheme.Text, opacity * Opacity), Weight, Mono, Math.Max(1, Bounds.Width));
    }

    public override bool HitTest(Vector2 point) => false;
}

/// <summary>A flat text/icon chip. As a toggle its checked state is shown as a tinted fill.</summary>
sealed class UiButton : UiElement
{
    public string Label = "";
    public string? Icon;
    public bool IsToggle;
    public bool IsDanger;
    public float FixedWidth;
    public float Size = 12;
    bool? isChecked = false;
    public event Action? Click;
    public event Action? CheckedChanged;
    public bool? IsChecked
    {
        get => isChecked;
        set { if (isChecked != value) { isChecked = value; Changed(); CheckedChanged?.Invoke(); } }
    }
    public string Text { get => Label; set { if (Label != value) { Label = value; Changed(); } } }

    public override Vector2 Measure(UiText text, float availableWidth)
    {
        if (FixedWidth > 0) return new(FixedWidth, 28);
        var width = 20f;
        if (Icon != null) width = Label.Length == 0 ? 32 : 10 + 18 + 6;
        if (Label.Length > 0) width += text.Measure(Label, Size).X + (Icon == null ? 0 : 10);
        return new(Math.Max(Icon != null && Label.Length == 0 ? 32 : 28, width), 28);
    }

    public override void Draw(UiPaint paint, float opacity)
    {
        if (!IsVisible) return;
        var alpha = opacity * (IsEnabled ? 1 : .45f);
        var fill = IsDanger && IsHovered && IsEnabled ? UiTheme.Bad :
            IsToggle && isChecked == true ? UiTheme.Checked :
            (IsHovered || IsPressed) && IsEnabled ? UiTheme.Hover : default;
        var color = IsDanger && IsHovered && IsEnabled ? UiPaint.Hex(0xFFFFFFFF) : IsToggle && isChecked == true ? UiTheme.CheckedText : UiTheme.Text;
        paint.FillRounded(Bounds, 8, UiPaint.WithAlpha(fill, alpha));
        var x = Bounds.X + (Icon != null && Label.Length == 0 ? (Bounds.Width - 18) / 2 : 10);
        var cy = Bounds.Y + Bounds.Height / 2;
        if (Icon != null)
        {
            paint.DrawIcon(Icon, new RectangleF(x, cy - 9, 18, 18), UiPaint.WithAlpha(color, alpha));
            x += 18 + 6;
        }
        if (Label.Length > 0)
        {
            var measured = paint.Text.Measure(Label, Size);
            var textX = Icon == null ? Bounds.X + (Bounds.Width - measured.X) / 2 : x;
            paint.DrawText(Label, textX, cy - measured.Y / 2, Size, UiPaint.WithAlpha(color, alpha));
        }
    }

    public override void PointerDown(Vector2 point) { if (!IsEnabled) return; IsPressed = true; Changed(); }
    public override void PointerUp(Vector2 point, bool inside)
    {
        var wasPressed = IsPressed;
        IsPressed = false; Changed();
        if (!inside || !wasPressed || !IsEnabled) return;
        if (IsToggle) IsChecked = isChecked != true;
        Click?.Invoke();
    }
    public override bool WantsPointerCapture => true;
}

/// <summary>Horizontal slider (value in [Minimum, Maximum], optionally snapped).</summary>
sealed class UiSlider : UiElement
{
    double value = 1;
    public double Minimum = 0, Maximum = 100, Step = 0;
    public event Action? ValueChanged;
    public double Value
    {
        get => value;
        set
        {
            value = Math.Clamp(Step > 0 ? Math.Round(value / Step) * Step : value, Minimum, Maximum);
            if (this.value == value) return;
            this.value = value; Changed(); ValueChanged?.Invoke();
        }
    }

    public override Vector2 Measure(UiText text, float availableWidth) => new(availableWidth, 22);

    public override void Draw(UiPaint paint, float opacity)
    {
        if (!IsVisible) return;
        var alpha = opacity * (IsEnabled ? 1 : .45f);
        var track = new RectangleF(Bounds.X + 7, Bounds.Y + Bounds.Height / 2 - 2, Bounds.Width - 14, 4);
        var fraction = Maximum > Minimum ? (float)((value - Minimum) / (Maximum - Minimum)) : 0;
        paint.FillRounded(track, 2, UiPaint.WithAlpha(UiTheme.Track, alpha));
        paint.FillRounded(new RectangleF(track.X, track.Y, track.Width * fraction, 4), 2, UiPaint.WithAlpha(UiTheme.Thumb, alpha));
        paint.FillCircle(new Vector2(track.X + track.Width * fraction, Bounds.Y + Bounds.Height / 2), IsPressed ? 8 : 7, UiPaint.WithAlpha(UiTheme.Thumb, alpha));
    }

    void SetFromPoint(Vector2 point)
    {
        var width = Math.Max(1, Bounds.Width - 14);
        Value = Minimum + (Maximum - Minimum) * Math.Clamp((point.X - Bounds.X - 7) / width, 0, 1);
    }
    public override void PointerDown(Vector2 point) { if (!IsEnabled) return; IsPressed = true; SetFromPoint(point); }
    public override void PointerMove(Vector2 point) { if (IsPressed && IsEnabled) SetFromPoint(point); }
    public override void PointerUp(Vector2 point, bool inside) { IsPressed = false; Changed(); }
    public override bool WantsPointerCapture => true;
}

sealed record UiChoiceItem(string Label, string Tag, bool Enabled, string? Hint);

/// <summary>A dropdown: the closed state is one chip; the open list is drawn inline by the owning panel.</summary>
sealed class UiChoice : UiElement
{
    public List<UiChoiceItem> Items { get; } = new();
    int selected = -1;
    public bool IsOpen;
    public float ScrollOffset;
    public event Action? SelectionChanged;
    public event Action? OpenChanged;
    public int SelectedIndex
    {
        get => selected;
        set { if (selected != value) { selected = value; Changed(); SelectionChanged?.Invoke(); } }
    }
    public UiChoiceItem? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null;
    public string? SelectedTag => SelectedItem?.Tag;

    public const float RowHeight = 28, MaxListHeight = 196;

    public override Vector2 Measure(UiText text, float availableWidth) => new(availableWidth, 30);

    public float ListHeight => Math.Min(MaxListHeight, Items.Count * RowHeight);
    public RectangleF ListBounds => new(Bounds.X, Bounds.Bottom + 4, Bounds.Width, ListHeight);

    public override void Draw(UiPaint paint, float opacity)
    {
        if (!IsVisible) return;
        var alpha = opacity * (IsEnabled ? 1 : .45f);
        paint.FillRounded(Bounds, 8, UiPaint.WithAlpha(IsHovered && IsEnabled ? UiPaint.Hex(0x33FFFFFF) : UiTheme.Hover, alpha));
        var label = SelectedItem?.Label ?? "";
        var measured = paint.Text.Measure(label, 12);
        paint.DrawText(label, Bounds.X + 10, Bounds.Y + (Bounds.Height - measured.Y) / 2, 12, UiPaint.WithAlpha(UiTheme.Text, alpha), 400, false, Bounds.Width - 40);
        paint.DrawIcon("chevron-down", new RectangleF(Bounds.Right - 26, Bounds.Y + (Bounds.Height - 16) / 2, 16, 16), UiPaint.WithAlpha(UiTheme.Subtle, alpha), 1.6f);
    }

    public void DrawList(UiPaint paint, float opacity)
    {
        if (!IsOpen) return;
        var list = ListBounds;
        paint.FillRounded(list, 8, UiPaint.WithAlpha(UiTheme.Hover, opacity));
        var context = paint.Context;
        context.PushAxisAlignedClip(list, Vortice.Direct2D1.AntialiasMode.PerPrimitive);
        for (var i = 0; i < Items.Count; i++)
        {
            var row = new RectangleF(list.X + 4, list.Y + i * RowHeight - ScrollOffset, list.Width - 8, RowHeight - 2);
            if (row.Bottom < list.Y || row.Y > list.Bottom) continue;
            var item = Items[i];
            var hot = hoverRow == i && item.Enabled;
            if (hot) paint.FillRounded(row, 6, UiPaint.WithAlpha(UiTheme.Hover, opacity));
            if (i == selected) paint.FillRounded(row, 6, UiPaint.WithAlpha(UiTheme.Checked, opacity * .7f));
            var alpha = opacity * (item.Enabled ? 1 : .4f);
            var measured = paint.Text.Measure(item.Label, 12);
            paint.DrawText(item.Label, row.X + 8, row.Y + (row.Height - measured.Y) / 2, 12, UiPaint.WithAlpha(UiTheme.Text, alpha), 400, false, row.Width - 16);
        }
        context.PopAxisAlignedClip();
    }

    int hoverRow = -1;
    public override void PointerMove(Vector2 point)
    {
        var list = ListBounds;
        var row = IsOpen && list.Contains(point.X, point.Y) ? (int)((point.Y - list.Y + ScrollOffset) / RowHeight) : -1;
        if (row != hoverRow) { hoverRow = row; Changed(); }
    }

    public bool ContainsList(Vector2 point) => IsOpen && ListBounds.Contains(point.X, point.Y);
    public override bool HitTest(Vector2 point) => IsVisible && (Bounds.Contains(point.X, point.Y) || ContainsList(point));
    public override void PointerDown(Vector2 point) { }
    public override void PointerUp(Vector2 point, bool inside)
    {
        if (!IsEnabled) return;
        if (IsOpen && ContainsList(point))
        {
            var row = (int)((point.Y - ListBounds.Y + ScrollOffset) / RowHeight);
            if (row >= 0 && row < Items.Count && Items[row].Enabled) SelectedIndex = row;
            IsOpen = false; OpenChanged?.Invoke(); Changed(); return;
        }
        if (Bounds.Contains(point.X, point.Y)) { IsOpen = !IsOpen; ScrollOffset = 0; OpenChanged?.Invoke(); Changed(); }
    }

    public override void Wheel(Vector2 point, float delta)
    {
        if (!IsOpen) return;
        var max = Math.Max(0, Items.Count * RowHeight - ListHeight);
        ScrollOffset = Math.Clamp(ScrollOffset - delta * RowHeight * 3, 0, max); Changed();
    }

    public void Close() { if (IsOpen) { IsOpen = false; OpenChanged?.Invoke(); Changed(); } }
    public override bool WantsPointerCapture => true;
}
