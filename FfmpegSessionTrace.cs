namespace Frd;

static class SessionTrace
{
    static readonly object gate = new();
    static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FRD", $"session-{Environment.ProcessId}.log");
    static long sequence;

    public static void Event(string channel, string state) => Write(channel, state, null);
    public static void Error(string channel, Exception error) => Write(channel, "error", error);

    static void Write(string channel, string state, Exception? error)
    {
        var number = Interlocked.Increment(ref sequence);
        var details = error == null ? "" : $" type={error.GetType().FullName} hresult=0x{error.HResult:X8}" +
            (error.InnerException is { } inner ? $" inner={inner.GetType().FullName} innerHresult=0x{inner.HResult:X8}" : "") +
            (error.StackTrace is { } stack ? " stack=" + stack.Replace('\r', ' ').Replace('\n', '|') : "");
        var line = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} seq={number} channel={channel} state={state}{details}{Environment.NewLine}";
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
            }
        }
        catch (Exception failure) { Console.Error.WriteLine($"[session trace] {failure.GetType().Name}: {failure.Message}"); }
    }
}
