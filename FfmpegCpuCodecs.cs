namespace Frd;

sealed class VideoEncoder : IDisposable
{
    readonly FfmpegEncoder ffmpeg;
    readonly CapturePixelMode pixelMode;
    public string Name => ffmpeg.Name;
    public bool SupportsMappedBgra => pixelMode == CapturePixelMode.Bgra && ffmpeg.SupportsMappedBgra;
    public bool HasBitrateCap => true;

    public VideoEncoder(string arguments, int width, int height, int fps, int bitrateKbps,
        CapturePixelMode pixelMode = CapturePixelMode.Bgra)
    {
        this.pixelMode = pixelMode;
        ffmpeg = new(arguments, width, height, fps, bitrateKbps);
    }

    public bool SetBitrate(int bitrateKbps) => ffmpeg.SetBitrate(bitrateKbps);
    public IReadOnlyList<CodecPacket> Encode(byte[] bgra, long pts, bool keyframe) => pixelMode switch
    {
        CapturePixelMode.Rgb332 => ffmpeg.EncodeQuantized(bgra, false, pts, keyframe),
        CapturePixelMode.Rgb565 => ffmpeg.EncodeRgb565(bgra, pts, keyframe),
        CapturePixelMode.Gray8 => ffmpeg.EncodeQuantized(bgra, true, pts, keyframe),
        CapturePixelMode.Gray4 => ffmpeg.EncodeGray4(bgra, pts, keyframe),
        _ => ffmpeg.Encode(bgra, pts, keyframe)
    };
    public IReadOnlyList<CodecPacket> EncodeMapped(MappedBgraFrame bgra, long pts, bool keyframe) =>
        ffmpeg.EncodeMapped(bgra, pts, keyframe);
    public void Dispose() => ffmpeg.Dispose();
}

sealed class VideoDecoder : IDisposable
{
    readonly FfmpegDecoder ffmpeg;
    public VideoDecoder(string arguments) => ffmpeg = new(arguments);
    public IReadOnlyList<DecodedPixels> Decode(byte[] compressed, long pts) => ffmpeg.Decode(compressed, pts);
    public void Dispose() => ffmpeg.Dispose();
}
