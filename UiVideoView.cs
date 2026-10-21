using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DCommon;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = Vortice.Mathematics.Color4;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Frd;

/// <summary>
/// The window's presentation surface. A render thread uploads decoded BGRA frames, draws the video letterboxed,
/// lets the owner paint overlays in the same Direct2D pass, presents, and confirms GPU completion of each new frame.
/// </summary>
sealed unsafe class UiVideoView : IDisposable
{
    readonly UiWindow window;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    DecodedPixels? pending;
    Thread? thread;
    bool stopped;
    int invalidated;
    int videoWidth = 1280, videoHeight = 720;
    long deferredPresentations;

    public long DeferredPresentations => Interlocked.Read(ref deferredPresentations);
    public int VideoWidth => Volatile.Read(ref videoWidth);
    public int VideoHeight => Volatile.Read(ref videoHeight);
    public event Action<long, long>? Presented;
    public event Action<Exception>? Failed;
    /// <summary>Paints overlays; arguments are the paint helper and the client size in DIPs. Runs on the render thread.</summary>
    public Action<UiPaint, float, float>? DrawOverlay;
    /// <summary>While true the render loop keeps redrawing (animations).</summary>
    public Func<bool>? NeedsContinuousRender;

    // Valid only during DrawOverlay.
    UiPaint? currentPaint;
    ID2D1Bitmap1? currentBitmap;
    RectangleF currentVideoRect;
    GaussianBlur? blur;

    public UiVideoView(UiWindow window)
    {
        this.window = window;
        thread = new Thread(Render) { IsBackground = true, Name = "FRD confirmed GPU presentation" };
    }

    public void Start() => thread!.Start();

    public void Submit(DecodedPixels pixels)
    {
        if (pixels.Width is <= 0 or > 8192 || pixels.Height is <= 0 or > 8192 || pixels.Bgra.Length != checked(pixels.Width * pixels.Height * 4))
            throw new ArgumentException("Invalid tightly packed BGRA image.", nameof(pixels));
        lock (gate)
        {
            if (stopped) return;
            pending = pixels;      // decoder output owns a fresh immutable array; replacement only discards unrendered output
        }
        wake.Set();
    }

    public void Invalidate() { Interlocked.Exchange(ref invalidated, 1); try { wake.Set(); } catch (ObjectDisposedException) { } }

