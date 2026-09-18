using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using Ffmpeg = FFmpeg.AutoGen.ffmpeg;
using SwsContext = FFmpeg.AutoGen.SwsContext;
using AVPixelFormat = FFmpeg.AutoGen.AVPixelFormat;
using SwsFlags = FFmpeg.AutoGen.SwsFlags;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Frd;

sealed record DesktopCaptureStatistics(string Backend, int SourceWidth, int SourceHeight, bool NewDesktopImage,
    double AcquireMs, double ScaleAndReadbackMs, bool? SeparateHardwareCursorVisible, string CursorPolicy,
    double DesktopCopySubmitMs = 0, double FrameReleaseMs = 0, double GpuScaleSubmitMs = 0,
    double ReadbackCopySubmitMs = 0, double MapWaitAndReadbackMs = 0, double PixelBufferAllocationMs = 0,
    double CpuRowCopyMs = 0, double UnmapMs = 0, double TotalCaptureMs = 0, double OtherCpuMs = 0,
    double CpuScaleMs = 0, string CapturePath = "VideoProcessor", double SourcePresentAgeMs = 0, uint AccumulatedFrames = 0,
    int OutputWidth = 1280, int OutputHeight = 720, bool DesktopImageInSystemMemory = false,
    string SourceBindFlags = "", bool? DirectShaderResourceSupported = null);

enum DesktopCapturePath { VideoProcessor, CpuSwscale, PixelShader, VideoProcessorLateRelease, NativeNoScale, DirectPixelShader }

sealed class DesktopCapture : IDisposable
{
    static readonly DesktopCapturePath[] DxgiPaths = [DesktopCapturePath.PixelShader, DesktopCapturePath.VideoProcessor];
    readonly object gate = new();
    readonly int width, height, stretchMode;
    DxgiDesktopCapture? dxgi;
    GdiDesktopCapture? gdi;
    int pathIndex;
    bool disposed;
    public string Backend => dxgi != null ? $"DXGI Desktop Duplication + {dxgi.Path}" : "GDI fallback";
    public DesktopCaptureStatistics? Statistics => dxgi?.Statistics;

    public DesktopCapture(int width, int height, int stretchMode = 4)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height; this.stretchMode = stretchMode;
        InitializeDxgi();
    }

    public byte[] Capture()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (dxgi != null)
            {
                try { return dxgi.Capture(); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[capture] {DxgiPaths[pathIndex]} capture failed; rebuilding desktop duplication: {error}");
                    dxgi.Dispose(); dxgi = null;
                    try { dxgi = new(width, height, DxgiPaths[pathIndex]); return dxgi.Capture(); }
                    catch (Exception retryError)
                    {
                        Console.Error.WriteLine($"[capture] {DxgiPaths[pathIndex]} retry failed; trying the next capture path: {retryError}");
                        dxgi?.Dispose(); dxgi = null; pathIndex++;
                        InitializeDxgi(retryError);
                    }
                }
            }
            return gdi!.Capture();
        }
    }

    void InitializeDxgi(Exception? lastError = null)
    {
        while (pathIndex < DxgiPaths.Length)
        {
            try { dxgi = new(width, height, DxgiPaths[pathIndex]); return; }
            catch (Exception error)
            {
                lastError = error;
                Console.Error.WriteLine($"[capture] {DxgiPaths[pathIndex]} initialization failed; trying the next capture path: {error}");
                pathIndex++;
            }
        }
        UseGdi(lastError ?? new InvalidOperationException("No DXGI capture path remains."));
    }

    void UseGdi(Exception error)
    {
        Console.Error.WriteLine($"[capture] DXGI unavailable; using existing GDI capture. Reason: {error}");
        gdi = new(width, height, stretchMode);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; dxgi?.Dispose(); gdi?.Dispose(); dxgi = null; gdi = null;
        }
    }
}

