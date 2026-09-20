using System.IO.MemoryMappedFiles;
using System.Diagnostics;

namespace Frd;

sealed unsafe class SecureDesktopShared : IDisposable
{
    const int Magic = 0x46524453, Version = 1, HeaderSize = 64, SlotHeaderSize = 16, Slots = 3;
    const int MaxWidth = 3840, MaxHeight = 2160;
    const int MaxPixels = MaxWidth * MaxHeight * 4;
    const int SlotSize = SlotHeaderSize + MaxPixels;
    public const long Capacity = HeaderSize + (long)Slots * SlotSize;
    readonly FileStream file;
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    readonly bool writer;
    byte* address;
    long published, consumed;
    bool disposed;

    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FRD", "SecureDesktopProbe", "frames.map");

    public static SecureDesktopShared? TryOpen(bool writer)
    {
        if (!writer && !File.Exists(PathName)) return null;
        return new(PathName, writer);
    }

    internal SecureDesktopShared(string path, bool writer)
    {
        this.writer = writer;
        if (writer) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file = new(path, writer ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (writer && file.Length != Capacity) file.SetLength(Capacity);
        if (file.Length != Capacity) throw new InvalidDataException("Secure desktop shared frame file has an unexpected size.");
        mapping = MemoryMappedFile.CreateFromFile(file, null, Capacity, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true);
        view = mapping.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
        address += view.PointerOffset;
        if (writer)
        {
            Volatile.Write(ref Int32(8), 0);
            for (var slot = 0; slot < Slots; slot++) Volatile.Write(ref State(slot), 0);
            published = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref Int64(16), published);
            Volatile.Write(ref Int32(4), Version);
            Volatile.Write(ref Int32(0), Magic);
        }
    }

    public bool Active => Valid && Volatile.Read(ref Int32(8)) == 1;
    bool Valid => Volatile.Read(ref Int32(0)) == Magic && Volatile.Read(ref Int32(4)) == Version;
    public void SetInactive()
    {
        if (!writer) throw new InvalidOperationException("Only the secure worker may publish frames.");
        Volatile.Write(ref Int32(8), 0);
    }

    public bool Publish(int width, int height, byte[] pixels)
    {
        if (!writer) throw new InvalidOperationException("Only the secure worker may publish frames.");
        if (width < 64 || height < 64 || width > MaxWidth || height > MaxHeight ||
            pixels.Length != checked(width * height * 4)) return false;
        var latest = Volatile.Read(ref Int32(12));
        var chosen = -1;
        for (var slot = 0; slot < Slots; slot++)
            if (Interlocked.CompareExchange(ref State(slot), 1, 0) == 0) { chosen = slot; break; }
        if (chosen < 0)
            for (var slot = 0; slot < Slots; slot++)
                if (slot != latest && Interlocked.CompareExchange(ref State(slot), 1, 2) == 2)
                { chosen = slot; break; }
        if (chosen < 0) return false;
        var target = address + HeaderSize + chosen * SlotSize;
        try
        {
            fixed (byte* source = pixels) Buffer.MemoryCopy(source, target + SlotHeaderSize, MaxPixels, pixels.Length);
            *(int*)target = width;
            *(int*)(target + 4) = height;
            *(int*)(target + 8) = pixels.Length;
            Volatile.Write(ref State(chosen), 2);
            Volatile.Write(ref Int32(12), chosen);
            Interlocked.Exchange(ref Int64(16), ++published);
            Volatile.Write(ref Int32(8), 1);
            return true;
        }
        catch
        {
            Volatile.Write(ref State(chosen), 0);
            throw;
        }
    }

    public (bool Active, SharedFrame? Frame, int Width, int Height) Take(bool repeat = false)
    {
        if (!Active) return (false, null, 0, 0);
        var sequence = Volatile.Read(ref Int64(16));
        var slot = Volatile.Read(ref Int32(12));
        if (slot is < 0 or >= Slots) return (false, null, 0, 0);
        var header = address + HeaderSize + slot * SlotSize;
        var width = *(int*)header;
        var height = *(int*)(header + 4);
        var length = *(int*)(header + 8);
        if (width < 64 || height < 64 || width > MaxWidth || height > MaxHeight ||
            length != checked(width * height * 4)) return (false, null, 0, 0);
        if (!repeat && sequence == consumed) return (true, null, width, height);
        if (Interlocked.CompareExchange(ref State(slot), 3, 2) != 2) return (true, null, width, height);
        if (!Active || sequence != Volatile.Read(ref Int64(16)))
        {
            Volatile.Write(ref State(slot), 2);
            return (true, null, width, height);
        }
        consumed = sequence;
        return (true, new((nint)(header + SlotHeaderSize), width, height,
            () => Volatile.Write(ref State(slot), 2)), width, height);
    }

    ref int Int32(int offset) => ref *(int*)(address + offset);
    ref long Int64(int offset) => ref *(long*)(address + offset);
    ref int State(int slot) => ref Int32(32 + slot * 4);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose(); mapping.Dispose(); file.Dispose();
    }
}

sealed class SharedFrame(nint pixels, int width, int height, Action release) : IDisposable
{
    Action? releaseFrame = release;
    public nint Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public void Dispose() => Interlocked.Exchange(ref releaseFrame, null)?.Invoke();
}
