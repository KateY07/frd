using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

public sealed record CodecPreset
{
    public string Label { get; init; } = "";
    public string EncoderArguments { get; init; } = "";
    public string DecoderArguments { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? UnavailableReason { get; init; }
}

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 3;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public double TransmissionScale { get; init; } = 1;
    public int FramesPerSecond { get; init; } = 30;
    public string InitialPreset { get; init; } = "h264";
    public int InitialBitrateKbps { get; init; } = 1000;
    public double MaximumBitrateMbps { get; init; } = 100;
    [System.Text.Json.Serialization.JsonIgnore]
    public int MaximumBitrateKbps => checked((int)Math.Round(MaximumBitrateMbps * 1000));
    public string LibraryDirectory { get; init; } = "ffmpeg";
    public NetworkSimulation Simulation { get; init; } = new();
    public Dictionary<string, CodecPreset> Presets { get; init; } = new();

    public static AppConfiguration Load(string path)
    {
        var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty codec configuration.");
        if (config.SchemaVersion != 3 || config.Width < 64 || config.Height < 64 || config.Width > 7680 || config.Height > 4320 || config.Width % 2 != 0 || config.Height % 2 != 0)
            throw new InvalidDataException("Expected schemaVersion=3 and even video dimensions between 64 and 7680×4320.");
        if (!double.IsFinite(config.MaximumBitrateMbps) || config.MaximumBitrateMbps is < .1 or > 1000)
            throw new InvalidDataException("maximumBitrateMbps must be between 0.1 and 1000.");
        if (config.FramesPerSecond is < 1 or > 30 || config.InitialBitrateKbps < 100 || config.InitialBitrateKbps > config.MaximumBitrateKbps || !config.Presets.ContainsKey(config.InitialPreset))
            throw new InvalidDataException("Invalid FPS, initial bitrate, or initial preset.");
        TransmissionGeometry.ValidateScale(config.TransmissionScale);
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, config.LibraryDirectory));
        FfmpegRuntime.Initialize(directory);
        return config;
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

public sealed record SessionStatus(string ActivePreset, int AppliedLimitKbps, double SentMbps, double ReceivedMbps,
    double EstimatedMbps, double DelayTrendMs, double LossRate, string Message)
{
    public double CaptureToRenderMs { get; init; }
    public double MeanCaptureToRenderMs { get; init; }
    public double RenderedFps { get; init; }
    public double DecodedFps { get; init; }
    public bool HasRenderTiming { get; init; }
    public bool HasRecentRenderTiming { get; init; }
    public int RenderTimingSamples { get; init; }
    public double RenderTimingWindowSeconds => 1;
    public double CaptureMs { get; init; }
    public double EncodeMs { get; init; }
    public double TransferMs { get; init; }
    public double DecodeMs { get; init; }
    public double RenderMs { get; init; }
    public double MeanCaptureMs { get; init; }
    public double MeanEncodeMs { get; init; }
    public double MeanTransferMs { get; init; }
    public double MeanDecodeMs { get; init; }
    public double MeanRenderMs { get; init; }
    public bool PacketDiagnosticsEnabled { get; init; }
    public double DiagnosticMbps { get; init; }
    public TransferTimings? TransferDetails { get; init; }
    public string TimingDescription { get; init; } = "同机单调时钟";
    public int SendBudgetKbps { get; init; }
    public int BurstBudgetWireBytes { get; init; }
    public double SimulatedCapacityMbps { get; init; }
}
public sealed record TransferTimings(int PayloadBytes, int Packets, double SenderMs, double PlannedWaitMs,
    double WakeupOverrunMs, double LastSendToReassemblyMs, double ReceiveQueueMs);
public sealed record ControlResult(bool Success, string Message, int Generation)
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double TransmissionScale { get; init; } = 1;
}
sealed record ControlCommand(string Kind, int Generation, string PresetId, int LimitKbps, bool DiagnosticsEnabled = false, double? Scale = null);
sealed record PendingControl(ControlCommand Command, TaskCompletionSource<ControlResult> Completion);

