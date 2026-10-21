using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Frd;

/// <summary>H.264 decoder on top of the Windows Media Foundation decoder MFT; produces BGRA like the FFmpeg decoder.</summary>
sealed class MfH264Decoder : IDisposable
{
    const int NotAccepting = unchecked((int)0xC00D36B2);   // MF_E_NOTACCEPTING
    readonly IDisposable runtime;
    IMFTransform? transform;
    CodecApi? codec;
    IMFSample? outputSample;
    IMFSample? inputSample;
    int inputCapacity;
    bool providesSamples, disposed;
    int frameWidth, frameHeight, stride, cropLeft, cropTop, visibleWidth, visibleHeight;
    public string Name => "mf-h264-decode";
    public string DeviceName { get; private set; } = "";

    public MfH264Decoder(string arguments)
    {
        var options = new FfmpegArguments(arguments);
        if (!options.CodecName.Equals("mf-h264-decode", StringComparison.OrdinalIgnoreCase) &&
            !options.CodecName.Equals("h264", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a Media Foundation H.264 decoder preset.");
        runtime = MediaFoundationRuntime.Acquire();
        try
        {
            var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
            using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, (uint)EnumFlag.EnumFlagSyncmft | (uint)EnumFlag.EnumFlagSortandfilter, input, output);
            var candidates = list.ToArray();
            if (candidates.Length == 0) throw new NotSupportedException("No Media Foundation H.264 decoder is installed (Windows N editions need the Media Feature Pack).");
            Exception? lastError = null;
            foreach (var activate in candidates)
            {
                try { Open(activate); return; }
                catch (Exception error) { lastError = error; Console.Error.WriteLine("[mf] H.264 decoder candidate rejected: " + error.Message); ReleaseTransform(); }
            }
            throw new NotSupportedException("No Media Foundation H.264 decoder accepted this configuration: " + lastError?.Message, lastError);
        }
        catch { Dispose(); throw; }
    }

    void Open(IMFActivate activate)
    {
        try { DeviceName = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch (Exception) { DeviceName = "unknown"; }
        transform = activate.ActivateObject<IMFTransform>();
        try { transform.Attributes.Set(MfKeys.LowLatency, 1u); } catch (Exception) { }
        codec = new CodecApi(transform);
        codec.TrySet("low latency mode", CodecApi.LowLatencyMode, true);
        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MfKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MfKeys.Subtype, VideoFormatGuids.H264);
            inputType.Set(MfKeys.InterlaceMode, 2u);
            transform.SetInputType(0, inputType, 0);
        }
        SelectOutputType();
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Console.Error.WriteLine($"Decoder mf-h264-decode: Media Foundation \"{DeviceName}\"; rendered output is BGRA");
    }

