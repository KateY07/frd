using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

sealed class RemoteUdpSession : IAsyncDisposable
{
    readonly Socket socket;
    readonly byte[] nonce;
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly Task inputWorker;
    readonly Task feedbackWorker;
    readonly AutoResetEvent feedbackReady = new(false);
    byte[]? pendingFeedback;
    readonly Action<Exception>? failed;
    readonly AutoResetEvent inputReady = new(false);
    readonly LinkedList<QueuedInput> pendingInput = new();
    internal const int MaximumInputAgeMs = 250;
    readonly record struct QueuedInput(RemoteInputEvent Event, long Received, long Sequence = 0);
    internal readonly record struct InputTiming(long Sequence, long Received, long Executing, long Completed);
    internal event Action<InputTiming>? InputMeasured;
    readonly TaskCompletionSource<IPEndPoint> registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate = new();
    IPEndPoint? peer;
    UdpVideoSender? sender;
    InputBinding? inputBinding;
    long latestInput;
    long appliedInputs;
    int failureReported;

    sealed class InputBinding(IRemoteInputInjector injector, Func<bool> enabled, Action<Exception>? failed, byte[] macKey)
    {
        public readonly byte[] MacKey = macKey;
        public readonly object Execution = new();
        public readonly IRemoteInputInjector Injector = injector;
        public readonly Func<bool> Enabled = enabled;
        public readonly byte[] Nonce = RandomNumberGenerator.GetBytes(16);
        public readonly Action<Exception>? Failed = failed;
        public bool Active = enabled();
        public Exception? Fault;
        public long Revision;
    }

    internal long AppliedInputs => Interlocked.Read(ref appliedInputs);
    internal int PendingInputs { get { lock (gate) return pendingInput.Count; } }

    /// <summary>True while someone uses the host's own keyboard or mouse; remote events other than releases are then dropped.</summary>
    public Func<bool>? LocalBusy { get; set; }
    long droppedForLocal;
    internal long DroppedForLocalInput => Interlocked.Read(ref droppedForLocal);

