using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Frd;

static class UdpResilienceRegression
{
    public static async Task RunAsync()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;
        var nonce = Guid.NewGuid().ToByteArray();
        Exception? failure = null;
        await using var host = new RemoteUdpSession(socket, Convert.ToHexString(nonce), CancellationToken.None,
            error => Volatile.Write(ref failure, error));
        using var stray = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        stray.SendTo(new byte[4096], endpoint);
        stray.SendTo(new byte[1], endpoint);
        using var receiver = new UdpVideoReceiver();
        await receiver.RegisterRemoteAsync(endpoint, nonce, CancellationToken.None);
        var peer = await host.WaitForRegistrationAsync(CancellationToken.None);
        using var sender = new UdpVideoSender(socket, peer);
        sender.SetEncoderBitrateKbps(5000);
        host.AttachSender(sender);
        var injector = new BlockingInjector();
        var enabled = true;
        host.AttachInput(injector, () => Volatile.Read(ref enabled));
        long sequence = 0, videos = 0;
        receiver.VideoReceived += _ => Interlocked.Increment(ref videos);
        Exception? receiverFailure = null;
        receiver.Failed += error => Volatile.Write(ref receiverFailure, error);
        void Send(RemoteInputEvent input)
        {
            var packet = new byte[UdpInputProtocol.PacketSize];
            UdpInputProtocol.Write(packet, nonce, ++sequence, input);
            receiver.SendInput(packet, endpoint);
        }
        try
        {
            Send(new(RemoteInputKind.KeyDown, ScanCode: 30));
            await UntilAsync(() => injector.Entered.IsSet);
            stray.SendTo(new byte[65000], endpoint);
            sender.Send(new(1, 1, true, new byte[3000]), CancellationToken.None);
            await UntilAsync(() => sender.Snapshot.ReceivedPackets >= 3 && Interlocked.Read(ref videos) == 1);
            if (injector.Resume.IsSet || failure != null) throw new InvalidOperationException("Blocked input stopped UDP feedback.");
            for (var i = 1; i <= 100; i++) Send(new(RemoteInputKind.MouseMove, i / 100d, .5));
            Send(new(RemoteInputKind.MouseDown, 1, .5));
            Send(new(RemoteInputKind.MouseUp, 1, .5));
            Send(new(RemoteInputKind.KeyUp, ScanCode: 30));
            await UntilAsync(() => host.PendingInputs == 4);
            injector.Resume.Set();
            await UntilAsync(() => host.AppliedInputs == 5);
            var events = injector.Events.ToArray();
            if (!events.Select(x => x.Kind).SequenceEqual(new[] { RemoteInputKind.KeyDown, RemoteInputKind.MouseMove,
                RemoteInputKind.MouseDown, RemoteInputKind.MouseUp, RemoteInputKind.KeyUp }) || events[1].X != 1)
                throw new InvalidOperationException("Input coalescing crossed a button/key boundary.");
            Console.WriteLine("PASS: unauthenticated 4096/65000-byte UDP packets ignored; blocked input does not block video feedback; latest move and button/key ordering preserved.");

            var callbacks = 0;
            receiver.DiagnosticReceived += _ => { Interlocked.Increment(ref callbacks); throw new InvalidDataException("Injected diagnostic callback failure."); };
            var diagnostic = FrameDiagnosticProtocol.Encode(new(1, 1, 1, 2, 3, Stopwatch.Frequency));
            sender.SendDiagnostic(diagnostic, peer, CancellationToken.None);
            await UntilAsync(() => Volatile.Read(ref callbacks) == 1);
            sender.SendDiagnostic(diagnostic, peer, CancellationToken.None);
            sender.Send(new(1, 2, true, new byte[2000]), CancellationToken.None);
            Send(new(RemoteInputKind.Wheel, WheelDelta: 120));
            await UntilAsync(() => Interlocked.Read(ref videos) == 2 && host.AppliedInputs == 6);
            if (receiverFailure != null || callbacks != 1) throw new InvalidOperationException("Diagnostics failure escaped its channel.");
            Console.WriteLine("PASS: failing diagnostic callback disabled once; subsequent video and input still delivered.");

            injector.Entered.Reset(); injector.Resume.Reset(); injector.BlockNext = true;
            Send(new(RemoteInputKind.KeyDown, ScanCode: 31));
            await UntilAsync(() => injector.Entered.IsSet);
            var baseline = injector.Events.Count;
            for (var i = 0; i < 150; i++) Send(new(RemoteInputKind.Wheel, WheelDelta: 120));
            Send(new(RemoteInputKind.KeyUp, ScanCode: 31));
            await UntilAsync(() => host.PendingInputs == 24);
            if (host.PendingInputs > 128) throw new InvalidOperationException("Unbounded input queue.");
            injector.Resume.Set();
            await UntilAsync(() => host.PendingInputs == 0 && injector.Events.Last().Kind == RemoteInputKind.KeyUp);
            if (injector.Events.ToArray()[baseline].Kind != RemoteInputKind.ReleaseAll)
                throw new InvalidOperationException("Overflow did not release held keys before newer events.");
            Console.WriteLine("PASS: overload queue bounded; pending backlog replaced with release before new input.");

            host.UpdateInputEnabled(injector, () => Volatile.Write(ref enabled, false));
            var beforeDisable = injector.Events.Count;
            Send(new(RemoteInputKind.KeyDown, ScanCode: 32));
            await Task.Delay(60);
            if (injector.Events.Count != beforeDisable || host.PendingInputs != 0)
                throw new InvalidOperationException("Disabled input was queued or applied.");
            host.UpdateInputEnabled(injector, () => Volatile.Write(ref enabled, true));
            Send(new(RemoteInputKind.KeyUp, ScanCode: 32));
            await UntilAsync(() => injector.Events.Count == beforeDisable + 1);
            if (injector.Events.Last().Kind != RemoteInputKind.KeyUp) throw new InvalidOperationException("Re-enable replayed stale input.");
            Console.WriteLine("PASS: disabled input rejected; re-enable accepts only fresh events.");

            host.DetachInput(injector);
            socket.Dispose();
            await UntilAsync(() => Volatile.Read(ref failure) != null);
            Console.WriteLine("PASS: fatal receiver failure immediately reported to session owner.");
        }
        finally { injector.Resume.Set(); host.DetachInput(injector); }

