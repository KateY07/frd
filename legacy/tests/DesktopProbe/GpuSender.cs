using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Frd;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;

namespace Frd.DesktopProbe;

sealed unsafe class GpuSender : IDisposable
{
    const int HardwareMapRead = 1, HardwareMapDirect = 8;
    IDXGIAdapter1? adapter;
    IDXGIOutput? output;
    IDXGIOutput1? output1;
    IDXGIOutputDuplication? duplication;
    ID3D11Device? device;
    ID3D11DeviceContext? context;
    ID3D11Multithread? multithread;
    ID3D11VideoDevice? videoDevice;
    ID3D11VideoContext? videoContext;
    ID3D11VideoProcessorEnumerator? enumerator;
    ID3D11VideoProcessor? processor;
    ID3D11Query? completion;
    AVBufferRef* d3dDevice;
    AVBufferRef* qsvDevice;
    AVBufferRef* d3dPool;
    AVBufferRef* qsvPool;
    AVCodecContext* encoder;
    AVFrame* d3dFrame;
    AVFrame* qsvFrame;
    AVPacket* packet;
    readonly int width = 1280, height = 720;
    readonly int codedWidth, codedHeight;
    readonly bool software = Environment.GetEnvironmentVariable("FRD_GPU_ENCODER") == "libx264";
    readonly bool reuseSurface = Environment.GetEnvironmentVariable("FRD_GPU_REUSE_SURFACE") == "1";
    public bool ExplicitGpuWait { get; } = Environment.GetEnvironmentVariable("FRD_GPU_FENCE") != "0";
    bool first = true;
    public double AcquireMs, ConvertReadyMs, EncodeMs, TotalMs, ReadbackMs;
    public string EncoderName => software ? "libx264" : "h264_qsv";
    public int Width => width;
    public int Height => height;
    public int CodedWidth => codedWidth;
    public int CodedHeight => codedHeight;