public sealed partial class DemoSession : IDisposable
{
    AppConfiguration config;
    readonly Func<byte[]> capture;
    readonly Action<int, int>? resizeCapture;
    readonly int sourceWidth, sourceHeight;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim controlGate = new(1, 1);
    readonly Channel<PendingControl> commands = Channel.CreateUnbounded<PendingControl>(new() { SingleReader = true });
    readonly object decoderGate = new();
    readonly object lifecycleGate = new();
    readonly object presentationGate = new();
    readonly ConcurrentDictionary<long, FrameTimeline> frameClocks = new();
    readonly Queue<(long Tick, double Delay, StageTimings Stages)> presentations = new();
    readonly Queue<long> recentDecoded = new();
    double lastPresentedMs, presentationTotalMs;
    StageTimings lastStages = new(), totalStages = new();
    long presentationCount;
    TransferTimings? lastTransfer;
    readonly Queue<TransferTimings> transferSamples = new();
    readonly Dictionary<int, FfmpegDecoder> decoders = new();
    readonly Dictionary<int, string> presetNames = new();
    readonly ConcurrentQueue<object> changes = new();
    readonly TcpListener controlServer = new(IPAddress.Loopback, 0);
    UdpVideoReceiver? receiver;
    UdpFrameDiagnosticsReceiver? diagnosticsReceiver;
    UdpVideoSender? sender;
    TcpClient? controlClient;
    StreamReader? replies;
    StreamWriter? requests;
    Task? serverTask, encodeTask, statusTask;
    Task? shutdown;
    string activePreset = "", message = "初始化";
    int requestedGeneration, appliedGeneration, appliedLimit, receiveGeneration, decodedGeneration, renderedGeneration;
    long nextFrameId, nextPacketId, lastReceivedId = -1, encodedFrames, decodedFrames, encodeTicks, captureTicks, decodeTicks, lastRecovery;
    bool needCleanFrame = true, disposed;
    bool packetDiagnosticsEnabled;
    long receivedDiagnosticFrames;
    public event Action<DecodedPixels>? FrameReceived;
    public event Action<SessionStatus>? StatusChanged;
    public event Action<Exception>? Failed;
    int failureReported;
    public int AppliedGeneration => Volatile.Read(ref appliedGeneration);
    public int DecodedGeneration => Volatile.Read(ref decodedGeneration);
    public int RenderedGeneration => Volatile.Read(ref renderedGeneration);

    sealed class FrameTimeline(long captureTick, long capturedTick, long encodedTick, int generation)
    {
        public long CaptureTick = captureTick, CapturedTick = capturedTick, EncodedTick = encodedTick;
        public readonly int Generation = generation;
        public bool HasRemoteDiagnostic;
        public long ReceivedTick, DecodedTick;
        public long ReassembledTick;
        public VideoSendTiming? Sending;
    }

    readonly record struct StageTimings(double Capture = 0, double Encode = 0, double Transfer = 0, double Decode = 0, double Render = 0)
    {
        public StageTimings Add(StageTimings other) => new(Capture + other.Capture, Encode + other.Encode,
            Transfer + other.Transfer, Decode + other.Decode, Render + other.Render);
        public StageTimings Divide(long count) => new(Capture / Math.Max(1, count), Encode / Math.Max(1, count),
            Transfer / Math.Max(1, count), Decode / Math.Max(1, count), Render / Math.Max(1, count));
    }

    public DemoSession(AppConfiguration config, Func<byte[]> capture, Action<int, int>? resizeCapture = null, int sourceWidth = 0, int sourceHeight = 0)
    {
        this.config = config; this.capture = capture; this.resizeCapture = resizeCapture;
        this.sourceWidth = sourceWidth; this.sourceHeight = sourceHeight;
    }

