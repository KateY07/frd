using System.ComponentModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Frd;

public sealed record CodecPacket(byte[] Data, bool KeyFrame, long Pts);
public sealed record DecodedPixels(byte[] Bgra, int Width, int Height, long Pts);

internal sealed class MappedBgraFrame(nint pixels, int stride, int width, int height, Action release) : IDisposable
{
    Action? releaseFrame = release;
    public nint Pixels { get; } = pixels;
    public int Stride { get; } = stride;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public void Dispose() => Interlocked.Exchange(ref releaseFrame, null)?.Invoke();
}

public static unsafe class FfmpegRuntime
{
    static readonly object Gate = new();
    static readonly av_log_set_callback_callback LogCallback = WriteLog;
    static readonly ConcurrentDictionary<nint, ConcurrentQueue<string>> InitializationProblems = new();
    static string? loadedDirectory;
    public static event Action<string>? Log;
    static string version = "not initialized";
    public static string Version => version;

    public static string ResolveArguments(string arguments, int bitrateKbps)
    {
        if (bitrateKbps <= 0 || bitrateKbps > 1_000_000) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        return arguments.Replace("{bitrateKbps}", bitrateKbps.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            var fullPath = Path.GetFullPath(directory);
            if (loadedDirectory != null)
            {
                if (!string.Equals(loadedDirectory, fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"FFmpeg already loaded from {loadedDirectory}; restart to change native libraries.");
                return;
            }
            if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"FFmpeg native library directory not found: {fullPath}");
            ffmpeg.RootPath = fullPath;
            var major = ffmpeg.avcodec_version() >> 16;
            if (major != 62) throw new NotSupportedException($"FFmpeg.AutoGen 8 requires libavcodec 62; found {major}.");
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
            ffmpeg.av_log_set_callback(LogCallback);
            version = ffmpeg.av_version_info();
            loadedDirectory = fullPath;
            Console.Error.WriteLine($"FFmpeg {Version}; native libraries: {fullPath}");
        }
    }

    public static bool HasEncoder(string name) { RequireInitialized(); return ffmpeg.avcodec_find_encoder_by_name(name) != null; }
    public static bool HasDecoder(string name) { RequireInitialized(); return ffmpeg.avcodec_find_decoder_by_name(name) != null; }
    internal static void RequireInitialized()
    {
        if (loadedDirectory == null) throw new InvalidOperationException("Call FfmpegRuntime.Initialize before using a codec.");
    }

    internal static void Check(int result, string operation)
    {
        if (result >= 0) return;
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(result, buffer, 1024);
        throw new InvalidOperationException($"{operation}: {Marshal.PtrToStringUTF8((nint)buffer)} ({result}).");
    }

    internal static void OpenCodec(AVCodecContext* context, AVCodec* codec, string description)
    {
        ConcurrentQueue<string> problems = new();
        InitializationProblems[(nint)context] = problems;
        try
        {
            Check(ffmpeg.avcodec_open2(context, codec, null), description);
            if (!problems.IsEmpty) throw new ArgumentException($"{description}: FFmpeg reported rejected parameters: {string.Join(" ", problems)}");
        }
        finally { InitializationProblems.TryRemove((nint)context, out _); }
    }

    static void WriteLog(void* context, int level, string format, byte* arguments)
    {
        if (level > ffmpeg.av_log_get_level()) return;
        var buffer = stackalloc byte[4096];
        var prefix = 1;
        ffmpeg.av_log_format_line(context, level, format, arguments, buffer, 4096, &prefix);
        var message = Marshal.PtrToStringUTF8((nint)buffer) ?? "FFmpeg log conversion failed";
        if (level <= ffmpeg.AV_LOG_WARNING && InitializationProblems.TryGetValue((nint)context, out var problems) &&
            (message.Contains("Error parsing option", StringComparison.OrdinalIgnoreCase) || message.Contains("Unknown option", StringComparison.OrdinalIgnoreCase) || message.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)))
            problems.Enqueue(message.Trim());
        try { Console.Error.Write(message); Log?.Invoke(message); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine($"FFmpeg logging failed: {error}; original message: {message}"); }
    }
}

internal sealed class FfmpegArguments
{
    public List<KeyValuePair<string, string>> Options { get; } = new();
    public string CodecName { get; }

