using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Frd;

sealed class LocalPathRelay : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly Socket video = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly Socket diagnostic = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly Socket input = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    readonly int hostPort;
    TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource inputResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? accepting, videoLoop, diagnosticLoop, inputLoop;
    TcpClient? controlConnection;
    TcpClient? inputConnection;
    byte[]? heldVideo;
    bool paused, udpPaused, diagnosticPaused, feedbackPaused, reorder;
    int nextPeer, clientVideoPort, clientDiagnosticPort, hostSenderPort, dropEvery;
    long videoForwarded, inputForwarded, udpDropped, tcpForwarded, udpSeen;

    public LocalPathRelay(int hostPort)
    {
        this.hostPort = hostPort;
        listener.Start();
        video.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        diagnostic.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        input.Bind(new IPEndPoint(IPAddress.Loopback, Port));
        resumed.TrySetResult();
        inputResumed.TrySetResult();
        accepting = AcceptAsync();
        videoLoop = RouteVideoAsync();
        diagnosticLoop = RouteDiagnosticsAsync();
        inputLoop = RouteInputAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int VideoPort => ((IPEndPoint)video.LocalEndPoint!).Port;
    public int DiagnosticPort => ((IPEndPoint)diagnostic.LocalEndPoint!).Port;
    public long VideoForwarded => Interlocked.Read(ref videoForwarded);
    public long InputForwarded => Interlocked.Read(ref inputForwarded);
    public long UdpDropped => Interlocked.Read(ref udpDropped);
    public long TcpForwarded => Interlocked.Read(ref tcpForwarded);
    public int TcpConnections => Volatile.Read(ref nextPeer);

    public void Pause()
    {
        lock (gate)
        {
            if (paused) return;
            paused = true;
            heldVideo = null;
            resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            paused = false;
            udpPaused = false;
            diagnosticPaused = false;
            feedbackPaused = false;
            heldVideo = null;
            resumed.TrySetResult();
            inputResumed.TrySetResult();
        }
    }

    public void PauseUdpOnly()
    {
        lock (gate) { udpPaused = true; heldVideo = null; }
    }

    public void PauseDiagnosticsOnly() { lock (gate) diagnosticPaused = true; }
    public void PauseFeedbackOnly() { lock (gate) feedbackPaused = true; }

    public void PauseInputTcpOnly()
    {
        lock (gate)
        {
            inputResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void CloseControl() => Volatile.Read(ref controlConnection)?.Dispose();
    public void CloseInput() => Volatile.Read(ref inputConnection)?.Dispose();

    public void SetImpairment(int dropEveryNthVideoPacket, bool reorderPairs)
    {
        lock (gate)
        {
            dropEvery = dropEveryNthVideoPacket;
            reorder = reorderPairs;
            heldVideo = null;
        }
    }

    async Task AwaitPathAsync(bool inputChannel, CancellationToken token)
    {
        Task path, auxiliary;
        lock (gate) { path = resumed.Task; auxiliary = inputResumed.Task; }
        await path.WaitAsync(token);
        if (inputChannel) await auxiliary.WaitAsync(token);
    }

    async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                var id = Interlocked.Increment(ref nextPeer);
                var task = HandleAsync(client);
                peers[id] = task;
                _ = task.ContinueWith(completed =>
                {
                    peers.TryRemove(id, out _);
                    if (completed.IsFaulted) Console.Error.WriteLine("[relay tcp] " + completed.Exception);
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay tcp] accept stopped."); }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay tcp] listener disposed."); }
    }

    async Task HandleAsync(TcpClient downstream)
    {
        using (downstream)
        using (var upstream = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true })
        {
            downstream.NoDelay = true;
            await upstream.ConnectAsync(IPAddress.Loopback, hostPort, stop.Token);
            var client = downstream.GetStream();
            var host = upstream.GetStream();
            var request = await RemoteWire.ReadAsync<RemoteRequest>(client, stop.Token);
            if (request.Kind == "hello") Volatile.Write(ref controlConnection, downstream);
            if (request.Kind == "input") Volatile.Write(ref inputConnection, downstream);
            if (request.Kind == "hello")
            {
                Volatile.Write(ref clientVideoPort, request.VideoPort);
                Volatile.Write(ref clientDiagnosticPort, request.DiagnosticPort);
                request = request with { VideoPort = VideoPort, DiagnosticPort = DiagnosticPort };
            }
            await RemoteWire.WriteAsync(host, request, stop.Token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(host, stop.Token);
            if (request.Kind == "hello" && reply.Welcome is { } welcome)
            {
                Volatile.Write(ref hostSenderPort, welcome.SenderPort);
                reply = reply with { Welcome = welcome with { SenderPort = VideoPort } };
            }
            await RemoteWire.WriteAsync(client, reply, stop.Token);
            var toHost = PumpAsync(client, host, request.Kind == "input");
            var toClient = PumpAsync(host, client, request.Kind == "input");
            await Task.WhenAny(toHost, toClient);
            if (request.Kind == "hello") Interlocked.CompareExchange(ref controlConnection, null, downstream);
            if (request.Kind == "input") Interlocked.CompareExchange(ref inputConnection, null, downstream);
        }
    }

    async Task PumpAsync(NetworkStream source, NetworkStream target, bool inputChannel)
    {
        var buffer = new byte[16 * 1024];
        while (!stop.IsCancellationRequested)
        {
            var length = await source.ReadAsync(buffer, stop.Token);
            if (length == 0) return;
            await AwaitPathAsync(inputChannel, stop.Token);
            await target.WriteAsync(buffer.AsMemory(0, length), stop.Token);
            Interlocked.Add(ref tcpForwarded, length);
        }
    }

    async Task RouteVideoAsync()
    {
        var buffer = new byte[2048];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await video.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var source = (IPEndPoint)received.RemoteEndPoint;
                var senderPort = Volatile.Read(ref hostSenderPort);
                var controllerPort = Volatile.Read(ref clientVideoPort);
                if (!source.Address.Equals(IPAddress.Loopback) || senderPort == 0 || controllerPort == 0) continue;
                var packet = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                if (source.Port == senderPort)
                {
                    Interlocked.Increment(ref udpSeen);
                    byte[]? earlier = null;
                    lock (gate)
                    {
                        if (paused || udpPaused || dropEvery > 0 && Interlocked.Read(ref udpSeen) % dropEvery == 0)
                        { Interlocked.Increment(ref udpDropped); continue; }
                        if (reorder)
                        {
                            if (heldVideo == null) { heldVideo = packet; continue; }
                            earlier = heldVideo; heldVideo = null;
                        }
                    }
                    video.SendTo(packet, new IPEndPoint(IPAddress.Loopback, controllerPort));
                    Interlocked.Increment(ref videoForwarded);
                    if (earlier != null)
                    {
                        video.SendTo(earlier, new IPEndPoint(IPAddress.Loopback, controllerPort));
                        Interlocked.Increment(ref videoForwarded);
                    }
                }
                else if (source.Port == controllerPort)
                {
                    lock (gate) if (paused || udpPaused || feedbackPaused) { Interlocked.Increment(ref udpDropped); continue; }
                    video.SendTo(packet, new IPEndPoint(IPAddress.Loopback, senderPort));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay video] stopped."); }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay video] socket disposed."); }
    }

    async Task RouteDiagnosticsAsync()
    {
        var buffer = new byte[2048];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await diagnostic.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var source = (IPEndPoint)received.RemoteEndPoint;
                var port = Volatile.Read(ref clientDiagnosticPort);
                if (!source.Address.Equals(IPAddress.Loopback) || source.Port != Volatile.Read(ref hostSenderPort) || port == 0) continue;
                lock (gate) if (paused || udpPaused || diagnosticPaused) { Interlocked.Increment(ref udpDropped); continue; }
                video.SendTo(buffer.AsSpan(0, received.ReceivedBytes).ToArray(),
                    new IPEndPoint(IPAddress.Loopback, port));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay diagnostic] stopped."); }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay diagnostic] socket disposed."); }
    }

    async Task RouteInputAsync()
    {
        var buffer = new byte[2048];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await input.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var source = (IPEndPoint)received.RemoteEndPoint;
                if (!source.Address.Equals(IPAddress.Loopback)) continue;
                lock (gate) if (paused || udpPaused) { Interlocked.Increment(ref udpDropped); continue; }
                input.SendTo(buffer.AsSpan(0, received.ReceivedBytes).ToArray(),
                    new IPEndPoint(IPAddress.Loopback, hostPort));
                Interlocked.Increment(ref inputForwarded);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay input] stopped."); }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay input] socket disposed."); }
    }

    public async ValueTask DisposeAsync()
    {
        Resume();
        stop.Cancel(); listener.Stop(); video.Dispose(); diagnostic.Dispose(); input.Dispose();
        if (accepting != null) await accepting;
        if (videoLoop != null) await videoLoop;
        if (diagnosticLoop != null) await diagnosticLoop;
        if (inputLoop != null) await inputLoop;
        await Task.WhenAll(peers.Values);
        stop.Dispose();
    }
}
