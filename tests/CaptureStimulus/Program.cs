using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Frd.CaptureStimulus;

static class Program
{
    public static bool FullMotion { get; private set; }

    public static void Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--full-motion"))
            throw new ArgumentException("Use no arguments for sparse motion or --full-motion for full-area changes.");
        FullMotion = args.Length == 1;
        using var timer = new NativeTimerResolution();
        AppBuilder.Configure<StimulusApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

sealed class NativeTimerResolution : IDisposable
{
    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint timeEndPeriod(uint period);

    public NativeTimerResolution()
    {
        if (timeBeginPeriod(1) != 0) throw new InvalidOperationException("Could not request 1 ms stimulus timer resolution.");
    }

    public void Dispose()
    {
        var result = timeEndPeriod(1);
        if (result != 0) System.Diagnostics.Trace.WriteLine($"timeEndPeriod failed: {result}");
    }
}

sealed class StimulusApp : Application
{
    public override void Initialize() { }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new StimulusWindow(Program.FullMotion);
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class StimulusWindow : Window
{
    readonly StimulusSurface surface;
    readonly Stopwatch clock = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(5) };

    public StimulusWindow(bool fullMotion)
    {
        surface = new(fullMotion);
        Title = fullMotion ? "FRD full-area capture stimulus" : "FRD sparse capture stimulus";
        WindowState = WindowState.FullScreen;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        Content = surface;
        timer.Tick += (_, _) => surface.Advance((int)(clock.ElapsedTicks * 36 / Stopwatch.Frequency));
        Opened += (_, _) => { clock.Start(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
}

sealed class StimulusSurface : Control
{
    static readonly IBrush[] FullPalette =
    [
        new SolidColorBrush(Color.FromRgb(180, 190, 200)),
        new SolidColorBrush(Color.FromRgb(200, 180, 190)),
        new SolidColorBrush(Color.FromRgb(190, 200, 180)),
        new SolidColorBrush(Color.FromRgb(175, 180, 210))
    ];
    readonly bool fullMotion;
    int frame;

    public StimulusSurface(bool fullMotion) => this.fullMotion = fullMotion;

    public void Advance(int absoluteFrame)
    {
        var next = absoluteFrame % 60;
        if (next == frame) return;
        frame = next;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (fullMotion)
        {
            var phase = frame % FullPalette.Length;
            context.FillRectangle(FullPalette[phase], new Rect(0, 0, width, height));
            for (var y = 0d; y < height; y += 128)
                for (var x = 0d; x < width; x += 128)
                {
                    var index = (phase + (((int)(x / 128) + (int)(y / 128)) & 1) * 2) % FullPalette.Length;
                    context.FillRectangle(FullPalette[index], new Rect(x, y, 128, 128));
                }
            return;
        }
        context.FillRectangle(Brushes.WhiteSmoke, new Rect(0, 0, width, height));
        for (var x = 0d; x < width; x += 96)
            context.FillRectangle(Brushes.LightGray, new Rect(x, 0, 1, height));
        for (var y = 0d; y < height; y += 96)
            context.FillRectangle(Brushes.LightGray, new Rect(0, y, width, 1));
        var horizontal = Math.Max(1d, width - 128);
        var vertical = Math.Max(1d, height - 128);
        var xPosition = frame * 83d % horizontal;
        var yPosition = frame * 43d % vertical;
        context.FillRectangle(Brushes.DarkBlue, new Rect(xPosition, yPosition, 96, 96));
        context.FillRectangle(Brushes.Gold, new Rect(xPosition + 20, yPosition + 20, 56, 56));
    }
}