    public FfmpegArguments(string arguments)
    {
        var tokens = Split(arguments);
        for (var i = 0; i < tokens.Count; i++)
        {
            var option = tokens[i];
            if (!option.StartsWith('-') || option.Length == 1)
                throw new ArgumentException($"Expected a codec option, found '{option}'. Do not include ffmpeg.exe, input files or output files.");
            var key = option[1..];
            if (key.EndsWith(":v:0", StringComparison.Ordinal)) key = key[..^4];
            else if (key.EndsWith(":v", StringComparison.Ordinal)) key = key[..^2];
            if (++i >= tokens.Count || (tokens[i].StartsWith('-') && tokens[i].Length > 1 && char.IsLetter(tokens[i][1])))
                throw new ArgumentException($"Option '{option}' requires an explicit value.");
            Options.Add(new(key, tokens[i]));
        }
        CodecName = Last("c") ?? Last("codec") ?? Last("vcodec") ?? throw new ArgumentException("Codec arguments must include -c:v <encoder-or-decoder-name>.");
        if (string.IsNullOrWhiteSpace(CodecName)) throw new ArgumentException("Codec name cannot be empty.");
    }

    public string? Last(string key) => Options.LastOrDefault(pair => pair.Key == key).Value;
    public static bool IsCodecName(string key) => key is "c" or "codec" or "vcodec";

    public static IReadOnlyList<string> Split(string arguments)
    {
        var pointer = CommandLineToArgvW("ffmpeg " + arguments, out var count);
        if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new List<string>(Math.Max(0, count - 1));
            for (var i = 1; i < count; i++) result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "");
            return result;
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    static extern nint LocalFree(nint memory);
}

public sealed unsafe class FfmpegEncoder : IDisposable
{
    static readonly av_buffer_create_free ReleaseBorrowedInput = (_, _) => { };
    AVCodecContext* context;
    AVFrame* frame;
    AVFrame* mappedFrame;
    AVPacket* packet;
    SwsContext* converter;
    SwsContext* grayConverter;
    SwsContext* rgb332Converter;
    SwsContext* rgb565Converter;
    readonly byte*[] inputPlanes = new byte*[4];
    readonly int[] inputStrides = new int[4];
    readonly byte*[] outputPlanes = new byte*[4];
    readonly int[] outputStrides = new int[4];
    readonly int width, height, fps;
    readonly long? configuredBitrate, configuredMaxRate, configuredMinRate;
    readonly int? configuredBufferSize;
    readonly bool hasDynamicArguments;
    int bitrateKbps;
    bool flushed;
    public string Name { get; }
    public int BitrateKbps => bitrateKbps;
    public long MaxRateBitsPerSecond => context == null ? 0 : context->rc_max_rate;
    public int BufferSizeBits => context == null ? 0 : context->rc_buffer_size;
    public string PixelFormat { get; }

