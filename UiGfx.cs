using System.Drawing;
using System.Globalization;
using SharpGen.Runtime;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2D = Vortice.Direct2D1;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using Color = Vortice.Mathematics.Color4;

namespace Frd;

/// <summary>One D3D11 device with the Direct2D and DirectWrite factories needed to draw the UI.</summary>
sealed class UiDevices : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIDevice1 DxgiDevice { get; }
    public IDXGIFactory2 Factory { get; }
    public ID2D1Factory1 D2D { get; }
    public ID2D1Device D2DDevice { get; }
    public IDWriteFactory DWrite { get; }

    public UiDevices()
    {
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device; Context = context;
        DxgiDevice = device.QueryInterface<IDXGIDevice1>();
        DxgiDevice.MaximumFrameLatency = 1;
        Factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        D2D = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
        D2DDevice = D2D.CreateDevice(DxgiDevice);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>();
    }

    public void Dispose()
    {
        DWrite.Dispose(); D2DDevice.Dispose(); D2D.Dispose(); Factory.Dispose(); DxgiDevice.Dispose(); Context.Dispose(); Device.Dispose();
    }
}

/// <summary>
/// A window-sized flip-model swap chain with a Direct2D device context drawing straight into the back buffer.
/// "Composition" surfaces are premultiplied-alpha (used with DirectComposition so the DWM backdrop shows through).
/// </summary>
sealed class UiSurface : IDisposable
{
    readonly UiDevices devices;
    readonly nint hwnd;
    readonly bool composition;
    IDXGISwapChain1? swapChain;
    ID2D1Bitmap1? target;
    IDCompositionDevice? compositionDevice;
    IDCompositionTarget? compositionTarget;
    IDCompositionVisual? visual;
    int width, height;
    float dpi = 96;
    public ID2D1DeviceContext Context { get; }
    public int Width => width;
    public int Height => height;
    public IDXGISwapChain1 SwapChain => swapChain!;

    public UiSurface(UiDevices devices, nint hwnd, bool composition)
    {
        this.devices = devices; this.hwnd = hwnd; this.composition = composition;
        Context = devices.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        Context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        Context.AntialiasMode = AntialiasMode.PerPrimitive;
    }

    public void Resize(int newWidth, int newHeight, float dpi)
    {
        newWidth = Math.Max(1, newWidth); newHeight = Math.Max(1, newHeight);
        Context.SetDpi(dpi, dpi); this.dpi = dpi;
        if (swapChain != null && newWidth == width && newHeight == height) return;
        Context.Target = null;
        target?.Dispose(); target = null;
        width = newWidth; height = newHeight;
        if (swapChain == null) CreateSwapChain();
        else swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        AttachTarget();
    }

    void CreateSwapChain()
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput, SampleDescription = new(1, 0), SwapEffect = SwapEffect.FlipDiscard,
            Scaling = composition ? Scaling.Stretch : Scaling.None, AlphaMode = composition ? Vortice.DXGI.AlphaMode.Premultiplied : Vortice.DXGI.AlphaMode.Ignore
        };
        if (!composition)
        {
            swapChain = devices.Factory.CreateSwapChainForHwnd(devices.Device, hwnd, description);
            devices.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            return;
        }
        swapChain = devices.Factory.CreateSwapChainForComposition(devices.Device, description);
        compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(devices.DxgiDevice);
        compositionDevice.CreateTargetForHwnd(hwnd, true, out compositionTarget).CheckError();
        visual = compositionDevice.CreateVisual();
        visual.SetContent(swapChain).CheckError();
        compositionTarget!.SetRoot(visual).CheckError();
        compositionDevice.Commit().CheckError();
    }

    void AttachTarget()
    {
        using var surface = swapChain!.GetBuffer<IDXGISurface>(0);
        target = Context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, composition ? Vortice.DCommon.AlphaMode.Premultiplied : Vortice.DCommon.AlphaMode.Ignore),
            dpi, dpi, BitmapOptions.Target | BitmapOptions.CannotDraw));
        Context.Target = target;
    }

    public void Begin() => Context.BeginDraw();

    /// <summary>Ends the D2D pass; throws when the device is lost so the caller can rebuild.</summary>
    public void End() => Context.EndDraw();

    public Result Present(uint syncInterval = 0) => swapChain!.Present(syncInterval, PresentFlags.None);

    public void Dispose()
    {
        Context.Target = null;
        target?.Dispose(); visual?.Dispose(); compositionTarget?.Dispose(); compositionDevice?.Dispose();
        swapChain?.Dispose(); Context.Dispose();
    }
}

