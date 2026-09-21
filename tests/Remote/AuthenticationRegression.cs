using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Frd.Tests;

static class AuthenticationRegression
{
    public static async Task Run(string executable, string report)
    {
        executable = Path.GetFullPath(executable);
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        List<object> checks = new();
        void Check(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
        }
        using (var binary = File.OpenRead(executable))
        using (var image = new System.Reflection.PortableExecutable.PEReader(binary))
            Check(image.PEHeaders.PEHeader?.Subsystem == System.Reflection.PortableExecutable.Subsystem.WindowsCui,
                "Console subsystem keeps interactive CLI invocation synchronous");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        Process StartCommand(params string[] arguments)
        {
            var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            return Process.Start(info) ?? throw new IOException("Cannot start authentication test host.");
        }
        Process Start(params string[] extra) => StartCommand(["--host", "--listen", "127.0.0.1", "--port", port.ToString(), .. extra]);
        foreach (var (argument, expectedExit, expectedOutput) in new[] { ("--help", 0, "FRD CLI"), ("--version", 0, "v2.pre1"), ("--unknown-option", 1, "") })
        {
            using var process = StartCommand(argument);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Check(process.ExitCode == expectedExit && (await output).Contains(expectedOutput), argument + ": noninteractive CLI result");
            await error;
        }
        reservation.Start();
        try
        {
            var busyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            using var process = StartCommand("--host", "--listen", "127.0.0.1", "--port", busyPort.ToString(), "--token", "test-only");
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Check(process.ExitCode == 1, "Occupied listener port fails with nonzero exit instead of idle window");
            await error; await output;
        }
        finally { reservation.Stop(); }
        foreach (var (arguments, name) in new[]
        {
            (new[] { "--host", "--port", port.ToString(), "--token", "test-only" }, "Missing explicit listener address rejected"),
            (new[] { "--host", "--listen", "127.0.0.1", "--token", "test-only" }, "Missing explicit listener port rejected"),
            (new[] { "--host", "--listen", "invalid-address", "--port", port.ToString(), "--token", "test-only" }, "Invalid listener address rejected")
        })
        {
            using var process = StartCommand(arguments);
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Check(process.ExitCode == 1 && (await error).Length > 0, name);
            Check(!(await output).Contains("FFmpeg", StringComparison.OrdinalIgnoreCase), name + "; rejected before codec initialization");
        }
        foreach (var (arguments, name) in new[]
        {
            (Array.Empty<string>(), "Missing command-line token rejected"),
            (new[] { "--token", "" }, "Empty command-line token rejected"),
            (new[] { "--token", "   " }, "Whitespace command-line token rejected")
        })
        {
            using var invalid = Start(arguments);
            var error = invalid.StandardError.ReadToEndAsync(); var output = invalid.StandardOutput.ReadToEndAsync();
            try { await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!invalid.HasExited) { invalid.Kill(); await invalid.WaitForExitAsync(); } }
            Check(invalid.ExitCode == 1 && (await error).Contains("--token"), name);
            await output;
            using var probe = new TcpClient();
            var connected = false;
            try { await probe.ConnectAsync(IPAddress.Loopback, port); connected = true; }
            catch (SocketException expected) when (expected.SocketErrorCode == SocketError.ConnectionRefused)
            { Console.WriteLine("Expected closed listener after invalid startup."); }
            Check(!connected, name + "; no listener remains");
        }
        var password = Guid.NewGuid().ToString("N");
        using (var disconnected = StartCommand("--connect", "127.0.0.1", "--port", port.ToString(), "--token", password))
        {
            var error = disconnected.StandardError.ReadToEndAsync(); var output = disconnected.StandardOutput.ReadToEndAsync();
            try { await disconnected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            finally { if (!disconnected.HasExited) { disconnected.Kill(); await disconnected.WaitForExitAsync(); } }
            Check(disconnected.ExitCode == 1 && (await error).Contains("Cannot connect"), "Connection startup failure exits with stderr and no idle demo window");
            await output;
        }
        using var host = Start("--token", password);
        var stderr = host.StandardError.ReadToEndAsync(); var stdout = host.StandardOutput.ReadToEndAsync();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, port); break; }
                catch (SocketException error) when (attempt < 50 && error.SocketErrorCode == SocketError.ConnectionRefused)
                { if (host.HasExited) throw new IOException("Authentication host exited."); await Task.Delay(100); }
            }
            using var video = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            async Task<JsonElement> Exchange(TcpClient client, object request)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(request); var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                var stream = client.GetStream(); await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(bytes, timeout.Token);
                await stream.ReadExactlyAsync(header, timeout.Token);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 1 or > 65536) throw new InvalidDataException("Invalid authentication reply size.");
                var body = new byte[size]; await stream.ReadExactlyAsync(body, timeout.Token);
                using var document = JsonDocument.Parse(body); return document.RootElement.Clone();
            }
            async Task<TcpClient> Connect()
            {
                var client = new TcpClient { NoDelay = true };
                try { await client.ConnectAsync(IPAddress.Loopback, port); return client; }
                catch { client.Dispose(); throw; }
            }
            foreach (var kind in new[] { "hello", "input", "cursor", "clipboard" })
            foreach (var token in new string?[] { null, "", "wrong-token" })
            {
                using var client = await Connect();
                var reply = await Exchange(client, new { Kind = kind, Token = token });
                Check(!reply.GetProperty("Success").GetBoolean(), kind + ": missing/empty/wrong token rejected");
            }
            using var controller = await Connect();
            var welcome = await Exchange(controller, new { Kind = "hello", Token = password });
            Check(welcome.GetProperty("Success").GetBoolean(), "Correct command-line token establishes session");
            var session = welcome.GetProperty("Welcome").GetProperty("Session").GetString();
            var registration = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(registration, 0x32445246);
            registration[4] = 3;
            Convert.FromHexString(session!).CopyTo(registration, 8);
            await video.SendAsync(registration, new IPEndPoint(IPAddress.Loopback, port));
            using (var udpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var acknowledgement = await video.ReceiveAsync(udpTimeout.Token);
                Check(acknowledgement.Buffer.Length == 24 && acknowledgement.Buffer[4] == 4,
                    "Authenticated UDP registration uses the fixed --port");
            }
            foreach (var kind in new[] { "input", "cursor", "clipboard" })
            {
                using var wrongSession = await Connect();
                var denied = await Exchange(wrongSession, new { Kind = kind, Token = password, Session = "wrong-session" });
                Check(!denied.GetProperty("Success").GetBoolean(), kind + ": correct token with wrong session rejected");
                using var valid = await Connect();
                var accepted = await Exchange(valid, new { Kind = kind, Token = password, Session = session });
                Check(accepted.GetProperty("Success").GetBoolean(), kind + ": correct token and active session accepted");
            }
            controller.Dispose();
            await Task.Delay(200);
            Check(!host.HasExited, "Authenticated host remains available for the next session after controller disconnect");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync();
            }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "authentication-host.log"), await stderr + await stdout);
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Checks = checks, InputInjected = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