    public FfmpegEncoder(string arguments, int width, int height, int fps, int bitrateKbps)
    {
        FfmpegRuntime.RequireInitialized();
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive and even.");
        if (fps <= 0 || fps > 240) throw new ArgumentOutOfRangeException(nameof(fps));
        if (bitrateKbps <= 0 || bitrateKbps > 1_000_000) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        hasDynamicArguments = arguments.Contains("{bitrateKbps}", StringComparison.Ordinal);
        arguments = FfmpegRuntime.ResolveArguments(arguments, bitrateKbps);
        var options = new FfmpegArguments(arguments);
        Name = options.CodecName;
        this.bitrateKbps = bitrateKbps;
        this.width = width;
        this.height = height;
        this.fps = fps;
        var codec = ffmpeg.avcodec_find_encoder_by_name(Name);
        if (codec == null) throw new NotSupportedException($"FFmpeg {FfmpegRuntime.Version} does not include encoder '{Name}'. Install a build that includes this backend; no fallback was selected.");
        if (codec->type != AVMediaType.AVMEDIA_TYPE_VIDEO) throw new ArgumentException($"'{Name}' is not a video encoder.");
        try
        {
            context = ffmpeg.avcodec_alloc_context3(codec);
            if (context == null) throw new OutOfMemoryException("avcodec_alloc_context3 failed.");
            context->width = width;
            context->height = height;
            context->time_base = new() { num = 1, den = fps };
            context->framerate = new() { num = fps, den = 1 };
            context->gop_size = fps * 2;
            context->max_b_frames = 0;
            context->thread_count = 1;
            context->color_range = Name == "libx264rgb" ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
            context->colorspace = Name == "libx264rgb" ? AVColorSpace.AVCOL_SPC_RGB : AVColorSpace.AVCOL_SPC_BT709;
            context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            var desiredFormat = options.Last("pix_fmt");
            context->pix_fmt = ChoosePixelFormat(codec, desiredFormat);
            foreach (var (key, value) in options.Options)
            {
                if (FfmpegArguments.IsCodecName(key) || key == "pix_fmt") continue;
                if (key is "hwaccel" or "hwaccel_device" or "hwaccel_output_format" or "init_hw_device" or "filter_hw_device")
                    throw new NotSupportedException($"Encoder option -{key} configures an FFmpeg input/filter device, not an encoder. Select a hardware encoder such as h264_nvenc and its -gpu option; this backend uploads software frames internally.");
                if (key is "x264-params" or "x264opts" or "x265-params" or "svtav1-params" or "aom-params")
                    RejectNestedRateOverride(key, value);
                FfmpegRuntime.Check(ffmpeg.av_opt_set(context, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"Encoder {Name}: invalid or unsupported -{key} {value}");
            }
            configuredBitrate = options.Last("b") != null ? context->bit_rate : null;
            configuredMaxRate = options.Last("maxrate") != null ? context->rc_max_rate : null;
            configuredMinRate = options.Last("minrate") != null ? context->rc_min_rate : null;
            configuredBufferSize = options.Last("bufsize") != null ? context->rc_buffer_size : null;
            if ((context->flags & ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER) != 0)
                throw new NotSupportedException("-flags +global_header requires separately transported codec extradata; use in-band headers for this packet interface.");
            ApplyRateCap(bitrateKbps);
            FfmpegRuntime.OpenCodec(context, codec, $"Open encoder {Name}");
            PixelFormat = ffmpeg.av_get_pix_fmt_name(context->pix_fmt);
            frame = ffmpeg.av_frame_alloc();
            if (Name == "libx264rgb" && context->pix_fmt == AVPixelFormat.AV_PIX_FMT_BGR0)
                mappedFrame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null || (Name == "libx264rgb" && context->pix_fmt == AVPixelFormat.AV_PIX_FMT_BGR0 && mappedFrame == null))
                throw new OutOfMemoryException("FFmpeg frame or packet allocation failed.");
            frame->format = (int)context->pix_fmt;
            frame->width = width;
            frame->height = height;
            frame->color_range = context->color_range;
            frame->colorspace = context->colorspace;
            frame->color_primaries = context->color_primaries;
            frame->color_trc = context->color_trc;
            FfmpegRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 32), "Allocate encoder input frame");
            converter = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, context->pix_fmt, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (converter == null) throw new NotSupportedException($"BGRA to {PixelFormat} conversion is unavailable.");
            var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            FfmpegRuntime.Check(ffmpeg.sws_setColorspaceDetails(converter, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16), "Configure BT.709 conversion");
            inputStrides[0] = width * 4;
            Console.Error.WriteLine($"Encoder {Name}: {width}x{height}, {fps} FPS, cap {bitrateKbps} kbps, {PixelFormat}; {arguments}");
        }
        catch { Dispose(); throw; }
    }

    static void RejectNestedRateOverride(string key, string value)
    {
        foreach (var option in value.Split(':'))
        {
            var name = option.Split('=', 2)[0].Trim();
            if (name.Contains("bitrate", StringComparison.OrdinalIgnoreCase) || name.Contains("vbv", StringComparison.OrdinalIgnoreCase) || name is "crf" or "qp" or "lossless" or "rc" or "tbr")
                throw new ArgumentException($"-{key} may not override '{name}'; use the manual bitrate cap and top-level FFmpeg rate options.");
        }
    }

    AVPixelFormat ChoosePixelFormat(AVCodec* codec, string? requested)
    {
        void* configurations = null;
        var count = 0;
        FfmpegRuntime.Check(ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configurations, &count), "Query encoder pixel formats");
        var formats = (AVPixelFormat*)configurations;
        var desired = requested != null ? ffmpeg.av_get_pix_fmt(requested) : Name.EndsWith("_nvenc", StringComparison.Ordinal) ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
        if (desired == AVPixelFormat.AV_PIX_FMT_NONE) throw new ArgumentException($"Unknown pixel format '{requested}'.");
        if (requested == null && formats != null)
        {
            var found = false;
            for (var i = 0; i < count; i++) found |= formats[i] == desired;
            if (!found)
            {
                desired = AVPixelFormat.AV_PIX_FMT_NONE;
                for (var i = 0; i < count; i++)
                    if (ffmpeg.sws_isSupportedOutput(formats[i]) != 0) { desired = formats[i]; break; }
            }
        }
        if (desired == AVPixelFormat.AV_PIX_FMT_NONE || ffmpeg.sws_isSupportedOutput(desired) == 0)
            throw new NotSupportedException($"Encoder {Name} requires a pixel format unavailable from the software input converter: {requested ?? "automatic"}.");
        if (formats != null)
        {
            var found = false;
            for (var i = 0; i < count; i++) found |= formats[i] == desired;
            if (!found) throw new NotSupportedException($"Encoder {Name} does not accept {ffmpeg.av_get_pix_fmt_name(desired)}.");
        }
        return desired;
    }

    public IReadOnlyList<CodecPacket> Encode(byte[] bgra, long pts, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        if (flushed) throw new InvalidOperationException("Cannot encode after flushing the session.");
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("BGRA input size does not match encoder dimensions.", nameof(bgra));
        FfmpegRuntime.Check(ffmpeg.av_frame_make_writable(frame), "Make encoder input writable");
        fixed (byte* input = bgra)
        {
            inputPlanes[0] = input;
            for (var i = 0; i < 4; i++) { outputPlanes[i] = frame->data[(uint)i]; outputStrides[i] = frame->linesize[(uint)i]; }
            var rows = ffmpeg.sws_scale(converter, inputPlanes, inputStrides, 0, height, outputPlanes, outputStrides);
            if (rows != height) throw new InvalidOperationException($"BGRA conversion returned {rows} rows, expected {height}.");
        }
        return SubmitFrame(pts, forceKeyframe);
    }

    internal IReadOnlyList<CodecPacket> EncodeQuantized(byte[] pixels, bool grayscale, long pts, bool forceKeyframe = false)
        => EncodePacked(pixels, grayscale ? AVPixelFormat.AV_PIX_FMT_GRAY8 : AVPixelFormat.AV_PIX_FMT_RGB8, 1, pts, forceKeyframe);

    internal IReadOnlyList<CodecPacket> EncodeRgb565(byte[] pixels, long pts, bool forceKeyframe = false)
        => EncodePacked(pixels, AVPixelFormat.AV_PIX_FMT_RGB565LE, 2, pts, forceKeyframe);

    byte[]? gray4Expanded;

    internal IReadOnlyList<CodecPacket> EncodeGray4(byte[] pixels, long pts, bool forceKeyframe = false)
    {
        if ((width & 1) != 0 || pixels.Length != checked(width * height / 2))
            throw new ArgumentException("Packed Gray4 input dimensions do not match the encoder.", nameof(pixels));
        var expanded = gray4Expanded ??= new byte[checked(width * height)];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pair = pixels[i];
            expanded[i * 2] = (byte)((pair >> 4) * 17);
            expanded[i * 2 + 1] = (byte)((pair & 15) * 17);
        }
        return EncodeQuantized(expanded, true, pts, forceKeyframe);
    }

    IReadOnlyList<CodecPacket> EncodePacked(byte[] pixels, AVPixelFormat sourceFormat, int bytesPerPixel, long pts, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        if (flushed) throw new InvalidOperationException("Cannot encode after flushing the session.");
        if (pixels.Length != checked(width * height * bytesPerPixel)) throw new ArgumentException("Packed input size does not match encoder dimensions.", nameof(pixels));
        FfmpegRuntime.Check(ffmpeg.av_frame_make_writable(frame), "Make quantized encoder input writable");
        var chosen = sourceFormat switch
        {
            AVPixelFormat.AV_PIX_FMT_GRAY8 => grayConverter,
            AVPixelFormat.AV_PIX_FMT_RGB8 => rgb332Converter,
            AVPixelFormat.AV_PIX_FMT_RGB565LE => rgb565Converter,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceFormat))
        };
        var firstUse = chosen == null;
        chosen = ffmpeg.sws_getCachedContext(chosen, width, height, sourceFormat, width, height, context->pix_fmt,
            (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (chosen == null) throw new NotSupportedException($"{sourceFormat} to {PixelFormat} conversion is unavailable.");
        if (firstUse)
        {
            var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            FfmpegRuntime.Check(ffmpeg.sws_setColorspaceDetails(chosen, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16),
                "Configure packed BT.709 conversion");
        }
        if (sourceFormat == AVPixelFormat.AV_PIX_FMT_GRAY8) grayConverter = chosen;
        else if (sourceFormat == AVPixelFormat.AV_PIX_FMT_RGB8) rgb332Converter = chosen;
        else rgb565Converter = chosen;
        fixed (byte* input = pixels)
        {
            inputPlanes[0] = input;
            inputStrides[0] = width * bytesPerPixel;
            for (var i = 0; i < 4; i++) { outputPlanes[i] = frame->data[(uint)i]; outputStrides[i] = frame->linesize[(uint)i]; }
            var rows = ffmpeg.sws_scale(chosen, inputPlanes, inputStrides, 0, height, outputPlanes, outputStrides);
            if (rows != height) throw new InvalidOperationException($"Quantized conversion returned {rows} rows, expected {height}.");
        }
        return SubmitFrame(pts, forceKeyframe);
    }

    IReadOnlyList<CodecPacket> SubmitFrame(long pts, bool forceKeyframe)
    {
        frame->pts = pts;
        frame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        List<CodecPacket> result = new();
        var status = ffmpeg.avcodec_send_frame(context, frame);
        if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Receive(result); status = ffmpeg.avcodec_send_frame(context, frame); }
        FfmpegRuntime.Check(status, $"Encode using {Name}");
        Receive(result);
        return result;
    }

    internal bool SupportsMappedBgra => true;

    internal IReadOnlyList<CodecPacket> EncodeMapped(MappedBgraFrame bgra, long pts, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        if (flushed) throw new InvalidOperationException("Cannot encode after flushing the session.");
        if (bgra.Width != width || bgra.Height != height || bgra.Pixels == 0 || bgra.Stride < checked(width * 4))
            throw new ArgumentException("Mapped BGRA input dimensions or stride do not match the encoder.", nameof(bgra));
        if (mappedFrame == null)
        {
            FfmpegRuntime.Check(ffmpeg.av_frame_make_writable(frame), "Make encoder input writable");
            inputPlanes[0] = (byte*)bgra.Pixels;
            inputStrides[0] = bgra.Stride;
            for (var i = 0; i < 4; i++) { outputPlanes[i] = frame->data[(uint)i]; outputStrides[i] = frame->linesize[(uint)i]; }
            var rows = ffmpeg.sws_scale(converter, inputPlanes, inputStrides, 0, height, outputPlanes, outputStrides);
            if (rows != height) throw new InvalidOperationException($"BGRA conversion returned {rows} rows, expected {height}.");
            return SubmitFrame(pts, forceKeyframe);
        }
        ffmpeg.av_frame_unref(mappedFrame);
        var buffer = ffmpeg.av_buffer_create((byte*)bgra.Pixels, checked((ulong)bgra.Stride * (ulong)height),
            ReleaseBorrowedInput, null, ffmpeg.AV_BUFFER_FLAG_READONLY);
        if (buffer == null) throw new OutOfMemoryException("Reference mapped desktop pixels.");
        mappedFrame->buf[0] = buffer;
        mappedFrame->data[0] = (byte*)bgra.Pixels;
        mappedFrame->linesize[0] = bgra.Stride;
        mappedFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR0;
        mappedFrame->width = width;
        mappedFrame->height = height;
        mappedFrame->color_range = context->color_range;
        mappedFrame->colorspace = context->colorspace;
        mappedFrame->color_primaries = context->color_primaries;
        mappedFrame->color_trc = context->color_trc;
        mappedFrame->pts = pts;
        mappedFrame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        try
        {
            List<CodecPacket> result = new();
            var status = ffmpeg.avcodec_send_frame(context, mappedFrame);
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Receive(result); status = ffmpeg.avcodec_send_frame(context, mappedFrame); }
            FfmpegRuntime.Check(status, $"Encode mapped desktop using {Name}");
            Receive(result);
            if (ffmpeg.av_buffer_get_ref_count(mappedFrame->buf[0]) != 1)
            {
                Dispose();
                throw new InvalidOperationException("libx264rgb retained the mapped desktop input; encoder stopped before unmapping it.");
            }
            return result;
        }
        catch
        {
            if (mappedFrame != null && mappedFrame->buf[0] != null && ffmpeg.av_buffer_get_ref_count(mappedFrame->buf[0]) != 1) Dispose();
            throw;
        }
        finally { if (mappedFrame != null) ffmpeg.av_frame_unref(mappedFrame); }
    }

    public IReadOnlyList<CodecPacket> Flush()
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        List<CodecPacket> result = new();
        if (!flushed)
        {
            var status = ffmpeg.avcodec_send_frame(context, null);
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Receive(result); status = ffmpeg.avcodec_send_frame(context, null); }
            if (status != ffmpeg.AVERROR_EOF) FfmpegRuntime.Check(status, "Flush encoder");
            flushed = true;
        }
        Receive(result);
        return result;
    }

    public bool SetBitrate(int bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        if (bitrateKbps <= 0 || bitrateKbps > 1_000_000) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        if (bitrateKbps == this.bitrateKbps) return true;
        if (hasDynamicArguments || Name is not ("libx264" or "libx264rgb")) return false;
        // FFmpeg's libx264 wrapper reconfigures VBV and ABR from AVCodecContext before each submitted frame.
        ApplyRateCap(bitrateKbps);
        this.bitrateKbps = bitrateKbps;
        Console.Error.WriteLine($"Encoder {Name}: manual cap updated to {bitrateKbps} kbps; maxrate {context->rc_max_rate}, bufsize {context->rc_buffer_size} bits.");
        return true;
    }

    void ApplyRateCap(int capKbps)
    {
        // The slider bounds bitrate and maxrate; explicit VBV size remains the user's burst/latency choice.
        var limit = checked(capKbps * 1000L);
        context->bit_rate = configuredBitrate is > 0 ? Math.Min(configuredBitrate.Value, limit) : limit;
        context->rc_max_rate = configuredMaxRate is > 0 ? Math.Min(configuredMaxRate.Value, limit) : limit;
        context->rc_min_rate = Math.Min(configuredMinRate ?? 0, context->rc_max_rate);
        context->rc_buffer_size = configuredBufferSize ?? checked((int)Math.Min(int.MaxValue, Math.Max(1000, context->rc_max_rate * 2 / fps)));
        if (context->rc_buffer_size <= 0) throw new ArgumentException("-bufsize must be positive when a manual bitrate cap is active.");
        context->rc_initial_buffer_occupancy = context->rc_buffer_size / 4 * 3;
    }

    void Receive(List<CodecPacket> result)
    {
        while (true)
        {
            var status = ffmpeg.avcodec_receive_packet(context, packet);
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN) || status == ffmpeg.AVERROR_EOF) return;
            FfmpegRuntime.Check(status, $"Read encoded packet from {Name}");
            try
            {
                var bytes = new byte[packet->size];
                Marshal.Copy((nint)packet->data, bytes, 0, bytes.Length);
                result.Add(new(bytes, (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, packet->pts));
            }
            finally { ffmpeg.av_packet_unref(packet); }
        }
    }

    public void Dispose()
    {
        if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; }
        if (grayConverter != null) { ffmpeg.sws_freeContext(grayConverter); grayConverter = null; }
        if (rgb332Converter != null) { ffmpeg.sws_freeContext(rgb332Converter); rgb332Converter = null; }
        if (rgb565Converter != null) { ffmpeg.sws_freeContext(rgb565Converter); rgb565Converter = null; }
        var savedFrame = frame; frame = null; if (savedFrame != null) ffmpeg.av_frame_free(&savedFrame);
        var savedMapped = mappedFrame; mappedFrame = null; if (savedMapped != null) ffmpeg.av_frame_free(&savedMapped);
        var savedPacket = packet; packet = null; if (savedPacket != null) ffmpeg.av_packet_free(&savedPacket);
        var savedContext = context; context = null; if (savedContext != null) ffmpeg.avcodec_free_context(&savedContext);
    }
}