sealed unsafe class DxgiDesktopCapture : IDisposable
{
    const int WaitTimeout = unchecked((int)0x887A0027);
    readonly int width, height;
    readonly DesktopCapturePath path;
    readonly bool probeDirectShaderResource;
    int sourceWidth, sourceHeight;
    int contentWidth, contentHeight, contentLeft, contentTop;
    IDXGIAdapter1? adapter;
    IDXGIOutput? output;
    IDXGIOutput1? output1;
    IDXGIOutputDuplication? duplication;
    ID3D11Device? device;
    ID3D11DeviceContext? context;
    ID3D11Multithread? multithread;
    ID3D11VideoDevice? videoDevice;
    ID3D11VideoContext? videoContext;
    ID3D11VideoProcessorEnumerator? enumerator;
    ID3D11VideoProcessor? processor;
    ID3D11Texture2D? desktop, scaled, staging;
    ID3D11VideoProcessorInputView? inputView;
    ID3D11VideoProcessorOutputView? outputView;
    ID3D11RenderTargetView? clearView;
    ID3D11VertexShader? vertexShader;
    ID3D11PixelShader? pixelShader;
    ID3D11ShaderResourceView? shaderInput;
    ID3D11SamplerState? sampler;
    ID3D11RasterizerState? rasterizer;
    SwsContext* cpuScaler;
    readonly byte*[] cpuInputPlanes = new byte*[4], cpuOutputPlanes = new byte*[4];
    readonly int[] cpuInputStrides = new int[4], cpuOutputStrides = new int[4];
    byte[]? lastPixels;
    bool? separateCursorVisible;
    bool desktopImageInSystemMemory;
    string sourceBindFlags = "";
    bool? directShaderResourceSupported;
    public DesktopCapturePath Path => path;
    public DesktopCaptureStatistics? Statistics { get; set; }

