using System.Diagnostics;
using FFmpeg.AutoGen;
using Frd;

namespace Frd.CodecLatency;

static unsafe class ConversionProbe
{
    public static object Run(int width, int height)
    {
        using var source = new Surface(width, height, AVPixelFormat.AV_PIX_FMT_BGRA);
        using var planar = new Surface(width, height, AVPixelFormat.AV_PIX_FMT_YUV420P);
        using var direct = new Surface(width, height, AVPixelFormat.AV_PIX_FMT_NV12);
        using var staged = new Surface(width, height, AVPixelFormat.AV_PIX_FMT_NV12);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = source.Frame->data[0] + y * source.Frame->linesize[0] + x * 4;
            pixel[0] = (byte)(x % 256); pixel[1] = (byte)(y % 256); pixel[2] = (byte)((x + y) % 256); pixel[3] = 255;
        }
        using var bgraToNv12 = new Converter(source, direct, true);
        using var bgraToPlanar = new Converter(source, planar, true);
        using var planarToNv12 = new Converter(planar, staged, false);
        var directMs = Measure(bgraToNv12.Run);
        var planarMs = Measure(bgraToPlanar.Run);
        var interleaveMs = Measure(planarToNv12.Run);
        var stagedMs = Measure(() => { bgraToPlanar.Run(); planarToNv12.Run(); });
        long differences = 0, sum = 0; var max = 0;
        for (uint plane = 0; plane < 2; plane++)
        for (var y = 0; y < (plane == 0 ? height : height / 2); y++)
        for (var x = 0; x < width; x++)
        {
            var delta = Math.Abs(direct.Frame->data[plane][y * direct.Frame->linesize[plane] + x] - staged.Frame->data[plane][y * staged.Frame->linesize[plane] + x]);
            if (delta != 0) differences++;
            sum += delta; max = Math.Max(max, delta);
        }
        return new { Width = width, Height = height, DirectBgraNv12Ms = directMs, BgraPlanarMs = planarMs,
            PlanarNv12Ms = interleaveMs, StagedBgraPlanarNv12Ms = stagedMs,
            PixelDifference = new { Count = differences, Max = max, Mean = sum / (width * height * 1.5) },
            Scope = "Isolated CPU swscale cost; preallocated surfaces; 10 warmup / 100 measured; not hardware encoding or whole-frame latency." };
    }

    static object Measure(Action action)
    {
        for (var i = 0; i < 10; i++) action();
        var samples = new double[100];
        for (var i = 0; i < samples.Length; i++) { var start = Stopwatch.GetTimestamp(); action(); samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        Array.Sort(samples);
        return new { Mean = samples.Average(), P95 = samples[94], Max = samples[^1] };
    }

    sealed class Surface : IDisposable
    {
        public AVFrame* Frame;
        public Surface(int width, int height, AVPixelFormat format)
        {
            Frame = ffmpeg.av_frame_alloc();
            if (Frame == null) throw new OutOfMemoryException();
            Frame->width = width; Frame->height = height; Frame->format = (int)format;
            try { FfmpegRuntime.Check(ffmpeg.av_frame_get_buffer(Frame, 32), "Conversion probe surface"); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { var frame = Frame; Frame = null; if (frame != null) ffmpeg.av_frame_free(&frame); }
    }

    sealed class Converter : IDisposable
    {
        SwsContext* context;
        readonly Surface source, target;
        readonly byte*[] input = new byte*[4], output = new byte*[4];
        readonly int[] inputStride = new int[4], outputStride = new int[4];
        public Converter(Surface source, Surface target, bool rgb)
        {
            this.source = source; this.target = target;
            context = ffmpeg.sws_getContext(source.Frame->width, source.Frame->height, (AVPixelFormat)source.Frame->format,
                target.Frame->width, target.Frame->height, (AVPixelFormat)target.Frame->format, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (context == null) throw new InvalidOperationException("Conversion probe context unavailable");
            try
            {
                var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
                FfmpegRuntime.Check(ffmpeg.sws_setColorspaceDetails(context, coefficients, rgb ? 1 : 0, coefficients, 0, 0, 1 << 16, 1 << 16), "Probe BT.709 conversion");
                for (uint i = 0; i < 4; i++) { input[i] = source.Frame->data[i]; inputStride[i] = source.Frame->linesize[i]; output[i] = target.Frame->data[i]; outputStride[i] = target.Frame->linesize[i]; }
            }
            catch { Dispose(); throw; }
        }
        public void Run()
        {
            var rows = ffmpeg.sws_scale(context, input, inputStride, 0, source.Frame->height, output, outputStride);
            if (rows != target.Frame->height) throw new InvalidOperationException("Conversion probe returned incorrect rows");
        }
        public void Dispose() { if (context != null) { ffmpeg.sws_freeContext(context); context = null; } }
    }
}
