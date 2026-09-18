using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

public sealed record ClipboardContents(string? Text = null, string[]? Paths = null, string? Origin = null);
public interface IClipboardAccess
{
    uint Sequence { get; }
    ClipboardContents? Read();
    void Write(ClipboardContents contents);
}

public sealed class WindowsClipboard(nint owner) : IClipboardAccess
{
    const uint UnicodeText = 13, FileDrop = 15;
    static readonly uint originFormat = RegisterClipboardFormat("FRD.Clipboard.Origin.v1");
    static readonly uint dropEffect = RegisterClipboardFormat("Preferred DropEffect");
    public uint Sequence => GetClipboardSequenceNumber();

    public ClipboardContents? Read()
    {
        Open();
        try
        {
            var origin = ReadString(originFormat, 256);
            if (IsClipboardFormatAvailable(FileDrop))
            {
                var handle = GetClipboardData(FileDrop);
                var count = DragQueryFile(handle, uint.MaxValue, null, 0);
                if (count > 128) throw new InvalidDataException("剪贴板最多支持 128 个顶层文件或目录。");
                var paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(handle, i, null, 0);
                    if (length is 0 or > 32767) throw new InvalidDataException("Invalid clipboard path length.");
                    var name = new StringBuilder((int)length + 1);
                    if (DragQueryFile(handle, i, name, length + 1) != length) throw new Win32Exception(Marshal.GetLastWin32Error());
                    paths[i] = name.ToString();
                }
                return paths.Length == 0 ? null : new(Paths: paths, Origin: origin);
            }
            var text = ReadString(UnicodeText, ClipboardSyncSession.MaximumTextBytes * 2);
            return text == null ? null : new(Text: text, Origin: origin);
        }
        finally { Close(); }
    }

    public void Write(ClipboardContents contents)
    {
        if (owner == 0) throw new InvalidOperationException("Clipboard owner window is not available.");
        byte[] data;
        uint format;
        if (contents.Paths is { Length: > 0 } paths)
        {
            var names = Encoding.Unicode.GetBytes(string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0");
            data = new byte[20 + names.Length];
            BinaryPrimitives.WriteInt32LittleEndian(data, 20);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1);
            names.CopyTo(data, 20); format = FileDrop;
        }
        else if (contents.Text != null) { data = Encoding.Unicode.GetBytes(contents.Text + '\0'); format = UnicodeText; }
        else throw new ArgumentException("Clipboard must contain text or file paths.");
        Open();
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetBytes(format, data);
            if (format == FileDrop) SetBytes(dropEffect, [1, 0, 0, 0]);
            if (contents.Origin != null) SetBytes(originFormat, Encoding.Unicode.GetBytes(contents.Origin + '\0'));
        }
        finally { Close(); }
    }

    void Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner)) return;
            Thread.Sleep(15);
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Clipboard is busy.");
    }
    static void Close() { if (!CloseClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static string? ReadString(uint format, int maximum)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var handle = GetClipboardData(format);
        var size = checked((long)GlobalSize(handle));
        if (size < 2 || size > maximum) throw new InvalidDataException("Clipboard text exceeds the supported size.");
        var pointer = GlobalLock(handle);
        if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var value = Marshal.PtrToStringUni(pointer, (int)size / 2)!;
            var end = value.IndexOf('\0');
            return end < 0 ? value : value[..end];
        }
        finally { GlobalUnlock(handle); }
    }
    internal static void SetBytes(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(2, (nuint)bytes.Length);
        if (handle == 0) throw new OutOfMemoryException();
        var transferred = false;
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(handle); }
            if (SetClipboardData(format, handle) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            transferred = true;
        }
        finally { if (!transferred) GlobalFree(handle); }
    }
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint handle);
}

public sealed record ClipboardEntry(string Path, bool Directory, long Length);
public sealed record ClipboardManifest(string Kind, Guid Id, int TextBytes, string[] Roots, ClipboardEntry[] Entries);
public sealed record ClipboardSyncStatus(long Sent, long Received, long SentBytes, long ReceivedBytes, string Message, bool Connected);

public sealed class ClipboardSyncSession : IAsyncDisposable
{
    public const int MaximumTextBytes = 4 * 1024 * 1024, MaximumEntries = 10000;
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    readonly TcpClient client;
    readonly IClipboardAccess clipboard;
    const string OriginPrefix = "FRD.clipboard:";
    readonly string cacheRoot, marker = OriginPrefix + Guid.NewGuid().ToString("N");
    readonly CancellationTokenSource stop;
    readonly object clipboardGate = new();
    readonly Channel<ClipboardContents> outbound = Channel.CreateBounded<ClipboardContents>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    readonly Task work;
    uint lastSequence;
    long sent, received, sentBytes, receivedBytes;
    int disposed;
    public event Action<ClipboardSyncStatus>? Status;
    ClipboardSyncStatus snapshot = new(0, 0, 0, 0, "等待新复制的文本或文件", true);
    public ClipboardSyncStatus Snapshot => Volatile.Read(ref snapshot);
    public Task Completion => work;