    public DxgiDesktopCapture(int width, int height, DesktopCapturePath path = DesktopCapturePath.PixelShader, bool probeDirectShaderResource = false)
    {
        this.width = width; this.height = height; this.path = path; this.probeDirectShaderResource = probeDirectShaderResource;
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; factory.EnumAdapters1(index, out var candidate).Success; index++)
            {
                var selected = false;
                for (uint outputIndex = 0; candidate.EnumOutputs(outputIndex, out var candidateOutput).Success; outputIndex++)
                {
                    var description = candidateOutput.Description;
                    var bounds = description.DesktopCoordinates;
                    if (description.AttachedToDesktop && bounds.Left == 0 && bounds.Top == 0)
                    { adapter = candidate; output = candidateOutput; selected = true; break; }
                    candidateOutput.Dispose();
                }
                if (selected) break;
                candidate.Dispose();
            }
            if (adapter == null || output == null) throw new InvalidOperationException("DXGI could not locate the primary desktop output.");
            var description1 = output.Description;
            if (description1.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                throw new NotSupportedException($"DXGI capture requires rotation handling for {description1.Rotation}; preserving GDI fallback for this display.");
            D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            multithread = device.QueryInterface<ID3D11Multithread>(); multithread.SetMultithreadProtected(true);
            output1 = output.QueryInterface<IDXGIOutput1>(); duplication = output1.DuplicateOutput(device);
            // Duplication reports physical texture dimensions even when output coordinates are DPI-virtualized.
            sourceWidth = checked((int)duplication.Description.ModeDescription.Width);
            sourceHeight = checked((int)duplication.Description.ModeDescription.Height);
            desktopImageInSystemMemory = duplication.Description.DesktopImageInSystemMemory;
            if (path == DesktopCapturePath.NativeNoScale)
            { width = sourceWidth; height = sourceHeight; this.width = width; this.height = height; }
            var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
            contentWidth = (int)Math.Round(sourceWidth * scale); contentHeight = (int)Math.Round(sourceHeight * scale);
            contentLeft = (width - contentWidth) / 2; contentTop = (height - contentHeight) / 2;
            if (path is DesktopCapturePath.CpuSwscale or DesktopCapturePath.NativeNoScale)
            {
                staging = device.CreateTexture2D(Texture(sourceWidth, sourceHeight, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read));
                if (path == DesktopCapturePath.CpuSwscale)
                {
                    cpuScaler = Ffmpeg.sws_getContext(sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                        contentWidth, contentHeight, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                    if (cpuScaler == null) throw new InvalidOperationException("libswscale could not create the full-desktop BGRA scaler.");
                }
            }
            else
            {
                if (path != DesktopCapturePath.DirectPixelShader)
                    desktop = device.CreateTexture2D(Texture(sourceWidth, sourceHeight, ResourceUsage.Default,
                        path == DesktopCapturePath.PixelShader ? BindFlags.ShaderResource : BindFlags.None, CpuAccessFlags.None));
                scaled = device.CreateTexture2D(Texture(width, height, ResourceUsage.Default, BindFlags.RenderTarget | BindFlags.ShaderResource, CpuAccessFlags.None));
                staging = device.CreateTexture2D(Texture(width, height, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read));
                clearView = device.CreateRenderTargetView(scaled);
            }
            if (path is DesktopCapturePath.PixelShader or DesktopCapturePath.DirectPixelShader) InitializePixelShader();
            if (path is DesktopCapturePath.VideoProcessor or DesktopCapturePath.VideoProcessorLateRelease)
            {
                videoDevice = device.QueryInterface<ID3D11VideoDevice>(); videoContext = context.QueryInterface<ID3D11VideoContext>();
                var content = new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputFrameRate = new Rational(30, 1),
                    InputWidth = (uint)sourceWidth,
                    InputHeight = (uint)sourceHeight,
                    OutputFrameRate = new Rational(30, 1),
                    OutputWidth = (uint)width,
                    OutputHeight = (uint)height,
                    Usage = VideoUsage.OptimalSpeed
                };
                enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
                var support = enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
                if ((support & (VideoProcessorFormatSupport.Input | VideoProcessorFormatSupport.Output)) != (VideoProcessorFormatSupport.Input | VideoProcessorFormatSupport.Output))
                    throw new NotSupportedException($"Video processor does not support BGRA input/output: {support}.");
                processor = videoDevice.CreateVideoProcessor(enumerator, 0);
                inputView = videoDevice.CreateVideoProcessorInputView(desktop!, enumerator, new VideoProcessorInputViewDescription
                { ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 } });
                outputView = videoDevice.CreateVideoProcessorOutputView(scaled!, enumerator, new VideoProcessorOutputViewDescription
                { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 } });
                videoContext.VideoProcessorSetOutputTargetRect(processor, true, new Vortice.RawRect(0, 0, width, height));
                videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(0, 0, sourceWidth, sourceHeight));
                videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new Vortice.RawRect(contentLeft, contentTop, contentLeft + contentWidth, contentTop + contentHeight));
                videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
                videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            }
            Console.Error.WriteLine($"[capture] DXGI primary display {description1.DeviceName}: {sourceWidth}x{sourceHeight} -> {width}x{height}; {path}, full desktop, letterbox {contentLeft},{contentTop},{contentWidth},{contentHeight}. Separate hardware cursor is not composited; the source pointer is never hidden or modified.");
        }
        catch { Dispose(); throw; }
    }

    static Texture2DDescription Texture(int width, int height, ResourceUsage usage, BindFlags bind, CpuAccessFlags cpu) => new()
    { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0), Usage = usage, BindFlags = bind, CPUAccessFlags = cpu };

    void InitializePixelShader()
    {
        const string source = """
            Texture2D<float4> Desktop : register(t0);
            SamplerState LinearClamp : register(s0);
            struct Vertex { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
            Vertex VS(uint id : SV_VertexID) {
                Vertex output;
                output.Uv = float2((id << 1) & 2, id & 2);
                output.Position = float4(output.Uv.x * 2 - 1, 1 - output.Uv.y * 2, 0, 1);
                return output;
            }
            float4 PS(Vertex input) : SV_Target { return float4(Desktop.SampleLevel(LinearClamp, input.Uv, 0).rgb, 1); }
            """;
        vertexShader = device!.CreateVertexShader(CompileShader(source, "VS", "vs_5_0"));
        pixelShader = device.CreatePixelShader(CompileShader(source, "PS", "ps_5_0"));
        if (desktop != null) shaderInput = device.CreateShaderResourceView(desktop);
        sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue
        });
        rasterizer = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true });
    }

    static byte[] CompileShader(string source, string entry, string target)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var status = D3DCompile(bytes, (nuint)bytes.Length, null, 0, 0, entry, target, 0x8000, 0, out var code, out var errors);
        try
        {
            if (errors != 0) Console.Error.WriteLine($"[capture shader] {Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0')}");
            Marshal.ThrowExceptionForHR(status);
            return ReadBlob(code);
        }
        finally { if (code != 0) Marshal.Release(code); if (errors != 0) Marshal.Release(errors); }
    }

    static byte[] ReadBlob(nint blob)
    {
        var table = *(nint**)blob;
        var pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)table[3])(blob);
        var length = ((delegate* unmanaged[Stdcall]<nint, nuint>)table[4])(blob);
        return new ReadOnlySpan<byte>((void*)pointer, checked((int)length)).ToArray();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile(byte[] data, nuint length, string? sourceName, nint defines, nint include,
        string entry, string target, uint flags, uint effectFlags, out nint code, out nint errors);

    public byte[] Capture()
    {
        ObjectDisposedException.ThrowIf(duplication == null, this);
        var started = Stopwatch.GetTimestamp();
        var result = duplication.AcquireNextFrame(lastPixels == null ? 1000u : 0u, out var info, out var resource);
        var acquired = Stopwatch.GetTimestamp();
        double desktopCopyMs = 0, releaseMs = 0, scaleMs = 0, readbackCopyMs = 0, mapMs = 0, allocationMs = 0, rowCopyMs = 0, unmapMs = 0, cpuScaleMs = 0;
        var changed = result.Success && (lastPixels == null || info.LastPresentTime != 0);
        var heldFrame = result.Success;
        ID3D11Texture2D? acquiredTexture = null;
        ID3D11ShaderResourceView? acquiredView = null;
        try
        {
            if (result.Success)
            {
                try
                {
                    using (resource)
                    {
                        if (info.LastMouseUpdateTime != 0) separateCursorVisible = info.PointerPosition.Visible;
                        if (changed)
                        {
                            using var texture = resource.QueryInterface<ID3D11Texture2D>();
                            if (texture.Description.Width != sourceWidth || texture.Description.Height != sourceHeight)
                                throw new InvalidOperationException("Desktop dimensions changed; recreate duplication.");
                            sourceBindFlags = texture.Description.BindFlags.ToString();
                            if (probeDirectShaderResource && directShaderResourceSupported == null)
                            {
                                try { using var view = device!.CreateShaderResourceView(texture); directShaderResourceSupported = true; }
                                catch (Exception error) { directShaderResourceSupported = false; Console.Error.WriteLine($"[capture] Optional direct desktop shader-resource probe unsupported: {error.Message}"); }
                                Console.Error.WriteLine($"[capture] DesktopImageInSystemMemory={desktopImageInSystemMemory}; acquired texture BindFlags={sourceBindFlags}; direct SRV={directShaderResourceSupported}. Probe only; the timed path retains its desktop copy.");
                            }
                            var copyStarted = Stopwatch.GetTimestamp();
                            if (path == DesktopCapturePath.DirectPixelShader)
                            {
                                acquiredTexture = texture.QueryInterface<ID3D11Texture2D>();
                                acquiredView = device!.CreateShaderResourceView(acquiredTexture);
                                directShaderResourceSupported = true;
                            }
                            else if (path is DesktopCapturePath.CpuSwscale or DesktopCapturePath.NativeNoScale)
                            {
                                context!.CopyResource(staging!, texture);
                                readbackCopyMs = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                            }
                            else
                            {
                                context!.CopyResource(desktop!, texture);
                                desktopCopyMs = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                            }
                        }
                    }
                }
                finally
                {
                    if (path is not (DesktopCapturePath.VideoProcessorLateRelease or DesktopCapturePath.DirectPixelShader)) ReleaseAcquiredFrame();
                }
            }
            else if (result.Code != WaitTimeout) result.CheckError();
            if (!changed)
            {
                if (lastPixels == null) throw new TimeoutException("DXGI returned no initial desktop image within one second.");
                ReleaseAcquiredFrame(); RecordStatistics(false);
                return lastPixels;
            }
            if (path is not (DesktopCapturePath.CpuSwscale or DesktopCapturePath.NativeNoScale))
            {
                var scaleStarted = Stopwatch.GetTimestamp();
                context!.ClearRenderTargetView(clearView!, new Color4(0, 0, 0, 1));
                if (path is DesktopCapturePath.PixelShader or DesktopCapturePath.DirectPixelShader)
                {
                    context.OMSetRenderTargets(clearView!);
                    context.RSSetViewport(contentLeft, contentTop, contentWidth, contentHeight);
                    context.RSSetState(rasterizer!);
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    context.VSSetShader(vertexShader!);
                    context.PSSetShader(pixelShader!);
                    context.PSSetShaderResource(0, acquiredView ?? shaderInput!);
                    context.PSSetSampler(0, sampler!);
                    context.Draw(3, 0);
                }
                else videoContext!.VideoProcessorBlt(processor!, outputView!, 0, [new VideoProcessorStream { Enable = true, InputSurface = inputView! }]).CheckError();
                scaleMs = Stopwatch.GetElapsedTime(scaleStarted).TotalMilliseconds;
                var readbackStarted = Stopwatch.GetTimestamp();
                context.CopyResource(staging!, scaled!);
                readbackCopyMs = Stopwatch.GetElapsedTime(readbackStarted).TotalMilliseconds;
            }
            var mapStarted = Stopwatch.GetTimestamp();
            var map = context!.Map(staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            mapMs = Stopwatch.GetElapsedTime(mapStarted).TotalMilliseconds;
            byte[] pixels;
            try
            {
                var allocationStarted = Stopwatch.GetTimestamp();
                pixels = new byte[checked(width * height * 4)];
                if (path == DesktopCapturePath.CpuSwscale) MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(0xff000000);
                allocationMs = Stopwatch.GetElapsedTime(allocationStarted).TotalMilliseconds;
                var rowCopyStarted = Stopwatch.GetTimestamp();
                if (path == DesktopCapturePath.CpuSwscale)
                {
                    fixed (byte* outputPixels = pixels)
                    {
                        cpuInputPlanes[0] = (byte*)map.DataPointer; cpuInputStrides[0] = checked((int)map.RowPitch);
                        cpuOutputPlanes[0] = outputPixels + (contentTop * width + contentLeft) * 4; cpuOutputStrides[0] = width * 4;
                        var rows = Ffmpeg.sws_scale(cpuScaler, cpuInputPlanes, cpuInputStrides, 0, sourceHeight, cpuOutputPlanes, cpuOutputStrides);
                        if (rows != contentHeight) throw new InvalidOperationException($"Full desktop scaling produced {rows}/{contentHeight} rows.");
                    }
                    cpuScaleMs = Stopwatch.GetElapsedTime(rowCopyStarted).TotalMilliseconds;
                }
                else
                {
                    for (var y = 0; y < height; y++)
                        new ReadOnlySpan<byte>((void*)(map.DataPointer + (nint)(y * (long)map.RowPitch)), width * 4).CopyTo(pixels.AsSpan(y * width * 4, width * 4));
                    rowCopyMs = Stopwatch.GetElapsedTime(rowCopyStarted).TotalMilliseconds;
                }
            }
            finally
            {
                var unmapStarted = Stopwatch.GetTimestamp();
                context.Unmap(staging!, 0);
                unmapMs = Stopwatch.GetElapsedTime(unmapStarted).TotalMilliseconds;
            }
            lastPixels = pixels;
            ReleaseAcquiredFrame();
            RecordStatistics(true);
            return pixels;
        }
        finally { ReleaseAcquiredFrame(); }

        void ReleaseAcquiredFrame()
        {
            if (!heldFrame) return;
            heldFrame = false;
            var releaseStarted = Stopwatch.GetTimestamp();
            if (acquiredView != null) { context!.PSSetShaderResource(0, null!); acquiredView.Dispose(); acquiredView = null; }
            acquiredTexture?.Dispose(); acquiredTexture = null;
            duplication.ReleaseFrame().CheckError();
            releaseMs = Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds;
        }

        void RecordStatistics(bool newDesktopImage)
        {
            var ended = Stopwatch.GetTimestamp();
            var totalMs = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            var acquireMs = Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds;
            // Submission times are CPU API duration; Map can wait for all preceding GPU work and the readback.
            var otherMs = Math.Max(0, totalMs - acquireMs - desktopCopyMs - releaseMs - scaleMs - readbackCopyMs - mapMs - allocationMs - rowCopyMs - unmapMs - cpuScaleMs);
            Statistics = new("DXGI", sourceWidth, sourceHeight, newDesktopImage, acquireMs,
                newDesktopImage ? Stopwatch.GetElapsedTime(acquired, ended).TotalMilliseconds : 0,
                separateCursorVisible, "Separate hardware cursor excluded; an OS-composited cursor may already be part of the desktop texture.",
                desktopCopyMs, releaseMs, scaleMs, readbackCopyMs, mapMs, allocationMs, rowCopyMs, unmapMs, totalMs, otherMs,
                cpuScaleMs, path.ToString(), info.LastPresentTime == 0 ? 0 : Stopwatch.GetElapsedTime(info.LastPresentTime, ended).TotalMilliseconds, info.AccumulatedFrames,
                width, height, desktopImageInSystemMemory, sourceBindFlags, directShaderResourceSupported);
        }
    }

    public void Dispose()
    {
        outputView?.Dispose(); inputView?.Dispose(); clearView?.Dispose(); staging?.Dispose(); scaled?.Dispose(); desktop?.Dispose();
        vertexShader?.Dispose(); pixelShader?.Dispose(); shaderInput?.Dispose(); sampler?.Dispose(); rasterizer?.Dispose();
        if (cpuScaler != null) Ffmpeg.sws_freeContext(cpuScaler);
        cpuScaler = null; vertexShader = null; pixelShader = null; shaderInput = null; sampler = null; rasterizer = null;
        processor?.Dispose(); enumerator?.Dispose(); videoContext?.Dispose(); videoDevice?.Dispose();
        duplication?.Dispose(); output1?.Dispose(); multithread?.Dispose(); context?.Dispose(); device?.Dispose(); output?.Dispose(); adapter?.Dispose();
        outputView = null; inputView = null; clearView = null; staging = scaled = desktop = null; processor = null; enumerator = null;
        videoContext = null; videoDevice = null; duplication = null; output1 = null; multithread = null; context = null; device = null; output = null; adapter = null;
    }
}

