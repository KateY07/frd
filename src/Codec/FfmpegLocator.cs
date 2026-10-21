namespace Frd;

/// <summary>
/// Finds the optional FFmpeg libraries. Search order: the folder named by <c>FRD_FFMPEG_DIR</c>; an <c>ffmpeg</c> folder next to FRD.exe
/// (what the zip package unpacks to); then the per-user install <c>%LOCALAPPDATA%\FRD\ffmpeg\&lt;version&gt;\</c> (what the installer writes),
/// newest version first. The per-user location is shared by every FRD version and also works for the single-file frd-&lt;version&gt;.exe.
/// </summary>
static class FfmpegLocator
{
    static readonly string[] RequiredFiles = ["avcodec-62.dll", "avutil-60.dll", "swscale-9.dll", "swresample-6.dll"];

    public static string UserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRD", "ffmpeg");

    public static bool IsComplete(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && RequiredFiles.All(name => File.Exists(Path.Combine(directory, name)));

    /// <summary>The first complete library folder, or <paramref name="beside"/> when none exists (so that messages still name the conventional place).</summary>
    public static string Find(string beside)
    {
        var explicitDirectory = Environment.GetEnvironmentVariable("FRD_FFMPEG_DIR");
        if (IsComplete(explicitDirectory)) return Path.GetFullPath(explicitDirectory!);
        if (IsComplete(beside)) return Path.GetFullPath(beside);
        try
        {
            if (Directory.Exists(UserRoot))
            {
                var versions = new DirectoryInfo(UserRoot).GetDirectories()
                    .OrderByDescending(directory => Version.TryParse(directory.Name, out var version) ? version : new Version(0, 0))
                    .ThenByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var directory in versions) if (IsComplete(directory.FullName)) return directory.FullName;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* an unreadable profile folder just means "not installed" */ }
        return Path.GetFullPath(beside);
    }
}
