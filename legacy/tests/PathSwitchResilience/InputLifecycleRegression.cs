using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Frd;

static class InputLifecycleRegression
{
    public static async Task RunAsync()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;
        var session = Guid.NewGuid().ToByteArray();
        Exception? sessionFailure = null, inputFailure = null;
        await using var host = new RemoteUdpSession(socket, Convert.ToHexString(session), CancellationToken.None,
            error => sessionFailure = error);
        using var client = new UdpVideoReceiver();
        await client.RegisterRemoteAsync(endpoint, session, CancellationToken.None);
        using var sender = new UdpVideoSender(socket, await host.WaitForRegistrationAsync(CancellationToken.None));
        sender.SetEncoderBitrateKbps(5000); host.AttachSender(sender);
        long frames = 0;
        client.VideoReceived += _ => Interlocked.Increment(ref frames);
        var first = new Probe();
        var oldKey = host.AttachInput(first, () => true);
        Send(oldKey, 100, new(RemoteInputKind.KeyDown, ScanCode: 30));
        await Until(() => first.Events.Count == 1);
        host.DetachInput(first);
        var second = new Probe();
        var freshKey = host.AttachInput(second, () => true, error => inputFailure = error);
        if (oldKey.SequenceEqual(freshKey)) throw new InvalidOperationException("Input reconnect reused its nonce.");
        Send(oldKey, 1000, new(RemoteInputKind.KeyDown, ScanCode: 31));
        Send(freshKey, 1, new(RemoteInputKind.KeyUp, ScanCode: 30));
        await Until(() => second.Events.Count == 1);
        if (second.Events.Single().Kind != RemoteInputKind.KeyUp) throw new InvalidOperationException("Old-channel packet contaminated new input.");
        Console.WriteLine("PASS: same UDP endpoint/session; old channel sequence 1000 rejected, fresh channel sequence 1 applied.");

        second.ThrowNext = true;
        Send(freshKey, 2, new(RemoteInputKind.KeyDown, ScanCode: 32));
        await Until(() => Volatile.Read(ref inputFailure) != null);
        await AssertVideo(1);
        if (sessionFailure != null || second.Releases == 0) throw new InvalidOperationException("Input failure escaped its channel or omitted release.");
        Send(freshKey, 3, new(RemoteInputKind.KeyDown, ScanCode: 33));
        await Task.Delay(50);
        if (second.Events.Any(x => x.ScanCode == 33)) throw new InvalidOperationException("Faulted input still accepted events.");
        host.DetachInput(second);
        Console.WriteLine("PASS: injector exception stops only input, releases held state, keeps video and feedback active.");

        var third = new Probe();
        var thirdKey = host.AttachInput(third, () => true);
        Send(thirdKey, 1, new(RemoteInputKind.KeyDown, ScanCode: 34));
        await Until(() => third.Events.Count == 1);
        third.Entered.Reset();
        Monitor.Enter(third.Gate);
        Task repeatedEnable;
        try
        {
            third.BlockNext = true;
            Send(thirdKey, 2, new(RemoteInputKind.Wheel, WheelDelta: 120));
            if (!third.Entered.Wait(1000)) throw new TimeoutException("Injector did not enter its barrier.");
            Send(thirdKey, 3, new(RemoteInputKind.KeyUp, ScanCode: 34));
            repeatedEnable = Task.Run(() => host.UpdateInputEnabled(third, true, () => throw new InvalidOperationException("Repeated enable must be idempotent.")));
        }
        finally { Monitor.Exit(third.Gate); }
        await repeatedEnable;
        await Until(() => third.Events.Count == 3);
        if (third.Events.Last().Kind != RemoteInputKind.KeyUp) throw new InvalidOperationException("Repeated enable lost a queued release.");
        host.DetachInput(third);
        Console.WriteLine("PASS: repeated enable preserves queued key release and does not invoke state-change callback.");

        var slow = new Probe { DelayMs = RemoteUdpSession.MaximumInputAgeMs + 75 };
        inputFailure = null;
        var slowKey = host.AttachInput(slow, () => true, error => inputFailure = error);
        Send(slowKey, 1, new(RemoteInputKind.KeyDown, ScanCode: 35));
        if (!slow.Entered.Wait(1000)) throw new TimeoutException("Slow injector did not start.");
        Send(slowKey, 2, new(RemoteInputKind.KeyDown, ScanCode: 36));
        await Until(() => slow.Releases > 0);
        if (slow.Events.Count != 1 || slow.Releases == 0 || host.PendingInputs != 0)
            throw new InvalidOperationException("Expired input was replayed or held state was not released.");
        await AssertVideo(2);
        slow.DelayMs = 0;
        Send(slowKey, 3, new(RemoteInputKind.KeyUp, ScanCode: 35));
        await Until(() => slow.Events.Count == 2);
        if (inputFailure != null) throw new InvalidOperationException("Temporary local backlog closed the input channel.");
        host.DetachInput(slow);
        Console.WriteLine("PASS: >250 ms stale local backlog discarded and keys released; fresh input, video and feedback still delivered on the same channels.");