sealed class GdiDesktopCapture : IDisposable
{
    readonly object gate = new();
    readonly int width, height;
    nint screen, memory, image, previousImage, pixels;
    bool disposed;

    public GdiDesktopCapture(int width, int height, int stretchMode = 4)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height;
        try
        {
            screen = GetDC(0); if (screen == 0) Fail("GetDC");
            memory = CreateCompatibleDC(screen); if (memory == 0) Fail("CreateCompatibleDC");
            var info = new BitmapInfo { Header = new() { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            image = CreateDIBSection(screen, ref info, 0, out pixels, 0, 0); if (image == 0) Fail("CreateDIBSection");
            previousImage = SelectObject(memory, image); if (previousImage == 0 || previousImage == -1) Fail("SelectObject");
            if (SetStretchBltMode(memory, stretchMode) == 0) Fail("SetStretchBltMode");
            if (!SetBrushOrgEx(memory, 0, 0, 0)) Fail("SetBrushOrgEx");
        }
        catch { Dispose(); throw; }
    }

    public byte[] Capture()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int sourceWidth = GetSystemMetrics(0), sourceHeight = GetSystemMetrics(1);
            if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("主屏幕尺寸不可用");
            if (!StretchBlt(memory, 0, 0, width, height, screen, 0, 0, sourceWidth, sourceHeight, 0x40CC0020)) Fail("StretchBlt");
            if (!GdiFlush()) Fail("GdiFlush");
            var frame = new byte[checked(width * height * 4)]; Marshal.Copy(pixels, frame, 0, frame.Length); return frame;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (memory != 0 && previousImage != 0 && previousImage != -1 && SelectObject(memory, previousImage) == 0) LogCleanup("SelectObject restore");
            if (image != 0 && !DeleteObject(image)) LogCleanup("DeleteObject");
            if (memory != 0 && !DeleteDC(memory)) LogCleanup("DeleteDC");
            if (screen != 0 && ReleaseDC(0, screen) == 0) LogCleanup("ReleaseDC");
            screen = memory = image = previousImage = pixels = 0;
        }
    }

    static void Fail(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), "屏幕捕获失败：" + operation);
    static void LogCleanup(string operation) => Console.Error.WriteLine($"[capture cleanup] {operation}: {Marshal.GetLastWin32Error()}");
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool SetBrushOrgEx(nint dc, int x, int y, nint oldOrigin);
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool StretchBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
}
