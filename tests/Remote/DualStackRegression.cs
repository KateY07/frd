using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Frd.Tests;

static class DualStackRegression
{
    public static async Task Run(string executable, string configPath, string report)
    {
        executable = Path.GetFullPath(executable); report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var config = AppConfiguration.Load(Path.GetFullPath(configPath));
        List<object> checks = new();
        void Check(bool passed, string name)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        foreach (var listen in new[] { "localhost", "::" })
        {
            using var reservation = new TcpListener(IPAddress.IPv6Any, 0);
            reservation.Server.DualMode = true; reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var token = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable), RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--host", "--listen", listen, "--port", port.ToString(), "--token", token }) start.ArgumentList.Add(arg);
            using var host = Process.Start(start) ?? throw new IOException("Cannot launch host.");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderr = Task.Run(async () =>
            {
                List<string> lines = new();
                while (await host.StandardError.ReadLineAsync() is { } line)
                { lines.Add(line); if (line.Contains("Remote listener ready:")) ready.TrySetResult(); }
                return string.Join(Environment.NewLine, lines);
            });
            var stdout = host.StandardOutput.ReadToEndAsync();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var bound = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(endpoint => endpoint.Port == port).ToArray();
                Check(listen == "localhost"
                    ? bound.Length == 2 && bound.All(endpoint => IPAddress.IsLoopback(endpoint.Address))
                    : bound.Any(endpoint => endpoint.Address.Equals(IPAddress.IPv6Any)) &&
                        bound.All(endpoint => endpoint.Address.Equals(IPAddress.IPv6Any) || endpoint.Address.Equals(IPAddress.Any)), listen + ": exact listener scope");
                foreach (var address in new[] { "127.0.0.1", "::1", "localhost" })
                {
                    using var session = new DemoSession(config, new(address, port, token));
                    long frames = 0, cursors = 0;
                    Exception? failure = null;
                    session.FrameReceived += _ => Interlocked.Increment(ref frames);
                    session.Failed += error => failure = error;
                    await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Check(session.SharesLocalDesktop, listen + " → " + address + ": same-machine UI uses the restricted local input target");
                    using (var input = await session.ConnectRemoteInputAsync(CancellationToken.None))
                    {
                        await using var cursor = await session.ConnectRemoteCursorAsync(_ => Interlocked.Increment(ref cursors), error => failure = error, CancellationToken.None);
                        var timer = Stopwatch.StartNew();
                        while ((Interlocked.Read(ref frames) < 3 || Interlocked.Read(ref cursors) < 1) && timer.Elapsed.TotalSeconds < 8 && failure == null) await Task.Delay(25);
                        Check(failure == null && frames >= 3 && cursors >= 1, listen + " → " + address + ": capture/encode/UDP/decode and authenticated input/cursor channels; no injected input");
                    }
                    await session.StopAsync();
                    await Task.Delay(300);
                }
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Refresh(); host.CloseMainWindow();
                    try { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                    catch (TimeoutException) { Console.Error.WriteLine("Host cleanup timed out."); host.Kill(); await host.WaitForExitAsync(); throw; }
                }
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, listen == "::" ? "dual-wildcard.log" : "dual-loopback.log"), await stderr + await stdout);
            }
            Check(host.ExitCode == 0, listen + ": clean shutdown");
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, InputInjected = false, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