    public async Task StartAsync()
    {
        if (remoteOptions != null) { await StartRemoteAsync(); return; }
        receiver = new();
        receiver.Failed += error => ReportError("UDP 接收", error);
        receiver.VideoReceived += ReceiveVideo;
        diagnosticsReceiver = new();
        diagnosticsReceiver.Failed += error => ReportError("UDP 诊断", error);
        diagnosticsReceiver.Received += ReceiveDiagnostic;
        sender = new(new(IPAddress.Loopback, receiver.Port), config.Simulation);
        sender.Failed += error => ReportError("UDP 发送/反馈", error);
        sender.FrameSending += (generation, id, timing) =>
        {
            if (frameClocks.TryGetValue(id, out var clock) && clock.Generation == generation)
                Volatile.Write(ref clock.Sending, timing);
        };
        sender.SetEncoderBitrateKbps(config.InitialBitrateKbps);
        controlServer.Start();
        serverTask = ServeControlAsync();
        encodeTask = Task.Run(EncodeLoop);
        statusTask = Task.Run(StatusLoopAsync);
        controlClient = new();
        await controlClient.ConnectAsync((IPEndPoint)controlServer.LocalEndpoint, stop.Token);
        requests = new(controlClient.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        replies = new(controlClient.GetStream(), Encoding.UTF8, leaveOpen: true);
        var result = await ApplyAsync(config.InitialPreset, config.InitialBitrateKbps);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    public async Task<ControlResult> ApplyAsync(string presetId, int limitKbps, double? transmissionScale = null)
    {
        if (transmissionScale is { } scale && !TransmissionGeometry.IsValidScale(scale)) return new(false, "传输比例仅支持 1×、0.75×、0.5×。", AppliedGeneration);
        if (limitKbps < 100 || limitKbps > config.MaximumBitrateKbps) return new(false, $"码率范围为 0.1–{config.MaximumBitrateMbps:0.###} Mbps。", AppliedGeneration);
        if (!config.Presets.TryGetValue(presetId, out var preset)) return new(false, "JSON 中没有此预设。", AppliedGeneration);
        if (!preset.Enabled) return new(false, preset.UnavailableReason ?? "此预设不可用。", AppliedGeneration);
        await controlGate.WaitAsync(stop.Token);
        var generation = Interlocked.Increment(ref requestedGeneration);
        try
        {
            FfmpegDecoder decoder;
            try { decoder = new(preset.DecoderArguments); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return new(false, ex.Message, AppliedGeneration); }
            lock (decoderGate) { decoders[generation] = decoder; presetNames[generation] = presetId; }
            var result = await ExchangeAsync(new("apply", generation, presetId, limitKbps, Scale: transmissionScale));
            if (!result.Success || result.Generation != generation)
            {
                lock (decoderGate) { decoders.Remove(generation); presetNames.Remove(generation); decoder.Dispose(); }
            }
            changes.Enqueue(new { Preset = presetId, LimitKbps = limitKbps, Result = result });
            if (result.Success && result.Width > 0 && result.Height > 0)
                config = config with { Width = result.Width, Height = result.Height, TransmissionScale = result.TransmissionScale };
            return result;
        }
        finally { controlGate.Release(); }
    }

    public async Task<ControlResult> SetPacketDiagnosticsAsync(bool enabled)
    {
        await controlGate.WaitAsync(stop.Token);
        try
        {
            if (enabled && remote != null && welcome != null)
                diagnosticsReceiver?.SetExpectedSource(new(remote.Endpoint.Address, welcome.SenderPort));
            var result = await ExchangeAsync(new("diagnostics", AppliedGeneration, activePreset, appliedLimit, enabled));
            changes.Enqueue(new { Kind = "Independent diagnostic UDP", Enabled = enabled, Result = result });
            return result;
        }
        finally { controlGate.Release(); }
    }

    async Task<ControlResult> ExchangeAsync(ControlCommand command)
    {
        if (remote != null) return await RemoteControlAsync(command);
        if (requests == null || replies == null) throw new InvalidOperationException("Control channel not connected.");
        await requests.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), stop.Token);
        var line = await replies.ReadLineAsync(stop.Token) ?? throw new IOException("Sender control channel closed.");
        return JsonSerializer.Deserialize<ControlResult>(line) ?? throw new IOException("Invalid control response.");
    }

