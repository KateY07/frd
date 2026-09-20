using System.Diagnostics;

namespace Frd;

enum CapturePixelMode { Bgra, Rgb332, Rgb565, Gray8, Gray4 }

static class CapturePixelModes
{
    public static CapturePixelMode Parse(string mode) => mode switch
    {
        "bgra" => CapturePixelMode.Bgra,
        "rgb332" => CapturePixelMode.Rgb332,
        "rgb565" => CapturePixelMode.Rgb565,
        "gray8" => CapturePixelMode.Gray8,
        "gray4" => CapturePixelMode.Gray4,
        _ => throw new ArgumentException($"Unsupported inputPixelMode '{mode}'. Expected bgra, rgb332, rgb565, gray8 or gray4.")
    };

    public static DesktopCapturePath CapturePath(CapturePixelMode mode) => mode switch
    {
        CapturePixelMode.Rgb332 => DesktopCapturePath.PixelShaderRgb332,
        CapturePixelMode.Rgb565 => DesktopCapturePath.PixelShaderRgb565,
        CapturePixelMode.Gray8 => DesktopCapturePath.PixelShaderGray8,
        CapturePixelMode.Gray4 => DesktopCapturePath.PixelShaderGray4,
        _ => throw new ArgumentException("BGRA uses the standard desktop capture path.", nameof(mode))
    };
}

