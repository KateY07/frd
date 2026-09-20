using FFmpeg.AutoGen;
using Frd;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Frd.CaptureEncodeBench;

readonly record struct EncodeTiming(double SendFrameMs, double ReceivePacketMs, double PacketCopyMs, double QueueCheckMs);

internal sealed unsafe class BenchEncoder : IDisposable
{
    AVCodecContext* context;
    AVPacket* packet;
    public string Name { get; }
    public AVPixelFormat Format { get; }
    public string Arguments { get; }
    public string SessionMode { get; }
    public EncodeTiming LastTiming { get; private set; }

    public BenchEncoder(string name, AVPixelFormat format, int width, int height, int fps, int bitrateKbps, string rateControl = "cbr", int cpuThreads = 4, AVBufferRef* hardwareFrames = null)
    {
        if (name is not ("libx264" or "libx264rgb" or "h264_qsv" or "h264_nvenc"))
            throw new NotSupportedException($"No fixed benchmark configuration exists for {name}.");
        if (width <= 0 || height <= 0 || ((width | height) & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Native dimensions must be positive and even; no implicit resize or crop is allowed.");
        if (fps is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(fps));
        if (bitrateKbps is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        if (rateControl is not ("cbr" or "quality")) throw new ArgumentOutOfRangeException(nameof(rateControl));
        if (cpuThreads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(cpuThreads));
        if (rateControl == "quality" && name == "h264_nvenc")
            throw new NotSupportedException("This fixed-quality experiment does not define an NVENC configuration.");
        FfmpegRuntime.RequireInitialized();
        Name = name; Format = format;
        SessionMode = name == "h264_qsv" && hardwareFrames != null
            ? "Isolated worker compatibility workaround: fresh avcodec dispatcher loader during open; parent loader restored; extra loader reclaimed at worker exit."
            : "Standard FFmpeg codec initialization";
        var software = name is "libx264" or "libx264rgb";
        var rgb = name == "libx264rgb";
        var fixedQuality = rateControl == "quality";
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null) throw new NotSupportedException($"FFmpeg does not include {name}; no fallback is permitted.");
        var bufferBits = checked((int)Math.Ceiling(bitrateKbps * 1000d / fps));
        var tuning = software ? $"-preset ultrafast -tune zerolatency -threads {cpuThreads}" : name == "h264_qsv"
            ? "-preset veryfast -async_depth 1 -look_ahead 0 -low_power 1"
            : "-preset p1 -tune ull -rc vbr -rc-lookahead 0 -zerolatency 1 -delay 0";
        var control = fixedQuality ? software ? "-crf 23" : "-flags +qscale -global_quality 3068 (CQP 26)" :
            $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bufferBits}";
        Arguments = $"-c:v {name} {tuning} -bf 0 -g 300 {control} -pix_fmt {ffmpeg.av_get_pix_fmt_name(format)}";
        try
        {
            context = ffmpeg.avcodec_alloc_context3(codec); packet = ffmpeg.av_packet_alloc();
            if (context == null || packet == null) throw new OutOfMemoryException("Allocate benchmark encoder.");
            context->width = width; context->height = height; context->pix_fmt = format;
            context->time_base = new() { num = 1, den = fps }; context->framerate = new() { num = fps, den = 1 };
            context->gop_size = 300; context->max_b_frames = 0; context->thread_count = software ? cpuThreads : 1;
            context->bit_rate = fixedQuality ? 0 : bitrateKbps * 1000L;
            context->rc_max_rate = fixedQuality ? 0 : context->bit_rate;
            context->rc_buffer_size = fixedQuality ? 0 : bufferBits;
            if (fixedQuality && !software)
            {
                context->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                context->global_quality = 26 * ffmpeg.FF_QP2LAMBDA;
            }
            context->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
            context->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : AVColorSpace.AVCOL_SPC_BT709;
            context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            if (hardwareFrames != null)
            {
                context->hw_frames_ctx = ffmpeg.av_buffer_ref(hardwareFrames);
                if (context->hw_frames_ctx == null) throw new OutOfMemoryException("Reference encoder hardware frame pool.");
            }
            void* formats = null;
            var formatCount = 0;
            Check(ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &formats, &formatCount), "Query encoder input formats");
            if (formats != null)
            {
                var supported = false;
                for (var i = 0; i < formatCount; i++) supported |= ((AVPixelFormat*)formats)[i] == format;
                if (!supported) throw new NotSupportedException($"{name} does not advertise {ffmpeg.av_get_pix_fmt_name(format)} input.");
            }
            Set("preset", software ? "ultrafast" : name == "h264_qsv" ? "veryfast" : "p1");
            if (software)
            {
                Set("tune", "zerolatency");
                if (fixedQuality) Set("crf", "23");
            }
            else if (name == "h264_qsv") { Set("async_depth", "1"); Set("look_ahead", "0"); Set("low_power", "1"); }
            else { Set("tune", "ull"); Set("rc", "vbr"); Set("rc-lookahead", "0"); Set("zerolatency", "1"); Set("delay", "0"); }
            if (name == "h264_qsv" && hardwareFrames != null) OpenIsolatedQsvCodec(codec, hardwareFrames);
            else FfmpegRuntime.OpenCodec(context, codec, $"Open benchmark {name}");
            Console.Error.WriteLine($"[benchmark encoder] {width}x{height}, {fps} FPS; {Arguments}; range={context->color_range}; matrix={context->colorspace}; hardwarePool={hardwareFrames != null}");
        }
        catch { Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PublicQsvDeviceContext
    {
        public nint Session;
        public nint Loader;
    }

    void OpenIsolatedQsvCodec(AVCodec* codec, AVBufferRef* hardwareFrames)
    {
        if (!Environment.GetCommandLineArgs().Contains("--worker", StringComparer.Ordinal))
            throw new InvalidOperationException("The QSV loader compatibility workaround requires a disposable benchmark worker.");
        var frames = (AVHWFramesContext*)hardwareFrames->data;
        if (frames->device_ctx == null || frames->device_ctx->type != AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
            throw new InvalidOperationException("Expected a public QSV hardware device context.");
        // Public ABI from FFmpeg 8 libavutil/hwcontext_qsv.h, not a private FFmpeg structure.
        var device = (PublicQsvDeviceContext*)frames->device_ctx->hwctx;
        if (device == null || device->Session == 0 || device->Loader == 0)
            throw new InvalidOperationException("Expected an initialized oneVPL QSV device and loader.");
        var loader = device->Loader;
        Console.Error.WriteLine("[QSV initialization] " + SessionMode);
        // This FFmpeg build cannot reopen a session with the parent loader. The replacement
        // loader has no exposed owner; this one-open worker bounds its lifetime to process exit.
        device->Loader = 0;
        try { FfmpegRuntime.OpenCodec(context, codec, $"Open isolated benchmark {Name}"); }
        finally { device->Loader = loader; }
    }

    public CodecPacket Encode(AVFrame* frame, long pts)
    {
        LastTiming = default;
        ObjectDisposedException.ThrowIf(context == null, this);
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (frame->width != context->width || frame->height != context->height || frame->format != (int)Format)
            throw new InvalidDataException($"{Name} input dimensions or format differ from its fixed benchmark configuration.");
        if (pts == ffmpeg.AV_NOPTS_VALUE) throw new ArgumentOutOfRangeException(nameof(pts));
        frame->pts = pts; frame->pict_type = pts == 0 ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        frame->color_range = context->color_range; frame->colorspace = context->colorspace;
        frame->color_primaries = context->color_primaries; frame->color_trc = context->color_trc;
        var started = Stopwatch.GetTimestamp();
        var sent = ffmpeg.avcodec_send_frame(context, frame);
        var sendMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Check(sent, $"Submit {Name} frame {pts}");
        try
        {
            started = Stopwatch.GetTimestamp();
            var status = ffmpeg.avcodec_receive_packet(context, packet);
            var receiveMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                throw new InvalidDataException($"{Name} buffered frame {pts}; the fixed test requires a complete packet before submitting another frame.");
            Check(status, $"Receive {Name} frame {pts}");
            if (packet->pts != pts || packet->size <= 0 || packet->data == null)
                throw new InvalidDataException($"{Name} returned invalid or delayed output: submitted PTS {pts}, received {packet->pts}, bytes {packet->size}.");
            started = Stopwatch.GetTimestamp();
            var result = new CodecPacket(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray(), (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, packet->pts);
            var copyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ffmpeg.av_packet_unref(packet);
            started = Stopwatch.GetTimestamp();
            var extra = ffmpeg.avcodec_receive_packet(context, packet);
            var queueMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (extra != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Check(extra, $"Check {Name} output queue");
                throw new InvalidDataException($"{Name} returned multiple packets for one input; this path does not meet the complete-single-packet timing contract.");
            }
            LastTiming = new(sendMs, receiveMs, copyMs, queueMs);
            return result;
        }
        finally { ffmpeg.av_packet_unref(packet); }
    }

    void Set(string name, string value) => Check(ffmpeg.av_opt_set(context, name, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"Set {Name} {name}={value}");
    static void Check(int status, string operation) => FfmpegRuntime.Check(status, operation);

    public void Dispose()
    {
        if (context != null) { var value = context; ffmpeg.avcodec_free_context(&value); context = null; }
        if (packet != null) { var value = packet; ffmpeg.av_packet_free(&value); packet = null; }
    }
}

internal sealed unsafe class CpuInput : IDisposable
{
    static readonly av_buffer_create_free ReleaseExternalBuffer = KeepExternalMemory;
    readonly int width, height;
    readonly AVPixelFormat target;
    AVFrame* source;
    AVFrame* output;
    SwsContext* converter;
    public int ConversionThreads { get; }
    public bool HasBorrowedBuffer => source != null && source->buf[0] != null;

    public CpuInput(int width, int height, AVPixelFormat target, int conversionThreads = 4)
    {
        if (width <= 0 || height <= 0 || ((width | height) & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Visible dimensions must be positive and even.");
        if (target is not (AVPixelFormat.AV_PIX_FMT_BGR0 or AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_NV12))
            throw new NotSupportedException($"No fixed CPU input path for {target}.");
        if (conversionThreads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(conversionThreads));
        FfmpegRuntime.RequireInitialized();
        this.width = width; this.height = height; this.target = target;
        try
        {
            source = ffmpeg.av_frame_alloc();
            if (source == null) throw new OutOfMemoryException("Allocate borrowed input frame.");
            if (target == AVPixelFormat.AV_PIX_FMT_BGR0) return;
            output = ffmpeg.av_frame_alloc(); converter = ffmpeg.sws_alloc_context();
            if (output == null || converter == null) throw new OutOfMemoryException("Allocate color conversion resources.");
            ConfigureFrame(output, target, false);
            Check(ffmpeg.av_frame_get_buffer(output, 32), "Allocate reusable conversion output");
            SetScale("srcw", width); SetScale("srch", height); SetScale("dstw", width); SetScale("dsth", height);
            SetScale("src_format", (int)AVPixelFormat.AV_PIX_FMT_BGRA); SetScale("dst_format", (int)target);
            SetScale("threads", conversionThreads); SetScale("sws_flags", (int)SwsFlags.SWS_FAST_BILINEAR);
            Check(ffmpeg.sws_init_context(converter, null, null), "Initialize threaded BGRA conversion");
            long actualThreads;
            Check(ffmpeg.av_opt_get_int(converter, "threads", 0, &actualThreads), "Read effective conversion thread count");
            ConversionThreads = checked((int)actualThreads);
            var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            Check(ffmpeg.sws_setColorspaceDetails(converter, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16), "Set full RGB to limited BT.709 conversion");
        }
        catch { Dispose(); throw; }
    }

    public AVFrame* Prepare(nint pixels, int stride)
    {
        ObjectDisposedException.ThrowIf(source == null, this);
        if (stride < checked(width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        Borrow(pixels, stride, checked((ulong)stride * (ulong)height), target == AVPixelFormat.AV_PIX_FMT_BGR0 ? target : AVPixelFormat.AV_PIX_FMT_BGRA);
        if (target == AVPixelFormat.AV_PIX_FMT_BGR0) return source;
        Check(ffmpeg.av_frame_make_writable(output), "Reuse conversion output");
        Check(ffmpeg.sws_scale_frame(converter, output, source), "Convert borrowed BGRA pixels");
        return output;
    }

    public AVFrame* PrepareNv12(nint pixels, int stride, int allocatedHeight)
    {
        ObjectDisposedException.ThrowIf(source == null, this);
        if (target != AVPixelFormat.AV_PIX_FMT_NV12) throw new InvalidOperationException("PrepareNv12 requires an NV12 input instance.");
        if (stride < width) throw new ArgumentOutOfRangeException(nameof(stride));
        if (allocatedHeight < height || (allocatedHeight & 1) != 0) throw new ArgumentOutOfRangeException(nameof(allocatedHeight));
        var lumaBytes = checked((ulong)stride * (ulong)allocatedHeight);
        Borrow(pixels, stride, checked(lumaBytes + lumaBytes / 2), AVPixelFormat.AV_PIX_FMT_NV12);
        source->data[1] = (byte*)pixels + checked((nint)lumaBytes); source->linesize[1] = stride;
        return source;
    }

    void Borrow(nint pixels, int stride, ulong bytes, AVPixelFormat format)
    {
        if (pixels == 0) throw new ArgumentNullException(nameof(pixels));
        if (HasBorrowedBuffer) throw new InvalidOperationException("ReleaseBorrowed must succeed before replacing a mapped input surface.");
        var buffer = ffmpeg.av_buffer_create((byte*)pixels, bytes, ReleaseExternalBuffer, null, ffmpeg.AV_BUFFER_FLAG_READONLY);
        if (buffer == null) throw new OutOfMemoryException("Reference mapped input surface without copying.");
        ConfigureFrame(source, format, format != AVPixelFormat.AV_PIX_FMT_NV12);
        source->buf[0] = buffer; source->data[0] = (byte*)pixels; source->linesize[0] = stride;
    }

    void ConfigureFrame(AVFrame* frame, AVPixelFormat format, bool rgb)
    {
        frame->format = (int)format; frame->width = width; frame->height = height;
        frame->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
        frame->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : AVColorSpace.AVCOL_SPC_BT709;
        frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
    }

    public void ReleaseBorrowed()
    {
        if (!HasBorrowedBuffer) return;
        var references = ffmpeg.av_buffer_get_ref_count(source->buf[0]);
        if (references != 1)
            throw new InvalidOperationException($"Mapped input still has {references} AVBuffer references. Dispose the encoder before releasing this borrow; do not unmap or reuse the source surface.");
        ffmpeg.av_frame_unref(source);
    }

    // The caller owns the mapped memory and unmaps only after ReleaseBorrowed succeeds.
    static void KeepExternalMemory(void* opaque, byte* data) { }
    void SetScale(string name, long value) => Check(ffmpeg.av_opt_set_int(converter, name, value, 0), $"Set conversion {name}");
    static void Check(int status, string operation) => FfmpegRuntime.Check(status, operation);

    public void Dispose()
    {
        if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; }
        ReleaseBorrowed();
        if (output != null) { var value = output; ffmpeg.av_frame_free(&value); output = null; }
        if (source != null) { var value = source; ffmpeg.av_frame_free(&value); source = null; }
    }
}
