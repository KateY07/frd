namespace Frd;

/// <summary>BT.709 limited-range BGRA/NV12 conversion, so the Media Foundation path needs neither swscale nor a converter MFT.</summary>
static unsafe class ColorConvert
{
    const int RowPairsPerChunk = 8;

    public static void BgraToNv12(nint source, int sourceStride, nint luma, int lumaStride, nint chroma, int chromaStride, int width, int height)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 dimensions must be even.");
        var chunks = (height / 2 + RowPairsPerChunk - 1) / RowPairsPerChunk;
        Parallel.For(0, chunks, chunk =>
        {
            var first = chunk * RowPairsPerChunk;
            var last = Math.Min(height / 2, first + RowPairsPerChunk);
            for (var pair = first; pair < last; pair++)
            {
                var top = (byte*)source + (long)sourceStride * pair * 2;
                var bottom = top + sourceStride;
                var yTop = (byte*)luma + (long)lumaStride * pair * 2;
                var yBottom = yTop + lumaStride;
                var uvRow = (byte*)chroma + (long)chromaStride * pair;
                for (var x = 0; x < width; x += 2)
                {
                    int b0 = top[x * 4], g0 = top[x * 4 + 1], r0 = top[x * 4 + 2];
                    int b1 = top[x * 4 + 4], g1 = top[x * 4 + 5], r1 = top[x * 4 + 6];
                    int b2 = bottom[x * 4], g2 = bottom[x * 4 + 1], r2 = bottom[x * 4 + 2];
                    int b3 = bottom[x * 4 + 4], g3 = bottom[x * 4 + 5], r3 = bottom[x * 4 + 6];
                    yTop[x] = (byte)(((47 * r0 + 157 * g0 + 16 * b0 + 128) >> 8) + 16);
                    yTop[x + 1] = (byte)(((47 * r1 + 157 * g1 + 16 * b1 + 128) >> 8) + 16);
                    yBottom[x] = (byte)(((47 * r2 + 157 * g2 + 16 * b2 + 128) >> 8) + 16);
                    yBottom[x + 1] = (byte)(((47 * r3 + 157 * g3 + 16 * b3 + 128) >> 8) + 16);
                    var r = r0 + r1 + r2 + r3; var g = g0 + g1 + g2 + g3; var b = b0 + b1 + b2 + b3;
                    uvRow[x] = Clamp(((-26 * r - 86 * g + 112 * b + 512) >> 10) + 128);
                    uvRow[x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 512) >> 10) + 128);
                }
            }
        });
    }

    /// <param name="chromaOffset">Byte offset of the UV plane from <paramref name="source"/> (stride * aligned frame height).</param>
    public static void Nv12ToBgra(nint source, int stride, long chromaOffset, int cropLeft, int cropTop, byte[] destination, int width, int height)
    {
        if (destination.Length < checked(width * height * 4)) throw new ArgumentException("Destination is too small.", nameof(destination));
        var chunks = (height + 15) / 16;
        fixed (byte* output = destination)
        {
            var outputAddress = (nint)output;
            Parallel.For(0, chunks, chunk =>
            {
                var firstRow = chunk * 16;
                var lastRow = Math.Min(height, firstRow + 16);
                for (var row = firstRow; row < lastRow; row++)
                {
                    var yRow = (byte*)source + (long)stride * (row + cropTop) + cropLeft;
                    var uvRow = (byte*)source + chromaOffset + (long)stride * ((row + cropTop) >> 1) + (cropLeft & ~1);
                    var target = (byte*)outputAddress + (long)row * width * 4;
                    var uvPhase = cropLeft & 1;
                    for (var x = 0; x < width; x++)
                    {
                        var c = (yRow[x] - 16) * 298 + 128;
                        var pair = ((x + uvPhase) >> 1) * 2;
                        var d = uvRow[pair] - 128;
                        var e = uvRow[pair + 1] - 128;
                        target[x * 4] = Clamp((c + 541 * d) >> 8);
                        target[x * 4 + 1] = Clamp((c - 55 * d - 136 * e) >> 8);
                        target[x * 4 + 2] = Clamp((c + 459 * e) >> 8);
                        target[x * 4 + 3] = 255;
                    }
                }
            });
        }
    }

    static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
