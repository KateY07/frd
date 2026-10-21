using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DCommon;
using Vortice.DXGI;

namespace Frd;

/// <summary>
/// Frosted glass for a window that floats over the desktop: grabs the pixels behind it (this app's own windows are excluded from
/// capture, so the grab shows what is really behind), blurs them with Direct2D and draws the result clipped to the card shape.
/// Unlike DWM acrylic this works for any shape and any window size.
/// </summary>
sealed class UiBackdrop : IDisposable
{
    const int BlurMargin = 28;
    nint screenDc, memoryDc, bitmapHandle, previousBitmap, bits;
    int width, height;
    ID2D1Bitmap? bitmap;
    GaussianBlur? blur;
    RectangleF captured;       // DIP rectangle (client coordinates of the owning window) the bitmap represents
    bool valid;

    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant; }

    /// <summary>Grabs <paramref name="cardDip"/> (plus a blur margin) from the screen. <paramref name="origin"/> is the window's screen position in pixels.</summary>
    public void Capture(UiPaint paint, RectangleF cardDip, Point origin, float scale)
    {
        valid = false;
        var area = RectangleF.Inflate(cardDip, BlurMargin / scale, BlurMargin / scale);
        var x = (int)Math.Floor(area.X * scale); var y = (int)Math.Floor(area.Y * scale);
        var w = Math.Max(1, (int)Math.Ceiling(area.Width * scale)); var h = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        if (w > 4096 || h > 4096) return;
        EnsureBuffers(w, h);
        if (screenDc == 0) screenDc = GetDC(0);
        // CAPTUREBLT includes layered windows; this app's windows are excluded from capture by their display affinity.
        if (!BitBlt(memoryDc, 0, 0, w, h, screenDc, origin.X + x, origin.Y + y, 0x00CC0020 | 0x40000000)) return;
        if (bitmap == null || bitmap.PixelSize.Width != w || bitmap.PixelSize.Height != h)
        {
            bitmap?.Dispose();
            bitmap = paint.Context.CreateBitmap(new Vortice.Mathematics.SizeI(w, h), bits, (uint)(w * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96 * scale, 96 * scale, BitmapOptions.None));
        }
        else bitmap.CopyFromMemory(bits, (uint)(w * 4));
        captured = new RectangleF(x / scale, y / scale, w / scale, h / scale);
        valid = true;
    }

    void EnsureBuffers(int w, int h)
    {
        if (memoryDc != 0 && w == width && h == height) return;
        ReleaseBuffers();
        width = w; height = h;
        var screen = GetDC(0);
        memoryDc = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        bitmapHandle = CreateDIBSection(screen, ref info, 0, out bits, 0, 0);
        ReleaseDC(0, screen);
        previousBitmap = SelectObject(memoryDc, bitmapHandle);
    }

    void ReleaseBuffers()
    {
        if (memoryDc != 0)
        {
            if (previousBitmap != 0) SelectObject(memoryDc, previousBitmap);
            DeleteObject(bitmapHandle); DeleteDC(memoryDc);
        }
        memoryDc = bitmapHandle = previousBitmap = bits = 0;
    }

    /// <summary>Draws the blurred pixels clipped to a rounded rectangle; false when nothing was captured.</summary>
    public bool Draw(UiPaint paint, RectangleF rect, float radius)
    {
        if (!valid || bitmap == null) return false;
        var context = paint.Context;
        blur ??= new GaussianBlur(context) { BorderMode = BorderMode.Hard };
        blur.SetInput(0, bitmap, true);
        blur.StandardDeviation = 12f;
        using var geometry = paint.Devices.D2D.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = rect, RadiusX = radius, RadiusY = radius });
        context.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), GeometricMask = geometry,
            MaskAntialiasMode = AntialiasMode.PerPrimitive, MaskTransform = Matrix3x2.Identity, Opacity = 1, LayerOptions = LayerOptions1.None
        }, null!);
        // The bitmap's DPI equals the window's, so its DIP size matches the grabbed rectangle.
        context.DrawImage(blur, new Vector2(captured.X, captured.Y), InterpolationMode.Linear, CompositeMode.SourceOver);
        context.PopLayer();
        return true;
    }

    public void Dispose()
    {
        blur?.Dispose(); bitmap?.Dispose();
        ReleaseBuffers();
        if (screenDc != 0) { ReleaseDC(0, screenDc); screenDc = 0; }
    }
}
