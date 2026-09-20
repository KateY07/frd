using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;

namespace Frd.CaptureEncodeBench;

sealed class DesktopSource : IDisposable
{
    IDXGIAdapter1? adapter;
    IDXGIOutput? output;
    IDXGIOutput1? output1;
    IDXGIOutputDuplication? duplication;
    ID3D11Multithread? multithread;
    ID3D11Texture2D? stagingBgra, stagingNv12, mappedTexture;
    bool acquired, desktopMapped;
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11Texture2D? Texture { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public string AdapterName { get; }
    public string DisplayName { get; }
    public long LastPresentTime { get; private set; }
    public uint AccumulatedFrames { get; private set; }
    public bool SystemMemory { get; }
    public string ReadbackKind { get; private set; } = "none";
    public double ReadbackSubmitMs { get; private set; }
    public double MapWaitMs { get; private set; }

    public DesktopSource(string? display)
    {
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var candidate).Success; a++)
            {
                for (uint o = 0; candidate.EnumOutputs(o, out var screen).Success; o++)
                {
                    var d = screen.Description;
                    var match = display != null ? string.Equals(display, d.DeviceName, StringComparison.OrdinalIgnoreCase) :
                        d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0;
                    if (d.AttachedToDesktop && match) { adapter = candidate; output = screen; break; }
                    screen.Dispose();
                }
                if (output != null) break;
                candidate.Dispose();
            }
            if (adapter == null || output == null) throw new NotSupportedException("Requested desktop output is not attached.");
            if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                throw new NotSupportedException("Rotated output is not silently rotated or cropped by this benchmark.");
            AdapterName = adapter.Description1.Description; DisplayName = output.Description.DeviceName;
            D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            Device = device; Context = context;
            multithread = Device.QueryInterface<ID3D11Multithread>(); multithread.SetMultithreadProtected(true);
            output1 = output.QueryInterface<IDXGIOutput1>(); duplication = output1.DuplicateOutput(Device);
            var description = duplication.Description;
            Width = checked((int)description.ModeDescription.Width); Height = checked((int)description.ModeDescription.Height);
            SystemMemory = description.DesktopImageInSystemMemory;
            if (Width <= 0 || Height <= 0 || ((Width | Height) & 1) != 0)
                throw new NotSupportedException($"All comparison routes require even native dimensions; got {Width}x{Height}. No resizing permitted.");
            stagingBgra = CreateStaging((uint)Width, (uint)Height, Format.B8G8R8A8_UNorm);
        }
        catch { Dispose(); throw; }
    }

    public bool Acquire()
    {
        if (acquired) throw new InvalidOperationException("Previous desktop frame is still held.");
        var result = duplication!.AcquireNextFrame(0, out var info, out var resource);
        if (result.Code == unchecked((int)0x887A0027)) return false;
        result.CheckError(); acquired = true;
        try
        {
            using (resource)
            {
                if (info.LastPresentTime == 0) { Release(); return false; }
                Texture = resource.QueryInterface<ID3D11Texture2D>();
            }
            var description = Texture.Description;
            if (description.Width != Width || description.Height != Height || description.Format != Format.B8G8R8A8_UNorm)
                throw new NotSupportedException("Desktop size/format changed; restart the run instead of changing the experiment.");
            LastPresentTime = info.LastPresentTime; AccumulatedFrames = info.AccumulatedFrames;
            ReadbackSubmitMs = MapWaitMs = 0; ReadbackKind = "none";
            return true;
        }
        catch { Release(); throw; }
    }

    public (nint Pixels, int Stride) MapBgra()
    {
        if (!acquired || Texture == null) throw new InvalidOperationException("Acquire a desktop frame first.");
        if (mappedTexture != null || desktopMapped) throw new InvalidOperationException("A readback is already mapped.");
        if (SystemMemory)
        {
            var started = Stopwatch.GetTimestamp();
            var mapped = duplication!.MapDesktopSurface(); desktopMapped = true;
            MapWaitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ReadbackKind = "DXGI MapDesktopSurface; no application pixel copy";
            return (mapped.DataPointer, checked((int)mapped.Pitch));
        }
        ReadbackKind = "GPU BGRA -> staging -> Map; no application row copy";
        return MapTexture(Texture, stagingBgra!);
    }

    public (nint Pixels, int Stride) MapNv12(ID3D11Texture2D texture)
    {
        var d = texture.Description;
        stagingNv12 ??= CreateStaging(d.Width, d.Height, Format.NV12);
        ReadbackKind = "GPU NV12 -> staging -> Map; no application row copy";
        return MapTexture(texture, stagingNv12);
    }

    (nint Pixels, int Stride) MapTexture(ID3D11Texture2D source, ID3D11Texture2D target)
    {
        if (mappedTexture != null || desktopMapped) throw new InvalidOperationException("A readback is already mapped.");
        var started = Stopwatch.GetTimestamp(); Context.CopyResource(target, source);
        ReadbackSubmitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp(); var mapped = Context.Map(target, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        mappedTexture = target; MapWaitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return (mapped.DataPointer, checked((int)mapped.RowPitch));
    }

    public unsafe byte[] CopyReference()
    {
        var (pointer, stride) = MapBgra();
        try
        {
            var pixels = new byte[checked(Width * Height * 4)];
            for (var y = 0; y < Height; y++)
                new ReadOnlySpan<byte>((byte*)pointer + y * stride, Width * 4).CopyTo(pixels.AsSpan(y * Width * 4));
            return pixels;
        }
        finally { Unmap(); }
    }

    ID3D11Texture2D CreateStaging(uint width, uint height, Format format) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
    });

    public void Unmap()
    {
        if (mappedTexture != null) { Context.Unmap(mappedTexture, 0); mappedTexture = null; }
        if (desktopMapped) { duplication!.UnMapDesktopSurface().CheckError(); desktopMapped = false; }
    }

    public void Release()
    {
        Unmap(); Texture?.Dispose(); Texture = null;
        if (acquired) { acquired = false; duplication!.ReleaseFrame().CheckError(); }
    }

    public void Dispose()
    {
        Release(); stagingNv12?.Dispose(); stagingBgra?.Dispose(); duplication?.Dispose(); output1?.Dispose();
        multithread?.Dispose(); Context?.Dispose(); Device?.Dispose(); output?.Dispose(); adapter?.Dispose();
    }
}

