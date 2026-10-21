using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>
/// The floating glass panel: an expanded bar/settings card that collapses (animated) into a draggable orb or a status pill.
/// Subclasses provide the content; this class owns geometry, dragging, animation and hit-testing.
/// </summary>
abstract class UiGlassPanel
{
    public const float Margin = 12, Radius = 16, OrbSize = 50, PillHeight = 40, AnimationSeconds = .24f, DragThreshold = 4;
    public float PillWidth = 116;

    protected readonly object Gate;
    long animationStart;
    RectangleF animationFrom, animationTo;
    bool animatingToCollapsed;
    Vector2 dragOrigin, pointerOrigin;
    bool dragging, dragMoved, pressedOnBackground;
    bool pendingOrbClick;

    public bool Collapsed { get; private set; }
    public bool Animating { get; private set; }
    public Vector2 PanelFraction = new(.5f, 0), OrbFraction = new(1, 0);
    public float MaxWidth = 440;
    public bool UsePill;
    public string PillText = "— ms";
    public Color StatusColor = UiTheme.Warn;
    public float ContentOpacity = 1, OrbOpacity;
    public RectangleF CurrentRect;
    public float CurrentRadius = Radius;
    public int ClientWidth, ClientHeight;
    public Action? Invalidate;
    public Action<bool>? AnimationFinished;           // argument: collapsed
    public Action? InteractionStarted;                 // any pointer use (resets the idle timer)
    /// <summary>When set, a press on the panel background is handed to the owner (for example a native window move) instead of dragging inside the window.</summary>
    public Func<Vector2, bool>? BackgroundPressed;
    public Func<UiPaint, RectangleF, float, float, bool>? Backdrop;   // paint glass behind (rect, radius, opacity); returns true when it drew a blur

    protected UiGlassPanel(object gate) => Gate = gate;

    protected abstract IEnumerable<UiElement> Elements { get; }
    protected abstract float MeasureContent(UiText text, float width);
    protected abstract void LayoutContent(UiText text, RectangleF content);
    protected abstract void DrawContent(UiPaint paint, float opacity);
    protected virtual void DrawOverlay(UiPaint paint, float opacity) { }
    protected virtual bool ContainsOverlay(Vector2 point) => false;

    public Vector2 CollapsedSize => UsePill ? new(PillWidth, PillHeight) : new(OrbSize, OrbSize);
    public float PanelWidth => Math.Max(280, Math.Min(MaxWidth, ClientWidth - Margin * 2));

    public RectangleF ExpandedRect(UiText text)
    {
        var width = PanelWidth;
        var height = MeasureContent(text, width - 10) + 10;
        return new(Margin + PanelFraction.X * Math.Max(0, ClientWidth - width - Margin * 2),
            Margin + PanelFraction.Y * Math.Max(0, ClientHeight - height - Margin * 2), width, height);
    }

    public RectangleF CollapsedRect()
    {
        var size = CollapsedSize;
        return new(Margin + OrbFraction.X * Math.Max(0, ClientWidth - size.X - Margin * 2),
            Margin + OrbFraction.Y * Math.Max(0, ClientHeight - size.Y - Margin * 2), size.X, size.Y);
    }

    /// <summary>Recomputes geometry for the current client size and state (not while animating).</summary>
    public void Layout(UiText text, int clientWidth, int clientHeight)
    {
        ClientWidth = clientWidth; ClientHeight = clientHeight;
        if (Animating) return;
        if (Collapsed) { CurrentRect = CollapsedRect(); CurrentRadius = CurrentRect.Height / 2; ContentOpacity = 0; OrbOpacity = 1; }
        else
        {
            CurrentRect = ExpandedRect(text); CurrentRadius = Radius; ContentOpacity = 1; OrbOpacity = 0;
            LayoutContent(text, new RectangleF(CurrentRect.X + 5, CurrentRect.Y + 5, CurrentRect.Width - 10, CurrentRect.Height - 10));
        }
    }

    public void Relayout(UiText text) => Layout(text, ClientWidth, ClientHeight);

