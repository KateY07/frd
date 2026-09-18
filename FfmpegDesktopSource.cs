namespace Frd;

public static class TransmissionGeometry
{
    public static bool IsValidScale(double scale) => scale is 1 or .75 or .5;
    public static void ValidateScale(double scale)
    {
        if (!IsValidScale(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Transmission scale must be 1, 0.75 or 0.5.");
    }
    public static (int Width, int Height) Dimensions(int width, int height, double scale)
    {
        ValidateScale(scale);
        if (width < 64 || height < 64 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }
}

sealed class DesktopStreamSource : IDisposable
{
    readonly object gate = new();
    DesktopCapture capture;
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int Width { get; }
    public int Height { get; }
    public DesktopCaptureStatistics? Statistics { get { lock (gate) return capture.Statistics; } }
    public string Backend { get { lock (gate) return capture.Backend; } }
    public DesktopStreamSource(double scale)
    {
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        SourceWidth = bounds.Width; SourceHeight = bounds.Height;
        (Width, Height) = TransmissionGeometry.Dimensions(SourceWidth, SourceHeight, scale);
        capture = new(Width, Height);
    }
    public byte[] Capture() { lock (gate) return capture.Capture(); }
    public void Resize(int width, int height)
    {
        var replacement = new DesktopCapture(width, height);
        try { replacement.Capture(); }
        catch { replacement.Dispose(); throw; }
        lock (gate) { var previous = capture; capture = replacement; previous.Dispose(); }
    }
    public void Dispose() { lock (gate) capture.Dispose(); }
}
