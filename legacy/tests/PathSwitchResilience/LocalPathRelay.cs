using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Frd;

sealed class LocalPathRelay : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> peers = new();
    readonly object gate = new();
    readonly int hostPort;
    TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource inputResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? accepting, udpLoop;
    TcpClient? controlConnection;
    TcpClient? inputConnection;
    byte[]? heldVideo;
    bool paused, udpPaused, diagnosticPaused, feedbackPaused, reorder;
    int nextPeer, dropEvery;
    IPEndPoint? clientUdp;
    long videoForwarded, inputForwarded, udpDropped, tcpForwarded, udpSeen;

    public LocalPathRelay(int hostPort)
    {
        this.hostPort = hostPort;
        listener.Start();
        udp.Bind(new IPEndPoint(IPAddress.Loopback, Port));
        resumed.TrySetResult();
        inputResumed.TrySetResult();
        accepting = AcceptAsync();
        udpLoop = RouteUdpAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
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
            await RemoteWire.WriteAsync(host, request, stop.Token);
            var reply = await RemoteWire.ReadAsync<RemoteReply>(host, stop.Token);
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

    async Task RouteUdpAsync()
    {
        var buffer = new byte[2048];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await udp.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var source = (IPEndPoint)received.RemoteEndPoint;
                if (!source.Address.Equals(IPAddress.Loopback)) continue;
                var packet = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                if (source.Port == hostPort)
                {
                    IPEndPoint? controller;
                    lock (gate) controller = clientUdp;
                    if (controller == null) continue;
                    Interlocked.Increment(ref udpSeen);
                    byte[]? earlier = null;
                    lock (gate)
                    {
                        var videoPacket = packet.Length >= 5 && BitConverter.ToUInt32(packet) == VideoDatagram.Magic && packet[4] == 1;
                        var diagnosticPacket = FrameDiagnosticProtocol.TryDecode(packet, out _);
                        if (paused || udpPaused || diagnosticPacket && diagnosticPaused ||
                            videoPacket && dropEvery > 0 && Interlocked.Read(ref udpSeen) % dropEvery == 0)
                        { Interlocked.Increment(ref udpDropped); continue; }
                        if (reorder && videoPacket)
                        {
                            if (heldVideo == null) { heldVideo = packet; continue; }
                            earlier = heldVideo; heldVideo = null;
                        }
                    }
                    udp.SendTo(packet, controller);
                    Interlocked.Increment(ref videoForwarded);
                    if (earlier != null)
                    {
                        udp.SendTo(earlier, controller);
                        Interlocked.Increment(ref videoForwarded);
                    }
                }
                else
                {
                    lock (gate)
                    {
                        clientUdp = source;
                        var feedback = packet.Length >= 5 && BitConverter.ToUInt32(packet) == VideoDatagram.Magic && packet[4] == 2;
                        if (paused || udpPaused || feedback && feedbackPaused) { Interlocked.Increment(ref udpDropped); continue; }
                    }
                    udp.SendTo(packet, new IPEndPoint(IPAddress.Loopback, hostPort));
                    if (packet.Length == UdpInputProtocol.PacketSize) Interlocked.Increment(ref inputForwarded);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay udp] stopped."); }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[relay udp] socket disposed."); }
    }

    public async ValueTask DisposeAsync()
    {
        Resume();
        stop.Cancel(); listener.Stop(); udp.Dispose();
        if (accepting != null) await accepting;
        if (udpLoop != null) await udpLoop;
        await Task.WhenAll(peers.Values);
        stop.Dispose();
    }
}