    public ClipboardSyncSession(TcpClient client, IClipboardAccess clipboard, string? cacheRoot = null, CancellationToken token = default)
    {
        this.client = client; this.clipboard = clipboard;
        this.cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRD", "Clipboard"));
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        lastSequence = clipboard.Sequence;
        work = Task.WhenAll(Run(WatchAsync), Run(SendAsync), Run(ReceiveAsync));
    }

    async Task Run(Func<Task> action)
    {
        try { await Task.Run(action); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("[clipboard] Synchronization stopped."); }
        catch (Exception error)
        {
            Console.Error.WriteLine("[clipboard] " + error);
            Publish(stop.IsCancellationRequested ? "剪贴板同步已关闭" : "剪贴板同步断开：" + error.Message, false);
        }
        finally { stop.Cancel(); client.Dispose(); }
    }

    async Task WatchAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            lock (clipboardGate)
            {
                var sequence = clipboard.Sequence;
                if (sequence != lastSequence)
                {
                    try
                    {
                        var contents = clipboard.Read(); lastSequence = sequence;
                        if (contents != null && contents.Origin?.StartsWith(OriginPrefix, StringComparison.Ordinal) != true) outbound.Writer.TryWrite(contents);
                    }
                    catch (Win32Exception error) { Console.Error.WriteLine("[clipboard] Busy, will retry: " + error.Message); }
                    catch (InvalidDataException error)
                    { lastSequence = sequence; Console.Error.WriteLine("[clipboard] " + error); Publish("复制未同步：" + error.Message); }
                }
            }
            await Task.Delay(150, stop.Token);
        }
    }

    async Task SendAsync()
    {
        var stream = client.GetStream();
        await foreach (var contents in outbound.Reader.ReadAllAsync(stop.Token))
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ClipboardManifest manifest;
            byte[]? text = null;
            try
            {
                if (contents.Paths is { Length: > 0 } paths) manifest = PrepareFiles(paths, files);
                else
                {
                    text = Encoding.UTF8.GetBytes(contents.Text ?? "");
                    manifest = new("text", Guid.NewGuid(), text.Length, [], []);
                }
                Validate(manifest);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { Console.Error.WriteLine("[clipboard] Copy rejected: " + error); Publish("复制未同步：" + error.Message); continue; }
            Publish(manifest.Kind == "text" ? "正在同步文本" : $"正在同步 {manifest.Entries.Count(x => !x.Directory)} 个文件");
            var header = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (header.Length > 2 * 1024 * 1024) throw new InvalidDataException("Clipboard manifest is too large.");
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, header.Length);
            await stream.WriteAsync(prefix, stop.Token); await stream.WriteAsync(header, stop.Token);
            if (text != null) { await stream.WriteAsync(text, stop.Token); Interlocked.Add(ref sentBytes, text.Length); }
            else foreach (var entry in manifest.Entries.Where(entry => !entry.Directory))
            {
                var path = files[entry.Path]; RejectReparse(path);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length != entry.Length) throw new IOException("复制过程中源文件发生变化。");
                var hash = await TransferAsync(file, stream, entry.Length);
                await stream.WriteAsync(hash, stop.Token); Interlocked.Add(ref sentBytes, entry.Length);
            }
            Interlocked.Increment(ref sent); Publish("剪贴板内容已发送");
        }
    }

    async Task ReceiveAsync()
    {
        var stream = client.GetStream();
        while (!stop.IsCancellationRequested)
        {
            var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, stop.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is < 2 or > 2 * 1024 * 1024) throw new InvalidDataException("Invalid clipboard manifest size.");
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, stop.Token);
            var manifest = JsonSerializer.Deserialize<ClipboardManifest>(bytes) ?? throw new InvalidDataException("Missing clipboard manifest.");
            Validate(manifest);
            uint sequence;
            lock (clipboardGate) sequence = clipboard.Sequence;
            ClipboardContents contents;
            string? directory = null;
            var committed = false;
            try
            {
                if (manifest.Kind == "text")
                {
                    var text = new byte[manifest.TextBytes]; await stream.ReadExactlyAsync(text, stop.Token);
                    contents = new(new UTF8Encoding(false, true).GetString(text), Origin: marker);
                    Interlocked.Add(ref receivedBytes, text.Length);
                }
                else
                {
                    Directory.CreateDirectory(cacheRoot); RejectReparse(cacheRoot);
                    var used = Directory.EnumerateFiles(cacheRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(path => new FileInfo(path).Length);
                    var incoming = manifest.Entries.Sum(entry => entry.Length);
                    if (used + incoming > MaximumFileBytes * 2) throw new IOException("剪贴板缓存超过 4 GiB，请在粘贴完成后清理 FRD/Clipboard 缓存。");
                    directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    foreach (var entry in manifest.Entries)
                    {
                        var destination = Resolve(directory, entry.Path);
                        if (entry.Directory) { Directory.CreateDirectory(destination); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        {
                            var actual = await TransferAsync(stream, file, entry.Length);
                            var expected = new byte[32]; await stream.ReadExactlyAsync(expected, stop.Token);
                            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidDataException("剪贴板文件 SHA-256 校验失败。");
                        }
                        Interlocked.Add(ref receivedBytes, entry.Length);
                    }
                    contents = new(Paths: manifest.Roots.Select(path => Resolve(directory, path)).ToArray(), Origin: marker);
                }
                lock (clipboardGate)
                {
                    // Do not overwrite a newer local copy made while a large incoming transfer was running.
                    if (clipboard.Sequence == sequence)
                    {
                        clipboard.Write(contents); lastSequence = clipboard.Sequence; committed = true;
                        Interlocked.Increment(ref received);
                    }
                }
                Publish(committed ? "已接收，可在本机粘贴" : "跳过旧传输：本机已有新的复制内容");
            }
            finally
            {
                if (!committed && directory != null && Path.GetDirectoryName(Path.GetFullPath(directory)) == cacheRoot)
                    Directory.Delete(directory, true);
            }
        }
    }

    async Task<byte[]> TransferAsync(Stream source, Stream destination, long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            for (long remaining = length; remaining > 0;)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), stop.Token);
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), stop.Token);
                remaining -= count;
            }
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    static ClipboardManifest PrepareFiles(string[] paths, Dictionary<string, string> files)
    {
        if (paths.Length is < 1 or > 128) throw new InvalidDataException("最多同步 128 个顶层文件或目录。");
        List<ClipboardEntry> entries = new(); List<string> roots = new();
        long total = 0;
        void Visit(string source, string relative)
        {
            RejectReparse(source);
            var directory = Directory.Exists(source);
            var length = directory ? 0 : new FileInfo(source).Length;
            total = checked(total + length);
            if (entries.Count >= MaximumEntries || total > MaximumFileBytes) throw new InvalidDataException("单次复制上限为 10000 项、2 GiB。");
            entries.Add(new(relative, directory, length));
            if (directory) foreach (var child in Directory.EnumerateFileSystemEntries(source)) Visit(child, relative + "/" + Path.GetFileName(child));
            else files.Add(relative, source);
        }
        for (var i = 0; i < paths.Length; i++)
        {
            var full = Path.GetFullPath(paths[i]);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            if (string.IsNullOrEmpty(name)) throw new InvalidDataException("请复制文件或目录，不支持整个驱动器。");
            var relative = $"item-{i:D3}/" + name;
            roots.Add(relative); Visit(full, relative);
        }
        return new("files", Guid.NewGuid(), 0, roots.ToArray(), entries.ToArray());
    }

    public static void Validate(ClipboardManifest manifest)
    {
        if (manifest.Roots == null || manifest.Entries == null || manifest.Id == Guid.Empty) throw new InvalidDataException("Invalid clipboard manifest.");
        if (manifest.Kind == "text")
        {
            if (manifest.TextBytes is < 0 or > MaximumTextBytes || manifest.Roots.Length != 0 || manifest.Entries.Length != 0) throw new InvalidDataException("Invalid clipboard text size.");
            return;
        }
        if (manifest.Kind != "files" || manifest.TextBytes != 0 || manifest.Roots.Length is < 1 or > 128 || manifest.Entries.Length is < 1 or > MaximumEntries)
            throw new InvalidDataException("Invalid clipboard file manifest.");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in manifest.Entries)
        {
            if (entry == null) throw new InvalidDataException("Null clipboard entry.");
            ValidPath(entry.Path);
            if (!paths.Add(entry.Path) || entry.Length < 0 || entry.Directory && entry.Length != 0 || entry.Length > MaximumFileBytes)
                throw new InvalidDataException("Invalid clipboard file entry.");
            total = checked(total + entry.Length);
            if (total > MaximumFileBytes) throw new InvalidDataException("Clipboard transfer exceeds 2 GiB.");
            if (!manifest.Roots.Any(root => entry.Path.Equals(root, StringComparison.OrdinalIgnoreCase) || entry.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("File is outside the copied roots.");
        }
        if (manifest.Roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Roots.Length || manifest.Roots.Any(root => !paths.Contains(root)))
            throw new InvalidDataException("Missing or duplicate clipboard root.");
        foreach (var root in manifest.Roots) ValidPath(root);
    }
    static void ValidPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2048 || path.Contains('\\') || Path.IsPathRooted(path)) throw new InvalidDataException("Invalid relative clipboard path.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException("Unsafe clipboard filename.");
        }
    }
    static string Resolve(string root, string path)
    {
        ValidPath(path);
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Clipboard path escaped its destination.");
        return full;
    }
    static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不自动跟随剪贴板文件中的符号链接或目录联接。");
    }
    void Publish(string message, bool connected = true)
    {
        Volatile.Write(ref snapshot, new(Interlocked.Read(ref sent), Interlocked.Read(ref received), Interlocked.Read(ref sentBytes), Interlocked.Read(ref receivedBytes), message, connected));
        try { Status?.Invoke(Snapshot); }
        catch (Exception error) { Console.Error.WriteLine("[clipboard] Status callback: " + error); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); client.Dispose(); await work; stop.Dispose();
    }
}
