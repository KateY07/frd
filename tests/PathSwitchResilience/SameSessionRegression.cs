using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Frd;

static class SameSessionRegression
{
    public static async Task RunAsync()
    {
        FfmpegRuntime.Initialize(Path.GetFullPath("third_party/ffmpeg/runtime"));
        var config = (JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText("codec-config.json"), AppConfiguration.JsonOptions)
            ?? throw new InvalidDataException("Codec config missing.")) with
        {
            AutoSelectCodec = false, FramesPerSecond = 10, InitialPreset = "h264_fast",
            InitialBitrateKbps = 2000
        };
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var secret = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHost(config, IPAddress.Loopback, hostPort, secret);
        Exception? hostFailure = null, clientFailure = null;
        host.Failed += error => Volatile.Write(ref hostFailure, error);
        host.Start();
        await using var relay = new LocalPathRelay(hostPort);
        using var session = new DemoSession(config, new RemoteOptions("127.0.0.1", relay.Port, secret));
        long frames = 0, statuses = 0;
        session.FrameReceived += _ => Interlocked.Increment(ref frames);
        session.StatusChanged += _ => Interlocked.Increment(ref statuses);
        session.Failed += error => Volatile.Write(ref clientFailure, error);
        await session.StartAsync();
        using var input = await session.ConnectRemoteInputAsync(CancellationToken.None);
        if (!(await input.SetEnabledAsync(true)).Accepted) throw new InvalidOperationException("Input not enabled.");
        await UntilAsync(() => Interlocked.Read(ref frames) > 0 && Interlocked.Read(ref statuses) > 0, 15);
        var connections = relay.TcpConnections;
        foreach (var pauseMs in new[] { 100, 500, 1000, 3000, 5000, 10000 })
        {
            var previousFrames = Interlocked.Read(ref frames);
            var previousStatuses = Interlocked.Read(ref statuses);
            relay.Pause();
            var pendingInput = input.SetEnabledAsync(true);
            await Task.Delay(pauseMs);
            if (Volatile.Read(ref hostFailure) is { } hostError) throw new InvalidOperationException($"Host failed during {pauseMs}ms pause.", hostError);
            if (Volatile.Read(ref clientFailure) is { } clientError) throw new InvalidOperationException($"Client failed during {pauseMs}ms pause.", clientError);
            relay.Resume();
            if (!(await pendingInput.WaitAsync(TimeSpan.FromSeconds(5))).Accepted)
                throw new InvalidOperationException($"Input acknowledgement rejected after {pauseMs}ms pause.");
            if (!(await input.SendAsync(new(RemoteInputKind.ReleaseAll))).Accepted)
                throw new InvalidOperationException($"Safe input command rejected after {pauseMs}ms pause.");
            await UntilAsync(() => Interlocked.Read(ref statuses) > previousStatuses, 8);
            if (relay.TcpConnections != connections) throw new InvalidOperationException("A paused connection was replaced.");
            Console.WriteLine($"PASS {pauseMs}ms: same {connections} TCP connections; status/input recovered; decoded frame delta={Interlocked.Read(ref frames) - previousFrames}; dropped UDP={relay.UdpDropped}.");
        }
        relay.SetImpairment(7, true);
        var before = Interlocked.Read(ref frames);
        var beforeLossStatuses = Interlocked.Read(ref statuses);
        await Task.Delay(2000);
        relay.SetImpairment(0, false);
        await UntilAsync(() => Interlocked.Read(ref statuses) > beforeLossStatuses, 3);
        if (relay.TcpConnections != connections) throw new InvalidOperationException("Loss/reorder replaced a connection.");
        Console.WriteLine($"PASS: UDP loss/reorder left the same session active; decoded frame delta={Interlocked.Read(ref frames) - before}.");

        var beforeStatuses = Interlocked.Read(ref statuses);
        relay.PauseUdpOnly();
        await Task.Delay(1000);
        if (Interlocked.Read(ref statuses) <= beforeStatuses) throw new InvalidOperationException("UDP-only pause also stopped control status.");
        relay.Resume();
        if (Volatile.Read(ref clientFailure) != null) throw new InvalidOperationException("UDP-only pause failed the session.");
        Console.WriteLine("PASS: UDP-only pause left TCP status active and did not terminate video session.");

        relay.PauseDiagnosticsOnly();
        await Task.Delay(250);
        if (Volatile.Read(ref clientFailure) != null) throw new InvalidOperationException("Diagnostic UDP pause failed the session.");
        relay.Resume();
        Console.WriteLine("PASS: diagnostic UDP pause did not terminate video session.");

        relay.PauseFeedbackOnly();
        await Task.Delay(250);
        if (Volatile.Read(ref clientFailure) != null) throw new InvalidOperationException("Feedback UDP pause failed the session.");
        relay.Resume();
        Console.WriteLine("PASS: feedback UDP pause did not terminate video session.");

        before = Interlocked.Read(ref statuses);
        relay.PauseInputTcpOnly();
        var pendingAuxiliary = input.SendAsync(new(RemoteInputKind.ReleaseAll));
        await Task.Delay(1000);
        if (Interlocked.Read(ref statuses) <= before) throw new InvalidOperationException("Input-only TCP pause stopped the control/status channel.");
        relay.Resume();
        if (!(await pendingAuxiliary.WaitAsync(TimeSpan.FromSeconds(5))).Accepted)
            throw new InvalidOperationException("Input did not recover after auxiliary TCP pause.");
        if (relay.TcpConnections != connections) throw new InvalidOperationException("Single-channel pause replaced a connection.");
        Console.WriteLine("PASS: input TCP-only pause left control/status active; input recovered on original channel.");