    public GpuSender(bool nativeResolution = false)
    {
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success; i++)
            {
                for (uint j = 0; candidate.EnumOutputs(j, out var screen).Success; j++)
                {
                    var desc = screen.Description;
                    if (desc.AttachedToDesktop && desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0) { adapter = candidate; output = screen; break; }
                    screen.Dispose();
                }
                if (output != null) break;
                candidate.Dispose();
            }
            if (adapter == null || output == null) throw new InvalidOperationException("Primary desktop output unavailable");
            Console.Error.WriteLine("GPU sender adapter: " + adapter.Description1.Description);
            D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            multithread = device.QueryInterface<ID3D11Multithread>(); multithread.SetMultithreadProtected(true);
            output1 = output.QueryInterface<IDXGIOutput1>(); duplication = output1.DuplicateOutput(device);
            if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)) throw new NotSupportedException("Rotated output needs explicit handling");
            var sourceWidth = duplication.Description.ModeDescription.Width; var sourceHeight = duplication.Description.ModeDescription.Height;
            if (nativeResolution)
            {
                width = checked((int)sourceWidth); height = checked((int)sourceHeight);
                if (width <= 0 || height <= 0 || ((width | height) & 1) != 0)
                    throw new NotSupportedException($"Native NV12 encoding requires positive even desktop dimensions; found {width}x{height}.");
            }
            codedWidth = checked(width + 31) & ~31; codedHeight = checked(height + 31) & ~31;
            Console.Error.WriteLine($"GPU sender dimensions: source {sourceWidth}x{sourceHeight}, visible {width}x{height}, allocated surface {codedWidth}x{codedHeight}; {(nativeResolution ? "native pixels, no scaling" : "720p fit")}; 5 Mbps, 30 FPS, VBV 167000 bits.");
            videoDevice = device.QueryInterface<ID3D11VideoDevice>(); videoContext = context.QueryInterface<ID3D11VideoContext>();
            enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription {
                InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = sourceWidth, InputHeight = sourceHeight,
                OutputWidth = (uint)width, OutputHeight = (uint)height, InputFrameRate = new(30, 1), OutputFrameRate = new(30, 1), Usage = VideoUsage.OptimalSpeed });
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
            var w = (int)(sourceWidth * scale) & ~1; var h = (int)(sourceHeight * scale) & ~1;
            var left = (width - w) / 2; var top = (height - h) / 2;
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new(0, 0, width, height));
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new(0, 0, (int)sourceWidth, (int)sourceHeight));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new(left, top, left + w, top + h));
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            using (var colors = videoContext.QueryInterface<ID3D11VideoContext1>())
            {
                colors.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
                colors.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.YcbcrStudioG22LeftP709);
            }
            completion = device.CreateQuery(new QueryDescription(QueryType.Event));
            d3dDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (d3dDevice == null) throw new OutOfMemoryException();
            var hw = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)d3dDevice->data)->hwctx;
            Marshal.AddRef(device.NativePointer); hw->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
            Check(ffmpeg.av_hwdevice_ctx_init(d3dDevice), "Initialize shared D3D11 device");
            AVBufferRef* derived = null;
            if (!software) { Check(ffmpeg.av_hwdevice_ctx_create_derived(&derived, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, d3dDevice, 0), "Derive QSV device"); qsvDevice = derived; }
            d3dPool = ffmpeg.av_hwframe_ctx_alloc(d3dDevice);
            if (d3dPool == null) throw new OutOfMemoryException();
            var pool = (AVHWFramesContext*)d3dPool->data;
            pool->format = AVPixelFormat.AV_PIX_FMT_D3D11; pool->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            pool->width = codedWidth; pool->height = codedHeight; pool->initial_pool_size = 0;
            ((AVD3D11VAFramesContext*)pool->hwctx)->BindFlags = (uint)BindFlags.RenderTarget;
            Check(ffmpeg.av_hwframe_ctx_init(d3dPool), "D3D11 NV12 pool");
            derived = null;
            if (!software) { Check(ffmpeg.av_hwframe_ctx_create_derived(&derived, AVPixelFormat.AV_PIX_FMT_QSV, qsvDevice, d3dPool, 0), "Derive QSV pool"); qsvPool = derived; }
            var codec = ffmpeg.avcodec_find_encoder_by_name(EncoderName);
            if (codec == null) throw new NotSupportedException(EncoderName + " unavailable");
            encoder = ffmpeg.avcodec_alloc_context3(codec);
            if (encoder == null) throw new OutOfMemoryException();
            encoder->width = width; encoder->height = height; encoder->time_base = new() { num = 1, den = 30 }; encoder->framerate = new() { num = 30, den = 1 };
            encoder->pix_fmt = software ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_QSV;
            if (!software) encoder->hw_frames_ctx = ffmpeg.av_buffer_ref(qsvPool);
            encoder->bit_rate = encoder->rc_max_rate = 5_000_000; encoder->rc_buffer_size = 167_000;
            encoder->max_b_frames = 0; encoder->gop_size = 300; encoder->thread_count = 1;
            encoder->color_range = AVColorRange.AVCOL_RANGE_MPEG; encoder->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            encoder->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; encoder->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            var options = software ? new[] { ("preset", "ultrafast"), ("tune", "zerolatency"), ("threads", "4") } :
                new[] { ("preset", "veryfast"), ("async_depth", "1"), ("look_ahead", "0"), ("low_power", "1") };
            foreach (var option in options)
                Check(ffmpeg.av_opt_set(encoder, option.Item1, option.Item2, ffmpeg.AV_OPT_SEARCH_CHILDREN), option.Item1);
            if (!software && Environment.GetEnvironmentVariable("FRD_QSV_FRESH_LOADER") == "1")
            {
                // Diagnostic only: distinguish cross-library loader reuse from GPU surface failures.
                var state = (QsvDeviceState*)((AVHWDeviceContext*)qsvDevice->data)->hwctx;
                var savedLoader = state->Loader;
                state->Loader = 0;
                try { Check(ffmpeg.avcodec_open2(encoder, codec, null), "Open QSV texture encoder with fresh dispatcher loader"); }
                finally { state->Loader = savedLoader; }
            }
            else Check(ffmpeg.avcodec_open2(encoder, codec, null), "Open QSV texture encoder");
            d3dFrame = ffmpeg.av_frame_alloc(); qsvFrame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
            if (d3dFrame == null || qsvFrame == null || packet == null) throw new OutOfMemoryException();
            if (software)
            {
                qsvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12; qsvFrame->width = codedWidth; qsvFrame->height = codedHeight;
                Check(ffmpeg.av_frame_get_buffer(qsvFrame, 32), "Allocate reusable software NV12 input");
            }
            else if (reuseSurface)
            {
                Check(ffmpeg.av_hwframe_get_buffer(qsvPool, qsvFrame, 0), "Allocate persistent QSV input");
                d3dFrame->format = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
                Check(ffmpeg.av_hwframe_map(d3dFrame, qsvFrame, 2 | 4), "Expose persistent QSV input as writable D3D11 texture");
            }
        }
        catch { Dispose(); throw; }
    }

    public CodecPacket? CaptureEncode(long id)
    {
        var start = Stopwatch.GetTimestamp();
        var result = duplication!.AcquireNextFrame(first ? 1000u : 0u, out var info, out var resource);
        if (result.Code == unchecked((int)0x887A0027)) return null;
        result.CheckError();
        try
        {
            using (resource)
            {
                if (!first && info.LastPresentTime == 0) return null;
                AcquireMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var convert = Stopwatch.GetTimestamp();
                if (!reuseSurface || software)
                {
                    ffmpeg.av_frame_unref(d3dFrame); if (!software) ffmpeg.av_frame_unref(qsvFrame);
                    Check(ffmpeg.av_hwframe_get_buffer(d3dPool, d3dFrame, 0), "Allocate GPU surface");
                }
                using var input = resource.QueryInterface<Vortice.Direct3D11.ID3D11Texture2D>();
                Marshal.AddRef((nint)d3dFrame->data[0]);
                using var outputTexture = new Vortice.Direct3D11.ID3D11Texture2D((nint)d3dFrame->data[0]);
                using var inputView = videoDevice!.CreateVideoProcessorInputView(input, enumerator!, new VideoProcessorInputViewDescription {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0, ArraySlice = 0 } });
                using var outputView = videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator!, new VideoProcessorOutputViewDescription {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0 } });
                videoContext!.VideoProcessorBlt(processor!, outputView, 0, [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
                if (ExplicitGpuWait)
                {
                    context!.End(completion!); context.Flush();
                    var completed = 0;
                    while (true)
                    {
                        var ready = context.GetData(completion!, (nint)(&completed), 4, AsyncGetDataFlags.DoNotFlush);
                        ready.CheckError();
                        if (ready.Code == 0 && completed != 0) break;
                        if (Stopwatch.GetElapsedTime(convert).TotalMilliseconds > 1000) throw new TimeoutException("GPU conversion completion");
                        Thread.SpinWait(32);
                    }
                }
                ConvertReadyMs = Stopwatch.GetElapsedTime(convert).TotalMilliseconds;
                var transfer = Stopwatch.GetTimestamp();
                if (software)
                {
                    qsvFrame->width = codedWidth; qsvFrame->height = codedHeight;
                    Check(ffmpeg.av_frame_make_writable(qsvFrame), "Make NV12 input writable");
                    Check(ffmpeg.av_hwframe_transfer_data(qsvFrame, d3dFrame, 0), "Download NV12 only");
                }
                else if (!reuseSurface)
                {
                    qsvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_QSV; qsvFrame->hw_frames_ctx = ffmpeg.av_buffer_ref(qsvPool);
                    Check(ffmpeg.av_hwframe_map(qsvFrame, d3dFrame, HardwareMapRead | HardwareMapDirect), "Map D3D11 to QSV without CPU copy");
                }
                ReadbackMs = software ? Stopwatch.GetElapsedTime(transfer).TotalMilliseconds : 0;
                var encode = Stopwatch.GetTimestamp();
                qsvFrame->width = width; qsvFrame->height = height; qsvFrame->pts = id;
                qsvFrame->pict_type = first ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
                Check(ffmpeg.avcodec_send_frame(encoder, qsvFrame), "Submit QSV texture");
                Check(ffmpeg.avcodec_receive_packet(encoder, packet), "Receive current QSV frame");
                try
                {
                    if (packet->pts != id) throw new InvalidDataException("QSV delayed output");
                    var bytes = new byte[packet->size]; Marshal.Copy((nint)packet->data, bytes, 0, bytes.Length);
                    first = false;
                    EncodeMs = Stopwatch.GetElapsedTime(encode).TotalMilliseconds;
                    return new(bytes, (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, packet->pts);
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
        }
        finally { duplication.ReleaseFrame().CheckError(); TotalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }

    static void Check(int result, string operation)
    {
        if (result >= 0) return;
        var text = stackalloc byte[1024]; ffmpeg.av_strerror(result, text, 1024);
        throw new InvalidOperationException(operation + ": " + Marshal.PtrToStringUTF8((nint)text));
    }
    [StructLayout(LayoutKind.Sequential)] struct QsvDeviceState { public nint Session, Loader; }
    public void Dispose()
    {
        var c = encoder; encoder = null; if (c != null) ffmpeg.avcodec_free_context(&c);
        var f = qsvFrame; qsvFrame = null; if (f != null) ffmpeg.av_frame_free(&f);
        f = d3dFrame; d3dFrame = null; if (f != null) ffmpeg.av_frame_free(&f);
        var p = packet; packet = null; if (p != null) ffmpeg.av_packet_free(&p);
        var b = qsvPool; qsvPool = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = d3dPool; d3dPool = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = qsvDevice; qsvDevice = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        b = d3dDevice; d3dDevice = null; if (b != null) ffmpeg.av_buffer_unref(&b);
        completion?.Dispose(); processor?.Dispose(); enumerator?.Dispose(); videoContext?.Dispose(); videoDevice?.Dispose();
        duplication?.Dispose(); output1?.Dispose(); output?.Dispose(); context?.Dispose(); multithread?.Dispose(); device?.Dispose(); adapter?.Dispose();
    }
}
