using System.Drawing;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DXGI;

namespace Frd;

/// <summary>
/// Draws the host's mouse pointer (position and shape from the cursor channel) over the video on the controller, so it can be seen
/// without forwarding input. Optional; the shapes are converted to premultiplied BGRA once and kept as Direct2D bitmaps.
/// </summary>
sealed class RemoteCursorOverlay
{
    sealed class Image
    {
        public int Width, Height, HotX, HotY;
        public byte[] Bgra = [];
        public ID2D1Bitmap? Bitmap;
    }

    readonly object gate = new();
    readonly Dictionary<long, Image> images = new();
    long id;
    bool visible;
    double x, y;

    /// <summary>Called when the cursor channel delivers a new position, visibility or shape.</summary>
    public void Apply(CursorUpdate update)
    {
        lock (gate)
        {
            if (update.Reset) { foreach (var image in images.Values) image.Bitmap?.Dispose(); images.Clear(); }
            if (update.Shape != null)
            {
                if (images.Remove(update.Id, out var old)) old.Bitmap?.Dispose();
                if (images.Count < 64) images[update.Id] = Convert(update.Shape);
            }
            id = update.Id; visible = update.Visible; x = update.X; y = update.Y;
        }
    }

    public void Clear()
    {
        lock (gate) { foreach (var image in images.Values) image.Bitmap?.Dispose(); images.Clear(); visible = false; }
    }

    /// <summary>Render thread. <paramref name="video"/> is the video rectangle in DIPs; <paramref name="dipPerVideoPixel"/> its scale.</summary>
    public void Draw(UiPaint paint, RectangleF video, float dipPerVideoPixel)
    {
        Image? image; double px, py;
        lock (gate)
        {
            if (!visible || id == 0 || !images.TryGetValue(id, out image)) return;
            px = x; py = y;
        }
        if (video.Width <= 0) return;
        if (image.Bitmap == null)
        {
            unsafe
            {
                fixed (byte* data = image.Bgra)
                    image.Bitmap = paint.Context.CreateBitmap(new Vortice.Mathematics.SizeI(image.Width, image.Height), (nint)data, (uint)(image.Width * 4),
                        new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
        }
        var scale = Math.Max(.5f, dipPerVideoPixel);
        var left = video.X + (float)px * (video.Width - 1) - image.HotX * scale;
        var top = video.Y + (float)py * (video.Height - 1) - image.HotY * scale;
        paint.Context.DrawBitmap(image.Bitmap, new RectangleF(left, top, image.Width * scale, image.Height * scale), 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
    }

    static bool Bit(byte[] bits, int rowBytes, int row, int column) => (bits[row * rowBytes + (column >> 3)] & (0x80 >> (column & 7))) != 0;

    /// <summary>Windows cursor bitmaps (bottom-up DIBs, AND/XOR masks) to a top-down premultiplied BGRA image.</summary>
    static Image Convert(CursorShape shape)
    {
        int width = shape.Width, height = shape.Height, rowBytes = (width + 31) / 32 * 4;
        var pixels = new byte[width * height * 4];
        if (shape.Color.Length != 0)
        {
            var hasAlpha = false;
            for (var i = 3; i < shape.Color.Length; i += 4) if (shape.Color[i] != 0) { hasAlpha = true; break; }
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var source = ((height - 1 - row) * width + column) * 4; var target = (row * width + column) * 4;
                    int alpha = hasAlpha ? shape.Color[source + 3] : (Bit(shape.Mask, rowBytes, height - 1 - row, column) ? 0 : 255);
                    pixels[target] = (byte)(shape.Color[source] * alpha / 255);
                    pixels[target + 1] = (byte)(shape.Color[source + 1] * alpha / 255);
                    pixels[target + 2] = (byte)(shape.Color[source + 2] * alpha / 255);
                    pixels[target + 3] = (byte)alpha;
                }
        }
        else
        {
            // Monochrome: the mask holds the AND plane on top and the XOR plane below; both are stored bottom-up.
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var and = Bit(shape.Mask, rowBytes, 2 * height - 1 - row, column);
                    var xor = Bit(shape.Mask, rowBytes, height - 1 - row, column);
                    var target = (row * width + column) * 4;
                    if (and && !xor) continue;                                   // transparent
                    byte value = !and && xor ? (byte)255 : and ? (byte)0x30 : (byte)0;   // white, "inverted" drawn dark, black
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = value; pixels[target + 3] = 255;
                }
        }
        return new Image { Width = width, Height = height, HotX = shape.HotX, HotY = shape.HotY, Bgra = pixels };
    }
}
