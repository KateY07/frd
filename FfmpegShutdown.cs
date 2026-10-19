using System.Diagnostics;

namespace Frd;

/// <summary>Keeps window shutdown from hanging on a stalled peer: bounded steps plus a process-level watchdog.</summary>
static class ShutdownGuard
{
    public static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(3);
    static int armed;

    /// <summary>Exits the process if graceful shutdown has not finished in time (Exit first, Kill if that stalls too).</summary>
    public static void Arm(string source, TimeSpan limit)
    {
        if (Interlocked.Exchange(ref armed, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(limit);
            SessionTrace.Event(source, "shutdown-watchdog-exit");
            Console.Error.WriteLine($"[{source}] Graceful shutdown exceeded {limit.TotalSeconds:0}s; exiting.");
            _ = Task.Run(async () => { await Task.Delay(2000); try { Process.GetCurrentProcess().Kill(); } catch { } });
            Environment.Exit(Environment.ExitCode);
        });
    }

    public static void Kill(string source)
    {
        SessionTrace.Event(source, "shutdown-forced");
        Console.Error.WriteLine($"[{source}] Forced exit requested.");
        _ = Task.Run(async () => { await Task.Delay(1500); try { Process.GetCurrentProcess().Kill(); } catch { } });
        Environment.Exit(Environment.ExitCode);
    }

    /// <summary>Runs one shutdown step; returns false (and logs) if it fails to finish within the limit.</summary>
    public static async Task<bool> Bounded(Func<Task> step, string name, TimeSpan? limit = null)
    {
        try { await step().WaitAsync(limit ?? StepLimit); return true; }
        catch (TimeoutException)
        {
            SessionTrace.Event("shutdown", name + "-timed-out");
            Console.Error.WriteLine($"[shutdown] {name} did not finish in time; continuing.");
            return false;
        }
    }
}