/// <summary>Cached DirectWrite text formats/layouts and helpers shared by measuring (any thread) and drawing (render thread).</summary>
sealed class UiText
{
    readonly IDWriteFactory factory;
    readonly Dictionary<(float Size, int Weight, bool Mono), IDWriteTextFormat> formats = new();
    readonly Dictionary<(string Text, float Size, int Weight, bool Mono, int Width), IDWriteTextLayout> layouts = new();
    readonly object gate = new();

    public UiText(IDWriteFactory factory) => this.factory = factory;

    IDWriteTextFormat Format(float size, int weight, bool mono)
    {
        if (!formats.TryGetValue((size, weight, mono), out var format))
        {
            format = factory.CreateTextFormat(mono ? "Cascadia Mono" : "Segoe UI", null!, (FontWeight)weight, FontStyle.Normal, FontStretch.Normal, size, "zh-CN");
            format.WordWrapping = WordWrapping.NoWrap;
            format.ParagraphAlignment = ParagraphAlignment.Near;
            formats[(size, weight, mono)] = format;
        }
        return format;
    }

    public IDWriteTextLayout Layout(string text, float size, int weight = 400, bool mono = false, float maxWidth = 4096)
    {
        lock (gate)
        {
            var key = (text, size, weight, mono, (int)maxWidth);
            if (layouts.TryGetValue(key, out var layout)) return layout;
            if (layouts.Count > 256) { foreach (var old in layouts.Values) old.Dispose(); layouts.Clear(); }
            layout = factory.CreateTextLayout(text, Format(size, weight, mono), maxWidth, 4096);
            layout.WordWrapping = WordWrapping.NoWrap;
            layouts[key] = layout;
            return layout;
        }
    }

    public Vector2 Measure(string text, float size, int weight = 400, bool mono = false, float maxWidth = 4096)
    {
        if (string.IsNullOrEmpty(text)) return new(0, size * 1.35f);
        var metrics = Layout(text, size, weight, mono, maxWidth).Metrics;
        return new(MathF.Ceiling(metrics.WidthIncludingTrailingWhitespace), MathF.Ceiling(metrics.Height));
    }
}