        using var silent = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var unregistered = new UdpVideoReceiver();
        var timer = Stopwatch.StartNew();
        try
        {
            await unregistered.RegisterRemoteAsync((IPEndPoint)silent.LocalEndPoint!, nonce, CancellationToken.None);
            throw new InvalidOperationException("Missing UDP registration unexpectedly succeeded.");
        }
        catch (IOException error) when (error.Message.Contains("UDP --port forwarding rule"))
        {
            if (timer.Elapsed.TotalSeconds is < 2 or > 6) throw new InvalidOperationException("Registration retry budget was not respected.");
            Console.WriteLine("PASS: five missing registration replies produce actionable forwarding error.");
        }
        using var cancelled = new UdpVideoReceiver();
        using var stop = new CancellationTokenSource(50);
        try
        {
            await cancelled.RegisterRemoteAsync((IPEndPoint)silent.LocalEndPoint!, nonce, stop.Token);
            throw new InvalidOperationException("Cancelled registration unexpectedly succeeded.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { Console.WriteLine("PASS: explicit registration cancellation preserved."); }
    }

    static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 3000)
        {
            if (predicate()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("UDP resilience assertion did not become true.");
    }

    sealed class BlockingInjector : IRemoteInputInjector
    {
        public readonly ConcurrentQueue<RemoteInputEvent> Events = new();
        public readonly ManualResetEventSlim Entered = new(), Resume = new();
        public bool BlockNext = true;
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            Events.Enqueue(input);
            if (BlockNext)
            {
                BlockNext = false; Entered.Set();
                if (!Resume.Wait(8000)) throw new TimeoutException("Test did not release blocking injector.");
            }
            return new(true, "applied");
        }
        public RemoteInputResult ReleaseAll() => new(true, "released");
    }
}
