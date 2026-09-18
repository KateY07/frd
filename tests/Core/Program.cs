using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Frd;

namespace Frd.Tests;

static class Program
{
    static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "results/core");
        Directory.CreateDirectory(output);
        List<object> checks = new();
        if (args.FirstOrDefault() == "--transport")
        {
            using var arrived = new AutoResetEvent(false);
            using var receiver = new UdpVideoReceiver();
            using var sender = new UdpVideoSender(new(IPAddress.Loopback, receiver.Port));
            ConcurrentDictionary<long, (ReceivedVideo Video, long Tick)> received = new();
            VideoSendTiming? timing = null;
            sender.FrameSending += (_, _, value) => timing = value;
            receiver.VideoReceived += frame => { received[frame.FrameId] = (frame, Stopwatch.GetTimestamp()); arrived.Set(); };
            var payload = new byte[6200];
            new Random(42).NextBytes(payload);
            long id = 0;
            var ready = false;
            for (var probe = 0; probe < 3 && !ready; probe++)
            {
                var frame = ++id;
                sender.Send(new(1, frame, true, new byte[1]), CancellationToken.None);
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                while (!received.ContainsKey(frame) && Stopwatch.GetTimestamp() < deadline) arrived.WaitOne(20);
                ready = received.TryRemove(frame, out _);
                Console.WriteLine($"Startup probe {probe + 1}: {ready}");
            }
            if (!ready) throw new TimeoutException("UDP startup probes failed");
            foreach (var cap in new[] { 1000, 5000, 1000 })
            {
                sender.SetEncoderBitrateKbps(cap);
                List<double> samples = new();
                for (var index = -2; index < 40; index++)
                {
                    var frame = ++id;
                    var start = Stopwatch.GetTimestamp();
                    sender.Send(new(1, frame, true, payload), CancellationToken.None);
                    var deadline = start + 2 * Stopwatch.Frequency;
                    while (!received.ContainsKey(frame) && Stopwatch.GetTimestamp() < deadline) arrived.WaitOne(20);
                    if (!received.TryRemove(frame, out var result)) throw new TimeoutException($"UDP frame {frame} missing at cap {cap}");
                    if (!result.Video.Data.AsSpan().SequenceEqual(payload) || timing?.PlannedWaitMs != 0 || timing.WakeupOverrunMs != 0)
                        throw new Exception("Immediate send changed payload or introduced pacing");
                    if (index >= 0) samples.Add((result.Tick - start) * 1000d / Stopwatch.Frequency);
                }
                checks.Add(new { Case = "Immediate UDP", EncoderCapKbps = cap, PayloadBytes = payload.Length,
                    Samples = samples.Count, MeanMs = samples.Average(), P95Ms = samples.Order().ElementAt(37), PlannedWaitMs = 0, Passed = true });
            }
            sender.SetSimulation(new(CapacityMbps: 1));
            sender.Send(new(1, ++id, true, payload), CancellationToken.None);
            if (timing!.PlannedWaitMs < 30) throw new Exception("Explicit capacity simulation stopped working");
            checks.Add(new { Case = "Explicit 1 Mbps simulation retained", timing.PlannedWaitMs, Passed = true });
            sender.SetSimulation(new());
            sender.Send(new(1, ++id, true, payload), CancellationToken.None);
            if (timing.PlannedWaitMs != 0) throw new Exception("Disabled simulation still paces");
            checks.Add(new { Case = "Removing simulation restores immediate sending", Passed = true });
        }
        else if (args.FirstOrDefault() == "--codec")
        {
            var configuration = AppConfiguration.Load(Path.GetFullPath(args.ElementAtOrDefault(2) ?? Path.Combine(AppContext.BaseDirectory, "codec-config.json")));
            const int width = 1280, height = 720, fps = 30, framesPerStep = 90;
            var pixels = new byte[width * height * 4];
            using var encoder = new FfmpegEncoder(configuration.Presets["h264"].EncoderArguments, width, height, fps, 1000);
            using var decoder = new FfmpegDecoder(configuration.Presets["h264"].DecoderArguments);
            long frame = 0;
            List<double> outputRates = new();
            foreach (var cap in new[] { 1000, 2000, 3000, 4000, 5000, 1000, 500, 5000, 10000, 20000, 1000 })
            {
                var previousBufferBits = Math.Max(encoder.BufferSizeBits, encoder.MaxRateBitsPerSecond / fps);
                if (!encoder.SetBitrate(cap) || encoder.MaxRateBitsPerSecond != cap * 1000L) throw new Exception("Bitrate update failed");
                var effectiveBufferBits = Math.Max(encoder.BufferSizeBits, encoder.MaxRateBitsPerSecond / fps);
                long bytes = 0, steadyBytes = 0;
                var decoded = 0;
                for (var index = 0; index < framesPerStep; index++, frame++)
                {
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var sy = y + (int)frame * 7;
                        var hash = unchecked((uint)(x / 3 * 747796405) ^ (uint)(sy / 3) * 2891336453 ^ (uint)frame * 277803737);
                        hash ^= hash >> 16; hash = unchecked(hash * 0x7feb352d); hash ^= hash >> 15;
                        var value = (byte)(hash >> 16);
                        var offset = (y * width + x) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                        pixels[offset + 3] = 255;
                    }
                    foreach (var packet in encoder.Encode(pixels, frame))
                    {
                        bytes += packet.Data.Length;
                        if (index >= fps) steadyBytes += packet.Data.Length;
                        decoded += decoder.Decode(packet.Data, packet.Pts).Count;
                    }
                }
                // x264 enforces at least one frame of VBV; a live downward change also has pre-change buffer state.
                var transitionBufferBits = Math.Max(previousBufferBits, effectiveBufferBits);
                var allowanceBits = cap * 1000L * framesPerStep / fps + transitionBufferBits;
                var steadyAllowanceBits = cap * 1000L * (framesPerStep - fps) / fps + effectiveBufferBits;
                if (decoded != framesPerStep || bytes * 8 > allowanceBits || steadyBytes * 8 > steadyAllowanceBits)
                    throw new Exception($"Codec cap {cap}: full {bytes * 8}/{allowanceBits}, steady {steadyBytes * 8}/{steadyAllowanceBits} bits, decoded {decoded}");
                outputRates.Add(bytes * 8d / (framesPerStep / fps) / 1000);
                checks.Add(new { Case = "Live encoder bitrate change without network shaping", CapKbps = cap,
                    ActualKbps = bytes * 8d / (framesPerStep / fps) / 1000, Frames = decoded,
                    SteadyActualKbps = steadyBytes * 8d / ((framesPerStep - fps) / fps) / 1000,
                    ConfiguredVbvBits = encoder.BufferSizeBits, EffectiveVbvBits = effectiveBufferBits,
                    TransitionBurstAllowanceBits = transitionBufferBits, SteadyBurstAllowanceBits = effectiveBufferBits, Passed = true });
            }
            if (outputRates[4] < outputRates[6] * 3 || outputRates[7] < outputRates[6] * 3)
                throw new Exception("Demand was insufficient or bitrate changes did not affect actual output");
        }
        else throw new ArgumentException("Use --transport OUTPUT or --codec OUTPUT CONFIG_PATH");
        var report = new { Passed = true, Scope = "Development regression only. UDP tests exclude capture/codec/render. Codec tests use synthetic moving pixels, no network limiter; budget uses media duration plus configured VBV burst allowance.", Checks = checks };
        File.WriteAllText(Path.Combine(output, args[0][2..] + ".json"), JsonSerializer.Serialize(report, AppConfiguration.JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(report, AppConfiguration.JsonOptions));
    }
}
