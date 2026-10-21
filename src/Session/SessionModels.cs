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

public sealed record SessionStatus(string ActivePreset, int AppliedLimitKbps, double SentMbps, double ReceivedMbps,
    double EstimatedMbps, double DelayTrendMs, double LossRate, string Message)
{
    public double CaptureToRenderMs { get; set; }
    public double MeanCaptureToRenderMs { get; set; }
    public double RenderedFps { get; set; }
    public double DecodedFps { get; set; }
    public bool HasRenderTiming { get; set; }
    public bool HasRecentRenderTiming { get; set; }
    public int RenderTimingSamples { get; set; }
    public double RenderTimingWindowSeconds => 1;
    public double CaptureMs { get; set; }
    public double EncodeMs { get; set; }
    public double TransferMs { get; set; }
    public double DecodeMs { get; set; }
    public double RenderMs { get; set; }
    public double MeanCaptureMs { get; set; }
    public double MeanEncodeMs { get; set; }
    public double MeanTransferMs { get; set; }
    public double MeanDecodeMs { get; set; }
    public double MeanRenderMs { get; set; }
    public bool PacketDiagnosticsEnabled { get; set; }
    /// <summary>The person at the host is using its keyboard or mouse; remote input is being dropped.</summary>
    public bool HostBusy { get; set; }
    public double DiagnosticMbps { get; set; }
    public TransferTimings? TransferDetails { get; set; }
    public string TimingDescription { get; set; } = Loc.T("同机单调时钟");
    public int SendBudgetKbps { get; set; }
    public int BurstBudgetWireBytes { get; set; }
    public double SimulatedCapacityMbps { get; set; }
}
public sealed record TransferTimings(int PayloadBytes, int Packets, double SenderMs, double PlannedWaitMs,
    double WakeupOverrunMs, double LastSendToReassemblyMs, double ReceiveQueueMs);
public sealed record ControlResult(bool Success, string Message, int Generation)
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double TransmissionScale { get; set; } = 1;
    public int FramesPerSecond { get; set; }
}
sealed record ControlCommand(string Kind, int Generation, string PresetId, int LimitKbps, bool DiagnosticsEnabled = false, double? Scale = null, int? FramesPerSecond = null);
sealed record PendingControl(ControlCommand Command, TaskCompletionSource<ControlResult> Completion);