    async Task ServeControlAsync()
    {
        try
        {
            using var peer = await controlServer.AcceptTcpClientAsync(stop.Token);
            using var input = new StreamReader(peer.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var output = new StreamWriter(peer.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (!stop.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(stop.Token);
                if (line == null) break;
                ControlResult result;
                try
                {
                    var command = JsonSerializer.Deserialize<ControlCommand>(line) ?? throw new InvalidDataException("Empty control command.");
                    if (command.Kind != "apply" && command.Kind != "keyframe" && command.Kind != "diagnostics") throw new InvalidDataException("Unknown control command.");
                    var pending = new PendingControl(command, new(TaskCreationOptions.RunContinuationsAsynchronously));
                    await commands.Writer.WriteAsync(pending, stop.Token);
                    result = await pending.Completion.Task.WaitAsync(stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine(ex); result = new(false, ex.Message, AppliedGeneration); }
                await output.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Control server stopped."); }
        catch (Exception ex) { ReportError("控制通道", ex); }
    }

    void EncodeLoop()
    {
        FfmpegEncoder? encoder = null;
        var force = true;
        try
        {
            var next = Stopwatch.GetTimestamp();
            var inputClocks = new Dictionary<long, (long Started, long Captured)>();
            while (!stop.IsCancellationRequested)
            {
                while (commands.Reader.TryRead(out var pending))
                {
                    try
                    {
                        var cmd = pending.Command;
                        if (cmd.Kind == "keyframe") { force = true; pending.Completion.TrySetResult(new(true, "已请求恢复帧", AppliedGeneration)); continue; }
                        if (cmd.Kind == "diagnostics")
                        {
                            Volatile.Write(ref packetDiagnosticsEnabled, cmd.DiagnosticsEnabled);
                            pending.Completion.TrySetResult(new(true, cmd.DiagnosticsEnabled ? "已开启独立诊断 UDP 包；视频包结构不变" : "已关闭诊断包；本机计时继续显示", AppliedGeneration));
                            continue;
                        }
                        if (!config.Presets.TryGetValue(cmd.PresetId, out var preset) || !preset.Enabled) throw new InvalidDataException("Preset unavailable on sender.");
                        if (cmd.LimitKbps < 100 || cmd.LimitKbps > config.MaximumBitrateKbps || cmd.Generation <= AppliedGeneration) throw new InvalidDataException("Invalid control revision or bitrate.");
                        var scale = cmd.Scale ?? config.TransmissionScale;
                        TransmissionGeometry.ValidateScale(scale);
                        var dimensions = resizeCapture == null ? (Width: config.Width, Height: config.Height) : TransmissionGeometry.Dimensions(sourceWidth, sourceHeight, scale);
                        if (resizeCapture == null && cmd.Scale is { } requestedScale && requestedScale != config.TransmissionScale)
                            throw new InvalidOperationException("This capture source cannot change transmission scale.");
                        var resize = dimensions.Width != config.Width || dimensions.Height != config.Height;
                        ControlResult Applied(string text, int revision) => new(true, text, revision)
                            { Width = dimensions.Width, Height = dimensions.Height, TransmissionScale = scale };
                        if (!resize && encoder != null && activePreset == cmd.PresetId && encoder.SetBitrate(cmd.LimitKbps))
                        {
                            sender!.SetEncoderBitrateKbps(cmd.LimitKbps); Volatile.Write(ref appliedLimit, cmd.LimitKbps);
                            pending.Completion.TrySetResult(Applied("发送端已在原会话更新码率上限", AppliedGeneration));
                            continue;
                        }
                        var replacement = new FfmpegEncoder(preset.EncoderArguments, dimensions.Width, dimensions.Height, config.FramesPerSecond, cmd.LimitKbps);
                        try { if (resize) resizeCapture!(dimensions.Width, dimensions.Height); }
                        catch { replacement.Dispose(); throw; }
                        encoder?.Dispose(); encoder = replacement;
                        config = config with { Width = dimensions.Width, Height = dimensions.Height, TransmissionScale = scale };
                        sender!.SetEncoderBitrateKbps(cmd.LimitKbps);
                        activePreset = cmd.PresetId; Volatile.Write(ref appliedLimit, cmd.LimitKbps); Volatile.Write(ref appliedGeneration, cmd.Generation);
                        force = true; message = "手动控制；带宽估计仅参考";
                        pending.Completion.TrySetResult(Applied("发送端已应用；等待新预设首帧", cmd.Generation));
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); pending.Completion.TrySetResult(new(false, ex.Message, AppliedGeneration)); }
                }
                if (encoder == null) { stop.Token.WaitHandle.WaitOne(5); continue; }
                var waitMs = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (waitMs > 0 && stop.Token.WaitHandle.WaitOne((int)Math.Ceiling(waitMs))) break;
                var captureStart = Stopwatch.GetTimestamp();
                var pixels = capture();
                var capturedTick = Stopwatch.GetTimestamp();
                Interlocked.Add(ref captureTicks, capturedTick - captureStart);
                var encodeStart = Stopwatch.GetTimestamp();
                var inputId = nextFrameId++; inputClocks[inputId] = (captureStart, capturedTick);
                inputClocks.Remove(inputId - 4096);
                var packets = encoder.Encode(pixels, inputId, force); force = false;
                var encodedTick = Stopwatch.GetTimestamp();
                Interlocked.Add(ref encodeTicks, encodedTick - encodeStart);
                Interlocked.Increment(ref encodedFrames);
                foreach (var packet in packets)
                {
                    var packetId = nextPacketId++;
                    if (inputClocks.TryGetValue(packet.Pts, out var source))
                    {
                        frameClocks[packetId] = new(source.Started, source.Captured, encodedTick, AppliedGeneration);
                        if (Volatile.Read(ref packetDiagnosticsEnabled) && diagnosticDestination == null)
                            sender!.SendDiagnostic(FrameDiagnosticProtocol.Encode(new(AppliedGeneration, packetId, source.Started,
                                source.Captured, encodedTick, Stopwatch.Frequency)), diagnosticDestination ?? new(IPAddress.Loopback, diagnosticsReceiver!.Port), stop.Token);
                    }
                    frameClocks.TryRemove(packetId - 4096, out _);
                    sender!.Send(new(AppliedGeneration, packetId, packet.KeyFrame, packet.Data), stop.Token);
                    if (diagnosticDestination != null && Volatile.Read(ref packetDiagnosticsEnabled) &&
                        frameClocks.TryGetValue(packetId, out var sentClock) && Volatile.Read(ref sentClock.Sending) is { } sending)
                        sender.SendDiagnostic(FrameDiagnosticProtocol.Encode(new(AppliedGeneration, packetId, sentClock.CaptureTick,
                            sentClock.CapturedTick, sentClock.EncodedTick, Stopwatch.Frequency) { Sending = sending }), diagnosticDestination, stop.Token);
                }
                next = Math.Max(next + Stopwatch.Frequency / config.FramesPerSecond, Stopwatch.GetTimestamp());
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Encoder loop stopped."); }
        catch (Exception ex) { ReportError("捕获/编码/发送", ex); stop.Cancel(); }
        finally
        {
            encoder?.Dispose();
            while (commands.Reader.TryRead(out var command)) command.Completion.TrySetCanceled();
        }
    }

    void ReceiveVideo(ReceivedVideo video)
    {
        var receivedTick = Stopwatch.GetTimestamp();
        lock (decoderGate)
        {
            if (video.Generation < receiveGeneration || !decoders.TryGetValue(video.Generation, out var decoder)) return;
            if (video.Generation == receiveGeneration && video.FrameId <= lastReceivedId) return;
            if (video.Generation != receiveGeneration) { needCleanFrame = true; lastReceivedId = -1; }
            if (lastReceivedId >= 0 && video.FrameId > lastReceivedId + 1) needCleanFrame = true;
            if (needCleanFrame && !video.KeyFrame) { RequestRecovery(); return; }
            try
            {
                if (remote != null) PrepareRemoteFrame(video, receivedTick);
                if (frameClocks.TryGetValue(video.FrameId, out var receivedClock))
                {
                    receivedClock.ReceivedTick = receivedTick;
                    receivedClock.ReassembledTick = video.ReassembledTick;
                }
                var start = Stopwatch.GetTimestamp();
                var frames = decoder.Decode(video.Data, video.FrameId);
                var decodedTick = Stopwatch.GetTimestamp();
                Interlocked.Add(ref decodeTicks, decodedTick - start);
                needCleanFrame = false; lastReceivedId = video.FrameId; receiveGeneration = video.Generation;
                foreach (var pixels in frames)
                {
                    if (frameClocks.TryGetValue(pixels.Pts, out var decodedClock)) decodedClock.DecodedTick = decodedTick;
                    Interlocked.Increment(ref decodedFrames); Volatile.Write(ref decodedGeneration, video.Generation);
                    lock (presentationGate) { recentDecoded.Enqueue(Stopwatch.GetTimestamp()); TrimPresentationStats(); }
                    FrameReceived?.Invoke(pixels);
                }
                if (frames.Count > 0)
                {
                    foreach (var obsolete in decoders.Keys.Where(x => x < video.Generation).ToArray()) { decoders[obsolete].Dispose(); decoders.Remove(obsolete); presetNames.Remove(obsolete); }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); needCleanFrame = true; RequestRecovery(); }
        }
    }

    void ReceiveDiagnostic(FrameDiagnostic diagnostic)
    {
        Interlocked.Increment(ref receivedDiagnosticFrames);
        if (remote != null) { ReceiveRemoteDiagnostic(diagnostic); return; }
        if (diagnostic.Frequency != Stopwatch.Frequency ||
            !frameClocks.TryGetValue(diagnostic.FrameId, out var local) || local.Generation != diagnostic.Generation) return;
        if (diagnostic.CaptureStarted != local.CaptureTick || diagnostic.CaptureCompleted != local.CapturedTick || diagnostic.EncodeCompleted != local.EncodedTick)
        {
            Console.Error.WriteLine($"[diagnostics] Timestamp mismatch for frame {diagnostic.Generation}/{diagnostic.FrameId}.");
            return;
        }
    }

    void RequestRecovery()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref lastRecovery) < Stopwatch.Frequency / 2) return;
        Interlocked.Exchange(ref lastRecovery, now);
        _ = RecoverAsync();
    }

    async Task RecoverAsync()
    {
        try
        {
            await controlGate.WaitAsync(stop.Token);
            try { await ExchangeAsync(new("keyframe", AppliedGeneration, activePreset, appliedLimit)); }
            finally { controlGate.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Recovery stopped."); }
        catch (Exception ex) { ReportError("请求恢复帧", ex); }
    }

    async Task StatusLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var network = remote != null ? await ReadRemoteStatusAsync() : sender!.Snapshot;
                var presentation = PresentationStatus();
                StatusChanged?.Invoke(new(activePreset, appliedLimit, network.SentMbps, network.ReceivedMbps, network.EstimatedMbps,
                    network.DelayTrendMs, network.LossRate, message + "；" + network.EstimateState)
                {
                    CaptureToRenderMs = presentation.Last, MeanCaptureToRenderMs = presentation.Mean,
                    RenderedFps = remote != null ? RemotePresentedFps() : presentation.Rendered, DecodedFps = presentation.Decoded,
                    CaptureMs = presentation.Stages.Capture, EncodeMs = presentation.Stages.Encode,
                    TransferMs = presentation.Stages.Transfer, DecodeMs = presentation.Stages.Decode, RenderMs = presentation.Stages.Render,
                    MeanCaptureMs = presentation.Averages.Capture, MeanEncodeMs = presentation.Averages.Encode,
                    MeanTransferMs = presentation.Averages.Transfer, MeanDecodeMs = presentation.Averages.Decode,
                    MeanRenderMs = presentation.Averages.Render,
                    PacketDiagnosticsEnabled = Volatile.Read(ref packetDiagnosticsEnabled), DiagnosticMbps = network.DiagnosticMbps,
                    TransferDetails = presentation.Transfer,
                    HasRecentRenderTiming = presentation.Rendered > 0, RenderTimingSamples = presentation.Rendered,
                    HasRenderTiming = Interlocked.Read(ref presentationCount) > 0 && (remote == null || packetDiagnosticsEnabled),
                    TimingDescription = remote != null ? RemoteTimingDescription : "同机单调时钟",
                    SendBudgetKbps = network.SendBudgetKbps, BurstBudgetWireBytes = network.BurstBudgetWireBytes,
                    SimulatedCapacityMbps = network.SimulatedCapacityMbps
                });
                await Task.Delay(200, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Status loop stopped."); }
        catch (Exception ex) { ReportError("状态统计", ex); }
    }

    void ReportError(string stage, Exception ex)
    {
        message = stage + "：" + ex.Message; Console.Error.WriteLine(stage + "\n" + ex);
        stop.Cancel();
        if (Interlocked.Exchange(ref failureReported, 1) == 0) Failed?.Invoke(ex);
    }

    public void ReportPresented(long frameId, long completedTick)
    {
        if (remote != null && !PrepareRemotePresentation(frameId, completedTick)) return;
        if (!frameClocks.TryRemove(frameId, out var clock) || completedTick < clock.DecodedTick ||
            clock.ReceivedTick < clock.EncodedTick || clock.DecodedTick < clock.ReceivedTick) return;
        var milliseconds = 1000.0 / Stopwatch.Frequency;
        var delay = (completedTick - clock.CaptureTick) * milliseconds;
        var stages = new StageTimings((clock.CapturedTick - clock.CaptureTick) * milliseconds,
            (clock.EncodedTick - clock.CapturedTick) * milliseconds, (clock.ReceivedTick - clock.EncodedTick) * milliseconds,
            (clock.DecodedTick - clock.ReceivedTick) * milliseconds, (completedTick - clock.DecodedTick) * milliseconds);
        lock (presentationGate)
        {
            lastTransfer = null;
            var sending = Volatile.Read(ref clock.Sending);
            if (sending != null && clock.ReassembledTick >= sending.LastSendTick && clock.ReceivedTick >= clock.ReassembledTick)
            {
                var transfer = new TransferTimings(sending.PayloadBytes, sending.Packets,
                    (sending.LastSendTick - clock.EncodedTick) * milliseconds, sending.PlannedWaitMs,
                    sending.WakeupOverrunMs, (clock.ReassembledTick - sending.LastSendTick) * milliseconds,
                    (clock.ReceivedTick - clock.ReassembledTick) * milliseconds);
                lastTransfer = transfer;
                transferSamples.Enqueue(transfer);
                while (transferSamples.Count > 256) transferSamples.Dequeue();
            }
            lastPresentedMs = delay; presentationTotalMs += delay; presentationCount++;
            lastStages = stages; totalStages = totalStages.Add(stages);
            Volatile.Write(ref renderedGeneration, clock.Generation);
            presentations.Enqueue((completedTick, delay, stages)); TrimPresentationStats();
        }
    }

    void TrimPresentationStats()
    {
        var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (presentations.TryPeek(out var sample) && sample.Tick < cutoff) presentations.Dequeue();
        while (recentDecoded.TryPeek(out var tick) && tick < cutoff) recentDecoded.Dequeue();
    }

    (double Last, double Mean, int Rendered, int Decoded, StageTimings Stages, StageTimings Averages,
        TransferTimings? Transfer, double SessionMean, StageTimings SessionAverages) PresentationStatus()
    {
        lock (presentationGate)
        {
            TrimPresentationStats();
            double delay = 0;
            StageTimings stages = new();
            foreach (var sample in presentations) { delay += sample.Delay; stages = stages.Add(sample.Stages); }
            return (lastPresentedMs, delay / Math.Max(1, presentations.Count), presentations.Count, recentDecoded.Count,
                lastStages, stages.Divide(presentations.Count), lastTransfer,
                presentationTotalMs / Math.Max(1, presentationCount), totalStages.Divide(presentationCount));
        }
    }

    public object GetReport() => new
    {
        Backend = "FFmpeg " + FfmpegRuntime.Version, ManualOnly = true, ActivePreset = activePreset, AppliedLimitKbps = appliedLimit,
        AppliedGeneration, DecodedGeneration, RenderedGeneration, EncodedFrames = encodedFrames, DecodedFrames = decodedFrames,
        ConfirmedPresentedFrames = presentationCount, MeanCaptureToGpuCompleteMs = PresentationStatus().SessionMean,
        PresentedStageMeansMs = PresentationStatus().SessionAverages,
        RollingWindowSeconds = 1, RollingPresentedFrames = PresentationStatus().Rendered,
        RollingMeanCaptureToGpuCompleteMs = PresentationStatus().Mean, RollingPresentedStageMeansMs = PresentationStatus().Averages,
        TransferSamples = GetTransferSamples(),
        PacketDiagnosticsEnabled = packetDiagnosticsEnabled, ReceivedDiagnosticFrames = Interlocked.Read(ref receivedDiagnosticFrames),
        MeanCaptureMs = captureTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, encodedFrames),
        MeanEncodeAndCopyMs = encodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, encodedFrames),
        MeanDecodeAndConvertMs = decodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, decodedFrames),
        Network = sender?.Snapshot ?? remoteNetwork, Changes = changes.ToArray(), Message = message,
        Mode = IsRemote ? "remote-controller" : diagnosticDestination != null ? "remote-host" : "localhost-demo",
        ClockTiming = IsRemote ? RemoteTimingDescription : "One local monotonic clock",
        Scope = "Capture start -> FFmpeg encode -> immediate UDP (unless explicit simulation is enabled) -> FFmpeg decode -> BGRA -> DXGI Present -> D3D11 GPU completion. Only confirmed presentations contribute to timing; physical scanout is not measured. Separate optional diagnostic UDP packets do not change video packets or gate rendering. Local mode uses one monotonic clock; remote mode estimates clock offset with the uncertainty shown in ClockTiming. The slider controls encoder bitrate only."
    };

    TransferTimings[] GetTransferSamples()
    {
        lock (presentationGate) return transferSamples.ToArray();
    }

    public Task StopAsync()
    {
        lock (lifecycleGate) return shutdown ??= ShutdownAsync();
    }

    async Task ShutdownAsync()
    {
        stop.Cancel(); commands.Writer.TryComplete(); controlServer.Stop(); controlClient?.Close();
        remote?.Dispose();
        await Task.WhenAll(new[] { encodeTask, serverTask, statusTask }.OfType<Task>());
        receiver?.Dispose(); diagnosticsReceiver?.Dispose(); sender?.Dispose();
        lock (decoderGate) { foreach (var decoder in decoders.Values) decoder.Dispose(); decoders.Clear(); }
        requests?.Dispose(); replies?.Dispose(); controlClient?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        StopAsync().GetAwaiter().GetResult(); disposed = true; stop.Dispose();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["--help-window"]) { RemoteLaunch.Help(args[0] == "--help-window"); return 0; }
            if (args is ["--version"])
            {
                Console.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion);
                return 0;
            }
            if (args.Length > 0 && args[0] is not ("--host" or "--connect" or "--codec-regression" or "--control-regression" or
                "--presentation-regression" or "--static-regression" or "--demo-regression")) throw new ArgumentException(RemoteLaunch.Usage);
            var remoteOptions = args.Length > 0 && args[0] is "--host" or "--connect" ? RemoteLaunch.Parse(args) : null;
            var configPath = Path.Combine(AppContext.BaseDirectory, "codec-config.json");
            var config = AppConfiguration.Load(configPath);
            if (remoteOptions != null) return RemoteLaunch.Run(config, remoteOptions);
            if (args.Length > 0 && args[0] == "--codec-regression") { FfmpegRegression.Run(config, args.Length > 1 ? args[1] : "results/ffmpeg-regression"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--control-regression") { FfmpegRegression.RunControl(config, args.Length > 1 ? args[1] : "results/ffmpeg-control"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--presentation-regression") { FfmpegUi.RunPresentationRegression(config, args.Length > 1 ? args[1] : "results/ffmpeg-presentation.json"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--static-regression") { FfmpegRegression.RunStaticLowBandwidth(config, args.Length > 1 ? args[1] : "results/ffmpeg-static"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--demo-regression")
            {
                var seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
                var output = args.Length > 2 ? args[2] : "results/ffmpeg-demo.json";
                FfmpegUi.Run(config, seconds, output); return Environment.ExitCode;
            }
            FfmpegUi.Run(config); return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString()); }
            catch (Exception logError) { Console.Error.WriteLine("Cannot write startup-error.txt: " + logError); }
            return 1;
        }
    }
}
