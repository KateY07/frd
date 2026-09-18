using System.Buffers.Binary;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Frd;

public sealed record CursorShape(int Width, int Height, int HotX, int HotY, byte[] Color, byte[] Mask);
public sealed record CursorUpdate(long Id, bool Visible, double X, double Y, CursorShape? Shape = null, bool Reset = false);

public static class CursorWire
{
    public static async Task WriteAsync(NetworkStream stream, CursorUpdate update, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(update);
        if (bytes.Length > 524288) throw new InvalidDataException("Cursor message too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
    public static async Task<CursorUpdate> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 524288) throw new InvalidDataException("Invalid cursor message length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        var update = JsonSerializer.Deserialize<CursorUpdate>(bytes) ?? throw new InvalidDataException("Empty cursor message.");
        if (update.Id < 0 || !double.IsFinite(update.X) || !double.IsFinite(update.Y)) throw new InvalidDataException("Invalid cursor state.");
        if (update.Shape != null) NativeCursor.Validate(update.Shape);
        return update;
    }
    public static async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        Dictionary<string, long> known = new();
        CursorUpdate? previous = null;
        long nextId = 0;
        nint previousHandle = 0;
        var bounds = Win32InputInjector.ReadPrimaryMonitor();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        do
        {
            var state = NativeCursor.ReadState();
            CursorShape? shape = null;
            var reset = false;
            var id = previous?.Id ?? 0;
            if (state.Handle != 0 && (previous == null || state.Handle != previousHandle))
            {
                shape = NativeCursor.ReadShape(state.Handle);
                var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(shape)));
                if (known.TryGetValue(fingerprint, out id)) shape = null;
                else
                {
                    if (known.Count >= 64) { known.Clear(); reset = true; }
                    id = ++nextId; known[fingerprint] = id;
                }
            }
            if (state.Handle == 0) id = 0;
            var update = new CursorUpdate(id, state.Visible, (state.X - bounds.Left) / (double)Math.Max(1, bounds.Width - 1),
                (state.Y - bounds.Top) / (double)Math.Max(1, bounds.Height - 1), shape, reset);
            if (previous == null || update.Id != previous.Id || update.Visible != previous.Visible || update.X != previous.X || update.Y != previous.Y || shape != null)
                await WriteAsync(client.GetStream(), update, token);
            previous = update;
            previousHandle = state.Handle;
        } while (await timer.WaitForNextTickAsync(token));
    }
}

public sealed class CursorClient : IAsyncDisposable
{
    readonly TcpClient client;
    readonly CancellationTokenSource stop;
    readonly Task reader;
    public long Updates { get; internal set; }
    public long Shapes { get; internal set; }
    public CursorClient(TcpClient client, Action<CursorUpdate> received, Action<Exception> failed, CancellationToken token)
    {
        this.client = client; stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        reader = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var update = await CursorWire.ReadAsync(client.GetStream(), stop.Token);
                    Updates++; if (update.Shape != null) Shapes++;
                    received(update);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Cursor feedback stopped."); }
            catch (Exception error) { Console.Error.WriteLine("[cursor] " + error); if (!stop.IsCancellationRequested) failed(error); }
        });
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); client.Dispose(); await reader; stop.Dispose(); }
}

public static class NativeCursor
{
    public static (nint Handle, bool Visible, int X, int Y) ReadState()
    {
        var state = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref state)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (state.Handle, (state.Flags & 1) != 0, state.X, state.Y);
    }
    public static CursorShape ReadShape(nint handle)
    {
        if (!GetIconInfo(handle, out var icon)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (GetObject(icon.Mask, Marshal.SizeOf<Bitmap>(), out var mask) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var height = icon.Color == 0 ? mask.Height / 2 : mask.Height;
            if (mask.Width is < 1 or > 256 || height is < 1 or > 256) throw new InvalidDataException("Unsupported cursor dimensions.");
            var result = new CursorShape(mask.Width, height, checked((int)icon.HotX), checked((int)icon.HotY),
                icon.Color == 0 ? [] : ReadBits(icon.Color, mask.Width, height, 32), ReadBits(icon.Mask, mask.Width, mask.Height, 1));
            Validate(result); return result;
        }
        finally { if (icon.Color != 0) DeleteObject(icon.Color); if (icon.Mask != 0) DeleteObject(icon.Mask); }
    }
    public static void Validate(CursorShape shape)
    {
        if (shape.Width is < 1 or > 256 || shape.Height is < 1 or > 256 || shape.HotX < 0 || shape.HotX >= shape.Width || shape.HotY < 0 || shape.HotY >= shape.Height ||
            shape.Color == null || shape.Mask == null || shape.Color.Length != 0 && shape.Color.Length != shape.Width * shape.Height * 4 ||
            shape.Mask.Length != ((shape.Width + 31) / 32 * 4) * shape.Height * (shape.Color.Length == 0 ? 2 : 1))
            throw new InvalidDataException("Invalid cursor bitmap, mask or hotspot.");
    }
    public static nint Create(CursorShape shape)
    {
        Validate(shape);
        nint color = 0, mask = 0;
        try
        {
            if (shape.Color.Length != 0) color = CreateBits(shape.Width, shape.Height, 32, shape.Color);
            mask = CreateBits(shape.Width, shape.Height * (shape.Color.Length == 0 ? 2 : 1), 1, shape.Mask);
            var icon = new IconInfo { HotX = (uint)shape.HotX, HotY = (uint)shape.HotY, Color = color, Mask = mask };
            var cursor = CreateIconIndirect(ref icon);
            if (cursor == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return cursor;
        }
        finally { if (color != 0) DeleteObject(color); if (mask != 0) DeleteObject(mask); }
    }
    static BitmapInfo Info(int width, int height, ushort depth) => new()
        { Size = 40, Width = width, Height = height, Planes = 1, BitCount = depth, White = 0x00FFFFFF };
    static byte[] ReadBits(nint bitmap, int width, int height, ushort depth)
    {
        var bytes = new byte[((width * depth + 31) / 32 * 4) * height];
        var info = Info(width, height, depth);
        var dc = GetDC(0);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { if (GetDIBits(dc, bitmap, 0, (uint)height, bytes, ref info, 0) != height) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { ReleaseDC(0, dc); }
        return bytes;
    }
    static nint CreateBits(int width, int height, ushort depth, byte[] bytes)
    {
        var info = Info(width, height, depth);
        var bitmap = CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.Copy(bytes, 0, bits, bytes.Length); return bitmap;
    }
    public static void Release(nint cursor) { if (cursor != 0 && !DestroyCursor(cursor)) Console.Error.WriteLine("[cursor] DestroyCursor: " + Marshal.GetLastWin32Error()); }
    public static nint Arrow => LoadCursor(0, 32512);
    public static void Set(nint cursor) => SetCursor(cursor);
    [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int Size, Flags; public nint Handle; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { public int IsIcon; public uint HotX, HotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] struct Bitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important, Black, White; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll")] static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int GetObject(nint obj, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bytes, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
}
