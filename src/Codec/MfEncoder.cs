using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Frd;

/// <summary>Verified against the Windows SDK headers (mfapi.h, mftransform.h).</summary>
static class MfKeys
{
    public static readonly Guid MajorType = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    public static readonly Guid Subtype = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    public static readonly Guid FrameSize = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    public static readonly Guid FrameRate = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    public static readonly Guid AvgBitrate = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    public static readonly Guid InterlaceMode = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
    public static readonly Guid PixelAspectRatio = new(0xc6376a1e, 0x8d0a, 0x4027, 0xbe, 0x45, 0x6d, 0x9a, 0x0a, 0xd3, 0x9b, 0xb6);
    public static readonly Guid DefaultStride = new(0x644b4e48, 0x1e02, 0x4516, 0xb0, 0xeb, 0xc0, 0x1c, 0xa9, 0xd4, 0x9a, 0xc6);
    public static readonly Guid MinimumDisplayAperture = new(0xd7388766, 0x18fe, 0x48c6, 0xa1, 0x77, 0xee, 0x89, 0x48, 0x67, 0xc8, 0xc4);
    public static readonly Guid VideoNominalRange = new(0xc21b8ee5, 0xb956, 0x4071, 0x8d, 0xaf, 0x32, 0x5e, 0xdf, 0x5c, 0xab, 0x11);
    public static readonly Guid YuvMatrix = new(0x3e23d450, 0x2c75, 0x4d25, 0xa0, 0x0e, 0xb9, 0x16, 0x70, 0xd1, 0x23, 0x27);
    public static readonly Guid VideoPrimaries = new(0xdbfbe4d7, 0x0740, 0x4ee0, 0x81, 0x92, 0x85, 0x0a, 0xb0, 0xe2, 0x19, 0x35);
    public static readonly Guid TransferFunction = new(0x5fb0fce9, 0xbe5c, 0x4935, 0xa8, 0x11, 0xec, 0x83, 0x8f, 0x8e, 0xed, 0x93);
    public static readonly Guid Mpeg2Profile = new(0xad76a80b, 0x2d5c, 0x4e0b, 0xb3, 0x75, 0x64, 0xe5, 0x20, 0x13, 0x70, 0x36);
    public static readonly Guid Mpeg2Level = new(0x96f66574, 0x11c5, 0x4015, 0x86, 0x66, 0xbf, 0xf5, 0x16, 0x43, 0x6d, 0xa7);
    public static readonly Guid MpegSequenceHeader = new(0x3c036de7, 0x3ad0, 0x4c9e, 0x92, 0x16, 0xee, 0x6d, 0x6a, 0xc2, 0x1c, 0xb3);
    public static readonly Guid CleanPoint = new(0x9cdf01d8, 0xa0f0, 0x43ba, 0xb0, 0x77, 0xea, 0xa0, 0x6c, 0xbd, 0x72, 0x8a);
    public static readonly Guid LowLatency = new(0x9c27891a, 0xed7a, 0x40e1, 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee);

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}

