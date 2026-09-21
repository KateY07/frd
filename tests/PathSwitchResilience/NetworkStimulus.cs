using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;

sealed class NetworkStimulus : IDisposable
{
    readonly Process process;
    NetworkStimulus(Process process) => this.process = process;
    public static NetworkStimulus Start()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PathSwitchResilience.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--network-stimulus");
        return new(Process.Start(start) ?? throw new InvalidOperationException("Capture stimulus did not start."));
    }
    public static void Run()
    {
        var thread = new Thread(() => AppBuilder.Configure<StimulusApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]));
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    public void Dispose()
    {
        if (!process.HasExited)
        {
            if (!process.CloseMainWindow() || !process.WaitForExit(3000)) process.Kill();
            process.WaitForExit();
        }
        process.Dispose();
    }
    sealed class StimulusApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new Window { Title = "FRD 网络恢复回归（完成后关闭）", Width = 320, Height = 180,
                    Position = new PixelPoint(20, 20), Topmost = true, ShowActivated = false };
                var tick = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) => window.Background = ++tick % 2 == 0 ? Brushes.SteelBlue : Brushes.Goldenrod;
                window.Opened += (_, _) => timer.Start();
                window.Closed += (_, _) => timer.Stop();
                desktop.MainWindow = window;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
