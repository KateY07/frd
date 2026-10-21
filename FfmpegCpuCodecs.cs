namespace Frd;

/// <summary>Encoder facade: Media Foundation (default, ships with Windows) or FFmpeg (optional extension), chosen by the preset's codec name.</summary>
sealed class VideoEncoder : IDisposable
{
    readonly FfmpegEncoder? ffmpeg;
    MfH264Encoder? mediaFoundation;
    readonly CapturePixelMode pixelMode;
    readonly string arguments;
    readonly int width, height, fps;
    readonly bool automaticHardware;
    int bitrateKbps, submitted, received;
    public string Name => ffmpeg?.Name ?? mediaFoundation!.Name;
    public string Description => mediaFoundation != null
        ? $"Media Foundation {(mediaFoundation.Hardware ? "hardware" : "software")} - {mediaFoundation.DeviceName}"
        : $"FFmpeg {FfmpegRuntime.Version} - {ffmpeg!.Name}";
    public bool SupportsMappedBgra => pixelMode == CapturePixelMode.Bgra && (mediaFoundation != null || ffmpeg!.SupportsMappedBgra);
    public bool HasBitrateCap => mediaFoundation?.HasBitrateCap ?? true;

    public VideoEncoder(string arguments, int width, int height, int fps, int bitrateKbps,
        CapturePixelMode pixelMode = CapturePixelMode.Bgra)
    {
        this.pixelMode = pixelMode; this.arguments = arguments; this.width = width; this.height = height; this.fps = fps; this.bitrateKbps = bitrateKbps;
        if (MfArguments.IsMediaFoundation(arguments))
        {
            if (pixelMode != CapturePixelMode.Bgra) throw new NotSupportedException("Media Foundation presets only accept full BGRA desktop input; packed pixel modes need the FFmpeg extension.");
            mediaFoundation = new(arguments, width, height, fps, bitrateKbps);
            automaticHardware = mediaFoundation.Hardware && new FfmpegArguments(arguments).Last("hw") == "auto";
        }
        else ffmpeg = new(arguments, width, height, fps, bitrateKbps);
    }

    public bool SetBitrate(int bitrateKbps)
    {
        var accepted = mediaFoundation?.SetBitrate(bitrateKbps) ?? ffmpeg!.SetBitrate(bitrateKbps);
        if (accepted) this.bitrateKbps = bitrateKbps;
        return accepted;
    }

    public IReadOnlyList<CodecPacket> Encode(byte[] bgra, long pts, bool keyframe)
    {
        if (mediaFoundation != null) return MfEncode(encoder => encoder.Encode(bgra, pts, keyframe));
        return pixelMode switch
        {
            CapturePixelMode.Rgb332 => ffmpeg!.EncodeQuantized(bgra, false, pts, keyframe),
            CapturePixelMode.Rgb565 => ffmpeg!.EncodeRgb565(bgra, pts, keyframe),
            CapturePixelMode.Gray8 => ffmpeg!.EncodeQuantized(bgra, true, pts, keyframe),
            CapturePixelMode.Gray4 => ffmpeg!.EncodeGray4(bgra, pts, keyframe),
            _ => ffmpeg!.Encode(bgra, pts, keyframe)
        };
    }

    public IReadOnlyList<CodecPacket> EncodeMapped(MappedBgraFrame bgra, long pts, bool keyframe) =>
        mediaFoundation != null ? MfEncode(encoder => encoder.EncodeMapped(bgra, pts, keyframe)) : ffmpeg!.EncodeMapped(bgra, pts, keyframe);

    // "-hw auto": hardware first; a hardware encoder that errors or stays silent drops back to the software MFT in place.
    IReadOnlyList<CodecPacket> MfEncode(Func<MfH264Encoder, IReadOnlyList<CodecPacket>> encode)
    {
        try
        {
            var packets = encode(mediaFoundation!);
            submitted++; received += packets.Count;
            if (automaticHardware && mediaFoundation!.Hardware && received == 0 && submitted >= 24)
                throw new TimeoutException("the hardware H.264 encoder produced no output");
            return packets;
        }
        catch (Exception error) when (automaticHardware && mediaFoundation!.Hardware)
        {
            Console.Error.WriteLine($"[mf] Hardware encoder failed ({error.Message}); continuing with the software encoder.");
            mediaFoundation!.Dispose();
            mediaFoundation = new(arguments.Replace("-hw auto", "-hw off", StringComparison.Ordinal), width, height, fps, bitrateKbps);
            submitted = received = 0;
            return encode(mediaFoundation);
        }
    }

    public IReadOnlyList<CodecPacket> Flush() => mediaFoundation != null ? mediaFoundation.Flush() : ffmpeg!.Flush();
    /// <summary>Asynchronous hardware encoders deliver output after the call that fed them; poll while idle.</summary>
    public bool HasPending => mediaFoundation?.HasPending ?? false;
    public IReadOnlyList<CodecPacket> Poll()
    {
        if (mediaFoundation == null) return [];
        var packets = mediaFoundation.Poll();
        received += packets.Count;
        return packets;
    }
    public void Dispose() { ffmpeg?.Dispose(); mediaFoundation?.Dispose(); }
}

/// <summary>Decoder facade. Plain "-c:v h264" falls back to the Media Foundation decoder when the FFmpeg extension is not installed.</summary>
sealed class VideoDecoder : IDisposable
{
    readonly FfmpegDecoder? ffmpeg;
    readonly MfH264Decoder? mediaFoundation;

    public VideoDecoder(string arguments)
    {
        var options = new FfmpegArguments(arguments);
        var plainH264 = options.CodecName.Equals("h264", StringComparison.OrdinalIgnoreCase) && options.Last("hwaccel") is null or "none";
        if (MfArguments.IsMediaFoundation(arguments) || (plainH264 && !FfmpegRuntime.IsAvailable)) mediaFoundation = new(arguments);
        else ffmpeg = new(arguments);
    }

    public IReadOnlyList<DecodedPixels> Decode(byte[] compressed, long pts) => mediaFoundation != null ? mediaFoundation.Decode(compressed, pts) : ffmpeg!.Decode(compressed, pts);
    public IReadOnlyList<DecodedPixels> Flush() => mediaFoundation != null ? mediaFoundation.Flush() : ffmpeg!.Flush();
    public void Dispose() { ffmpeg?.Dispose(); mediaFoundation?.Dispose(); }
}