/// <summary>Options shared by the MF encoder and decoder; the argument syntax mirrors the FFmpeg preset strings.</summary>
static class MfArguments
{
    public static bool IsMediaFoundation(string arguments)
    {
        try { var name = new FfmpegArguments(arguments).CodecName; return name.StartsWith("mf-", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>H.264 encoder on top of Windows Media Foundation (hardware MFT first when allowed, Microsoft software MFT otherwise).</summary>
sealed class MfH264Encoder : IDisposable
{
    readonly IDisposable runtime;
    readonly int width, height, fps;
    readonly string rateControl;
    readonly int profile, gop;
    readonly IMFSample[] inputPool = new IMFSample[4];
    int poolIndex;
    IMFTransform? transform;
    CodecApi? codec;
    IMFMediaEventGenerator? eventGenerator;
    IMFSample? outputSample;
    byte[]? sequenceHeader;
    bool asynchronous, providesSamples, disposed;
    int pendingInputRequests;
    int bitrateKbps;
    static readonly bool Trace = Environment.GetEnvironmentVariable("FRD_MF_TRACE") == "1";
    public string Name => "mf-h264";
    public string DeviceName { get; private set; } = "";
    public bool Hardware { get; private set; }
    public bool HasBitrateCap => rateControl != "quality";
    public int BitrateKbps => bitrateKbps;

    public MfH264Encoder(string arguments, int width, int height, int fps, int bitrateKbps)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive and even.");
        if (fps <= 0 || fps > 240) throw new ArgumentOutOfRangeException(nameof(fps));
        if (bitrateKbps <= 0 || bitrateKbps > 1_000_000) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        var options = new FfmpegArguments(FfmpegRuntime.ResolveArguments(arguments, bitrateKbps));
        if (!options.CodecName.Equals("mf-h264", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not a Media Foundation H.264 encoder preset.");
        this.width = width; this.height = height; this.fps = fps; this.bitrateKbps = bitrateKbps;
        rateControl = (options.Last("rc") ?? "pvbr").ToLowerInvariant();
        if (rateControl is not ("cbr" or "pvbr" or "quality")) throw new ArgumentException("-rc must be cbr, pvbr or quality.");
        profile = (options.Last("profile") ?? "high").ToLowerInvariant() switch { "base" or "baseline" => 66, "main" => 77, "high" => 100, _ => throw new ArgumentException("-profile must be base, main or high.") };
        gop = int.Parse(options.Last("g") ?? (fps * 10).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var hardwareMode = (options.Last("hw") ?? "off").ToLowerInvariant();
        if (hardwareMode is not ("off" or "auto" or "on")) throw new ArgumentException("-hw must be off, auto or on.");
        var quality = int.Parse(options.Last("quality") ?? "70", CultureInfo.InvariantCulture);
        runtime = MediaFoundationRuntime.Acquire();
        try
        {
            Exception? lastError = null;
            var tried = false;
            foreach (var hardware in hardwareMode == "off" ? new[] { false } : hardwareMode == "on" ? new[] { true } : new[] { true, false })
            {
                var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
                var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
                var flags = hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft;
                using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags | (uint)EnumFlag.EnumFlagSortandfilter, input, output);
                foreach (var activate in list.ToArray())
                {
                    tried = true;
                    try { Open(activate, hardware, quality); return; }
                    catch (Exception error)
                    {
                        lastError = error;
                        Console.Error.WriteLine($"[mf] H.264 encoder candidate rejected ({(hardware ? "hardware" : "software")}): {error.Message}");
                        ReleaseTransform();
                    }
                }
            }
            throw new NotSupportedException(tried
                ? "No Media Foundation H.264 encoder accepted this configuration: " + lastError?.Message
                : "No Media Foundation H.264 encoder is installed (Windows N editions need the Media Feature Pack).", lastError);
        }
        catch { Dispose(); throw; }
    }

    void Open(IMFActivate activate, bool hardware, int quality)
    {
        DeviceName = TryGetName(activate);
        transform = activate.ActivateObject<IMFTransform>();
        Hardware = hardware;
        asynchronous = false;
        try { asynchronous = transform.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; } catch (Exception) { }
        if (asynchronous)
        {
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            eventGenerator = transform.QueryInterface<IMFMediaEventGenerator>();
        }
        try { transform.Attributes.Set(MfKeys.LowLatency, 1u); } catch (Exception) { }
        codec = new CodecApi(transform);
        // Properties first: several encoders only accept rate control before the media types are fixed.
        codec.TrySet("low latency", CodecApi.CommonLowLatency, true);
        codec.TrySet("real time", CodecApi.CommonRealTime, true);
        codec.TrySet("low latency mode", CodecApi.LowLatencyMode, true);
        codec.TrySet("B pictures", CodecApi.DefaultBPictureCount, 0u);
        codec.TrySet("GOP size", CodecApi.GopSize, (uint)Math.Max(1, gop));
        codec.TrySet("rate control", CodecApi.RateControlMode, rateControl switch
            { "cbr" => CodecApi.RateCbr, "pvbr" => CodecApi.RatePeakConstrainedVbr, _ => CodecApi.RateQuality });
        if (rateControl == "quality") codec.TrySet("quality", CodecApi.Quality, (uint)Math.Clamp(quality, 1, 100));
        else ApplyBitrate(bitrateKbps);

        using (var outputType = MediaFactory.MFCreateMediaType())
        {
            outputType.Set(MfKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MfKeys.Subtype, VideoFormatGuids.H264);
            outputType.Set(MfKeys.AvgBitrate, (uint)(bitrateKbps * 1000L));
            outputType.Set(MfKeys.FrameSize, MfKeys.Pack(width, height));
            outputType.Set(MfKeys.FrameRate, MfKeys.Pack(fps, 1));
            outputType.Set(MfKeys.PixelAspectRatio, MfKeys.Pack(1, 1));
            outputType.Set(MfKeys.InterlaceMode, 2u);
            outputType.Set(MfKeys.Mpeg2Profile, (uint)profile);
            transform.SetOutputType(0, outputType, 0);
        }
        SelectInputType();
        if (asynchronous == false) ConfigureOutputBuffers();
        else { var info = transform.GetOutputStreamInfo(0); providesSamples = (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0; if (!providesSamples) outputSample = CreateSample(Math.Max(info.Size, width * height)); }
        try { sequenceHeader = transform.GetOutputCurrentType(0).GetBlob(MfKeys.MpegSequenceHeader); } catch (Exception) { sequenceHeader = null; }
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        for (var i = 0; i < inputPool.Length; i++) inputPool[i] = CreateSample(checked(width * height * 3 / 2));
        Console.Error.WriteLine($"Encoder mf-h264: {width}x{height}, {fps} FPS, cap {bitrateKbps} kbps, rc {rateControl}, profile {profile}; {(Hardware ? "hardware" : "software")} MFT \"{DeviceName}\"{(asynchronous ? " (async)" : "")}");
    }

    static string TryGetName(IMFActivate activate)
    {
        try { return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch (Exception) { return "unknown"; }
    }

    void SelectInputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType candidate;
            try { candidate = transform!.GetInputAvailableType(0, index); }
            catch (Exception) when (index > 0) { break; }
            using (candidate)
            {
                if (candidate.GetGUID(MfKeys.Subtype) != VideoFormatGuids.NV12) continue;
                candidate.Set(MfKeys.FrameSize, MfKeys.Pack(width, height));
                candidate.Set(MfKeys.FrameRate, MfKeys.Pack(fps, 1));
                candidate.Set(MfKeys.PixelAspectRatio, MfKeys.Pack(1, 1));
                candidate.Set(MfKeys.InterlaceMode, 2u);
                candidate.Set(MfKeys.VideoNominalRange, 2u);   // MFNominalRange_16_235
                candidate.Set(MfKeys.YuvMatrix, 1u);            // MFVideoTransferMatrix_BT709
                candidate.Set(MfKeys.VideoPrimaries, 2u);       // MFVideoPrimaries_BT709
                candidate.Set(MfKeys.TransferFunction, 5u);     // MFVideoTransFunc_709
                transform!.SetInputType(0, candidate, 0);
                return;
            }
        }
        throw new NotSupportedException("The H.264 encoder does not accept NV12 input.");
    }

    void ConfigureOutputBuffers()
    {
        var info = transform!.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        if (!providesSamples) outputSample = CreateSample(Math.Max(info.Size, width * height));
    }

    static IMFSample CreateSample(int bytes)
    {
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(bytes, 63);
        sample.AddBuffer(buffer);
        buffer.Dispose();
        return sample;
    }

    void ApplyBitrate(int kbps)
    {
        var cap = (uint)(kbps * 1000L);
        if (rateControl == "cbr") { codec!.TrySet("mean bitrate", CodecApi.MeanBitRate, cap); }
        else
        {
            codec!.TrySet("mean bitrate", CodecApi.MeanBitRate, (uint)(cap * 0.75));
            codec.TrySet("max bitrate", CodecApi.MaxBitRate, cap);
        }
    }

    public bool SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (kbps <= 0 || kbps > 1_000_000) throw new ArgumentOutOfRangeException(nameof(kbps));
        if (kbps == bitrateKbps) return true;
        if (rateControl == "quality" || codec == null || !codec.Available) return false;
        ApplyBitrate(kbps);
        bitrateKbps = kbps;
        Console.Error.WriteLine($"Encoder mf-h264: manual cap updated to {kbps} kbps.");
        return true;
    }

    public IReadOnlyList<CodecPacket> Encode(byte[] bgra, long pts, bool forceKeyframe = false)
    {
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("BGRA input size does not match encoder dimensions.", nameof(bgra));
        unsafe { fixed (byte* pixels = bgra) return EncodeBgra((nint)pixels, width * 4, pts, forceKeyframe); }
    }

    public IReadOnlyList<CodecPacket> EncodeMapped(MappedBgraFrame frame, long pts, bool forceKeyframe = false)
    {
        if (frame.Width != width || frame.Height != height || frame.Pixels == 0 || frame.Stride < width * 4)
            throw new ArgumentException("Mapped BGRA input dimensions or stride do not match the encoder.", nameof(frame));
        return EncodeBgra(frame.Pixels, frame.Stride, pts, forceKeyframe);
    }

    IReadOnlyList<CodecPacket> EncodeBgra(nint pixels, int stride, long pts, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new List<CodecPacket>();
        if (asynchronous && !WaitForInputRequest(result)) return result;   // hardware encoder is backed up: drop this frame
        var sample = inputPool[poolIndex++ % inputPool.Length];
        var buffer = sample.GetBufferByIndex(0);
        try
        {
            buffer.Lock(out var address, out _, out _);
            try { ColorConvert.BgraToNv12(pixels, stride, address, width, address + width * height, width, width, height); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = width * height * 3 / 2;
        }
        finally { buffer.Dispose(); }
        sample.SampleTime = pts;
        sample.SampleDuration = 10_000_000L / fps;
        if (forceKeyframe) codec!.TrySet("force key frame", CodecApi.ForceKeyFrame, 1u);
        transform!.ProcessInput(0, sample, 0);
        if (Trace && asynchronous) Console.Error.WriteLine($"[mf trace] ProcessInput pts {pts}, pending requests {pendingInputRequests}");
        if (asynchronous) { pendingInputRequests--; inFlight++; PumpEvents(result, TimeSpan.FromMilliseconds(2), stopAfterOutput: true); inFlight -= result.Count; }
        else DrainSynchronous(result);
        return result;
    }

    void DrainSynchronous(List<CodecPacket> result)
    {
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0, Sample = providesSamples ? null : outputSample };
            if (!providesSamples && outputSample != null) ResetOutputSample(outputSample);
            var status = transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (status == ResultCode.TransformNeedMoreInput) { output.Events?.Dispose(); return; }
            if (status == ResultCode.TransformStreamChange) { output.Events?.Dispose(); HandleOutputStreamChange(); continue; }
            status.CheckError();
            try { if (output.Sample != null) result.Add(ReadPacket(output.Sample)); }
            finally { output.Events?.Dispose(); if (providesSamples) output.Sample?.Dispose(); }
        }
    }

    /// <summary>Encoders may announce a changed output type (for example the final SPS/PPS); accept the type they offer.</summary>
    void HandleOutputStreamChange()
    {
        using (var type = transform!.GetOutputAvailableType(0, 0)) transform.SetOutputType(0, type, 0);
        try { sequenceHeader = transform.GetOutputCurrentType(0).GetBlob(MfKeys.MpegSequenceHeader); } catch (Exception) { }
        var info = transform.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        outputSample?.Dispose(); outputSample = providesSamples ? null : CreateSample(Math.Max(info.Size, width * height));
        if (Trace) Console.Error.WriteLine("[mf trace] output stream change handled");
    }

    static void ResetOutputSample(IMFSample sample)
    {
        using var buffer = sample.GetBufferByIndex(0);
        buffer.CurrentLength = 0;
    }

    CodecPacket ReadPacket(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var address, out _, out var length);
        byte[] data;
        try { data = new byte[length]; Marshal.Copy(address, data, 0, length); }
        finally { contiguous.Unlock(); }
        var keyFrame = sample.GetUInt32(MfKeys.CleanPoint, out var clean).Success && clean != 0;
        if (keyFrame && sequenceHeader is { Length: > 0 } && !H264AnnexB.Contains(data, 7)) data = [.. sequenceHeader, .. data];
        return new(data, keyFrame, sample.SampleTime);
    }

    // Asynchronous (usually hardware) MFTs announce what they want through events.
    bool WaitForInputRequest(List<CodecPacket> result)
    {
        var deadline = Environment.TickCount64 + (long)Math.Max(20, 2000.0 / fps);
        while (pendingInputRequests == 0)
        {
            PumpEvents(result, TimeSpan.FromMilliseconds(2));
            if (pendingInputRequests > 0) break;
            if (Environment.TickCount64 > deadline) { if (Trace) Console.Error.WriteLine("[mf trace] no NeedInput within deadline; dropping frame"); return false; }
        }
        return true;
    }

    void PumpEvents(List<CodecPacket> result, TimeSpan wait, bool stopAfterOutput = false)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            IMFMediaEvent? mediaEvent;
            try { mediaEvent = eventGenerator!.GetEvent(1 /* MF_EVENT_FLAG_NO_WAIT */); }
            catch (SharpGen.Runtime.SharpGenException error) when (error.ResultCode.Code == unchecked((int)0xC00D3E80))
            { mediaEvent = null; }
            if (mediaEvent == null)
            {
                if (Environment.TickCount64 >= deadline) return;
                Thread.Sleep(1);
                continue;
            }
            using (mediaEvent)
            {
                if (Trace) Console.Error.WriteLine($"[mf trace] event {(int)mediaEvent.EventType} status {mediaEvent.Status}");
                switch ((int)mediaEvent.EventType)
                {
                    case 601: pendingInputRequests++; break;           // METransformNeedInput
                    case 602: CollectAsyncOutput(result); break;        // METransformHaveOutput
                    case 603: break;                                    // METransformDrainComplete
                }
            }
            if (stopAfterOutput && result.Count > 0) return;
            if (Environment.TickCount64 >= deadline && pendingInputRequests > 0) return;
        }
    }