sealed unsafe class GpuInput : IDisposable
{
    readonly DesktopSource source;
    readonly bool rgb, qsv, fence;
    readonly int outputWidth, outputHeight;
    readonly av_buffer_create_free FreeTexture = ReleaseTexture;
    ID3D11VideoDevice? videoDevice;
    ID3D11VideoContext? videoContext;
    ID3D11VideoProcessorEnumerator? enumerator;
    ID3D11VideoProcessor? processor;
    ID3D11Query? completion;
    AVBufferRef* d3dDevice;
    AVBufferRef* qsvDevice;
    AVBufferRef* d3dPool;
    AVBufferRef* qsvPool;
    AVFrame* d3dFrame;
    AVFrame* qsvFrame;
    public AVBufferRef* EncoderPool => qsv ? qsvPool : d3dPool;
    public AVPixelFormat EncoderFormat => qsv ? AVPixelFormat.AV_PIX_FMT_QSV : AVPixelFormat.AV_PIX_FMT_D3D11;
    public int SurfaceHeight { get; }
    public double SubmitMs { get; private set; }
    public double FenceMs { get; private set; }
    public double MapMs { get; private set; }

    public GpuInput(DesktopSource source, bool rgb, bool qsv, bool fence, int outputWidth = 0, int outputHeight = 0)
    {
        this.source = source; this.rgb = rgb; this.qsv = qsv; this.fence = fence;
        this.outputWidth = outputWidth == 0 ? source.Width : outputWidth;
        this.outputHeight = outputHeight == 0 ? source.Height : outputHeight;
        if (rgb && (this.outputWidth != source.Width || this.outputHeight != source.Height))
            throw new NotSupportedException("RGB texture borrowing does not scale.");
        try
        {
            var surfaceWidth = rgb ? this.outputWidth : (this.outputWidth + 31) & ~31;
            SurfaceHeight = rgb ? this.outputHeight : (this.outputHeight + 31) & ~31;
            d3dDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (d3dDevice == null) throw new OutOfMemoryException();
            var hardware = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)d3dDevice->data)->hwctx;
            Marshal.AddRef(source.Device.NativePointer); hardware->device = (FFmpeg.AutoGen.ID3D11Device*)source.Device.NativePointer;
            FfmpegRuntime.Check(ffmpeg.av_hwdevice_ctx_init(d3dDevice), "Share capture D3D11 device");
            if (qsv)
            {
                AVBufferRef* derived = null;
                FfmpegRuntime.Check(ffmpeg.av_hwdevice_ctx_create_derived(&derived, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, d3dDevice, 0), "Derive QSV device");
                qsvDevice = derived;
            }
            d3dPool = ffmpeg.av_hwframe_ctx_alloc(d3dDevice);
            if (d3dPool == null) throw new OutOfMemoryException();
            var pool = (AVHWFramesContext*)d3dPool->data;
            pool->format = AVPixelFormat.AV_PIX_FMT_D3D11; pool->sw_format = rgb ? AVPixelFormat.AV_PIX_FMT_BGRA : AVPixelFormat.AV_PIX_FMT_NV12;
            pool->width = surfaceWidth; pool->height = SurfaceHeight; pool->initial_pool_size = 0;
            ((AVD3D11VAFramesContext*)pool->hwctx)->BindFlags = rgb ? 0 : (uint)BindFlags.RenderTarget;
            FfmpegRuntime.Check(ffmpeg.av_hwframe_ctx_init(d3dPool), "Initialize D3D11 input pool");
            if (qsv)
            {
                AVBufferRef* derived = null;
                FfmpegRuntime.Check(ffmpeg.av_hwframe_ctx_create_derived(&derived, AVPixelFormat.AV_PIX_FMT_QSV, qsvDevice, d3dPool, 0), "Derive QSV input pool");
                qsvPool = derived;
            }
            d3dFrame = ffmpeg.av_frame_alloc(); qsvFrame = ffmpeg.av_frame_alloc();
            if (d3dFrame == null || qsvFrame == null) throw new OutOfMemoryException();
            if (!rgb)
            {
                videoDevice = source.Device.QueryInterface<ID3D11VideoDevice>(); videoContext = source.Context.QueryInterface<ID3D11VideoContext>();
                enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = (uint)source.Width, InputHeight = (uint)source.Height,
                    OutputWidth = (uint)this.outputWidth, OutputHeight = (uint)this.outputHeight,
                    InputFrameRate = new(30, 1), OutputFrameRate = new(30, 1), Usage = VideoUsage.OptimalSpeed
                });
                processor = videoDevice.CreateVideoProcessor(enumerator, 0);
                videoContext.VideoProcessorSetOutputTargetRect(processor, true, new(0, 0, this.outputWidth, this.outputHeight));
                videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new(0, 0, source.Width, source.Height));
                videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new(0, 0, this.outputWidth, this.outputHeight));
                videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
                videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
                using var colors = videoContext.QueryInterface<ID3D11VideoContext1>();
                colors.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
                colors.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.YcbcrStudioG22LeftP709);
            }
            if (fence) completion = source.Device.CreateQuery(new QueryDescription(QueryType.Event));
        }
        catch { Dispose(); throw; }
    }

    public AVFrame* Prepare()
    {
        var texture = source.Texture ?? throw new InvalidOperationException("No captured texture.");
        var started = Stopwatch.GetTimestamp(); FenceMs = MapMs = 0;
        ffmpeg.av_frame_unref(qsvFrame); ffmpeg.av_frame_unref(d3dFrame);
        if (rgb)
        {
            d3dFrame->format = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
            d3dFrame->hw_frames_ctx = ffmpeg.av_buffer_ref(d3dPool);
            if (d3dFrame->hw_frames_ctx == null) throw new OutOfMemoryException();
            Marshal.AddRef(texture.NativePointer);
            d3dFrame->buf[0] = ffmpeg.av_buffer_create((byte*)texture.NativePointer, 1, FreeTexture, null, 0);
            if (d3dFrame->buf[0] == null) { Marshal.Release(texture.NativePointer); throw new OutOfMemoryException(); }
            d3dFrame->data[0] = (byte*)texture.NativePointer;
        }
        else
        {
            FfmpegRuntime.Check(ffmpeg.av_hwframe_get_buffer(d3dPool, d3dFrame, 0), "Get NV12 GPU surface");
            using var outputTexture = GetTexture();
            using var inputView = videoDevice!.CreateVideoProcessorInputView(texture, enumerator!, new VideoProcessorInputViewDescription
            { ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0, ArraySlice = 0 } });
            using var outputView = videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator!, new VideoProcessorOutputViewDescription
            { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0 } });
            videoContext!.VideoProcessorBlt(processor!, outputView, 0, [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
        }
        SubmitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (fence)
        {
            started = Stopwatch.GetTimestamp(); source.Context.End(completion!); source.Context.Flush();
            var completed = 0;
            while (true)
            {
                var result = source.Context.GetData(completion!, (nint)(&completed), 4, AsyncGetDataFlags.DoNotFlush);
                result.CheckError(); if (result.Code == 0 && completed != 0) break;
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2) throw new TimeoutException("GPU fence exceeded two seconds.");
                Thread.SpinWait(32);
            }
            FenceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        d3dFrame->width = outputWidth; d3dFrame->height = outputHeight;
        if (!qsv) return d3dFrame;
        started = Stopwatch.GetTimestamp();
        qsvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_QSV; qsvFrame->hw_frames_ctx = ffmpeg.av_buffer_ref(qsvPool);
        if (qsvFrame->hw_frames_ctx == null) throw new OutOfMemoryException();
        FfmpegRuntime.Check(ffmpeg.av_hwframe_map(qsvFrame, d3dFrame, 1 | 8), "Map D3D11 to QSV (READ | DIRECT)");
        qsvFrame->width = outputWidth; qsvFrame->height = outputHeight;
        MapMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return qsvFrame;
    }

    public ID3D11Texture2D GetTexture()
    {
        if (d3dFrame == null || d3dFrame->data[0] == null) throw new InvalidOperationException("No GPU surface prepared.");
        var pointer = (nint)d3dFrame->data[0]; Marshal.AddRef(pointer); return new(pointer);
    }

    static void ReleaseTexture(void* opaque, byte* data) => Marshal.Release((nint)data);

    public void Dispose()
    {
        var f = qsvFrame; qsvFrame = null; if (f != null) ffmpeg.av_frame_free(&f);
        f = d3dFrame; d3dFrame = null; if (f != null) ffmpeg.av_frame_free(&f);
        var b = qsvPool; qsvPool = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = d3dPool; d3dPool = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = qsvDevice; qsvDevice = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = d3dDevice; d3dDevice = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        completion?.Dispose(); processor?.Dispose(); enumerator?.Dispose(); videoContext?.Dispose(); videoDevice?.Dispose();
    }
}
