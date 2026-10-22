using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Frd;

/// <summary>Grabs the primary monitor as top-down BGRA with GDI. Windows that exclude themselves from capture (the FRD cards) are not in it.</summary>
static class ScreenGrab
{
    public static (byte[] Bgra, int Width, int Height) CapturePrimary()
    {
        var area = Win32InputInjector.ReadPrimaryMonitor();
        int width = area.Width, height = area.Height;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidOperationException("Unsupported screen size.");
        var screen = GetDC(0);
        if (screen == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, 0x00CC0020 /* SRCCOPY: no layered windows */))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return (pixels, width, height);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
}

/// <summary>Minimal PNG writer for BGRA images (8-bit RGB, alpha dropped).</summary>
static class PngWriter
{
    static readonly uint[] crcTable = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static void Write(string path, byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4)) throw new ArgumentException("Image size does not match its pixels.");
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 3 + 1];   // row[0] = filter type 0 (none)
            for (var y = 0; y < height; y++)
            {
                var source = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    row[1 + x * 3] = bgra[source + x * 4 + 2];
                    row[2 + x * 3] = bgra[source + x * 4 + 1];
                    row[3 + x * 3] = bgra[source + x * 4];
                }
                zlib.Write(row, 0, row.Length);
            }
        }
        var temporary = path + ".part";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width); WriteBigEndian(header, 4, (uint)height);
            header[8] = 8; header[9] = 2;   // 8 bits per channel, truecolour
            Chunk(file, "IHDR", header);
            Chunk(file, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
            Chunk(file, "IEND", []);
        }
        File.Move(temporary, path, overwrite: true);
    }

    static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        number[0] = (byte)(data.Length >> 24); number[1] = (byte)(data.Length >> 16); number[2] = (byte)(data.Length >> 8); number[3] = (byte)data.Length;
        stream.Write(number);
        Span<byte> name = stackalloc byte[4];
        for (var i = 0; i < 4; i++) name[i] = (byte)type[i];
        stream.Write(name);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in name) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;
        number[0] = (byte)(crc >> 24); number[1] = (byte)(crc >> 16); number[2] = (byte)(crc >> 8); number[3] = (byte)crc;
        stream.Write(number);
    }

    static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16); target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value;
    }
}

/// <summary>Puts a BGRA image on the clipboard as a bottom-up 32-bit DIB.</summary>
static class ImageClipboard
{
    public static void Copy(nint owner, byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var size = 40 + stride * height;
        var memory = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (nuint)size);
        if (memory == 0) throw new OutOfMemoryException();
        var owned = true;
        try
        {
            var target = GlobalLock(memory);
            if (target == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Marshal.WriteInt32(target, 0, 40); Marshal.WriteInt32(target, 4, width); Marshal.WriteInt32(target, 8, height);
                Marshal.WriteInt16(target, 12, 1); Marshal.WriteInt16(target, 14, 32);
                for (var y = 0; y < height; y++) Marshal.Copy(bgra, (height - 1 - y) * stride, target + 40 + y * stride, stride);
            }
            finally { GlobalUnlock(memory); }
            if (!OpenClipboard(owner)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                EmptyClipboard();
                if (SetClipboardData(8 /* CF_DIB */, memory) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                owned = false;   // the clipboard owns the memory now
            }
            finally { CloseClipboard(); }
        }
        finally { if (owned) GlobalFree(memory); }
    }

    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
}