        await CheckTcpLifecycle();
        await CheckPermissionOwnership();

        void Send(byte[] key, long sequence, RemoteInputEvent input)
        {
            var bytes = new byte[UdpInputProtocol.PacketSize];
            UdpInputProtocol.Write(bytes, key, sequence, input);
            client.SendInput(bytes, endpoint);
        }
        async Task AssertVideo(long id)
        {
            var feedback = sender.Snapshot.ReceivedPackets;
            sender.Send(new(1, id, true, new byte[2048]), CancellationToken.None);
            await Until(() => Interlocked.Read(ref frames) >= id && sender.Snapshot.ReceivedPackets > feedback);
            if (sessionFailure != null) throw new InvalidOperationException("Video session was cancelled by an input problem.");
        }
    }

    static async Task CheckTcpLifecycle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        using var stop = new CancellationTokenSource();
        var probe = new Probe();
        var serving = RemoteInputServer.ServePeerAsync(probe, peer, stop.Token, allowTcpEvents: false);
        var stream = client.GetStream();
        await InputProtocol.WriteAsync(stream, new InputRequest(1, true, null), stop.Token);
        if (!(await InputProtocol.ReadAsync<InputReply>(stream, stop.Token)).Accepted) throw new InvalidOperationException("TCP enable rejected.");
        await InputProtocol.WriteAsync(stream, new InputRequest(2, null, new(RemoteInputKind.KeyDown, ScanCode: 30)), stop.Token);
        var denied = await InputProtocol.ReadAsync<InputReply>(stream, stop.Token);
        if (denied.Accepted || !denied.Message.Contains("UDP") || probe.Events.Count != 0)
            throw new InvalidOperationException("Remote TCP bypassed UDP-only input policy.");
        await InputProtocol.WriteAsync(stream, new InputRequest(3, false, null), stop.Token);
        if (!(await InputProtocol.ReadAsync<InputReply>(stream, stop.Token)).Accepted) throw new InvalidOperationException("TCP disable rejected.");
        stop.Cancel(); await serving;
        Console.WriteLine("PASS: remote TCP accepts enable/disable but rejects input events without injection.");
    }

    static async Task CheckPermissionOwnership()
    {
        await using var host = new RemoteHost(new AppConfiguration(), IPAddress.Loopback, 1, "fixture");
        foreach (var input in new[] { true, false })
        {
            using var oldChannel = new CancellationTokenSource();
            using var newChannel = new CancellationTokenSource();
            void Set(string name, object value) => typeof(RemoteHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
            Set("sessionId", "new-session");
            Set(input ? "inputPermissionStop" : "clipboardPermissionStop", newChannel);
            Set(input ? "hasInput" : "hasClipboard", true);
            host.ReleasePermissionChannel("old-session", oldChannel, input);
            host.ReleasePermissionChannel("new-session", oldChannel, input);
            if (input) host.SetInputAllowed(false); else host.SetClipboardAllowed(false);
            if (!newChannel.IsCancellationRequested) throw new InvalidOperationException("Stale cleanup removed the new permission owner.");
            host.ReleasePermissionChannel("new-session", newChannel, input);
        }
        Console.WriteLine("PASS: deterministic old-session and old-channel cleanup cannot prevent current input/clipboard revocation.");
    }

    static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 3000) throw new TimeoutException("Input lifecycle condition not reached.");
            await Task.Delay(5);
        }
    }

    sealed class Probe : IRemoteInputInjector
    {
        public readonly ConcurrentQueue<RemoteInputEvent> Events = new();
        public readonly object Gate = new();
        public readonly ManualResetEventSlim Entered = new();
        public bool ThrowNext, BlockNext;
        public int DelayMs, Releases;
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("Injected input failure."); }
            Entered.Set();
            if (BlockNext) { BlockNext = false; lock (Gate) { } }
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            Events.Enqueue(input);
            return new(true, "applied");
        }
        public RemoteInputResult ReleaseAll() { Interlocked.Increment(ref Releases); return new(true, "released"); }
    }
}
