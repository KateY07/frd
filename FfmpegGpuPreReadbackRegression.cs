using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Frd;

static class GpuPreReadbackRegression
{
    sealed record Timing(double MeanMs, double P95Ms, double MaxMs);
    sealed record RouteResult(string Name, int Width, int Height, int BytesPerPixel, int RawBytes,
        bool Complete, bool Under10Ms, Timing? CaptureToCpu, Timing? Acquire, Timing? DesktopCopySubmit,
        Timing? GpuTransformSubmit, Timing? ReadbackCopySubmit, Timing? MapWait, Timing? CpuRowCopy,
        string? FrameHash, int DistinctByteValues, string? Error);

    public static int Run(string output)
    {
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        var width = bounds.Width & ~1;
        var height = bounds.Height & ~1;
        var routes = new (string Name, DesktopCapturePath Path, int Width, int Height)[]
        {
            ("BGRA8 native", DesktopCapturePath.PixelShader, width, height),
            ("BGRA8 half width and height", DesktopCapturePath.PixelShader, width / 2 & ~1, height / 2 & ~1),
            ("Gray8 native", DesktopCapturePath.PixelShaderGray8, width, height),
            ("Gray8 half width and height", DesktopCapturePath.PixelShaderGray8, width / 2 & ~1, height / 2 & ~1)
        };
        List<RouteResult> results = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        foreach (var route in routes)
        {
            try
            {
                using var source = new DxgiDesktopCapture(route.Width, route.Height, route.Path,
                    reusePixelBuffer: true, forceProcessUnchanged: true);
                source.Capture();
                List<DesktopCaptureStatistics> samples = new();
                byte[] pixels = [];
                for (var i = 0; i < 35; i++)
                {
                    pixels = source.Capture();
                    if (i >= 5) samples.Add(source.Statistics ?? throw new InvalidDataException("Missing capture timing."));
                }
                var expected = checked(route.Width * route.Height * source.BytesPerPixel);
                if (pixels.Length != expected || samples.Count != 30) throw new InvalidDataException("Unexpected capture output size or sample count.");
                var distinct = pixels.Distinct().Count();
                if (distinct < 8) throw new InvalidDataException($"GPU output has only {distinct} byte values; cannot validate image conversion.");
                var total = Measure(samples.Select(sample => sample.TotalCaptureMs));
                results.Add(new(route.Name, route.Width, route.Height, source.BytesPerPixel, expected,
                    true, total.MeanMs < 10 && total.P95Ms < 10, total,
                    Measure(samples.Select(sample => sample.AcquireMs)),
                    Measure(samples.Select(sample => sample.DesktopCopySubmitMs)),
                    Measure(samples.Select(sample => sample.GpuScaleSubmitMs)),
                    Measure(samples.Select(sample => sample.ReadbackCopySubmitMs)),
                    Measure(samples.Select(sample => sample.MapWaitAndReadbackMs)),
                    Measure(samples.Select(sample => sample.CpuRowCopyMs)),
                    Convert.ToHexString(SHA256.HashData(pixels)), distinct, null));
                Console.WriteLine($"{route.Name}: {route.Width}x{route.Height}, {expected / 1_000_000d:F2} MB, capture+GPU+CPU readback {total.MeanMs:F2} ms average / {total.P95Ms:F2} ms P95");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[GPU pre-readback] {route.Name}: {error}");
                results.Add(new(route.Name, route.Width, route.Height,
                    route.Path == DesktopCapturePath.PixelShaderGray8 ? 1 : 4, 0,
                    false, false, null, null, null, null, null, null, null, null, 0, error.ToString()));
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Source = "Real primary desktop DDA BGRA8 texture; transformation runs on the same D3D11 GPU before staging readback.",
                Boundary = "AcquireNextFrame(0) call start through GPU transform, staging Map, and complete CPU output buffer; excludes encode, UDP, decode and rendering.",
                Stimulus = "One real desktop frame per route; 5 warmup + 30 forced GPU transformations of the retained texture. Does not measure display-frame waiting or dynamic content quality.",
                Decision = "A route requires both mean and P95 under 10 ms for this feasibility screen; this is not an end-to-end latency claim.",
                NativeWidth = width, NativeHeight = height, Routes = results
            }, AppConfiguration.JsonOptions));
        }
        return results.All(result => result.Complete) ? 0 : 1;
    }

    static Timing Measure(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new(ordered.Average(), ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }
}
