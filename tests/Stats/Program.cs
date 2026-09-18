using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Frd;

namespace Frd.Tests;

static class Program
{
    static int Main(string[] args)
    {
        List<object> checks = new();
        using var session = new DemoSession(new(), () => []);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var journal = typeof(DemoSession).GetField("frameClocks", flags)!.GetValue(session)!;
        var timeline = typeof(DemoSession).GetNestedType("FrameTimeline", BindingFlags.NonPublic)!;
        var snapshot = typeof(DemoSession).GetMethod("PresentationStatus", flags)!;
        void Present(long id, double ageMs, params double[] stages)
        {
            var completed = Stopwatch.GetTimestamp() - (long)(ageMs * Stopwatch.Frequency / 1000);
            var ticks = new long[6]; ticks[5] = completed;
            for (var i = 4; i >= 0; i--) ticks[i] = ticks[i + 1] - (long)(stages[i] * Stopwatch.Frequency / 1000);
            var clock = Activator.CreateInstance(timeline, flags, null, [ticks[0], ticks[1], ticks[2], 1], null)!;
            timeline.GetField("ReceivedTick")!.SetValue(clock, ticks[3]);
            timeline.GetField("DecodedTick")!.SetValue(clock, ticks[4]);
            journal.GetType().GetProperty("Item")!.SetValue(journal, clock, [id]);
            session.ReportPresented(id, completed);
        }
        ITuple Read() => (ITuple)snapshot.Invoke(session, null)!;
        void Check(bool passed, string name, object measured)
        {
            checks.Add(new { Name = name, Passed = passed, Measured = measured });
            if (!passed) throw new InvalidOperationException(name);
        }
        Present(0, 2000, 40, 20, 30, 10, 100);
        Present(1, 0, 1, 2, 3, 4, 5);
        var current = Read();
        Check((int)current[2]! == 1 && Math.Abs((double)current[1]! - 15) < .001 && Math.Abs((double)current[7]! - 107.5) < .001,
            "expired 200ms spike excluded from recent mean, retained in session mean", new { RecentMean = current[1], SessionMean = current[7] });
        Present(2, 0, 1, 1, 1, 1, 1);
        current = Read();
        var stageMean = current[5]!;
        var sum = new[] { "Capture", "Encode", "Transfer", "Decode", "Render" }.Sum(name => (double)stageMean.GetType().GetProperty(name)!.GetValue(stageMean)!);
        Check((int)current[2]! == 2 && Math.Abs((double)current[1]! - 10) < .001 && Math.Abs(sum - (double)current[1]!) < .001,
            "recent frame weighted mean and five stages use the same samples", new { Samples = current[2], Mean = current[1], StageSum = sum });
        Thread.Sleep(1100);
        current = Read();
        Check((int)current[2]! == 0 && (double)current[1]! == 0 && Math.Abs((double)current[0]! - 5) < .001,
            "idle window has no samples while last frame remains identifiable", new { Samples = current[2], RecentMean = current[1], LastFrame = current[0] });
        var output = args.FirstOrDefault() ?? "results/ffmpeg-stats/rolling-window.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Passed = true, Scope = "Production session statistics with deterministic synthetic timestamps; no capture, codec, UDP, rendering or performance claim.", Checks = checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} rolling-window checks");
        return 0;
    }
}