    public void StartAnimation(UiText text, bool toCollapsed, bool instant = false)
    {
        if (Animating || Collapsed == toCollapsed) return;
        animationFrom = CurrentRect;
        // Target geometry: the orb/pill, or the expanded card (laid out at its final place).
        if (toCollapsed) animationTo = CollapsedRect();
        else
        {
            animationTo = ExpandedRect(text);
            LayoutContent(text, new RectangleF(animationTo.X + 5, animationTo.Y + 5, animationTo.Width - 10, animationTo.Height - 10));
        }
        if (instant) { Collapsed = toCollapsed; Layout(text, ClientWidth, ClientHeight); AnimationFinished?.Invoke(Collapsed); Invalidate?.Invoke(); return; }
        animatingToCollapsed = toCollapsed;
        animationStart = Stopwatch.GetTimestamp();
        Animating = true;
        Invalidate?.Invoke();
    }

    /// <summary>Advances the animation; returns true while it is still running.</summary>
    public bool Tick(UiText text)
    {
        if (!Animating) return false;
        var t = Math.Min(1f, (float)((Stopwatch.GetTimestamp() - animationStart) / (double)Stopwatch.Frequency / AnimationSeconds));
        var e = 1 - MathF.Pow(1 - t, 3);
        float Lerp(float a, float b) => a + (b - a) * e;
        CurrentRect = new RectangleF(Lerp(animationFrom.X, animationTo.X), Lerp(animationFrom.Y, animationTo.Y), Lerp(animationFrom.Width, animationTo.Width), Lerp(animationFrom.Height, animationTo.Height));
        var fromRadius = animationFrom.Height <= PillHeight + 1 ? animationFrom.Height / 2 : Radius;
        var toRadius = animationTo.Height <= PillHeight + 1 ? animationTo.Height / 2 : Radius;
        CurrentRadius = Lerp(fromRadius, toRadius);
        ContentOpacity = animatingToCollapsed ? 1 - e : e;
        OrbOpacity = animatingToCollapsed ? e : 1 - e;
        if (t >= 1)
        {
            Animating = false; Collapsed = animatingToCollapsed;
            Layout(text, ClientWidth, ClientHeight);
            AnimationFinished?.Invoke(Collapsed);
        }
        Invalidate?.Invoke();
        return Animating;
    }

    // ---- drawing -----------------------------------------------------------------------------------------------

    public void Draw(UiPaint paint)
    {
        var rect = CurrentRect;
        if (rect.Width <= 0) return;
        var blurred = Backdrop?.Invoke(paint, rect, CurrentRadius, 1) ?? false;
        paint.FillRounded(rect, CurrentRadius, blurred ? UiTheme.Tint : UiPaint.Hex(0xCC161A22));
        paint.StrokeRounded(rect, CurrentRadius, UiTheme.Border);
        if (ContentOpacity > .01f)
        {
            // Content is clipped to the (possibly still growing) card while it fades.
            paint.Context.PushAxisAlignedClip(new RectangleF(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), Vortice.Direct2D1.AntialiasMode.PerPrimitive);
            DrawContent(paint, ContentOpacity);
            paint.Context.PopAxisAlignedClip();
            DrawOverlay(paint, ContentOpacity);
        }
        if (OrbOpacity > .01f) DrawOrb(paint, rect, OrbOpacity);
    }

    void DrawOrb(UiPaint paint, RectangleF rect, float opacity)
    {
        var cy = rect.Y + rect.Height / 2;
        if (UsePill)
        {
            paint.FillCircle(new Vector2(rect.X + 16, cy), 4, UiPaint.WithAlpha(StatusColor, opacity));
            var measured = paint.Text.Measure(PillText, 12);
            paint.DrawText(PillText, rect.X + 28, cy - measured.Y / 2, 12, UiPaint.WithAlpha(UiTheme.Text, opacity));
            paint.DrawIcon("menu", new RectangleF(rect.Right - 14 - 18, cy - 9, 18, 18), UiPaint.WithAlpha(UiTheme.Text, opacity));
        }
        else
        {
            paint.DrawIcon("menu", new RectangleF(rect.X + (rect.Width - 24) / 2, cy - 12, 24, 24), UiPaint.WithAlpha(UiTheme.Text, opacity));
            paint.FillCircle(new Vector2(rect.Right - 10, rect.Y + 10), 3.5f, UiPaint.WithAlpha(StatusColor, opacity));
        }
    }

    // ---- pointer handling (all coordinates in DIPs) ----------------------------------------------------------

    public bool Contains(Vector2 point) => CurrentRect.Contains(point.X, point.Y) || (!Collapsed && !Animating && ContainsOverlay(point));

    UiElement? Hit(Vector2 point)
    {
        UiElement? found = null;
        foreach (var element in Elements) if (element.IsVisible && element.HitTest(point)) found = element;
        return found;
    }

