using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Frd;

/// <summary>Shared flat frosted-glass look for the controller overlay and the host window.</summary>
static class Glass
{
    public sealed record Palette(Color Tint, Color Border, Color Text, Color Subtle, Color Hover, Color Checked, Color CheckedText,
        Color Line, Color Good, Color Warn, Color Bad);

    public static readonly WindowTransparencyLevel[] Levels = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent];

    public static Palette For(bool dark) => dark
        ? new(Color.Parse("#A6161A22"), Color.Parse("#2EFFFFFF"), Color.Parse("#F2F5F9"), Color.Parse("#CCD3DC"),
            Color.Parse("#26FFFFFF"), Color.Parse("#66528CFF"), Color.Parse("#E6EEFF"), Color.Parse("#26FFFFFF"),
            Color.Parse("#3DD68C"), Color.Parse("#F5B84A"), Color.Parse("#F06A66"))
        : new(Color.Parse("#8CFFFFFF"), Color.Parse("#A6FFFFFF"), Color.Parse("#1D2530"), Color.Parse("#4F5B6B"),
            Color.Parse("#66FFFFFF"), Color.Parse("#D9FFFFFF"), Color.Parse("#0C447C"), Color.Parse("#33445566"),
            Color.Parse("#1E9E5E"), Color.Parse("#B7791F"), Color.Parse("#D64540"));

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    /// <summary>Hovering the orb must keep it glass: raise the tint slightly instead of swapping in a near-transparent wash.</summary>
    static Color Lift(Color c) => Color.FromArgb(c.A, (byte)Math.Min(255, c.R + 18), (byte)Math.Min(255, c.G + 18), (byte)Math.Min(255, c.B + 22));

    public static Styles BuildStyles(Palette p)
    {
        Style On(Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object? Value)[] setters)
        {
            var style = new Style(selector);
            foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
            return style;
        }
        const string Part = "PART_ContentPresenter";
        return
        [
            On(x => x.Is<Button>().Class("chip").Class(":pointerover").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(p.Hover))),
            On(x => x.Is<Button>().Class("chip").Class(":pressed").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(p.Hover))),
            On(x => x.Is<ToggleButton>().Class("chip").Class(":checked").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(p.Checked)),
                (ContentPresenter.ForegroundProperty, Brush(p.CheckedText))),
            On(x => x.Is<Button>().Class("orb").Class(":pointerover").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(Lift(p.Tint)))),
            On(x => x.Is<Button>().Class("orb").Class(":pressed").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(Lift(p.Tint)))),
            On(x => x.Is<Button>().Class("chip").Class(":disabled"), (Visual.OpacityProperty, .45)),
            On(x => x.Is<Button>().Class("danger").Class(":pointerover").Template().OfType<ContentPresenter>().Name(Part),
                (ContentPresenter.BackgroundProperty, Brush(p.Bad)),
                (ContentPresenter.ForegroundProperty, Brushes.White)),
        ];
    }

    static readonly Dictionary<string, string> Icons = new()
    {
        ["mouse"] = "M12 3 C8.5 3 6.5 5.5 6.5 9 V15 C6.5 18.5 8.5 21 12 21 C15.5 21 17.5 18.5 17.5 15 V9 C17.5 5.5 15.5 3 12 3 Z M12 3 V10",
        ["clipboard"] = "M8 5 H6 A2 2 0 0 0 4 7 V19 A2 2 0 0 0 6 21 H18 A2 2 0 0 0 20 19 V7 A2 2 0 0 0 18 5 H16 M9 3 H15 V7 H9 Z",
        ["expand"] = "M4 9 V4 H9 M15 4 H20 V9 M20 15 V20 H15 M9 20 H4 V15",
        ["shrink"] = "M9 4 V9 H4 M20 9 H15 V4 M15 20 V15 H20 M4 15 H9 V20",
        ["sliders"] = "M3 8 H5.5 M10.5 8 H21 M3 16 H13.5 M18.5 16 H21 M10.5 8 A2.5 2.5 0 1 0 5.5 8 A2.5 2.5 0 1 0 10.5 8 M18.5 16 A2.5 2.5 0 1 0 13.5 16 A2.5 2.5 0 1 0 18.5 16",
        ["power"] = "M12 3 V12 M7 6.5 A8 8 0 1 0 17 6.5",
        ["chevrons"] = "M6 6 L12 12 L6 18 M13 6 L19 12 L13 18",
        ["monitor"] = "M3 5 H21 V16 H3 Z M8 20 H16 M12 16 V20",
        ["menu"] = "M4 7 H20 M4 12 H20 M4 17 H20",
        ["activity"] = "M3 12 H7 L10 5 L14 19 L17 12 H21",
        ["pulse"] = "M4 12 H8 L10.5 6 L13.5 18 L16 12 H20"
    };

    /// <summary>A 24-unit outline icon that follows the owning button's foreground.</summary>
    public static Path Icon(string name, double size = 18) => IconPath(name, size);

    static Path IconPath(string name, double size)
    {
        var geometry = StreamGeometry.Parse(Icons[name]);
        geometry.Transform = new ScaleTransform(size / 24, size / 24);
        var path = new Path
        {
            Data = geometry, Width = size, Height = size, Stretch = Stretch.None,
            StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        path.Bind(Shape.StrokeProperty, new Binding("Foreground")
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ContentPresenter) } });
        return path;
    }

    /// <summary>Gives a button an icon, with an optional text label next to it.</summary>
    public static void SetIcon(Button button, string icon, string? text = null, double size = 18)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(IconPath(icon, size));
        if (!string.IsNullOrEmpty(text)) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        button.Content = row;
        if (string.IsNullOrEmpty(text)) { button.Width = 32; button.Padding = new Thickness(0); }
    }

    /// <summary>A flat chip; toggle chips show their state as a tinted fill. Pass an icon for an icon (and label) chip.</summary>
    public static T Chip<T>(T button, string text, string? name = null, string? icon = null, bool label = true) where T : Button
    {
        button.Classes.Add("chip");
        if (name != null) button.Name = name;
        button.Content = text;
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(8); button.Padding = new Thickness(10, 0);
        button.MinHeight = 28; button.Height = 28; button.FontSize = 12;
        button.VerticalContentAlignment = VerticalAlignment.Center; button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Cursor = new Cursor(StandardCursorType.Hand);
        if (icon != null) SetIcon(button, icon, label ? text : null);
        return button;
    }

    public static Border Divider(Palette p) => new() { Height = 1, Background = Brush(p.Line), Margin = new Thickness(0, 4) };

    [DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    /// <summary>Clips the native window (and its acrylic backdrop) to a rounded rectangle.</summary>
    public static void ClipRounded(Window window, double radiusDip)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        var scale = window.RenderScaling;
        var width = (int)Math.Ceiling(window.ClientSize.Width * scale);
        var height = (int)Math.Ceiling(window.ClientSize.Height * scale);
        if (width <= 0 || height <= 0) return;
        var diameter = (int)Math.Round(Math.Min(radiusDip * 2 * scale, Math.Min(width, height)));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        SetWindowRgn(handle, region, true);
    }
}
