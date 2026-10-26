using System.Net;

namespace Frd;

/// <summary>Entry points that run a native window on the main thread's message loop.</summary>
static class UiApp
{
    static void Prepare()
    {
        UiIcons.Register();
        UiThread.Error += (source, error) => CrashLog.Write(source, error);
        // Screenshot aid kept from earlier builds: FRD_UI_ALLOW_CAPTURE=1 lets screen capture see FRD's own windows.
        if (Environment.GetEnvironmentVariable("FRD_UI_ALLOW_CAPTURE") == "1") Environment.SetEnvironmentVariable("GLASSPANE_ALLOW_CAPTURE", "1");
    }

    public static int RunHost(AppConfiguration config, IPAddress[] addresses, int port, string token, CodecProbeSample[]? probeSamples)
    {
        RemoteLaunch.hostExitCode = 0;
        Prepare();
        UiThread.Initialize();
        using var window = new UiHostWindow(config, addresses, port, token, probeSamples);
        UiThread.Run();
        SessionTrace.Event("host-process", $"exit-code-{RemoteLaunch.hostExitCode}");
        return RemoteLaunch.hostExitCode;
    }

    public static void RunController(AppConfiguration config, int autoCloseSeconds = 0, string? reportPath = null, RemoteOptions? remote = null, string? title = null)
    {
        UiControllerWindow.RegressionExitCode = 0;
        Prepare();
        UiThread.Initialize();
        using var window = new UiControllerWindow(config, autoCloseSeconds, reportPath, remote, title);
        UiThread.Run();
        if (UiControllerWindow.RegressionExitCode != 0) Environment.ExitCode = UiControllerWindow.RegressionExitCode;
        SessionTrace.Event("controller-process", $"exit-code-{Environment.ExitCode}");
    }
}
