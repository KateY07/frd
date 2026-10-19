namespace Frd;

/// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\FRD\crash-*.log so a vanishing window can be diagnosed.</summary>
static class CrashLog
{
    static int installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("unhandled" + (e.IsTerminating ? "-fatal" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("unobserved-task", e.Exception); e.SetObserved(); };
    }

    public static void Write(string kind, Exception? error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRD");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"crash-{Environment.ProcessId}.log"),
                $"{DateTimeOffset.Now:O} {kind} {Environment.CommandLine}\n{error}\n\n");
            Console.Error.WriteLine($"[crash-log] {kind}: {error}");
        }
        catch (Exception) { /* never fail while reporting a failure */ }
    }
}