public static class TransmissionGeometry
{
    public static bool IsValidScale(double scale) => scale == 1;
    public static void ValidateScale(double scale)
    {
        if (!IsValidScale(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Transmission scale must be 1.");
    }
    public static (int Width, int Height) Dimensions(int width, int height, double scale)
    {
        ValidateScale(scale);
        if (width < 64 || height < 64 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        return (width & ~1, height & ~1);
    }
}

sealed class DesktopStreamSource : IDisposable
{
    readonly object gate = new();
    readonly SecureDesktopFrames? secure;
    DesktopCapture? capture;
    DxgiDesktopCapture? packedCapture;
    CapturePixelMode pixelMode;
    bool disposed;
    bool secureWasActive;
    long secureFailureStarted;
    int width, height, sourceWidth, sourceHeight;
    public int SourceWidth { get { lock (gate) return sourceWidth; } }
    public int SourceHeight { get { lock (gate) return sourceHeight; } }
    public int Width { get { lock (gate) return width; } }
    public int Height { get { lock (gate) return height; } }
    public DesktopCaptureStatistics? Statistics { get { lock (gate) return packedCapture?.Statistics ?? capture?.Statistics; } }
    public bool WaitingForSecureTransition { get { lock (gate) return secureFailureStarted != 0; } }
    public bool SecureActive { get { lock (gate) return secure?.Active == true; } }
    public string Backend { get { lock (gate) return secure?.Active == true ? "SYSTEM Winlogon capture → loopback UDP" :
        packedCapture == null ? capture?.Backend ?? "未初始化" : $"DXGI Desktop Duplication + {packedCapture.Path}"; } }
    public DesktopStreamSource(double scale, bool secureDesktop = false)
    {
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        sourceWidth = bounds.Width; sourceHeight = bounds.Height;
        (width, height) = TransmissionGeometry.Dimensions(sourceWidth, sourceHeight, scale);
        capture = new(Width, Height);
        if (secureDesktop) secure = SecureDesktopFrames.TryStart();
    }
    public byte[] Capture()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (secure is { } source)
            {
                var frame = source.Take();
                if (frame.Active)
                {
                    secureWasActive = true; secureFailureStarted = 0;
                    if (frame.Pixels == null) return null!;
                    var pixels = NativeSecure(frame.Pixels, frame.Width, frame.Height);
                    return ConvertSecure(pixels, pixelMode, width, height);
                }
            }
            try
            {
                RestoreOrdinaryDesktop();
                var pixels = packedCapture?.Capture() ?? capture?.Capture() ?? throw new InvalidOperationException("Desktop capture is unavailable.");
                secureFailureStarted = 0;
                return pixels;
            }
            catch (Exception error) when (WaitForSecureTransition(error)) { return null!; }
        }
    }
    public MappedBgraFrame? CaptureMapped(bool allowUnchanged)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (secure is { } source)
            {
                var shared = source.TakeSharedMapped(allowUnchanged);
                if (shared.Active)
                {
                    secureWasActive = true; secureFailureStarted = 0;
                    if (shared.Frame == null) return null;
                    SetSecureSize(shared.Width, shared.Height);
                    return new(shared.Frame.Pixels, shared.Width * 4, shared.Width, shared.Height, shared.Frame.Dispose);
                }
                var frame = source.Take(allowUnchanged);
                if (frame.Active)
                {
                    secureWasActive = true; secureFailureStarted = 0;
                    if (frame.Pixels == null) return null;
                    var pixels = NativeSecure(frame.Pixels, frame.Width, frame.Height);
                    var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels,
                        System.Runtime.InteropServices.GCHandleType.Pinned);
                    return new(pin.AddrOfPinnedObject(), width * 4, width, height, pin.Free);
                }
            }
            try
            {
                RestoreOrdinaryDesktop();
                if (packedCapture != null) throw new InvalidOperationException("Packed capture cannot be passed as BGRA mapped pixels.");
                if (capture == null) throw new InvalidOperationException("Mapped desktop capture is unavailable.");
                var mapped = capture.CaptureMapped(allowUnchanged);
                secureFailureStarted = 0;
                return mapped;
            }
            catch (Exception error) when (WaitForSecureTransition(error)) { return null; }
        }
    }

    byte[] NativeSecure(byte[] pixels, int frameWidth, int frameHeight)
    {
        SetSecureSize(frameWidth, frameHeight);
        return pixels;
    }

    void SetSecureSize(int frameWidth, int frameHeight)
    {
        if (frameWidth != width || frameHeight != height)
        {
            width = frameWidth; height = frameHeight;
            Console.Error.WriteLine($"[secure capture] Native frame size changed to {width}x{height}; no scaling.");
        }
        sourceWidth = frameWidth; sourceHeight = frameHeight;
    }

    void RestoreOrdinaryDesktop()
    {
        if (!secureWasActive) return;
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        sourceWidth = bounds.Width; sourceHeight = bounds.Height;
        var dimensions = TransmissionGeometry.Dimensions(sourceWidth, sourceHeight, 1);
        Reconfigure(pixelMode, dimensions.Width, dimensions.Height);
        secureWasActive = false;
        Console.Error.WriteLine("[capture] Ordinary desktop restored after secure desktop.");
    }

    bool WaitForSecureTransition(Exception error)
    {
        if (secure == null) return false;
        if (!secureWasActive)
        {
            try
            {
                var bounds = Win32InputInjector.ReadPrimaryMonitor();
                if (bounds.Width != sourceWidth || bounds.Height != sourceHeight)
                {
                    sourceWidth = bounds.Width; sourceHeight = bounds.Height;
                    var dimensions = TransmissionGeometry.Dimensions(sourceWidth, sourceHeight, 1);
                    Reconfigure(pixelMode, dimensions.Width, dimensions.Height);
                    Console.Error.WriteLine($"[capture] Display resolution changed to {width}x{height}.");
                }
            }
            catch (Exception refreshError) { Console.Error.WriteLine("[capture] Resolution refresh failed: " + refreshError); }
        }
        var now = Stopwatch.GetTimestamp();
        if (secureFailureStarted == 0)
        {
            secureFailureStarted = now;
            Console.Error.WriteLine("[capture] Waiting for secure desktop transition: " + error);
        }
        return true;
    }

    static byte[] ConvertSecure(byte[] bgra, CapturePixelMode mode, int width, int height)
    {
        if (mode == CapturePixelMode.Bgra) return bgra;
        var pixels = checked(width * height);
        var packed = new byte[mode == CapturePixelMode.Rgb565 ? pixels * 2 :
            mode == CapturePixelMode.Gray4 ? pixels / 2 : pixels];
        for (var index = 0; index < pixels; index++)
        {
            var source = index * 4;
            var blue = bgra[source]; var green = bgra[source + 1]; var red = bgra[source + 2];
            switch (mode)
            {
                case CapturePixelMode.Rgb332:
                    packed[index] = (byte)((red * 7 / 255 << 5) | (green * 7 / 255 << 2) | blue * 3 / 255);
                    break;
                case CapturePixelMode.Rgb565:
                    var rgb = (red * 31 / 255 << 11) | (green * 63 / 255 << 5) | blue * 31 / 255;
                    packed[index * 2] = (byte)rgb; packed[index * 2 + 1] = (byte)(rgb >> 8);
                    break;
                case CapturePixelMode.Gray8:
                    packed[index] = (byte)((red * 77 + green * 150 + blue * 29) >> 8);
                    break;
                case CapturePixelMode.Gray4:
                    var gray = (red * 77 + green * 150 + blue * 29) >> 8;
                    if ((index & 1) == 0) packed[index / 2] = (byte)((gray >> 4) << 4);
                    else packed[index / 2] |= (byte)(gray >> 4);
                    break;
            }
        }
        return packed;
    }

    public void SetPixelMode(CapturePixelMode mode)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pixelMode == mode) return;
            if (secureWasActive)
            {
                pixelMode = mode;
                Console.Error.WriteLine($"[capture] Secure desktop pixel mode changed to {mode}.");
                return;
            }
            Reconfigure(mode, width, height);
            Console.Error.WriteLine($"[capture] Pixel mode changed to {mode}; {width}x{height}.");
        }
    }
    public void Resize(int width, int height)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (this.width == width && this.height == height) return;
            Reconfigure(pixelMode, width, height);
            Console.Error.WriteLine($"[capture] Output resized to {width}x{height}; {pixelMode}.");
        }
    }

    void Reconfigure(CapturePixelMode mode, int targetWidth, int targetHeight)
    {
        var originalMode = pixelMode;
        var originalWidth = width;
        var originalHeight = height;
        ReleaseActive();
        try
        {
            Initialize(mode, targetWidth, targetHeight);
            pixelMode = mode;
            width = targetWidth;
            height = targetHeight;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[capture] Reconfigure {mode} {targetWidth}x{targetHeight} failed: {error}");
            ReleaseActive();
            try { Initialize(originalMode, originalWidth, originalHeight); }
            catch (Exception restoreError)
            {
                Console.Error.WriteLine($"[capture] Restore {originalMode} {originalWidth}x{originalHeight} failed: {restoreError}");
                throw new AggregateException("Capture reconfiguration and restoration both failed.", error, restoreError);
            }
            throw;
        }
    }

    void Initialize(CapturePixelMode mode, int targetWidth, int targetHeight)
    {
        if (mode == CapturePixelMode.Bgra)
        {
            var replacement = new DesktopCapture(targetWidth, targetHeight);
            try { replacement.Capture(); capture = replacement; }
            catch { replacement.Dispose(); throw; }
        }
        else
        {
            var replacement = new DxgiDesktopCapture(targetWidth, targetHeight, CapturePixelModes.CapturePath(mode), reusePixelBuffer: true);
            try { replacement.Capture(); packedCapture = replacement; }
            catch { replacement.Dispose(); throw; }
        }
    }

    void ReleaseActive()
    {
        var standard = capture;
        var packed = packedCapture;
        capture = null;
        packedCapture = null;
        packed?.Dispose();
        standard?.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            secure?.Dispose();
            ReleaseActive();
        }
    }
}
