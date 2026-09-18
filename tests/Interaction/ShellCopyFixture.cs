using System.Runtime.InteropServices;

namespace Frd.InteractionTests;

static class ShellCopyFixture
{
    public static object Run(string[] sources, string destination)
    {
        Directory.CreateDirectory(destination);
        List<string> copied = new(); List<string> extras = new();
        for (var i = 0; i < sources.Length; i++)
        {
            var slot = Path.Combine(destination, i.ToString()); Directory.CreateDirectory(slot);
            Copy(sources[i], slot);
            var target = Path.Combine(slot, Path.GetFileName(sources[i])); copied.Add(target);
        }
        if (!TestData.Equal(sources, copied.ToArray())) throw new IOException("Windows Shell copied content mismatch.");
        for (var i = 0; i < copied.Count; i++)
        {
            var target = copied[i];
            if (Directory.Exists(target))
            {
                var extra = Path.Combine(target, "keep-existing-file.txt"); File.WriteAllText(extra, "keep this during merge"); extras.Add(extra);
                foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Where(file => file != extra))
                    File.WriteAllText(file, "replace this test content");
            }
            else File.WriteAllText(target, "replace this test content");
            Copy(sources[i], Path.GetDirectoryName(target)!);
        }
        for (var i = 0; i < sources.Length; i++)
        {
            if (File.Exists(sources[i]))
            {
                if (TestData.Digest(sources[i]) != TestData.Digest(copied[i])) throw new IOException("Windows Shell replacement mismatch.");
            }
            else foreach (var source in Directory.EnumerateFileSystemEntries(sources[i], "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copied[i], Path.GetRelativePath(sources[i], source));
                if (Directory.Exists(source) ? !Directory.Exists(target) : TestData.Digest(source) != TestData.Digest(target))
                    throw new IOException("Windows Shell merged content mismatch.");
            }
        }
        if (extras.Any(path => File.ReadAllText(path) != "keep this during merge")) throw new IOException("Merge removed existing destination data.");
        return new { Passed = true, FilesReadable = true, ShellCopy = true, Replacement = true, DirectoryMerge = true,
            KeptExistingFiles = extras.Count, Note = "Windows Shell copy engine; automatic confirmation applies only to synthetic test files." };
    }
    static void Copy(string source, string destination)
    {
        var operation = new FileOperation { Function = 2, From = source + '\0' + '\0', To = destination + '\0' + '\0', Flags = 0x614 };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.Aborted) throw new IOException($"Windows Shell copy failed: 0x{result:X}, aborted={operation.Aborted}.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOperation
    {
        public nint Window; public uint Function; public string From, To; public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings; public string? ProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref FileOperation operation);
}
