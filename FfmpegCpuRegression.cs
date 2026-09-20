using System.Diagnostics;
using System.Text.Json;

namespace Frd;

static class CpuPresetRegression
{
    public static async Task Run(AppConfiguration config, string reportPath)
    {
        if (!config.Presets.TryGetValue("h264_rgb_fast", out var preset))
            throw new InvalidDataException("CPU regression needs h264_rgb_fast preset.");
        var synthetic = new byte[640 * 360 * 4];
        Random.Shared.NextBytes(synthetic);
        const int paddedStride = 640 * 4 + 64;
        var padded = new byte[paddedStride * 360];
        for (var row = 0; row < 360; row++)
            synthetic.AsSpan(row * 640 * 4, 640 * 4).CopyTo(padded.AsSpan(row * paddedStride));
        using (var encoder = new VideoEncoder(preset.EncoderArguments, 640, 360, 30, 2000))
        using (var decoder = new VideoDecoder(preset.DecoderArguments))
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(padded, System.Runtime.InteropServices.GCHandleType.Pinned);
            using var mapped = new MappedBgraFrame(pin.AddrOfPinnedObject(), paddedStride, 640, 360, pin.Free);
            var packet = encoder.EncodeMapped(mapped, 0, true).Single();
            var decoded = decoder.Decode(packet.Data, packet.Pts).Single();
            if (decoded.Width != 640 || decoded.Height != 360 || decoded.Bgra.Length != synthetic.Length)
                throw new InvalidDataException("Borrowed x264rgb frame did not decode at the source dimensions.");
        }
        using var capture = new DesktopStreamSource(1);
        var active = config with { InitialPreset = "h264_rgb_fast", InitialBitrateKbps = 2000,
            Width = capture.Width, Height = capture.Height };
        using var session = new DemoSession(active, capture.Capture, capture.Resize, capture.SourceWidth,
            capture.SourceHeight, capture.CaptureMapped);
        var received = new List<(int Width, int Height, long Pts)>();
        var gate = new object();
        session.FrameReceived += frame => { lock (gate) received.Add((frame.Width, frame.Height, frame.Pts)); };
        await session.StartAsync();
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            lock (gate) if (received.Count > 0) break;
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("CPU preset delivered no frames.");
            await Task.Delay(25);
        }
        var stats = capture.Statistics;
        if (stats?.CapturePath != "NativeNoScale" || stats.CpuRowCopyMs != 0)
            throw new InvalidDataException("x264rgb did not use a native-size mapped DDA frame without an application row copy.");
        foreach (var fps in new[] { 10, 20, 30, 60 })
        {
            var result = await session.ApplyAsync("h264_rgb_fast", 2000, framesPerSecond: fps);
            if (!result.Success || result.FramesPerSecond != fps || session.Configuration.FramesPerSecond != fps)
                throw new InvalidDataException($"Runtime FPS change to {fps} did not apply: {result.Message}");
            timeout.Restart();
            while (session.DecodedGeneration < result.Generation)
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(8))
                    throw new TimeoutException($"No decoded frame after switching to {fps} FPS.");
                await Task.Delay(25);
            }
        }
        await session.StopAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Passed = true, Capture = "DDA native-size staging Map; no application pixel row copy",
            CaptureSize = new { capture.SourceWidth, capture.SourceHeight },
            X264Rgb = new { stats.CapturePath, stats.CpuRowCopyMs },
            RuntimeFpsSwitches = new[] { 10, 20, 30, 60 },
            UdpFrames = received.Count,
            Scope = "Synthetic x264rgb codec roundtrip plus real desktop frame through localhost UDP, decoder and session callback; display scan-out is not tested."
        }, AppConfiguration.JsonOptions));
        Console.WriteLine("CPU preset regression PASS: " + Path.GetFullPath(reportPath));
    }
}
