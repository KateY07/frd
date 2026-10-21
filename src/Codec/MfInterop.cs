using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Frd;

/// <summary>Reference-counted Media Foundation startup shared by every MF codec instance in the process.</summary>
static class MediaFoundationRuntime
{
    static readonly object Gate = new();
    static int users;

    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (users == 0) MediaFactory.MFStartup(false).CheckError();
            users++;
        }
        return new Lease();
    }

    sealed class Lease : IDisposable
    {
        int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (Gate) { if (--users == 0) MediaFactory.MFShutdown(); }
        }
    }

    /// <summary>True when the system has an H.264 encoder or decoder MFT (absent on N editions without the Media Feature Pack).</summary>
    public static bool HasTransform(Guid category, Guid inputSubtype, Guid outputSubtype)
    {
        try
        {
            using var lease = Acquire();
            var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = inputSubtype };
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = outputSubtype };
            foreach (var flags in new[] { EnumFlag.EnumFlagSyncmft, EnumFlag.EnumFlagAsyncmft, EnumFlag.EnumFlagHardware })
            {
                using var list = MediaFactory.MFTEnumEx(category, (uint)flags | (uint)EnumFlag.EnumFlagSortandfilter, input, output);
                if (list.Any()) return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    public static bool HasH264Encoder() => HasTransform(TransformCategoryGuids.VideoEncoder, VideoFormatGuids.NV12, VideoFormatGuids.H264);
    public static bool HasH264Decoder() => HasTransform(TransformCategoryGuids.VideoDecoder, VideoFormatGuids.H264, VideoFormatGuids.NV12);
}

sealed class CodecApi : IDisposable
{
    // GUIDs and enum values from the Windows SDK codecapi.h.
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid CommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid CommonRealTime = new("143a0ff6-a131-43da-b81e-98fbb8ec378e");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid H264Cabac = new("ee6cad62-d305-4248-a50e-e1b255f7caf8");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public const uint RateCbr = 0, RatePeakConstrainedVbr = 1, RateUnconstrainedVbr = 2, RateQuality = 3, RateLowDelayVbr = 4;

    // ICodecAPI is not exposed by Vortice; the few encoder properties FRD needs are called through its vtable (AOT-safe, no RCW).
    static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    nint api;
    public CodecApi(IMFTransform transform)
    {
        var iid = IidCodecApi;
        if (Marshal.QueryInterface(transform.NativePointer, in iid, out var pointer) == 0) api = pointer;
    }

    public bool Available => api != 0;
    unsafe nint Slot(int index) => ((nint*)*(nint*)api)[index];

    public unsafe bool IsSupported(Guid property) => api != 0 && ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(3))(api, &property) == 0;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public long Value; }

    public unsafe bool TrySet(string name, Guid property, object value)
    {
        if (api == 0) return false;
        var variant = value switch
        {
            bool flag => new Variant { Type = 11, Value = flag ? -1 : 0 },       // VT_BOOL
            uint number => new Variant { Type = 19, Value = number },            // VT_UI4
            int number => new Variant { Type = 3, Value = number },              // VT_I4
            ulong number => new Variant { Type = 21, Value = (long)number },     // VT_UI8
            _ => throw new ArgumentException("Unsupported ICodecAPI value type: " + value.GetType())
        };
        var result = ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Slot(9))(api, &property, &variant);
        // E_NOTIMPL / E_INVALIDARG just mean this MFT lacks an optional tuning property.
        if (result != 0 && Environment.GetEnvironmentVariable("FRD_MF_TRACE") == "1") Console.Error.WriteLine($"[mf] ICodecAPI {name} = {value} rejected (0x{result:X8}).");
        return result == 0;
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref api, 0);
        if (current != 0) Marshal.Release(current);
    }
}