public sealed unsafe class FfmpegDecoder : IDisposable
{
    const int HardwareDeviceContextMethod = 1;
    AVCodecContext* context;
    AVFrame* frame;
    AVFrame* downloadedFrame;
    AVPacket* packet;
    SwsContext* converter;
    AVPixelFormat hardwareFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    readonly AVCodecContext_get_format formatCallback;
    readonly byte*[] inputPlanes = new byte*[4];
    readonly int[] inputStrides = new int[4];
    readonly byte*[] outputPlanes = new byte*[4];
    readonly int[] outputStrides = new int[4];
    public string Name { get; }

    public FfmpegDecoder(string arguments)
    {
        FfmpegRuntime.RequireInitialized();
        var options = new FfmpegArguments(arguments);
        Name = options.CodecName;
        formatCallback = SelectHardwareFormat;
        var codec = ffmpeg.avcodec_find_decoder_by_name(Name);
        if (codec == null) throw new NotSupportedException($"FFmpeg {FfmpegRuntime.Version} does not include decoder '{Name}'. No fallback was selected.");
        if (codec->type != AVMediaType.AVMEDIA_TYPE_VIDEO) throw new ArgumentException($"'{Name}' is not a video decoder.");
        try
        {
            context = ffmpeg.avcodec_alloc_context3(codec);
            if (context == null) throw new OutOfMemoryException("avcodec_alloc_context3 failed.");
            context->thread_count = 1;
            context->thread_type = ffmpeg.FF_THREAD_SLICE;
            foreach (var (key, value) in options.Options)
            {
                if (FfmpegArguments.IsCodecName(key) || key is "hwaccel" or "hwaccel_device" or "hwaccel_output_format") continue;
                FfmpegRuntime.Check(ffmpeg.av_opt_set(context, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"Decoder {Name}: invalid or unsupported -{key} {value}");
            }
            ConfigureHardware(codec, options);
            FfmpegRuntime.OpenCodec(context, codec, $"Open decoder {Name}");
            frame = ffmpeg.av_frame_alloc();
            downloadedFrame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || downloadedFrame == null || packet == null) throw new OutOfMemoryException("FFmpeg decoder buffer allocation failed.");
            Console.Error.WriteLine($"Decoder {Name}: {arguments}; rendered output is BGRA");
        }
        catch { Dispose(); throw; }
    }

