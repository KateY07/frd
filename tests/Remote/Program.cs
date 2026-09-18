using System.Diagnostics;
using System.Text.Json;
using Frd;

if (args.Length < 2) throw new ArgumentException("CONFIG_PATH REPORT_PATH [--qsv]; host must listen on 127.0.0.1:45170 with token regression-local-45170");
var config = AppConfiguration.Load(Path.GetFullPath(args[0]));
List<object> steps = new();
var options = new RemoteOptions("127.0.0.1", 45170, "regression-local-45170");
using (var rejected = new DemoSession(config, options with { Token = "incorrect" }))
{
    var refused = false;
    try { await rejected.StartAsync(); }
    catch (IOException error) { Console.Error.WriteLine("Expected auth refusal: " + error.Message); refused = true; }
    if (!refused) throw new Exception("Invalid token accepted");
    steps.Add(new { Case = "Wrong token rejected", Passed = true });
}
for (var connection = 0; connection < 2; connection++)
{
    using var session = new DemoSession(config, options);
    long frames = 0;
    SessionStatus? status = null;
    session.FrameReceived += frame => { Interlocked.Increment(ref frames); session.ReportPresented(frame.Pts, Stopwatch.GetTimestamp()); };
    session.StatusChanged += value => status = value;
    await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
    async Task AwaitFrames(int generation, long previous)
    {
        var timer = Stopwatch.StartNew();
        while (session.DecodedGeneration != generation || Interlocked.Read(ref frames) < previous + 10)
        { if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Missing remote frames"); await Task.Delay(20); }
    }
    await AwaitFrames(session.AppliedGeneration, 0);
    using var input = await session.ConnectRemoteInputAsync(CancellationToken.None);
    if (!(await input.SetEnabledAsync(true)).Accepted || !(await input.SetEnabledAsync(false)).Accepted) throw new Exception("Remote input enable/disable rejected");
    steps.Add(new { Case = "Authenticated separate input channel; no user input injected", Passed = true });
    var presets = new List<(string, int)> { ("h264_fast", 1000), ("h264_fast", 500) };
    if (args.Contains("--qsv")) presets.AddRange([("h264_qsv", 1000), ("h264_qsv_swdec", 1000)]);
    presets.AddRange([("h264_lowdelay", 1000), ("h264", 2000)]);
    foreach (var (preset, cap) in presets)
    {
        var before = Interlocked.Read(ref frames);
        var result = await session.ApplyAsync(preset, cap);
        if (!result.Success) throw new Exception(result.Message);
        await AwaitFrames(result.Generation, before);
        steps.Add(new { Case = $"Connection {connection}: {preset} {cap}", Passed = true, Generation = result.Generation });
    }
    var unsupported = await session.ApplyAsync("av2", 1000);
    if (unsupported.Success) throw new Exception("AV2 unsupported accepted");
    var oldFrames = Interlocked.Read(ref frames);
    await session.SetPacketDiagnosticsAsync(false);
    await AwaitFrames(session.AppliedGeneration, oldFrames);
    await Task.Delay(350);
    if (status?.PacketDiagnosticsEnabled != false || status.HasRenderTiming) throw new Exception("Diagnostics off still claims remote total timing");
    steps.Add(new { Case = "Diagnostics off preserves video and suppresses remote total timing", Passed = true });
    await session.SetPacketDiagnosticsAsync(true);
    await Task.Delay(400);
    if (status?.HasRenderTiming != true || !status.TimingDescription.Contains("跨机估算")) throw new Exception("Remote timing missing");
    if (status.TransferDetails == null) throw new Exception("Remote sender/reassembly timing missing");
    steps.Add(new { Case = "Diagnostics on restores labelled clock estimate and transfer breakdown", Passed = true, Status = status });
    await session.StopAsync();
    await Task.Delay(300);
}
var clock = new ClockEstimate();
var freq = Stopwatch.Frequency;
clock.Observe(100 * freq, 100 * freq + freq / 50, 200 * freq + freq / 100, 200 * freq + freq / 100, freq);
if (Math.Abs(clock.ToLocal(201 * freq, freq) - 101 * freq) > 2 || Math.Abs(clock.UncertaintyMs - 10) > .001) throw new Exception("Clock offset conversion wrong");
steps.Add(new { Case = "Independent clock epoch offset and uncertainty", Passed = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(new { Passed = true, Scope = "Separate host process and real desktop capture over UDP, remote control, diagnostics, reconnect; presentation callbacks simulated for timing plumbing only. No physical input injection or real GPU presentation in this harness.", Steps = steps }, AppConfiguration.JsonOptions));
Console.WriteLine($"PASS {steps.Count} remote checks");
