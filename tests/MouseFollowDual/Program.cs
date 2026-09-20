using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Frd;

namespace Frd.MouseFollowDual;

static class Program
{
    const int InputPort = 52117, ControlPort = 52118, BackgroundPort = 52119;
    static readonly int[] RatesMbps = [0, 1, 5, 10, 20, 40, 0];

    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--server", var bind]) await ServeAsync(IPAddress.Parse(bind));
            else if (args is ["--client", var host, var output]) await MeasureAsync(host, Path.GetFullPath(output));
            else if (args is ["--udp-loopback"]) await UdpLoopbackAsync();
            else throw new ArgumentException("Usage: --server BIND_IP | --client HOST OUTPUT_JSON | --udp-loopback");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static async Task ServeAsync(IPAddress bind)
    {
        if (!GetCursorPos(out var original)) throw new InvalidOperationException("Cannot read the original remote cursor position.");
        using var injector = new CursorConfirmingInjector();
        using var inputServer = new RemoteInputServer(injector);
        using var inputListener = new TcpListener(bind, InputPort);
        using var controlListener = new TcpListener(bind, ControlPort);
        using var stop = new CancellationTokenSource();
        inputListener.Start(1);
        controlListener.Start(1);
        Console.WriteLine($"Mouse-follow probe ready: {bind}:{InputPort}/{ControlPort}");
        var proxy = ProxyInputAsync(inputListener, inputServer.Endpoint, stop.Token);
        try
        {
            using var control = await controlListener.AcceptTcpClientAsync(stop.Token);
            control.NoDelay = true;
            var peer = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
            Console.WriteLine("Background UDP target address: " + peer);
            using var sender = new BackgroundVideoSender(peer, stop.Token);
            using var reader = new StreamReader(control.GetStream());
            using var writer = new StreamWriter(control.GetStream()) { AutoFlush = true };
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (line == "STOP") { await writer.WriteLineAsync("STOPPED"); break; }
                var parts = line.Split(' ');
                if (parts.Length != 3 || parts[0] != "RATE" || !int.TryParse(parts[1], out var rate) ||
                    !int.TryParse(parts[2], out var port) || rate is < 0 or > 100 || port is < 1 or > 65535)
                    throw new InvalidDataException("Invalid rate command.");
                sender.SetRate(rate, port);
                await writer.WriteLineAsync($"READY {rate}");
            }
        }
        finally
        {
            stop.Cancel();
            inputListener.Stop();
            controlListener.Stop();
            try { await proxy; }
            catch (OperationCanceledException) { Console.Error.WriteLine("Input proxy stopped."); }
            if (!SetCursorPos(original.X, original.Y))
                Console.Error.WriteLine("Could not restore remote cursor: " + Marshal.GetLastWin32Error());
            else Console.WriteLine("Original remote cursor position restored.");
        }
    }

    static async Task ProxyInputAsync(TcpListener listener, IPEndPoint target, CancellationToken token)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            client.NoDelay = true;
            using var upstream = new TcpClient { NoDelay = true };
            await upstream.ConnectAsync(target, token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var up = client.GetStream().CopyToAsync(upstream.GetStream(), cancel.Token);
            var down = upstream.GetStream().CopyToAsync(client.GetStream(), cancel.Token);
            await Task.WhenAny(up, down);
            cancel.Cancel();
            client.Close(); upstream.Close();
            try { await Task.WhenAll(up, down); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            { Console.Error.WriteLine("Input proxy closed: " + error.GetType().Name); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Console.Error.WriteLine("Input proxy cancelled."); }
    }

    static async Task MeasureAsync(string host, string output)
    {
        using var background = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        Console.WriteLine("Background UDP local endpoint: " + background.Client.LocalEndPoint);
        using var receiveStop = new CancellationTokenSource();
        long receivedBytes = 0, receivedPackets = 0;
        var receive = Task.Run(async () =>
        {
            try
            {
                while (!receiveStop.IsCancellationRequested)
                {
                    var packet = await background.ReceiveAsync(receiveStop.Token);
                    Interlocked.Add(ref receivedBytes, packet.Buffer.Length);
                    Interlocked.Increment(ref receivedPackets);
                }
            }
            catch (OperationCanceledException) when (receiveStop.IsCancellationRequested)
            { Console.Error.WriteLine("Background UDP receiver stopped."); }
        });
        using var control = new TcpClient { NoDelay = true };
        await control.ConnectAsync(host, ControlPort);
        var remoteAddress = Dns.GetHostAddresses(host).First(x => x.AddressFamily == AddressFamily.InterNetwork);
        using var reader = new StreamReader(control.GetStream());
        using var writer = new StreamWriter(control.GetStream()) { AutoFlush = true };
        using var input = await RemoteInputClient.ConnectAsync(new(Dns.GetHostAddresses(host).First(x => x.AddressFamily == AddressFamily.InterNetwork), InputPort));
        List<object> cases = new();
        var allApplied = true;
        var backgroundDelivered = true;
        try
        {
            if (!(await input.SetEnabledAsync(true)).Accepted) throw new InvalidOperationException("Remote input was not enabled.");
            foreach (var rate in RatesMbps)
            {
                background.Send([1], new IPEndPoint(remoteAddress, BackgroundPort));
                await writer.WriteLineAsync($"RATE {rate} {((IPEndPoint)background.Client.LocalEndPoint!).Port}");
                if (await reader.ReadLineAsync() != $"READY {rate}") throw new IOException("Remote rate change was not acknowledged.");
                await Task.Delay(500);
                var startBytes = Interlocked.Read(ref receivedBytes);
                var startPackets = Interlocked.Read(ref receivedPackets);
                var phaseStart = Stopwatch.GetTimestamp();
                var samples = await MeasureRateAsync(input);
                await Task.Delay(250);
                var phaseSeconds = Ms(Stopwatch.GetTimestamp() - phaseStart) / 1000;
                var actualMbps = (Interlocked.Read(ref receivedBytes) - startBytes) * 8d / phaseSeconds / 1_000_000;
                allApplied &= samples.Applied == samples.Events && samples.Errors.Length == 0;
                if (rate > 0) backgroundDelivered &= actualMbps >= rate * .5;
                var result = new
                {
                    OfferedVideoMbps = rate, ReceivedVideoMbps = actualMbps,
                    ReceivedVideoPackets = Interlocked.Read(ref receivedPackets) - startPackets,
                    samples.Events, samples.Applied, samples.Errors, samples.DurationSeconds,
                    SendCompletionMs = Stats(samples.SendMs), CursorConfirmedAckMs = Stats(samples.AckMs)
                };
                cases.Add(result);
                Console.WriteLine($"{rate,2} Mbps offered, {actualMbps:F2} received: cursor ACK mean/P95 {result.CursorConfirmedAckMs.Mean:F2}/{result.CursorConfirmedAckMs.P95:F2} ms; applied {samples.Applied}/{samples.Events}");
                await Task.Delay(500);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Passed = cases.Count == RatesMbps.Length && allApplied && backgroundDelivered,
                BackgroundLoadValid = backgroundDelivered,
                Machine = Environment.MachineName, Remote = host, Utc = DateTimeOffset.UtcNow,
                Scope = "Real two-machine LAN, production RemoteInputClient/RemoteInputServer and Windows SendInput/GetCursorPos, with synthetic 30 FPS burst UDP background. No FRD screen capture, codec, decoder, renderer, UI input queue or physical scanout. CursorConfirmedAck is local event generation to remote OS pointer at target and application ACK received; it is an upper bound on remote cursor application, not visual glass-to-glass.",
                InputRateHz = 120, EventsPerRate = 240, BackgroundRatesMbps = RatesMbps, Cases = cases
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Report: " + output);
        }
        finally
        {
            try { await input.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) { Console.Error.WriteLine("Input disable failed: " + error); }
            await writer.WriteLineAsync("STOP");
            if (await reader.ReadLineAsync() != "STOPPED") throw new IOException("Remote probe did not confirm shutdown.");
            receiveStop.Cancel();
            await receive;
        }
    }

    static async Task<RateSamples> MeasureRateAsync(RemoteInputClient input)
    {
        const int events = 240;
        const double hz = 120;
        var sent = new ConcurrentBag<double>();
        var acknowledged = new ConcurrentBag<double>();
        var errors = new ConcurrentQueue<string>();
        var applied = 0;
        List<Task> replies = new(events);
        var began = Stopwatch.GetTimestamp();
        for (var i = 0; i < events; i++)
        {
            var due = began + (long)(i * Stopwatch.Frequency / hz);
            while (Stopwatch.GetTimestamp() < due)
            {
                var remain = (due - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                if (remain > 2) await Task.Delay(1);
                else Thread.Yield();
            }
            var generated = Stopwatch.GetTimestamp();
            var x = .3 + .4 * i / (events - 1d);
            var confirmation = await input.QueueAsync(new(RemoteInputKind.MouseMove, x, .5)).WaitAsync(TimeSpan.FromSeconds(3));
            sent.Add(Ms(Stopwatch.GetTimestamp() - generated));
            replies.Add(Observe(confirmation, generated));
        }
        await Task.WhenAll(replies).WaitAsync(TimeSpan.FromSeconds(5));
        return new(events, applied, errors.ToArray(), Ms(Stopwatch.GetTimestamp() - began) / 1000, sent.ToArray(), acknowledged.ToArray());

        async Task Observe(Task<RemoteInputResult> reply, long generated)
        {
            var result = await reply;
            if (!result.Accepted || !result.Message.StartsWith("cursor=", StringComparison.Ordinal))
                errors.Enqueue(result.Message);
            else Interlocked.Increment(ref applied);
            acknowledged.Add(Ms(Stopwatch.GetTimestamp() - generated));
        }
    }

    static Distribution Stats(double[] values)
    {
        Array.Sort(values);
        return new(values.Average(), values[(int)Math.Ceiling(values.Length * .5) - 1],
            values[(int)Math.Ceiling(values.Length * .95) - 1], values[^1]);
    }

    static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    static async Task UdpLoopbackAsync()
    {
        var nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var injector = new RecordingInjector();
        using var tcpServer = new RemoteInputServer(injector);
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource();
        var enabled = 1;
        var receive = RemoteHost.ReceiveMouseAsync(udp, injector, IPAddress.Loopback, nonce,
            () => Volatile.Read(ref enabled) != 0, stop.Token);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(tcpServer.Endpoint);
            using var input = new RemoteInputClient(tcp, (IPEndPoint)udp.LocalEndPoint!, Convert.FromHexString(nonce));
            if (!(await input.SetEnabledAsync(true)).Accepted) throw new InvalidOperationException("TCP input enable failed.");
            if (!(await await input.QueueAsync(new(RemoteInputKind.MouseMove, .25, .75))).Accepted)
                throw new InvalidOperationException("UDP mouse send failed.");
            await WaitCountAsync(1);
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.SendTo(MousePacket(nonce, 3, .5, .5), udp.LocalEndPoint!);
            await WaitCountAsync(2);
            var wrong = MousePacket(Convert.ToHexString(Guid.NewGuid().ToByteArray()), 100, .9, .9);
            sender.SendTo(wrong, udp.LocalEndPoint!);
            sender.SendTo(MousePacket(nonce, 1, .8, .8), udp.LocalEndPoint!);
            sender.SendTo(MousePacket(nonce, 2, .7, .7), udp.LocalEndPoint!);
            await Task.Delay(200);
            var received = injector.Inputs.ToArray();
            if (received.Length != 2 || received[0].X != .25 || received[0].Y != .75 ||
                received[1].X != .5 || received[1].Y != .5)
                throw new InvalidOperationException("UDP authentication, latest-only ordering or client send failed: " + JsonSerializer.Serialize(received));
            injector.BlockNext();
            sender.SendTo(MousePacket(nonce, 4, .6, .6), udp.LocalEndPoint!);
            await injector.WaitBlockedAsync();
            try
            {
                for (var sequence = 5; sequence <= 104; sequence++)
                    sender.SendTo(MousePacket(nonce, sequence, .9, .9), udp.LocalEndPoint!);
            }
            finally { injector.Unblock(); }
            await WaitCountAsync(4);
            await Task.Delay(100);
            var batched = injector.Inputs.ToArray();
            if (batched.Length != 4 || batched[2].X != .6 || batched[3].X != .9)
                throw new InvalidOperationException("Queued UDP positions were not reduced to their latest value: " + JsonSerializer.Serialize(batched));
            Volatile.Write(ref enabled, 0);
            sender.SendTo(MousePacket(nonce, 105, .6, .6), udp.LocalEndPoint!);
            await Task.Delay(50);
            if (injector.Inputs.Count != 4) throw new InvalidOperationException("Disabled UDP input was injected.");
            Console.WriteLine("UDP mouse loopback passed: latest-only backlog, authentication, stale/forged/disabled packet rejection.");
            async Task WaitCountAsync(int count)
            {
                for (var attempt = 0; attempt < 100 && injector.Inputs.Count < count; attempt++) await Task.Delay(10);
                if (injector.Inputs.Count < count) throw new TimeoutException($"Expected {count} UDP mouse moves, got {injector.Inputs.Count}.");
            }
        }
        finally { stop.Cancel(); await receive; }
    }

    static byte[] MousePacket(string session, long sequence, double x, double y)
    {
        var packet = new byte[40];
        Convert.FromHexString(session).CopyTo(packet, 0);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), sequence);
        BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(24), x);
        BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(32), y);
        return packet;
    }

    sealed class RecordingInjector : IRemoteInputInjector
    {
        readonly ManualResetEventSlim unblock = new(true);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int blockNext;
        public ConcurrentQueue<RemoteInputEvent> Inputs { get; } = new();
        public void BlockNext() { blocked = new(TaskCreationOptions.RunContinuationsAsynchronously); unblock.Reset(); Volatile.Write(ref blockNext, 1); }
        public Task WaitBlockedAsync() => blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        public void Unblock() => unblock.Set();
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            Inputs.Enqueue(input);
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            { blocked.TrySetResult(); if (!unblock.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Blocked injector was not released."); }
            return new(true, "recorded");
        }
        public RemoteInputResult ReleaseAll() => new(true, "released");
    }

    sealed record RateSamples(int Events, int Applied, string[] Errors, double DurationSeconds, double[] SendMs, double[] AckMs);
    sealed record Distribution(double Mean, double P50, double P95, double Max);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetCursorPos(int x, int y);

    sealed class CursorConfirmingInjector : IRemoteInputInjector, IDisposable
    {
        readonly Win32InputInjector inner = new();
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            if (input.Kind != RemoteInputKind.MouseMove) return inner.Inject(input);
            var monitor = Win32InputInjector.ReadPrimaryMonitor();
            var target = InputCoordinates.Map(input.X, input.Y, monitor,
                GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            var result = inner.Inject(input);
            if (!result.Accepted) return result;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (!GetCursorPos(out var cursor)) return new(false, "GetCursorPos failed: " + Marshal.GetLastWin32Error());
                if (Math.Abs(cursor.X - target.X) <= 1 && Math.Abs(cursor.Y - target.Y) <= 1)
                    return new(true, $"cursor={cursor.X},{cursor.Y}");
                Thread.Yield();
            }
            return new(false, $"Remote cursor did not reach {target.X},{target.Y} within 50 ms.");
        }
        public RemoteInputResult ReleaseAll() => inner.ReleaseAll();
        public void Dispose() => inner.Dispose();
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    }

    sealed class BackgroundVideoSender : IDisposable
    {
        readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        readonly CancellationTokenSource stop;
        readonly Task worker;
        readonly IPAddress target;
        int rateMbps, port;
        long sentBytes;
        public BackgroundVideoSender(IPAddress target, CancellationToken parent)
        {
            this.target = target;
            socket.Bind(new IPEndPoint(IPAddress.Any, BackgroundPort));
            stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
            worker = Task.Run(RunAsync);
        }
        public void SetRate(int rate, int targetPort)
        {
            Volatile.Write(ref port, targetPort);
            Volatile.Write(ref rateMbps, rate);
        }
        async Task RunAsync()
        {
            var bytes = new byte[1172];
            Random.Shared.NextBytes(bytes);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    var rate = Volatile.Read(ref rateMbps);
                    if (rate == 0) continue;
                    var endpoint = new IPEndPoint(target, Volatile.Read(ref port));
                    var remaining = rate * 1_000_000 / 8 / 30;
                    while (remaining > 0)
                    {
                        var count = Math.Min(bytes.Length, remaining);
                        socket.SendTo(bytes.AsSpan(0, count), SocketFlags.None, endpoint);
                        Interlocked.Add(ref sentBytes, count);
                        remaining -= count;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { Console.Error.WriteLine("Background UDP sender stopped."); }
        }
        public void Dispose()
        {
            stop.Cancel();
            try { worker.GetAwaiter().GetResult(); }
            finally { Console.WriteLine("Background UDP bytes sent: " + Interlocked.Read(ref sentBytes)); socket.Dispose(); stop.Dispose(); }
        }
    }
}