/// <summary>Draw-time helper bound to one D2D context: brush cache, rounded rectangles, text and icon paths.</summary>
sealed class UiPaint : IDisposable
{
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
        ["pulse"] = "M4 12 H8 L10.5 6 L13.5 18 L16 12 H20",
        ["chevron-down"] = "M6 9 L12 15 L18 9",
    };

    readonly ID2D1DeviceContext context;
    readonly UiDevices devices;
    readonly Dictionary<uint, ID2D1SolidColorBrush> brushes = new();
    readonly Dictionary<string, ID2D1PathGeometry> geometries = new();
    readonly UiText text;
    ID2D1StrokeStyle? roundStroke;
    public ID2D1DeviceContext Context => context;
    public UiText Text => text;
    public UiDevices Devices => devices;

    public UiPaint(UiDevices devices, ID2D1DeviceContext context, UiText text)
    {
        this.devices = devices; this.context = context; this.text = text;
    }

    public ID2D1SolidColorBrush Brush(Color color)
    {
        var key = ((uint)(color.A * 255 + .5f) << 24) | ((uint)(color.R * 255 + .5f) << 16) | ((uint)(color.G * 255 + .5f) << 8) | (uint)(color.B * 255 + .5f);
        if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = context.CreateSolidColorBrush(color);
        return brush;
    }

    public static Color Hex(uint argb) => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);
    public static Color Hex(string argb) => Hex(uint.Parse(argb.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    public static Color WithAlpha(Color color, float factor) => new(color.R, color.G, color.B, color.A * factor);

    public void FillRounded(RectangleF rect, float radius, Color color)
    {
        if (color.A <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        context.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = radius, RadiusY = radius }, Brush(color));
    }

    public void StrokeRounded(RectangleF rect, float radius, Color color, float thickness = 1)
    {
        if (color.A <= 0) return;
        var inset = thickness / 2;
        var r = new RectangleF(rect.X + inset, rect.Y + inset, rect.Width - thickness, rect.Height - thickness);
        context.DrawRoundedRectangle(new RoundedRectangle { Rect = r, RadiusX = Math.Max(0, radius - inset), RadiusY = Math.Max(0, radius - inset) }, Brush(color), thickness);
    }

    public void FillCircle(Vector2 center, float radius, Color color) =>
        context.FillEllipse(new Ellipse(center, radius, radius), Brush(color));

    public void DrawText(string value, float x, float y, float size, Color color, int weight = 400, bool mono = false, float maxWidth = 4096)
    {
        if (string.IsNullOrEmpty(value) || color.A <= 0) return;
        var layout = text.Layout(value, size, weight, mono, maxWidth);
        if (maxWidth < 4096) { using var sign = devices.DWrite.CreateEllipsisTrimmingSign(layout); layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign); }
        context.DrawTextLayout(new Vector2(x, y), layout, Brush(color), DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
    }

    public void DrawTextCentered(string value, RectangleF rect, float size, Color color, int weight = 400)
    {
        var measured = text.Measure(value, size, weight);
        DrawText(value, rect.X + (rect.Width - measured.X) / 2, rect.Y + (rect.Height - measured.Y) / 2, size, color, weight);
    }

    /// <summary>Strokes one of the 24-unit outline icons into <paramref name="box"/>.</summary>
    public void DrawIcon(string name, RectangleF box, Color color, float strokeWidth = 1.7f)
    {
        if (!geometries.TryGetValue(name, out var geometry)) geometries[name] = geometry = BuildGeometry(Icons[name]);
        var scale = Math.Min(box.Width, box.Height) / 24f;
        var previous = context.Transform;
        context.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(box.X + (box.Width - 24 * scale) / 2, box.Y + (box.Height - 24 * scale) / 2) * previous;
        roundStroke ??= devices.D2D.CreateStrokeStyle(new StrokeStyleProperties1 { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
        context.DrawGeometry(geometry, Brush(color), strokeWidth / Math.Max(scale, .01f), roundStroke);
        context.Transform = previous;
    }

    ID2D1PathGeometry BuildGeometry(string data)
    {
        var geometry = devices.D2D.CreatePathGeometry();
        using var sink = geometry.Open();
        var tokens = System.Text.RegularExpressions.Regex.Matches(data, @"[MLHVCAZ]|-?\d*\.?\d+").Select(m => m.Value).ToArray();
        var index = 0;
        float Next() => float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        var open = false; Vector2 current = default, start = default;
        while (index < tokens.Length)
        {
            var command = tokens[index++];
            switch (command)
            {
                case "M":
                    if (open) sink.EndFigure(FigureEnd.Open);
                    current = start = new Vector2(Next(), Next()); sink.BeginFigure(current, FigureBegin.Hollow); open = true; break;
                case "L": current = new Vector2(Next(), Next()); sink.AddLine(current); break;
                case "H": current = new Vector2(Next(), current.Y); sink.AddLine(current); break;
                case "V": current = new Vector2(current.X, Next()); sink.AddLine(current); break;
                case "C":
                {
                    var a = new Vector2(Next(), Next()); var b = new Vector2(Next(), Next()); current = new Vector2(Next(), Next());
                    sink.AddBezier(new BezierSegment { Point1 = a, Point2 = b, Point3 = current }); break;
                }
                case "A":
                {
                    var rx = Next(); var ry = Next(); var rotation = Next(); var large = Next() != 0; var sweep = Next() != 0;
                    current = new Vector2(Next(), Next());
                    sink.AddArc(new ArcSegment { Point = current, Size = new(rx, ry), RotationAngle = rotation,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise, ArcSize = large ? ArcSize.Large : ArcSize.Small });
                    break;
                }
                case "Z": sink.EndFigure(FigureEnd.Closed); open = false; current = start; break;
                default: throw new FormatException("Unsupported icon path command " + command);
            }
        }
        if (open) sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geometry;
    }

    public void Dispose()
    {
        foreach (var brush in brushes.Values) brush.Dispose();
        foreach (var geometry in geometries.Values) geometry.Dispose();
        roundStroke?.Dispose();
    }
}