    void ConfigureHardware(AVCodec* codec, FfmpegArguments options)
    {
        var acceleration = options.Last("hwaccel");
        if (acceleration == null || acceleration == "none")
        {
            if (options.Last("hwaccel_device") != null || options.Last("hwaccel_output_format") != null)
                throw new ArgumentException("-hwaccel_device and -hwaccel_output_format require an explicit -hwaccel device type.");
            return;
        }
        var type = ffmpeg.av_hwdevice_find_type_by_name(acceleration);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) throw new NotSupportedException($"Unsupported hardware device '{acceleration}'; use an explicit type such as cuda or d3d11va.");
        for (var index = 0; ; index++)
        {
            var configuration = ffmpeg.avcodec_get_hw_config(codec, index);
            if (configuration == null) break;
            if (configuration->device_type == type && (configuration->methods & HardwareDeviceContextMethod) != 0)
            { hardwareFormat = configuration->pix_fmt; break; }
        }
        if (hardwareFormat == AVPixelFormat.AV_PIX_FMT_NONE) throw new NotSupportedException($"Decoder {Name} does not support -hwaccel {acceleration}.");
        var requested = options.Last("hwaccel_output_format");
        if (requested != null && ffmpeg.av_get_pix_fmt(requested) != hardwareFormat)
            throw new NotSupportedException($"-hwaccel {acceleration} produces {ffmpeg.av_get_pix_fmt_name(hardwareFormat)}, not {requested}.");
        AVBufferRef* device = null;
        FfmpegRuntime.Check(ffmpeg.av_hwdevice_ctx_create(&device, type, options.Last("hwaccel_device"), null, 0), $"Initialize {acceleration} decoder device");
        context->hw_device_ctx = device;
        context->get_format = formatCallback;
    }

    AVPixelFormat SelectHardwareFormat(AVCodecContext* codec, AVPixelFormat* formats)
    {
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++) if (*format == hardwareFormat) return *format;
        Console.Error.WriteLine($"Decoder {Name}: requested hardware format was not offered; refusing silent software fallback.");
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public IReadOnlyList<DecodedPixels> Decode(byte[] compressed, long pts)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        if (compressed.Length == 0) throw new ArgumentException("Compressed packet is empty.", nameof(compressed));
        ffmpeg.av_packet_unref(packet);
        FfmpegRuntime.Check(ffmpeg.av_new_packet(packet, compressed.Length), "Allocate decoder packet");
        Marshal.Copy(compressed, 0, (nint)packet->data, compressed.Length);
        packet->pts = pts;
        packet->dts = ffmpeg.AV_NOPTS_VALUE;
        List<DecodedPixels> result = new();
        try
        {
            var status = ffmpeg.avcodec_send_packet(context, packet);
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Receive(result); status = ffmpeg.avcodec_send_packet(context, packet); }
            FfmpegRuntime.Check(status, $"Decode using {Name}");
            Receive(result);
        }
        finally { ffmpeg.av_packet_unref(packet); }
        return result;
    }

    public IReadOnlyList<DecodedPixels> Flush()
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        List<DecodedPixels> result = new();
        var status = ffmpeg.avcodec_send_packet(context, null);
        if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Receive(result); status = ffmpeg.avcodec_send_packet(context, null); }
        if (status != ffmpeg.AVERROR_EOF) FfmpegRuntime.Check(status, "Flush decoder");
        Receive(result);
        return result;
    }

    void Receive(List<DecodedPixels> result)
    {
        while (true)
        {
            var status = ffmpeg.avcodec_receive_frame(context, frame);
            if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN) || status == ffmpeg.AVERROR_EOF) return;
            FfmpegRuntime.Check(status, $"Read decoded frame from {Name}");
            try
            {
                var source = frame;
                if (frame->hw_frames_ctx != null)
                {
                    ffmpeg.av_frame_unref(downloadedFrame);
                    FfmpegRuntime.Check(ffmpeg.av_hwframe_transfer_data(downloadedFrame, frame, 0), "Download decoded hardware frame");
                    FfmpegRuntime.Check(ffmpeg.av_frame_copy_props(downloadedFrame, frame), "Copy decoded frame properties");
                    source = downloadedFrame;
                }
                var width = source->width;
                var height = source->height;
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException($"Invalid decoded size {width}x{height}.");
                converter = ffmpeg.sws_getCachedContext(converter, width, height, (AVPixelFormat)source->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (converter == null) throw new NotSupportedException("Decoded pixel format cannot be converted to BGRA.");
                var matrix = source->colorspace == AVColorSpace.AVCOL_SPC_BT709 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_DEFAULT;
                var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(matrix);
                FfmpegRuntime.Check(ffmpeg.sws_setColorspaceDetails(converter, coefficients, source->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, coefficients, 1, 0, 1 << 16, 1 << 16), "Configure decoded color conversion");
                var bytes = new byte[checked(width * height * 4)];
                fixed (byte* output = bytes)
                {
                    for (var i = 0; i < 4; i++) { inputPlanes[i] = source->data[(uint)i]; inputStrides[i] = source->linesize[(uint)i]; }
                    outputPlanes[0] = output;
                    outputStrides[0] = width * 4;
                    var rows = ffmpeg.sws_scale(converter, inputPlanes, inputStrides, 0, height, outputPlanes, outputStrides);
                    if (rows != height) throw new InvalidOperationException($"Decoded conversion returned {rows} rows, expected {height}.");
                }
                var timestamp = frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
                result.Add(new(bytes, width, height, timestamp));
            }
            finally { ffmpeg.av_frame_unref(frame); ffmpeg.av_frame_unref(downloadedFrame); }
        }
    }

    public void Dispose()
    {
        if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; }
        var savedFrame = frame; frame = null; if (savedFrame != null) ffmpeg.av_frame_free(&savedFrame);
        var savedDownload = downloadedFrame; downloadedFrame = null; if (savedDownload != null) ffmpeg.av_frame_free(&savedDownload);
        var savedPacket = packet; packet = null; if (savedPacket != null) ffmpeg.av_packet_free(&savedPacket);
        var savedContext = context; context = null; if (savedContext != null) ffmpeg.avcodec_free_context(&savedContext);
        GC.KeepAlive(formatCallback);
    }
}
