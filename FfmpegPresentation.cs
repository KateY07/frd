using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Frd;

public sealed class ConfirmedVideoView : NativeControlHost
{
    readonly object gate = new();
    readonly AutoResetEvent changed = new(false);
    DecodedPixels? pending;
    NativeInputSource? inputSource;
    Thread? renderThread;
    nint sourceWindow;
    bool stopped, inputEnabled;
    int sourceWidth = 1280, sourceHeight = 720;

    public event Action<long, long>? Presented;
    public event Action<Exception>? Failed;
    public event Action<RemoteInputEvent>? Input;
    public event Action? InputExitRequested;
    public nint SourceWindow => sourceWindow;

    public void SetInputEnabled(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Input mode must change on the UI thread.");
        inputEnabled = enabled;
        inputSource?.SetEnabled(enabled);
    }

    public ConfirmedVideoView()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Submit(DecodedPixels pixels)
    {
        if (pixels.Width is <= 0 or > 8192 || pixels.Height is <= 0 or > 8192 ||
            pixels.Bgra.Length != checked(pixels.Width * pixels.Height * 4))
            throw new ArgumentException("Invalid tightly packed BGRA image.", nameof(pixels));
        lock (gate)
        {
            if (stopped) return;
            // Decoder output owns a fresh immutable array; replacement only discards unrendered output.
            pending = pixels;
            changed.Set();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Volatile.Read(ref sourceWidth);
        var height = Volatile.Read(ref sourceHeight);
        var scale = Math.Min(availableSize.Width / width, availableSize.Height / height);
        if (!double.IsFinite(scale)) scale = 1;
        return new(width * Math.Max(0, scale), height * Math.Max(0, scale));
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var hwnd = CreateWindowEx(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x04000000,
            0, 0, sourceWidth, sourceHeight, parent.Handle, 0, 0, 0);
        if (hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create D3D11 presentation window.");
        sourceWindow = hwnd;
        inputSource = new(hwnd);
        inputSource.Input += value => Input?.Invoke(value);
        inputSource.ExitRequested += () => { inputEnabled = false; InputExitRequested?.Invoke(); };
        inputSource.Failed += Report;
        inputSource.SetEnabled(inputEnabled);
        lock (gate)
        {
            if (!stopped)
            {
                renderThread = new(() => Render(hwnd)) { IsBackground = true, Name = "FRD confirmed GPU presentation" };
                renderThread.Start();
            }
        }
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        inputSource?.Dispose();
        inputSource = null;
        Stop();
        if (control.Handle != 0 && !DestroyWindow(control.Handle))
            Report(new Win32Exception(Marshal.GetLastWin32Error(), "Cannot destroy D3D11 presentation window."));
        sourceWindow = 0;
    }

    public void Stop()
    {
        Thread? thread;
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            pending = null;
            thread = renderThread;
            changed.Set();
        }
        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(2000))
        {
            Report(new TimeoutException("D3D11 presentation thread did not stop within 2 seconds."));
            return;
        }
        changed.Dispose();
    }

    unsafe void Render(nint hwnd)
    {
        try
        {
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            using (device)
            using (context)
            using (var factory = CreateDXGIFactory2<IDXGIFactory2>(false))
            using (var completion = device.CreateQuery(new QueryDescription(QueryType.Event)))
            {
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice1>()) dxgiDevice.MaximumFrameLatency = 1;
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter).CheckError();
                IDXGISwapChain1? swap = null;
                byte[]? lastPixels = null;
                var width = 0;
                var height = 0;
                try
                {
                    while (true)
                    {
                        DecodedPixels? pixels;
                        lock (gate)
                        {
                            if (stopped) return;
                            pixels = pending;
                            pending = null;
                        }
                        if (pixels is null) { changed.WaitOne(250); continue; }
                        if (pixels.Width == width && pixels.Height == height && lastPixels is not null &&
                            pixels.Bgra.AsSpan().SequenceEqual(lastPixels)) continue;
                        if (pixels.Width != width || pixels.Height != height)
                        {
                            swap?.Dispose();
                            swap = factory.CreateSwapChainForHwnd(device, hwnd, new SwapChainDescription1
                            {
                                Width = (uint)pixels.Width, Height = (uint)pixels.Height,
                                Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new(1, 0),
                                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
                            });
                            width = pixels.Width;
                            height = pixels.Height;
                            Volatile.Write(ref sourceWidth, width);
                            Volatile.Write(ref sourceHeight, height);
                            Dispatcher.UIThread.Post(InvalidateMeasure);
                        }
                        using (var backBuffer = swap!.GetBuffer<ID3D11Texture2D>(0))
                            fixed (byte* source = pixels.Bgra)
                                context.UpdateSubresource(backBuffer, 0, null, (nint)source, (uint)(width * 4), 0);
                        var presentResult = swap!.Present(0, PresentFlags.None);
                        presentResult.CheckError();
                        if (presentResult.Code != 0) continue;
                        // The event confirms GPU commands after Present; it does not measure physical scan-out.
                        context.End(completion);
                        context.Flush();
                        uint done = 0;
                        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        while (true)
                        {
                            var result = context.GetData(completion, (nint)(&done), sizeof(uint), AsyncGetDataFlags.DoNotFlush);
                            result.CheckError();
                            if (result.Code == 0 && done != 0) break;
                            lock (gate) { if (stopped) return; }
                            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("GPU presentation confirmation exceeded 1 second.");
                            Thread.Yield();
                        }
                        var completed = Stopwatch.GetTimestamp();
                        lastPixels = pixels.Bgra;
                        Presented?.Invoke(pixels.Pts, completed);
                    }
                }
                finally { swap?.Dispose(); }
            }
        }
        catch (Exception error) { Report(error); }
    }

    void Report(Exception error)
    {
        Console.Error.WriteLine($"[GPU presentation] {error}");
        try { Failed?.Invoke(error); }
        catch (Exception callbackError) { Console.Error.WriteLine($"[GPU presentation error callback] {callbackError}"); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyWindow(nint hwnd);
}