    /// <summary>The video rectangle inside a client area, in the same unit as the arguments (aspect-preserving, centered).</summary>
    public static (float X, float Y, float Width, float Height) FitRect(float clientWidth, float clientHeight, int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0 || clientWidth <= 0 || clientHeight <= 0) return (0, 0, Math.Max(0, clientWidth), Math.Max(0, clientHeight));
        var scale = Math.Min(clientWidth / videoWidth, clientHeight / videoHeight);
        var width = videoWidth * scale; var height = videoHeight * scale;
        return ((clientWidth - width) / 2, (clientHeight - height) / 2, width, height);
    }

    /// <summary>Video area in client pixels (used to normalise pointer coordinates for remote input).</summary>
    public (int X, int Y, int Width, int Height) VideoAreaPixels
    {
        get
        {
            var rect = FitRect(window.ClientWidth, window.ClientHeight, VideoWidth, VideoHeight);
            return ((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)));
        }
    }

    public void Stop()
    {
        Thread? current;
        lock (gate)
        {
            if (stopped) return;
            stopped = true; pending = null; current = thread;
        }
        try { wake.Set(); } catch (ObjectDisposedException) { }
        if (current is { IsAlive: true } && current != Thread.CurrentThread && !current.Join(2000))
            Report(new TimeoutException("D3D11 presentation thread did not stop within 2 seconds."));
    }

    public void Dispose() { Stop(); }

    void Report(Exception error)
    {
        Console.Error.WriteLine($"[GPU presentation] {error}");
        try { Failed?.Invoke(error); }
        catch (Exception callbackError) { Console.Error.WriteLine($"[GPU presentation error callback] {callbackError}"); }
    }

    // ---- glass ---------------------------------------------------------------------------------------------------

    /// <summary>Blurs the video behind <paramref name="rect"/> (DIPs, rounded corners). Returns false when there is no video yet.</summary>
    public bool DrawGlass(UiPaint paint, RectangleF rect, float radius)
    {
        if (currentBitmap == null || currentPaint == null || currentVideoRect.Width <= 0) return false;
        var context = paint.Context;
        var scale = currentVideoRect.Width / VideoWidth;
        blur ??= new GaussianBlur(context) { BorderMode = BorderMode.Hard };
        blur.SetInput(0, currentBitmap, true);
        blur.StandardDeviation = Math.Max(1f, 22f / Math.Max(scale, .05f));
        using var geometry = paint.Devices.D2D.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = rect, RadiusX = radius, RadiusY = radius });
        var parameters = new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), GeometricMask = geometry, MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity, Opacity = 1, OpacityBrush = null, LayerOptions = LayerOptions1.None
        };
        context.PushLayer(parameters, null!);
        var previous = context.Transform;
        context.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(currentVideoRect.X, currentVideoRect.Y) * previous;
        context.DrawImage(blur, InterpolationMode.Linear, CompositeMode.SourceOver);
        context.Transform = previous;
        context.PopLayer();
        return true;
    }

    // ---- render thread -------------------------------------------------------------------------------------------

    void Render()
    {
        try
        {
            using var devices = new UiDevices();
            using var text = new UiTextHolder(devices);
            using var surface = new UiSurface(devices, window.Handle, false);
            using var paint = new UiPaint(devices, surface.Context, text.Text);
            using var completion = devices.Device.CreateQuery(new QueryDescription(QueryType.Event));
            ID3D11Texture2D? texture = null;
            ID2D1Bitmap1? bitmap = null;
            byte[]? lastPixels = null;
            var uploadedWidth = 0; var uploadedHeight = 0;
            var lastPresentedAt = 0L;
            try
            {
                while (true)
                {
                    DecodedPixels? pixels;
                    lock (gate)
                    {
                        if (stopped) return;
                        pixels = pending; pending = null;
                    }
                    var forced = Interlocked.Exchange(ref invalidated, 0) != 0;
                    var animating = NeedsContinuousRender?.Invoke() == true;
                    var newFrame = false;
                    if (pixels is not null && !(pixels.Width == uploadedWidth && pixels.Height == uploadedHeight && lastPixels is not null && pixels.Bgra.AsSpan().SequenceEqual(lastPixels)))
                    {
                        if (pixels.Width != uploadedWidth || pixels.Height != uploadedHeight)
                        {
                            bitmap?.Dispose(); texture?.Dispose();
                            texture = devices.Device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)pixels.Width, Height = (uint)pixels.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
                            });
                            using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
                            bitmap = surface.Context.CreateBitmapFromDxgiSurface(dxgiSurface,
                                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.None));
                            uploadedWidth = pixels.Width; uploadedHeight = pixels.Height;
                            Volatile.Write(ref videoWidth, uploadedWidth); Volatile.Write(ref videoHeight, uploadedHeight);
                        }
                        fixed (byte* source = pixels.Bgra)
                            devices.Context.UpdateSubresource(texture!, 0, null, (nint)source, (uint)(uploadedWidth * 4), 0);
                        lastPixels = pixels.Bgra;
                        newFrame = true;
                    }
                    var needsDraw = newFrame || forced || animating || surface.Width != Math.Max(1, window.ClientWidth) || surface.Height != Math.Max(1, window.ClientHeight);
                    if (!needsDraw) { wake.WaitOne(250); continue; }

                    if (window.IsDestroyed || window.ClientWidth <= 0 || window.ClientHeight <= 0 || window.State == UiWindowState.Minimized)
                    { wake.WaitOne(100); continue; }

                    surface.Resize(window.ClientWidth, window.ClientHeight, window.Dpi);
                    var dipWidth = window.ClientWidth / window.Scale; var dipHeight = window.ClientHeight / window.Scale;
                    surface.Begin();
                    var context = surface.Context;
                    context.Clear(new Color(0, 0, 0, 1));
                    currentPaint = paint; currentBitmap = bitmap;
                    if (bitmap != null)
                    {
                        var fit = FitRect(dipWidth, dipHeight, uploadedWidth, uploadedHeight);
                        currentVideoRect = new RectangleF(fit.X, fit.Y, fit.Width, fit.Height);
                        context.DrawBitmap(bitmap, currentVideoRect, 1f, InterpolationMode.HighQualityCubic, null, null);
                    }
                    else currentVideoRect = default;
                    try { DrawOverlay?.Invoke(paint, dipWidth, dipHeight); }
                    catch (Exception error) { Console.Error.WriteLine($"[UI draw] {error}"); }
                    finally { currentPaint = null; currentBitmap = null; }
                    surface.End();
                    var presentResult = surface.Present(0);
                    presentResult.CheckError();
                    if (!newFrame) { if (animating) Thread.Sleep(Math.Max(1, 8 - (int)((Stopwatch.GetTimestamp() - lastPresentedAt) * 1000 / Stopwatch.Frequency))); lastPresentedAt = Stopwatch.GetTimestamp(); continue; }
                    if (presentResult.Code != 0) { Interlocked.Increment(ref deferredPresentations); continue; }
                    // The event confirms GPU commands after Present; it does not measure physical scan-out.
                    devices.Context.End(completion);
                    devices.Context.Flush();
                    uint done = 0;
                    var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    while (true)
                    {
                        var result = devices.Context.GetData(completion, (nint)(&done), sizeof(uint), AsyncGetDataFlags.DoNotFlush);
                        result.CheckError();
                        if (result.Code == 0 && done != 0) break;
                        lock (gate) { if (stopped) return; }
                        if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("GPU presentation confirmation exceeded 1 second.");
                        Thread.Yield();
                    }
                    var completed = Stopwatch.GetTimestamp();
                    Presented?.Invoke(pixels!.Pts, completed);
                    lastPresentedAt = completed;
                }
            }
            finally { blur?.Dispose(); bitmap?.Dispose(); texture?.Dispose(); }
        }
        catch (Exception error) { Report(error); }
    }

    sealed class UiTextHolder : IDisposable
    {
        public UiText Text { get; }
        public UiTextHolder(UiDevices devices) => Text = new UiText(devices.DWrite);
        public void Dispose() { }
    }
}