        relay.CloseInput();
        await UntilAsync(() => input.Failure != null, 5);
        await Task.Delay(100);
        using var replacementInput = await session.ConnectRemoteInputAsync(CancellationToken.None);
        if (!(await replacementInput.SetEnabledAsync(true)).Accepted ||
            !(await replacementInput.SendAsync(new(RemoteInputKind.ReleaseAll))).Accepted)
            throw new InvalidOperationException("New auxiliary input connection did not work after explicit input EOF.");
        if (Volatile.Read(ref clientFailure) != null || relay.TcpConnections != connections + 1)
            throw new InvalidOperationException("Explicit input EOF replaced the video/control session.");
        Console.WriteLine("PASS: explicit input-only EOF permits a new auxiliary input channel while video/control remain unchanged.");

        relay.CloseControl();
        await UntilAsync(() => Volatile.Read(ref clientFailure) != null, 5);
        if (relay.TcpConnections != connections + 1) throw new InvalidOperationException("Explicit TCP close caused silent reconnection.");
        Console.WriteLine($"PASS: explicit TCP close reported {clientFailure!.GetType().Name}; no implicit new session.");
    }

    public static async Task RunWindowAsync()
    {
        FfmpegRuntime.Initialize(Path.GetFullPath("third_party/ffmpeg/runtime"));
        var config = (JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText("codec-config.json"), AppConfiguration.JsonOptions)
            ?? throw new InvalidDataException("Codec config missing.")) with
        {
            AutoSelectCodec = false, FramesPerSecond = 10, InitialPreset = "h264_fast",
            InitialBitrateKbps = 2000
        };
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var secret = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHost(config, IPAddress.Loopback, hostPort, secret);
        host.Start();
        await using var relay = new LocalPathRelay(hostPort);
        var folder = Path.GetFullPath("artifacts/path-switch");
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "window-report.json");
        if (File.Exists(report)) File.Delete(report);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FRD.exe"))
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "--connect", "127.0.0.1", "--port", relay.Port.ToString(),
            "--token", secret, "--report", report }) start.ArgumentList.Add(argument);
        using var controller = Process.Start(start) ?? throw new InvalidOperationException("Controller process did not start.");
        var stderr = controller.StandardError.ReadToEndAsync();
        var stdout = controller.StandardOutput.ReadToEndAsync();
        try
        {
            await UntilAsync(() => relay.TcpConnections >= 3 && relay.VideoForwarded > 0 || controller.HasExited, 12);
            if (controller.HasExited) throw new InvalidOperationException($"Controller exited before path migration: {controller.ExitCode}.");
            var connections = relay.TcpConnections;
            var packets = relay.VideoForwarded;
            relay.Pause();
            await Task.Delay(10000);
            if (controller.HasExited) throw new InvalidOperationException($"Controller window exited during unchanged-endpoint pause: {controller.ExitCode}.");
            relay.Resume();
            await UntilAsync(() => relay.VideoForwarded > packets || controller.HasExited, 8);
            if (controller.HasExited || relay.TcpConnections != connections)
                throw new InvalidOperationException($"Controller UI exited or replaced a connection after the path resumed; exit={(controller.HasExited ? controller.ExitCode : -1)}.");
            await Task.Delay(2000);
            var trace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FRD", $"session-{controller.Id}.log");
            var videoBeforeInputClose = relay.VideoForwarded;
            relay.CloseInput();
            await UntilAsync(() => File.Exists(trace) && File.ReadAllText(trace).Contains("channel=input-auxiliary state=error"), 5);
            await UntilAsync(() => relay.VideoForwarded > videoBeforeInputClose, 5);
            if (controller.HasExited) throw new InvalidOperationException("Auxiliary input TCP close exited the video window.");
            relay.CloseControl();
            await UntilAsync(() => File.Exists(trace) && File.ReadAllText(trace).Contains("disconnected-awaiting-user"), 5);
            if (controller.HasExited) throw new InvalidOperationException("Controller window closed on explicit TCP EOF instead of displaying reconnect entry.");
            if (!controller.CloseMainWindow()) throw new InvalidOperationException("Controller UI could not be closed gracefully.");
            await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (controller.ExitCode != 1 || !File.Exists(report)) throw new InvalidOperationException("Controller UI did not record the explicit connection error.");
            using var document = JsonDocument.Parse(File.ReadAllText(report));
            var presented = document.RootElement.GetProperty("GpuConfirmedFrames").GetInt64();
            if (presented < 5) throw new InvalidOperationException($"Only {presented} UI frames were rendered across the path switch.");
            Console.WriteLine($"PASS: Avalonia window survived 10s unchanged-endpoint pause and rendered {presented} frames; input-only close preserved video; explicit control EOF kept manual new-session entry visible.");
        }
        finally
        {
            relay.Resume();
            if (!controller.HasExited) { controller.Kill(); await controller.WaitForExitAsync(); }
            File.WriteAllText(Path.Combine(folder, "window-stderr.log"), await stderr);
            File.WriteAllText(Path.Combine(folder, "window-stdout.log"), await stdout);
        }
    }

    static async Task UntilAsync(Func<bool> predicate, int seconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Session did not recover in {seconds}s.");
    }
}