    void CollectAsyncOutput(List<CodecPacket> result)
    {
        var output = new OutputDataBuffer { StreamID = 0, Sample = providesSamples ? null : outputSample };
        if (!providesSamples && outputSample != null) ResetOutputSample(outputSample);
        var status = transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        if (status == ResultCode.TransformStreamChange) { output.Events?.Dispose(); HandleOutputStreamChange(); return; }
        if (status == ResultCode.TransformNeedMoreInput) { output.Events?.Dispose(); return; }
        if (Trace) Console.Error.WriteLine($"[mf trace] ProcessOutput status {status} sample {(output.Sample != null)} provides {providesSamples}");
        status.CheckError();
        try { if (output.Sample != null) result.Add(ReadPacket(output.Sample)); }
        finally { output.Events?.Dispose(); if (providesSamples) output.Sample?.Dispose(); }
    }

    int inFlight;

    /// <summary>True when an asynchronous (hardware) encoder still owes output for submitted frames.</summary>
    public bool HasPending => asynchronous && inFlight > 0;

    /// <summary>Collects output that became ready since the last call; call it while idle so the last frame of a burst is not held back.</summary>
    public IReadOnlyList<CodecPacket> Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new List<CodecPacket>();
        if (asynchronous) { PumpEvents(result, TimeSpan.Zero, stopAfterOutput: false); inFlight -= result.Count; }
        return result;
    }

    public IReadOnlyList<CodecPacket> Flush()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new List<CodecPacket>();
        transform!.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        if (asynchronous) PumpEvents(result, TimeSpan.FromMilliseconds(200));
        else DrainSynchronous(result);
        return result;
    }

    void ReleaseTransform()
    {
        codec?.Dispose(); codec = null;
        eventGenerator?.Dispose(); eventGenerator = null;
        outputSample?.Dispose(); outputSample = null;
        foreach (var sample in inputPool.Where(s => s != null)) sample.Dispose();
        Array.Clear(inputPool);
        if (transform != null)
        {
            try { transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch (Exception) { }
            transform.Dispose(); transform = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseTransform();
        runtime.Dispose();
    }
}

static class H264AnnexB
{
    /// <summary>True when the access unit contains a NAL unit of the given type (e.g. 5 = IDR slice, 7 = SPS, 8 = PPS).</summary>
    public static bool Contains(ReadOnlySpan<byte> data, int nalType)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1 && (data[i + 3] & 0x1F) == nalType) return true;
            if (data[i + 2] == 0 && i + 4 < data.Length && data[i + 3] == 1 && (data[i + 4] & 0x1F) == nalType) return true;
        }
        return false;
    }
}
