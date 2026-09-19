using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Frd;

namespace Frd.InputPipeline;

static class Program
{
    static readonly List<object> checks = new();
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    static readonly RemoteInputEvent Key = new(RemoteInputKind.KeyDown, ScanCode: 0x1e);

    static async Task<int> Main(string[] args)
    {
        var watch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await PipelineOrdering();
            await ProductionReleaseAndReconnect();
            foreach (var mode in new[] { "disconnect", "wrong-sequence", "dispose" }) await PendingFailure(mode);
            await CancelAfterWrite();
            await CancelDisableBeforeWrite();
            await CancelDisableWaitingForSlot();
            await DelayedEnableCannotReviveDisable();
            await RejectedDisableClosesConnection();
            await WindowAndTimeout();
        }
        catch (Exception error) { Console.Error.WriteLine(error); failure = error; }
        var report = Path.GetFullPath(args.FirstOrDefault() ?? "results/input-latency/pipeline-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var result = new { Passed = failure is null, DurationSeconds = watch.Elapsed.TotalSeconds,
            Scope = "Actual loopback TCP with production RemoteInputClient/RemoteInputServer, scripted ACK peer and mock injector. No windows, SendInput, cursor movement, physical keyboard state or real application interaction.",
            Checks = checks, Error = failure?.ToString() };
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{(failure is null ? "PASS" : "FAIL")} {checks.Count} checks, {watch.Elapsed.TotalSeconds:F2}s; {report}");
        return failure is null ? 0 : 1;
    }

    static async Task PipelineOrdering()
    {
        using var peer = await ScriptedPeer.Create();
        await peer.Enable();
        RemoteInputEvent[] inputs = [Key, new(RemoteInputKind.Wheel, .2, .7, WheelDelta: -120), new(RemoteInputKind.KeyUp, ScanCode: 0x1e)];
        List<Task<RemoteInputResult>> replies = new();
        foreach (var input in inputs) replies.Add(await peer.Client.QueueAsync(input).WaitAsync(Deadline));
        var requests = new List<InputRequest>();
        foreach (var unused in inputs) requests.Add(await peer.Read());
        Check(requests.Select(x => x.Sequence).SequenceEqual(new long[] { 2, 3, 4 }) && requests.Select(x => x.Input).SequenceEqual(inputs),
            "All requests arrive in exact sequence while the first event ACK is withheld", requests);
        Check(replies.All(x => !x.IsCompleted), "Returned acknowledgement tasks really await remote replies");
        await peer.Reply(requests[0], false, "deliberate target rejection");
        await peer.Reply(requests[1], true, "wheel accepted");
        await peer.Reply(requests[2], true, "key released");
        var results = await Task.WhenAll(replies).WaitAsync(Deadline);
        Check(results.SequenceEqual(new RemoteInputResult[] { new(false, "deliberate target rejection"), new(true, "wheel accepted"), new(true, "key released") }),
            "Remote acceptance, rejection and messages stay associated with their exact request", results);

        var held = await peer.Client.QueueAsync(Key).WaitAsync(Deadline);
        var disabling = peer.Client.SetEnabledAsync(false);
        Check(!peer.Client.Enabled, "Disable clears Enabled before any acknowledgement arrives");
        var blocked = await peer.Client.SendAsync(new(RemoteInputKind.MouseDown, .5, .5)).WaitAsync(Deadline);
        Check(!blocked.Accepted, "New input is refused immediately during disable", blocked);
        var prior = await peer.Read(); var disable = await peer.Read();
        Check(prior.Sequence == 5 && prior.Input == Key && disable.Sequence == 6 && disable.Enable == false && disable.Input == null,
            "Previously written input stays before disable on the wire", new { prior, disable });
        await peer.Reply(prior); await peer.Reply(disable);
        Check((await held.WaitAsync(Deadline)).Accepted && (await disabling.WaitAsync(Deadline)).Accepted && !peer.Client.Enabled,
            "Pending input and disable both complete from their real ACKs");
        Check(peer.Available == 0, "Rejected new input added no wire data");
    }

    static async Task ProductionReleaseAndReconnect()
    {
        var mock = new MockInjector();
        using var server = new RemoteInputServer(mock);
        using (var client = await RemoteInputClient.ConnectAsync(server.Endpoint).WaitAsync(Deadline))
        {
            Check(!client.Enabled, "New real-server connection starts disabled");
            await client.SetEnabledAsync(true).WaitAsync(Deadline);
            var down = await client.QueueAsync(Key).WaitAsync(Deadline);
            var button = await client.QueueAsync(new(RemoteInputKind.MouseDown, .1, .1)).WaitAsync(Deadline);
            await Task.WhenAll(down, button).WaitAsync(Deadline);
            Check(mock.Held == 2, "Production server delivered held key and mouse button to mock", new { mock.Held });
            await client.SetEnabledAsync(false).WaitAsync(Deadline);
            Check(mock.Held == 0 && mock.Releases > 0, "Production disable invokes ReleaseAll", new { mock.Held, mock.Releases });
            await client.SetEnabledAsync(true).WaitAsync(Deadline);
            await client.SendAsync(Key).WaitAsync(Deadline);
        }
        await Until(() => mock.Held == 0);
        Check(mock.Held == 0, "Closing a production connection releases its held key", new { mock.Releases });
        using var next = await RemoteInputClient.ConnectAsync(server.Endpoint).WaitAsync(Deadline);
        var before = mock.Count;
        var rejected = await next.SendAsync(Key).WaitAsync(Deadline);
        Check(!next.Enabled && !rejected.Accepted && mock.Count == before, "Reconnect remains disabled and cannot inject without explicit enable");
    }

    static async Task PendingFailure(string mode)
    {
        using var peer = await ScriptedPeer.Create();
        await peer.Enable();
        List<Task<RemoteInputResult>> pending = new();
        for (var i = 0; i < 4; i++) pending.Add(await peer.Client.QueueAsync(Key).WaitAsync(Deadline));
        var requests = new List<InputRequest>();
        for (var i = 0; i < 4; i++) requests.Add(await peer.Read());
        if (mode == "disconnect") peer.ClosePeer();
        else if (mode == "dispose") peer.Client.Dispose();
        else await peer.Reply(requests[0] with { Sequence = requests[0].Sequence + 1 });
        var errors = await Task.WhenAll(pending.Select(Fault)).WaitAsync(Deadline);
        Check(errors.All(x => x is not null) && pending.All(x => x.IsFaulted) && !peer.Client.Enabled,
            mode + " terminates every pending acknowledgement and disables forwarding", errors.Select(x => x!.GetType().Name).ToArray());
        if (mode == "wrong-sequence") Check(errors.All(x => x is InvalidDataException), "Out-of-order ACK is reported as protocol corruption");
        if (mode == "dispose") Check(errors.All(x => x is ObjectDisposedException), "Dispose never leaves outstanding callers waiting");
    }

    static async Task CancelAfterWrite()
    {
        var mock = new MockInjector();
        using var server = new RemoteInputServer(mock);
        await using var relay = new AckRelay(server.Endpoint);
        using var client = await RemoteInputClient.ConnectAsync(relay.Endpoint).WaitAsync(Deadline);
        await client.SetEnabledAsync(true).WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var pending = await client.QueueAsync(Key, cancellation.Token).WaitAsync(Deadline);
        await relay.Withheld.Task.WaitAsync(Deadline);
        Check(mock.Held == 1 && !pending.IsCompleted, "Cancellation fixture holds a real server ACK after its mock has received key-down");
        cancellation.Cancel();
        var error = await Fault(pending).WaitAsync(Deadline);
        await Until(() => mock.Held == 0);
        await relay.Completion.WaitAsync(Deadline);
        Check(error is OperationCanceledException && !client.Enabled && mock.Held == 0 && mock.Releases > 0,
            "Cancellation after writing closes TCP and production server releases held input", new { Error = error?.GetType().Name, client.Enabled, mock.Held, mock.Releases });
    }

    static async Task CancelDisableBeforeWrite()
    {
        var mock = new MockInjector();
        using var server = new RemoteInputServer(mock);
        using var client = await RemoteInputClient.ConnectAsync(server.Endpoint).WaitAsync(Deadline);
        await client.SetEnabledAsync(true).WaitAsync(Deadline);
        await client.SendAsync(Key).WaitAsync(Deadline);
        Check(mock.Held == 1, "Pre-cancelled disable fixture has a key held at the production receiver");
        var releases = mock.Releases;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Fault(client.SetEnabledAsync(false, cancellation.Token)).WaitAsync(Deadline);
        await Until(() => mock.Held == 0 && mock.Releases > releases);
        Check(error is OperationCanceledException && !client.Enabled && mock.Held == 0,
            "Disable cancelled before its write closes TCP and releases the remote held key",
            new { Error = error?.GetType().Name, client.Enabled, mock.Held, mock.Releases });
    }

    static async Task CancelDisableWaitingForSlot()
    {
        var mock = new MockInjector();
        using var server = new RemoteInputServer(mock);
        await using var relay = new AckRelay(server.Endpoint);
        using var client = await RemoteInputClient.ConnectAsync(relay.Endpoint).WaitAsync(Deadline);
        await client.SetEnabledAsync(true).WaitAsync(Deadline);
        List<Task<RemoteInputResult>> pending = new();
        for (var i = 0; i < 64; i++) pending.Add(await client.QueueAsync(Key).WaitAsync(Deadline));
        await relay.Withheld.Task.WaitAsync(Deadline);
        await Until(() => mock.Count == 64);
        using var cancellation = new CancellationTokenSource();
        var disabling = client.SetEnabledAsync(false, cancellation.Token);
        await Task.Delay(30);
        Check(!disabling.IsCompleted && !client.Enabled && pending.All(task => !task.IsCompleted) && mock.Held == 64,
            "Disable waits for the exhausted ACK window while the production receiver still holds input");
        cancellation.Cancel();
        var error = await Fault(disabling).WaitAsync(Deadline);
        var pendingErrors = await Task.WhenAll(pending.Select(Fault)).WaitAsync(Deadline);
        await Until(() => mock.Held == 0);
        await relay.Completion.WaitAsync(Deadline);
        Check(error is OperationCanceledException && pendingErrors.All(value => value is OperationCanceledException) &&
            !client.Enabled && mock.Held == 0 && mock.Releases > 0,
            "Cancelling disable while waiting for a slot faults all in-flight callers and releases remote input",
            new { Error = error?.GetType().Name, Pending = pendingErrors.Length, client.Enabled, mock.Held, mock.Releases });
    }

    static async Task DelayedEnableCannotReviveDisable()
    {
        using var peer = await ScriptedPeer.Create();
        var enabling = peer.Client.SetEnabledAsync(true);
        var enable = await peer.Read();
        var disabling = peer.Client.SetEnabledAsync(false);
        var disable = await peer.Read();
        Check(enable.Enable == true && disable.Enable == false && !peer.Client.Enabled,
            "Disable follows the pending enable on the wire and immediately keeps forwarding off");
        await peer.Reply(enable);
        Check((await enabling.WaitAsync(Deadline)).Accepted && !peer.Client.Enabled && !disabling.IsCompleted,
            "A delayed enable ACK cannot revive forwarding after a newer disable request");
        await peer.Reply(disable);
        Check((await disabling.WaitAsync(Deadline)).Accepted && !peer.Client.Enabled,
            "The final disable ACK preserves disabled state");
    }

    static async Task RejectedDisableClosesConnection()
    {
        var mock = new MockInjector();
        using var server = new RemoteInputServer(mock);
        using var client = await RemoteInputClient.ConnectAsync(server.Endpoint).WaitAsync(Deadline);
        await client.SetEnabledAsync(true).WaitAsync(Deadline);
        await client.SendAsync(Key).WaitAsync(Deadline);
        Interlocked.Exchange(ref mock.ReleaseRejections, 1);
        var result = await client.SetEnabledAsync(false).WaitAsync(Deadline);
        await Until(() => mock.Held == 0 && mock.Releases >= 2);
        Check(!result.Accepted && result.Message == "deliberate release rejection" && !client.Enabled && mock.Held == 0,
            "A rejected disable result is preserved while closing TCP retries remote ReleaseAll",
            new { Result = result, client.Enabled, mock.Held, mock.Releases });
    }

    static async Task WindowAndTimeout()
    {
        using var peer = await ScriptedPeer.Create();
        await peer.Enable();
        var watch = Stopwatch.StartNew();
        List<Task<RemoteInputResult>> replies = new();
        for (var i = 0; i < 64; i++) replies.Add(await peer.Client.QueueAsync(Key).WaitAsync(Deadline));
        var requests = new List<InputRequest>();
        for (var i = 0; i < 64; i++) requests.Add(await peer.Read());
        var sixtyFifth = peer.Client.QueueAsync(Key);
        await Task.Delay(30);
        Check(!sixtyFifth.IsCompleted && peer.Available == 0 && replies.All(x => !x.IsCompleted),
            "Exactly 64 unacknowledged requests impose backpressure before request 65 reaches TCP");
        await peer.Reply(requests[0]);
        var extra = await sixtyFifth.WaitAsync(Deadline);
        var released = await peer.Read();
        Check(released.Sequence == 66 && (await replies[0].WaitAsync(Deadline)).Accepted && !extra.IsCompleted,
            "One ACK releases exactly one window slot and preserves sequence");
        var outstanding = replies.Skip(1).Append(extra).ToArray();
        var errors = await Task.WhenAll(outstanding.Select(Fault)).WaitAsync(TimeSpan.FromSeconds(6));
        Check(errors.All(x => x is TimeoutException) && !peer.Client.Enabled && outstanding.All(x => x.IsFaulted),
            "One missing ACK timeout faults every remaining request and closes forwarding", new { Outstanding = errors.Length, Seconds = watch.Elapsed.TotalSeconds });
        Check(watch.Elapsed.TotalSeconds is >= 4.5 and < 7, "The configured acknowledgement deadline is approximately five seconds; tested once", watch.Elapsed.TotalSeconds);
    }

    static async Task<Exception?> Fault(Task task)
    {
        try { await task; return null; }
        catch (Exception error) { Console.Error.WriteLine("Expected test failure: " + error.GetType().Name + ": " + error.Message); return error; }
    }

    static async Task Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > Deadline) throw new TimeoutException("Test condition did not complete within two seconds.");
            await Task.Delay(5);
        }
    }

    static void Check(bool condition, string name, object? detail = null)
    {
        checks.Add(new { Passed = condition, Name = name, Detail = detail });
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition) throw new InvalidOperationException(name);
    }

    sealed class ScriptedPeer : IDisposable
    {
        readonly TcpListener listener;
        readonly TcpClient peer;
        public RemoteInputClient Client { get; }
        public int Available => peer.Available;

        ScriptedPeer(TcpListener listener, TcpClient peer, RemoteInputClient client) { this.listener = listener; this.peer = peer; Client = client; }

        public static async Task<ScriptedPeer> Create()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var connect = RemoteInputClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            try
            {
                var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline); peer.NoDelay = true;
                return new(listener, peer, await connect.WaitAsync(Deadline));
            }
            catch { listener.Stop(); throw; }
        }

        public Task<InputRequest> Read() => InputProtocol.ReadAsync<InputRequest>(peer.GetStream(), CancellationToken.None).WaitAsync(Deadline);
        public Task Reply(InputRequest request, bool accepted = true, string message = "accepted") =>
            InputProtocol.WriteAsync(peer.GetStream(), new InputReply(request.Sequence, accepted, message), CancellationToken.None).WaitAsync(Deadline);

        public async Task Enable()
        {
            var enabled = Client.SetEnabledAsync(true);
            var request = await Read();
            if (request.Sequence != 1 || request.Enable != true || request.Input != null) throw new InvalidDataException("Invalid initial enable request.");
            await Reply(request);
            if (!(await enabled.WaitAsync(Deadline)).Accepted) throw new InvalidOperationException("Enable rejected.");
        }

        public void ClosePeer() => peer.Dispose();
        public void Dispose() { Client.Dispose(); peer.Dispose(); listener.Stop(); }
    }

    sealed class MockInjector : IRemoteInputInjector
    {
        readonly ConcurrentQueue<RemoteInputEvent> events = new();
        int held, releases;
        public int ReleaseRejections;
        public int Held => Volatile.Read(ref held);
        public int Releases => Volatile.Read(ref releases);
        public int Count => events.Count;
        public RemoteInputResult Inject(RemoteInputEvent input)
        {
            events.Enqueue(input);
            if (input.Kind is RemoteInputKind.KeyDown or RemoteInputKind.MouseDown) Interlocked.Increment(ref held);
            if (input.Kind is RemoteInputKind.KeyUp or RemoteInputKind.MouseUp) Interlocked.Decrement(ref held);
            return input.Kind == RemoteInputKind.ReleaseAll ? ReleaseAll() : new(true, "mock accepted");
        }
        public RemoteInputResult ReleaseAll()
        {
            Interlocked.Increment(ref releases);
            if (Interlocked.Exchange(ref ReleaseRejections, 0) != 0) return new(false, "deliberate release rejection");
            Interlocked.Exchange(ref held, 0);
            return new(true, "mock released");
        }
    }

    sealed class AckRelay : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly IPEndPoint destination;
        public readonly TaskCompletionSource Withheld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; }
        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

        public AckRelay(IPEndPoint destination) { this.destination = destination; listener.Start(1); Completion = Run(); }

        async Task Run()
        {
            using var downstream = await listener.AcceptTcpClientAsync(stop.Token);
            using var upstream = new TcpClient { NoDelay = true };
            downstream.NoDelay = true;
            await upstream.ConnectAsync(destination, stop.Token);
            var requests = Requests(); var replies = Replies();
            try { await Task.WhenAny(requests, replies); }
            finally
            {
                stop.Cancel(); downstream.Close(); upstream.Close();
                await Task.WhenAll(requests, replies);
            }

            async Task Requests()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var request = await InputProtocol.ReadAsync<InputRequest>(downstream.GetStream(), stop.Token);
                        await InputProtocol.WriteAsync(upstream.GetStream(), request, stop.Token);
                    }
                }
                catch (Exception error) when (error is EndOfStreamException || stop.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException)
                { Console.Error.WriteLine("ACK relay request channel closed: " + error.GetType().Name); }
            }

            async Task Replies()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var reply = await InputProtocol.ReadAsync<InputReply>(upstream.GetStream(), stop.Token);
                        if (reply.Sequence == 1) await InputProtocol.WriteAsync(downstream.GetStream(), reply, stop.Token);
                        else Withheld.TrySetResult();
                    }
                }
                catch (Exception error) when (error is EndOfStreamException || stop.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException)
                { Console.Error.WriteLine("ACK relay reply channel closed: " + error.GetType().Name); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await Completion.WaitAsync(Deadline); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("ACK relay stopped."); }
            stop.Dispose();
        }
    }
}
