using System.Collections.Concurrent;
using System.Drawing;
using System.Numerics;
using Color = Vortice.Mathematics.Color4;
using static Glasspane.UiNative;

namespace Frd;

/// <summary>
/// The host's annotation layer: a transparent, click-through, topmost window covering the primary monitor that shows what the controller
/// draws. It is excluded from screen capture (the controller already draws the same marks locally) and never takes focus.
/// <para>
/// It is deliberately cheap: steps queue up from the network thread and are applied in small batches on a 30 Hz timer, the surface is only
/// redrawn when something changed, and the window is hidden whenever there is nothing to show.
/// </para>
/// </summary>
sealed class UiAnnotationOverlay : UiWindow
{
    readonly UiDevices devices = new();
    readonly UiText text;
    readonly AnnotationBoard board = new();
    readonly ConcurrentQueue<AnnotationMessage> inbox = new();
    readonly UiTimer timer = new(33);
    UiSurface? surface;
    UiPaint? paint;
    bool shown, dirty;
    AnnotationKind kind = AnnotationKind.Pen;
    Color color = new(1, .3f, .3f, 1);
    float size = .004f;

    public UiAnnotationOverlay()
    {
        var area = Win32InputInjector.ReadPrimaryMonitor();
        CreateNative("FRD annotation", WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP | 0x20 /* WS_EX_TRANSPARENT */ | 0x80000 /* WS_EX_LAYERED */,
            area.Left, area.Top, area.Width, area.Height);
        // WS_EX_LAYERED together with WS_EX_TRANSPARENT is what lets mouse input fall through to the windows below, whatever process they belong to
        // (answering HTTRANSPARENT to WM_NCHITTEST only works for windows of the same thread).
        SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */);
        text = new UiText(devices.DWrite);
        ExcludeFromCapture();
        surface = new UiSurface(devices, Handle, true);
        paint = new UiPaint(devices, surface.Context, text);
        timer.Tick += Pump;
        timer.Start();
    }

    /// <summary>Queues a step from any thread.</summary>
    public void Enqueue(AnnotationMessage message) => inbox.Enqueue(message);

    /// <summary>Drops everything (UI thread).</summary>
    public void Reset()
    {
        while (inbox.TryDequeue(out _)) { }
        board.Clear(); dirty = true;
    }

    void Pump()
    {
        // At most 300 steps per tick, so a flood cannot hold the UI thread (which also runs the host status card).
        for (var i = 0; i < 300 && inbox.TryDequeue(out var message); i++) Apply(message);
        var want = board.HasShapes;
        if (want != shown) { shown = want; if (want) { Show(SW_SHOWNOACTIVATE); SetTopmost(true); } else Hide(); dirty = true; }
        if (dirty && shown) Render();
        dirty = false;
    }

    void Apply(AnnotationMessage message)
    {
        dirty = true;
        switch (message.Op)
        {
            case "begin":
                if (message.Points is { Length: >= 2 } start && message.Kind is "pen" or "rect")
                {
                    kind = message.Kind == "rect" ? AnnotationKind.Rectangle : AnnotationKind.Pen;
                    color = FromArgb(message.Color); size = Math.Clamp(message.Width, .0005f, .05f);
                    board.Begin(kind, new Vector2(start[0], start[1]), color, size);
                }
                break;
            case "add":
                if (message.Points is { } points) for (var i = 0; i + 1 < points.Length; i += 2) board.Add(new Vector2(points[i], points[i + 1]));
                break;
            case "text":
                if (message.Points is { Length: >= 2 } at && message.Text != null)
                    board.AddText(new Vector2(at[0], at[1]), message.Text, FromArgb(message.Color), Math.Clamp(message.Width, .004f, .1f));
                break;
            case "end": board.End(); break;
            case "undo": board.Undo(); break;
            case "clear": board.Clear(); break;
        }
    }

    static Color FromArgb(uint argb) => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

    void Render()
    {
        if (surface == null || paint == null || IsDestroyed || ClientWidth <= 0) return;
        try
        {
            surface.Resize(ClientWidth, ClientHeight, Dpi);
            surface.Begin();
            surface.Context.Clear(new Color(0, 0, 0, 0));
            board.Draw(paint, new RectangleF(0, 0, ClientWidth / Scale, ClientHeight / Scale));
            surface.End();
            surface.Present(0).CheckError();
        }
        catch (Exception error) { Console.Error.WriteLine($"[annotation] draw failed: {error.Message}"); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    protected override nint OnMessage(uint message, nuint wParam, nint lParam, out bool handled)
    {
        handled = false;
        if (message == WM_NCHITTEST) { handled = true; return -1; /* HTTRANSPARENT: clicks go to the window below */ }
        if (message == WM_MOUSEACTIVATE) { handled = true; return 3; /* MA_NOACTIVATE */ }
        return 0;
    }

    protected override void OnDestroying()
    {
        timer.Dispose();
        paint?.Dispose(); surface?.Dispose(); devices.Dispose();
    }
}
