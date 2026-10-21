using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>A round colour dot in the annotation tool row; <see cref="Click"/> fires on release inside it.</summary>
sealed class UiSwatch : UiElement
{
    public Color Fill;
    public bool IsSelected;
    public event Action? Click;

    public override Vector2 Measure(UiText text, float availableWidth) => new(26, 28);
    public override bool WantsPointerCapture => true;

    public override void Draw(UiPaint paint, float opacity)
    {
        if (!IsVisible) return;
        var center = new Vector2(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
        if (IsHovered || IsSelected) paint.FillCircle(center, 12, UiPaint.WithAlpha(IsSelected ? UiTheme.Checked : UiTheme.Hover, opacity));
        paint.FillCircle(center, 7.5f, UiPaint.WithAlpha(Fill, opacity));
    }

    public override void PointerUp(Vector2 point, bool inside)
    {
        if (inside && IsEnabled) Click?.Invoke();
    }
}

enum AnnotationKind { Pen, Rectangle, Text }

/// <summary>
/// Annotations drawn over the video: freehand strokes, rectangles and text. Everything is kept in video-relative coordinates
/// (positions and sizes are fractions of the picture), so a mark stays on the same spot of the remote desktop at any window size.
/// The same class draws on the controller and on the host overlay.
/// </summary>
sealed class AnnotationBoard
{
    public const int MaximumShapes = 1000, MaximumPointsPerShape = 20000, MaximumTextLength = 200;

    sealed class Shape(AnnotationKind kind, Color color, float size)
    {
        public readonly AnnotationKind Kind = kind;
        public readonly Color Color = color;
        public readonly float Size = size;          // line width, or font size, as a fraction of the video width
        public readonly List<Vector2> Points = new();
        public string Text = "";
    }

    readonly object gate = new();
    readonly List<Shape> shapes = new();
    Shape? current;
    ID2D1StrokeStyle? roundStyle, squareStyle;

    public bool HasShapes { get { lock (gate) return shapes.Count > 0; } }
    public bool IsDrawing { get { lock (gate) return current != null; } }

    /// <summary>Starts a pen stroke or a rectangle at <paramref name="relative"/>; <paramref name="width"/> is a fraction of the video width.</summary>
    public void Begin(AnnotationKind kind, Vector2 relative, Color color, float width)
    {
        if (kind == AnnotationKind.Text) return;
        lock (gate)
        {
            if (shapes.Count >= MaximumShapes) shapes.RemoveAt(0);
            current = new Shape(kind, color, width);
            current.Points.Add(relative); current.Points.Add(relative);      // a click leaves a dot, or an empty rectangle
            shapes.Add(current);
        }
    }

    /// <summary>Extends the pen stroke, or moves the rectangle's opposite corner.</summary>
    public void Add(Vector2 relative)
    {
        lock (gate)
        {
            if (current == null) return;
            if (current.Kind == AnnotationKind.Rectangle) { current.Points[1] = relative; return; }
            if (Vector2.DistanceSquared(current.Points[^1], relative) < 1e-7f) return;
            if (current.Points.Count < MaximumPointsPerShape) current.Points.Add(relative);
        }
    }

    public void AddText(Vector2 relative, string text, Color color, float fontSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (gate)
        {
            if (shapes.Count >= MaximumShapes) shapes.RemoveAt(0);
            var shape = new Shape(AnnotationKind.Text, color, fontSize) { Text = text.Length > MaximumTextLength ? text[..MaximumTextLength] : text };
            shape.Points.Add(relative);
            shapes.Add(shape);
        }
    }

    public void End() { lock (gate) current = null; }
    public void Undo() { lock (gate) { current = null; if (shapes.Count > 0) shapes.RemoveAt(shapes.Count - 1); } }
    public void Clear() { lock (gate) { current = null; shapes.Clear(); } }

    /// <summary>Render thread. <paramref name="video"/> is the video rectangle in the drawing surface's units (DIPs).</summary>
    public void Draw(UiPaint paint, RectangleF video)
    {
        lock (gate)
        {
            if (shapes.Count == 0 || video.Width <= 0) return;
            roundStyle ??= paint.Devices.D2D.CreateStrokeStyle(new StrokeStyleProperties1 { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
            squareStyle ??= paint.Devices.D2D.CreateStrokeStyle(new StrokeStyleProperties1 { LineJoin = LineJoin.Round });
            var context = paint.Context;
            Vector2 Map(Vector2 p) => new(video.X + p.X * video.Width, video.Y + p.Y * video.Height);
            foreach (var shape in shapes)
            {
                switch (shape.Kind)
                {
                    case AnnotationKind.Pen:
                    {
                        var brush = paint.Brush(shape.Color); var width = Math.Max(1.5f, shape.Size * video.Width);
                        for (var i = 1; i < shape.Points.Count; i++) context.DrawLine(Map(shape.Points[i - 1]), Map(shape.Points[i]), brush, width, roundStyle);
                        break;
                    }
                    case AnnotationKind.Rectangle:
                    {
                        var a = Map(shape.Points[0]); var b = Map(shape.Points[1]);
                        var rect = new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                        context.DrawRectangle(new Vortice.RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), paint.Brush(shape.Color), Math.Max(1.5f, shape.Size * video.Width), squareStyle);
                        break;
                    }
                    case AnnotationKind.Text:
                    {
                        var at = Map(shape.Points[0]); var size = Math.Max(8f, shape.Size * video.Width);
                        // A dark shadow keeps the text readable on any background.
                        paint.DrawText(shape.Text, at.X + 1, at.Y + 1, size, new Color(0, 0, 0, .55f), 600);
                        paint.DrawText(shape.Text, at.X, at.Y, size, shape.Color, 600);
                        break;
                    }
                }
            }
        }
    }
}
