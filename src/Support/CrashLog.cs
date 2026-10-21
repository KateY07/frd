using System.Text.RegularExpressions;

namespace Frd;

/// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\FRD\crash-*.log so a vanishing window can be diagnosed.</summary>
static class CrashLog
{
    static int installed;

    const int KeepNewest = 20;
    static readonly Regex TokenArgument = new(@"(--token(?:\s+|=))(""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The command line with the value of <c>--token</c> replaced, so a connection secret never reaches a log file.</summary>
    public static string RedactedCommandLine() => Redact(Environment.CommandLine);

    internal static string Redact(string text) => TokenArgument.Replace(text, "$1***");

    /// <summary>Keeps the newest <see cref="KeepNewest"/> crash and session logs and scrubs tokens that older builds wrote into the ones that stay.</summary>
    static void Prune()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRD");
            if (!Directory.Exists(directory)) return;
            foreach (var pattern in new[] { "crash-*.log", "session-*.log" })
            {
                var files = new DirectoryInfo(directory).GetFiles(pattern).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (i >= KeepNewest) { files[i].Delete(); continue; }
                        if (pattern.StartsWith("crash", StringComparison.Ordinal))
                        {
                            var text = File.ReadAllText(files[i].FullName);
                            var clean = Redact(text);
                            if (!ReferenceEquals(text, clean) && text != clean) File.WriteAllText(files[i].FullName, clean);
                        }
                    }
                    catch (Exception) { /* a log that is in use or read-only is left alone */ }
                }
            }
        }
        catch (Exception) { /* housekeeping must never stop the program */ }
    }

    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        Prune();
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
                $"{DateTimeOffset.Now:O} {kind} {RedactedCommandLine()}\n{error}\n\n");
            Console.Error.WriteLine($"[crash-log] {kind}: {error}");
        }
        catch (Exception) { /* never fail while reporting a failure */ }
    }
}