    UiElement? hover, pressed;
    public bool IsPointerOver { get; private set; }
    /// <summary>True while a press that started on the panel is still held (drag or button press): the pointer belongs to the panel even outside it.</summary>
    public bool IsCapturing => dragging || pressed != null;
    public UiElement? HoveredElement => hover;

    public bool PointerMove(Vector2 point)
    {
        var inside = Contains(point);
        IsPointerOver = inside;
        if (dragging)
        {
            if (!dragMoved && Vector2.Distance(point, pointerOrigin) > DragThreshold) dragMoved = true;
            if (dragMoved) MoveTo(dragOrigin + (point - pointerOrigin));
            Invalidate?.Invoke();
            return true;
        }
        if (pressed != null) { pressed.PointerMove(point); Invalidate?.Invoke(); return true; }
        var target = !Collapsed && !Animating ? Hit(point) : null;
        if (!ReferenceEquals(target, hover))
        {
            if (hover != null) hover.IsHovered = false;
            hover = target;
            if (hover != null) hover.IsHovered = true;
            Invalidate?.Invoke();
        }
        if (target != null) target.PointerMove(point);
        if (inside) InteractionStarted?.Invoke();
        return inside;
    }

    public void PointerLeave()
    {
        IsPointerOver = false;
        if (hover != null) { hover.IsHovered = false; hover = null; Invalidate?.Invoke(); }
    }

    public bool PointerDown(Vector2 point)
    {
        if (!Contains(point)) return false;
        InteractionStarted?.Invoke();
        if (Animating) return true;
        var target = !Collapsed ? Hit(point) : null;
        if (target is { IsEnabled: true } or UiChoice)
        {
            pressed = target; target.PointerDown(point);
            if (!target.WantsPointerCapture) { pressed = null; }
        }
        else if (BackgroundPressed?.Invoke(point) == true) { /* the owner took over (window move) */ }
        else
        {
            dragging = true; dragMoved = false; pointerOrigin = point;
            dragOrigin = new Vector2(CurrentRect.X, CurrentRect.Y);
            pressedOnBackground = true;
            pendingOrbClick = Collapsed;
        }
        Invalidate?.Invoke();
        return true;
    }

    /// <summary>Returns true when the release ended a click on the collapsed orb (the owner expands the panel).</summary>
    public bool PointerUp(Vector2 point, out bool consumed)
    {
        consumed = false;
        var orbClick = false;
        if (dragging)
        {
            dragging = false; consumed = true; pressedOnBackground = false;
            if (!dragMoved && pendingOrbClick) orbClick = true;
            pendingOrbClick = false;
        }
        else if (pressed != null)
        {
            var element = pressed; pressed = null; consumed = true;
            element.PointerUp(point, element.HitTest(point));
        }
        else if (Contains(point))
        {
            consumed = true;
            foreach (var element in Elements) if (element is UiChoice choice && choice.IsOpen) choice.PointerUp(point, false);
        }
        // A click outside an open dropdown closes it.
        foreach (var element in Elements)
            if (element is UiChoice { IsOpen: true } open && !open.ContainsList(point) && !open.Bounds.Contains(point.X, point.Y)) open.Close();
        Invalidate?.Invoke();
        return orbClick;
    }

    public bool Wheel(Vector2 point, float delta)
    {
        foreach (var element in Elements) if (element is UiChoice { IsOpen: true } choice && choice.ContainsList(point)) { choice.Wheel(point, delta); return true; }
        return false;
    }

    void MoveTo(Vector2 topLeft)
    {
        var size = new Vector2(CurrentRect.Width, CurrentRect.Height);
        var availableX = Math.Max(1, ClientWidth - size.X - Margin * 2);
        var availableY = Math.Max(1, ClientHeight - size.Y - Margin * 2);
        var fraction = new Vector2(Math.Clamp((topLeft.X - Margin) / availableX, 0, 1), Math.Clamp((topLeft.Y - Margin) / availableY, 0, 1));
        if (Collapsed) OrbFraction = fraction; else PanelFraction = fraction;
        CurrentRect = new RectangleF(Margin + fraction.X * Math.Max(0, ClientWidth - size.X - Margin * 2),
            Margin + fraction.Y * Math.Max(0, ClientHeight - size.Y - Margin * 2), size.X, size.Y);
        if (!Collapsed) pendingRelayout = true;
    }

    protected bool pendingRelayout;
    public bool TakePendingRelayout() { var value = pendingRelayout; pendingRelayout = false; return value; }
}