    void SelectOutputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType candidate;
            try { candidate = transform!.GetOutputAvailableType(0, index); }
            catch (Exception) when (index > 0) { throw new NotSupportedException("The H.264 decoder cannot output NV12."); }
            using (candidate)
            {
                if (candidate.GetGUID(MfKeys.Subtype) != VideoFormatGuids.NV12) continue;
                transform!.SetOutputType(0, candidate, 0);
                break;
            }
        }
        ReadOutputGeometry();
        var info = transform!.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        outputSample?.Dispose(); outputSample = null;
        if (!providesSamples)
        {
            var size = Math.Max(info.Size, Math.Max(1, stride * Math.Max(frameHeight, 16) * 3 / 2));
            outputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(size, 63);
            outputSample.AddBuffer(buffer);
        }
    }

    void ReadOutputGeometry()
    {
        using var type = transform!.GetOutputCurrentType(0);
        var size = type.GetUInt64(MfKeys.FrameSize);
        frameWidth = (int)(size >> 32); frameHeight = (int)(size & 0xFFFFFFFF);
        stride = 0;
        try { stride = Math.Abs((int)type.GetUInt32(MfKeys.DefaultStride)); } catch (Exception) { }
        if (stride == 0) stride = frameWidth;
        visibleWidth = frameWidth; visibleHeight = frameHeight; cropLeft = cropTop = 0;
        try
        {
            var area = type.GetBlob(MfKeys.MinimumDisplayAperture);
            if (area.Length >= 16)
            {
                cropLeft = BitConverter.ToInt16(area, 2); cropTop = BitConverter.ToInt16(area, 6);
                visibleWidth = BitConverter.ToInt32(area, 8); visibleHeight = BitConverter.ToInt32(area, 12);
            }
        }
        catch (Exception) { }
        if (visibleWidth <= 0 || visibleHeight <= 0 || cropLeft + visibleWidth > frameWidth || cropTop + visibleHeight > frameHeight)
        { visibleWidth = frameWidth; visibleHeight = frameHeight; cropLeft = cropTop = 0; }
    }

    public IReadOnlyList<DecodedPixels> Decode(byte[] compressed, long pts)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (compressed.Length == 0) throw new ArgumentException("Compressed packet is empty.", nameof(compressed));
        var result = new List<DecodedPixels>();
        PrepareInput(compressed, pts);
        try { transform!.ProcessInput(0, inputSample!, 0); }
        catch (SharpGen.Runtime.SharpGenException error) when (error.ResultCode.Code == NotAccepting)
        {
            Drain(result);
            transform!.ProcessInput(0, inputSample!, 0);
        }
        Drain(result);
        return result;
    }

    void PrepareInput(byte[] data, long pts)
    {
        if (inputSample == null || inputCapacity < data.Length)
        {
            inputSample?.Dispose();
            inputCapacity = Math.Max(data.Length * 2, 256 * 1024);
            inputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(inputCapacity, 15);
            inputSample.AddBuffer(buffer);
        }
        using (var buffer = inputSample.GetBufferByIndex(0))
        {
            buffer.Lock(out var address, out _, out _);
            try { Marshal.Copy(data, 0, address, data.Length); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = data.Length;
        }
        inputSample.SampleTime = pts;
        inputSample.SampleDuration = 0;
    }

    void Drain(List<DecodedPixels> result)
    {
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0, Sample = providesSamples ? null : outputSample };
            if (!providesSamples && outputSample != null) { using var buffer = outputSample.GetBufferByIndex(0); buffer.CurrentLength = 0; }
            var status = transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (status == ResultCode.TransformNeedMoreInput) { output.Events?.Dispose(); return; }
            if (status == ResultCode.TransformStreamChange) { output.Events?.Dispose(); SelectOutputType(); continue; }
            status.CheckError();
            try { if (output.Sample != null) result.Add(ConvertFrame(output.Sample)); }
            finally { output.Events?.Dispose(); if (providesSamples) output.Sample?.Dispose(); }
        }
    }

    DecodedPixels ConvertFrame(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var address, out _, out var length);
        try
        {
            var chromaOffset = (long)stride * frameHeight;
            if (length < chromaOffset + (long)stride * frameHeight / 2)
                throw new InvalidDataException($"Decoded NV12 buffer is too small: {length} bytes for {frameWidth}x{frameHeight} (stride {stride}).");
            var bgra = new byte[checked(visibleWidth * visibleHeight * 4)];
            ColorConvert.Nv12ToBgra(address, stride, chromaOffset, cropLeft, cropTop, bgra, visibleWidth, visibleHeight);
            return new(bgra, visibleWidth, visibleHeight, sample.SampleTime);
        }
        finally { contiguous.Unlock(); }
    }

    public IReadOnlyList<DecodedPixels> Flush()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new List<DecodedPixels>();
        transform!.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Drain(result);
        return result;
    }

    void ReleaseTransform()
    {
        codec?.Dispose(); codec = null;
        outputSample?.Dispose(); outputSample = null;
        inputSample?.Dispose(); inputSample = null;
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