    public RemoteUdpSession(Socket socket, string session, CancellationToken token, Action<Exception>? failed = null)
    {
        this.socket = socket;
        nonce = Convert.FromHexString(session);
        this.failed = failed;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        worker = Task.Factory.StartNew(Receive, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        inputWorker = Task.Factory.StartNew(DispatchInput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        feedbackWorker = Task.Factory.StartNew(DispatchFeedback, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<IPEndPoint> WaitForRegistrationAsync(CancellationToken token) =>
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

    public void AttachSender(UdpVideoSender value)
    {
        lock (gate) sender = value;
    }

    public byte[] AttachInput(IRemoteInputInjector value, Func<bool> enabled, byte[] macKey, Action<Exception>? failed = null)
    {
        lock (gate)
        {
            if (inputBinding != null) throw new InvalidOperationException("Input dispatcher is already attached.");
            inputBinding = new(value, enabled, failed, macKey); pendingInput.Clear(); latestInput = 0;
            return inputBinding.Nonce.ToArray();
        }
    }

    public void DetachInput(IRemoteInputInjector value)
    {
        InputBinding? binding;
        lock (gate)
        {
            binding = inputBinding;
            if (binding == null || !ReferenceEquals(binding.Injector, value)) return;
            inputBinding = null; pendingInput.Clear(); latestInput = 0;
        }
        // Wait only on the lifecycle caller, never on the UDP receiver, before disposing the injector.
        lock (binding.Execution)
        {
            try
            {
                var result = binding.Injector.ReleaseAll();
                if (!result.Accepted) Console.Error.WriteLine("[Input] Detach release failed: " + result.Message);
            }
            catch (Exception error) { Console.Error.WriteLine("[Input] Detach release threw: " + error); }
        }
    }

    public void UpdateInputEnabled(IRemoteInputInjector value, bool enabled, Action update)
    {
        InputBinding binding;
        lock (gate)
            binding = inputBinding is { } current && ReferenceEquals(current.Injector, value) ? current :
                throw new InvalidOperationException("Input dispatcher is no longer attached.");
        lock (binding.Execution)
        {
            lock (gate)
            {
                if (!ReferenceEquals(inputBinding, binding)) throw new InvalidOperationException("Input dispatcher was detached.");
                if (enabled && binding.Fault != null) throw new IOException("Input channel failed; reconnect input before enabling.", binding.Fault);
                if (binding.Active == enabled && binding.Enabled() == enabled) return;
                pendingInput.Clear(); binding.Revision++; binding.Active = enabled;
            }
            update();
        }
    }

    void DispatchInput()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                InputBinding? binding;
                QueuedInput? input;
                long revision;
                lock (gate)
                {
                    binding = inputBinding;
                    revision = binding?.Revision ?? 0;
                    input = pendingInput.First?.Value;
                    if (input != null) pendingInput.RemoveFirst();
                }
                if (binding == null || input == null) { inputReady.WaitOne(20); continue; }
                try
                {
                    lock (binding.Execution)
                    {
                        lock (gate)
                            if (!ReferenceEquals(inputBinding, binding) || revision != binding.Revision || binding.Fault != null ||
                                !binding.Active || !binding.Enabled() || stop.IsCancellationRequested) continue;
                        if (Stopwatch.GetElapsedTime(input.Value.Received).TotalMilliseconds > MaximumInputAgeMs)
                        {
                            lock (gate) pendingInput.Clear();
                            Console.Error.WriteLine("[Input] Local backlog exceeded 250 ms; discarded pending events and releasing held input. Channel remains active.");
                            var released = binding.Injector.ReleaseAll();
                            if (!released.Accepted) Console.Error.WriteLine("[Input] Backlog release rejected: " + released.Message);
                            continue;
                        }
                        var executing = Stopwatch.GetTimestamp();
                        var result = binding.Injector.Inject(input.Value.Event);
                        var completed = Stopwatch.GetTimestamp();
                        if (result.Accepted) Interlocked.Increment(ref appliedInputs);
                        else Console.Error.WriteLine("[Input] UDP event rejected: " + result.Message);
                        try { InputMeasured?.Invoke(new(input.Value.Sequence, input.Value.Received, executing, completed)); }
                        catch (Exception diagnosticError)
                        {
                            InputMeasured = null;
                            Console.Error.WriteLine("[Input] Timing observer disabled after failure: " + diagnosticError);
                        }
                    }
                }
                catch (Exception error) { FailInput(binding, error); }
            }
        }
        catch (Exception error) { Fail(error); }
    }

    void FailInput(InputBinding binding, Exception error)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inputBinding, binding) || binding.Fault != null) return;
            binding.Fault = error; binding.Active = false; binding.Revision++; pendingInput.Clear();
        }
        Console.Error.WriteLine("[Input] Channel stopped; video and UDP feedback remain active: " + error);
        lock (binding.Execution)
        {
            lock (gate) if (!ReferenceEquals(inputBinding, binding)) return;
            try
            {
                var released = binding.Injector.ReleaseAll();
                if (!released.Accepted) Console.Error.WriteLine("[Input] Failure release rejected: " + released.Message);
            }
            catch (Exception releaseError) { Console.Error.WriteLine("[Input] Failure release threw: " + releaseError); }
        }
        try { binding.Failed?.Invoke(error); }
        catch (Exception callbackError) { Console.Error.WriteLine("[Input] Failure notification failed: " + callbackError); }
    }

    void DispatchFeedback()
    {
        while (!stop.IsCancellationRequested)
        {
            var packet = Interlocked.Exchange(ref pendingFeedback, null);
            if (packet == null) { feedbackReady.WaitOne(20); continue; }
            UdpVideoSender? activeSender;
            IPEndPoint? activePeer;
            lock (gate) { activeSender = sender; activePeer = peer; }
            if (activeSender == null || activePeer == null) continue;
            try { activeSender.ProcessFeedback(packet, activePeer); }
            catch (Exception error) { Console.Error.WriteLine("[UDP] Feedback processing failed; input receiver remains active: " + error); }
        }
    }

    void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref failureReported, 1) != 0) return;
        Console.Error.WriteLine("[UDP session] " + error);
        registered.TrySetException(error);
        stop.Cancel(); inputReady.Set(); feedbackReady.Set();
        failed?.Invoke(error);
    }

    void Receive()
    {
        var packet = new byte[65536];
        var any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                EndPoint source = new IPEndPoint(any, 0);
                int length;
                try
                {
                    if (!socket.Poll(20_000, SelectMode.SelectRead)) continue;
                    length = socket.ReceiveFrom(packet, ref source);
                }
                catch (SocketException error) when (!stop.IsCancellationRequested && VideoDatagram.Recoverable(error, "Session receive"))
                { continue; }
                var received = Stopwatch.GetTimestamp();
                if (length > VideoDatagram.MaxSize) continue;
                var endpoint = (IPEndPoint)source;
                var bytes = packet.AsSpan(0, length);
                if (bytes.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VideoDatagram.Magic && bytes[4] == 3 &&
                    CryptographicOperations.FixedTimeEquals(bytes[8..24], nonce))
                {
                    endpoint = new(FrdNetwork.Canonical(endpoint.Address), endpoint.Port);
                    lock (gate)
                    {
                        if (peer != null && !FrdNetwork.SameEndpoint(peer, endpoint)) continue;
                        peer = endpoint;
                    }
                    try { socket.SendTo(VideoDatagram.Registration(nonce, 4), endpoint); }
                    catch (SocketException error) when (!stop.IsCancellationRequested && VideoDatagram.Recoverable(error, "Registration reply"))
                    { continue; }
                    registered.TrySetResult(endpoint);
                    continue;
                }
                IPEndPoint? activePeer;
                lock (gate) activePeer = peer;
                if (activePeer == null || !FrdNetwork.SameEndpoint(activePeer, endpoint)) continue;
                if (bytes.Length == VideoDatagram.FeedbackSize && bytes[4] == 2 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VideoDatagram.Magic)
                {
                    // Feedback carries cumulative counters; keep only the latest while statistics are busy.
                    Interlocked.Exchange(ref pendingFeedback, bytes.ToArray());
                    feedbackReady.Set();
                    continue;
                }
                lock (gate)
                {
                    if (inputBinding == null || inputBinding.Fault != null || !inputBinding.Active || !inputBinding.Enabled() ||
                        !UdpInputProtocol.TryRead(bytes, inputBinding.Nonce, inputBinding.MacKey, out var sequence, out var input) || sequence <= latestInput) continue;
                    latestInput = sequence;
                    // Releases always pass, otherwise a key or button pressed just before the local user took over would stay stuck down.
                    if (input!.Kind is not (RemoteInputKind.MouseUp or RemoteInputKind.KeyUp or RemoteInputKind.ReleaseAll) && LocalBusy?.Invoke() == true)
                    {
                        Interlocked.Increment(ref droppedForLocal);
                        continue;
                    }
                    var queued = new QueuedInput(input!, received, sequence);
                    if (input!.Kind == RemoteInputKind.MouseMove && pendingInput.Last?.Value.Event.Kind == RemoteInputKind.MouseMove)
                        pendingInput.Last.Value = queued;
                    else
                    {
                        if (pendingInput.Count >= 128)
                        {
                            pendingInput.Clear();
                            pendingInput.AddLast(new QueuedInput(new(RemoteInputKind.ReleaseAll), Stopwatch.GetTimestamp()));
                            Console.Error.WriteLine("[Input] UDP input queue saturated; discarded pending input and scheduled key/button release.");
                        }
                        pendingInput.AddLast(queued);
                    }
                }
                inputReady.Set();
            }
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote UDP session socket stopped."); }
        catch (SocketException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Remote UDP session socket stopped."); }
        catch (Exception error) { Fail(error); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); inputReady.Set(); feedbackReady.Set();
        try { await Task.WhenAll(worker, inputWorker, feedbackWorker); }
        finally { inputReady.Dispose(); feedbackReady.Dispose(); stop.Dispose(); }
    }
}
