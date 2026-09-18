using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Frd;

static partial class FfmpegUi
{
    sealed record InteractionScript(int SourceWidth, int SourceHeight, int Left, int Top, int Width, int Height,
        string Report, string? HoldUntilFile = null, bool Clipboard = false, long FixtureWindow = 0, bool Input = true);

    sealed partial class DesktopWindow
    {
        readonly List<CursorUpdate> interactionCursors = new();
        readonly List<object> interactionInputTrace = new();
        async Task RunInteractionAsync(string path)
        {
            List<object> checks = new(); List<object> expected = new();
            var script = JsonSerializer.Deserialize<InteractionScript>(File.ReadAllText(path), AppConfiguration.JsonOptions) ?? throw new InvalidDataException("Missing interaction script.");
            void Check(bool passed, string name, object? evidence = null)
            {
                checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
                if (!passed) throw new InvalidOperationException("Interaction regression: " + name);
            }
            async Task Until(Func<bool> condition)
            {
                var started = Stopwatch.GetTimestamp();
                while (!condition())
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException("Interaction test condition not reached.");
                    await Task.Delay(20);
                }
            }
            try
            {
                preview.Input += input => interactionInputTrace.Add(new { Tick = Environment.TickCount64, input.Kind, input.ScanCode });
                Check(script.SourceWidth == session!.RemoteSourceWidth && script.SourceHeight == session.RemoteSourceHeight, "Fixture/source desktop dimensions agree");
                await Until(() => preview.VideoWidth == session.Configuration.Width && receivedFrames >= 3);
                foreach (var cap in new[] { .5m, 5m, 10m, 20m, 5m })
                {
                    if (cap > bitrateLabel.Maximum) continue;
                    var before = receivedFrames;
                    bitrateLabel.Value = cap;
                    await Until(() => !applying && pendingStatus?.AppliedLimitKbps == (int)(cap * 1000) && receivedFrames > before);
                    Check(Math.Abs(bitrate.Value - (double)cap) < .00001 && bitrateLabel.Value == cap,
                        "Mbps numeric input, slider and sender acknowledgement " + cap, new { Mbps = cap, SenderKbps = pendingStatus?.AppliedLimitKbps });
                }
                if (details.IsVisible) ToggleDetails();
                var bounds = Win32InputInjector.ReadPrimaryMonitor();
                foreach (var (size, scale) in new[] { (new Size(780, 520), 1d), (new Size(1060, 640), .75), (new Size(900, 820), .5) })
                {
                    Width = size.Width; Height = size.Height;
                    Position = new PixelPoint(Math.Max(0, bounds.Width - (int)(size.Width * RenderScaling) - 16), 16);
                    transmissionScale.SelectedIndex = scale == 1 ? 0 : scale == .75 ? 1 : 2;
                    var dimensions = TransmissionGeometry.Dimensions(script.SourceWidth, script.SourceHeight, scale);
                    await Until(() => !applying && session.Configuration.TransmissionScale == scale && preview.VideoWidth == dimensions.Width && preview.VideoHeight == dimensions.Height);
                    await Task.Delay(200);
                    Check(preview.VideoWidth == dimensions.Width && preview.VideoHeight == dimensions.Height, "Real decoded dimensions at " + scale + "×", new { size, dimensions.Width, dimensions.Height });
                    var watermarkOrigin = watermark.PointToScreen(new Point());
                    var clientOrigin = this.PointToScreen(new Point());
                    Check(watermark.VerifyPassThrough(preview.SourceWindow) && watermarkOrigin.X >= clientOrigin.X && watermarkOrigin.Y >= clientOrigin.Y &&
                        watermarkOrigin.X + watermark.ClientSize.Width * RenderScaling <= clientOrigin.X + ClientSize.Width * RenderScaling + 1 &&
                        watermarkOrigin.Y + watermark.ClientSize.Height * RenderScaling <= clientOrigin.Y + ClientSize.Height * RenderScaling + 1,
                        "Click-through, non-activating watermark stays inside resized preview", new { size, watermark.ClientSize });
                    if (!script.Input) continue;
                    await SetInputAsync(true);
                    for (var point = 0; point < 3; point++)
                    {
                        var targetX = script.Left + script.Width * (point + .5) / 3;
                        var targetY = script.Top + script.Height * .55;
                        GetClientRect(preview.SourceWindow, out var rectangle);
                        var x = (int)Math.Round(targetX / (script.SourceWidth - 1) * (rectangle.Right - 1));
                        var y = (int)Math.Round(targetY / (script.SourceHeight - 1) * (rectangle.Bottom - 1));
                        var lp = (nint)((y << 16) | (x & 0xFFFF));
                        var before = Interlocked.Read(ref inputSent);
                        SendMessage(preview.SourceWindow, 0x0200, 0, lp);
                        await Until(() => Interlocked.Read(ref inputSent) > before);
                        await Task.Delay(120);
                        SendMessage(preview.SourceWindow, 0x0201, 1, lp); await Task.Delay(70);
                        SendMessage(preview.SourceWindow, 0x0202, 0, lp); await Task.Delay(70);
                        if (script.FixtureWindow != 0 && remoteOptions?.Host == "127.0.0.1")
                        {
                            // A shared desktop moves focus away from the controller; isolate keyboard routing to our owned test fixture.
                            if (!SetForegroundWindow((nint)script.FixtureWindow)) throw new InvalidOperationException("Cannot focus local input fixture.");
                            await Task.Delay(60);
                        }
                        SendMessage(preview.SourceWindow, 0x0100, 0x58, (nint)(0x2D << 16 | 1));
                        SendMessage(preview.SourceWindow, 0x0101, 0x58, unchecked((nint)(long)(0xC0000001u | 0x2Du << 16)));
                        var screen = new InteractionPoint { X = x, Y = y }; ClientToScreen(preview.SourceWindow, ref screen);
                        SendMessage(preview.SourceWindow, 0x020A, (nuint)(120 << 16), (nint)((screen.Y << 16) | (screen.X & 0xFFFF)));
                        await Task.Delay(100);
                        expected.Add(new { Scale = scale, WindowWidth = size.Width, WindowHeight = size.Height, TargetX = targetX, TargetY = targetY,
                            PixelTolerance = Math.Ceiling(Math.Max(script.SourceWidth / (double)rectangle.Right, script.SourceHeight / (double)rectangle.Bottom)) + 2 });
                    }
                    await SetInputAsync(false);
                }
                var invalid = await session.ApplyAsync(config.InitialPreset, (int)Math.Round(bitrate.Value * 1000), .25);
                Check(!invalid.Success && session.Configuration.TransmissionScale == .5, "Invalid scale preserves current stream");
                if (script.Input)
                {
                await SetInputAsync(true);
                SendMessage(preview.SourceWindow, 0x0100, 0x10, (nint)(0x2A << 16 | 1));
                await Task.Delay(120);
                SendMessage(preview.SourceWindow, 0x0100, 0x1B, (nint)(1 << 16 | 1));
                await Until(() => inputClient?.Enabled == false);
                Check(inputEnabled.IsChecked == false, "Escape exits control and releases held input");
                Check(inputRejected == 0, "No injected input rejected", new { inputSent, inputRejected });
                Check(cursorClient is { Updates: > 0, Shapes: >= 3 }, "Cursor shape feedback is independent of video", new { cursorClient?.Updates, cursorClient?.Shapes });
                }
                else Check(inputSent == 0 && inputEnabled.IsChecked != true, "Automatic keyboard/mouse tests disabled; no input sent");
                if (script.Clipboard) { await SetClipboardAsync(true); updatingClipboard = true; clipboardEnabled.IsChecked = true; updatingClipboard = false; }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(script.Report))!);
                File.WriteAllText(script.Report, JsonSerializer.Serialize(new { Passed = true, Checks = checks, ExpectedInput = expected, InputTrace = interactionInputTrace,
                    CursorShapes = interactionCursors.Where(update => update.Shape != null).Select(update => new { update.Id, update.Shape!.Width, update.Shape.Height, update.Shape.HotX, update.Shape.HotY }),
                    ClipboardEnabled = script.Clipboard, Note = "Input delivery is verified independently against the target window event log." }, AppConfiguration.JsonOptions));
                if (script.HoldUntilFile != null)
                {
                    var holdStart = Stopwatch.GetTimestamp();
                    while (!File.Exists(script.HoldUntilFile) && !stopping)
                    {
                        if (Stopwatch.GetElapsedTime(holdStart).TotalMinutes > 5) throw new TimeoutException("Interaction orchestration did not finish.");
                        await Task.Delay(100);
                    }
                }
            }
            catch (Exception error)
            {
                ReportError(error); regressionExitCode = 1;
                File.WriteAllText(script.Report, JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString(), ExpectedInput = expected }, AppConfiguration.JsonOptions));
            }
            finally { if (!stopping) Close(); }
        }
        [StructLayout(LayoutKind.Sequential)] struct InteractionRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct InteractionPoint { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out InteractionRect rectangle);
        [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref InteractionPoint point);
        static nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            var previous = SetMessageExtraInfo(0);
            try { return NativeSendMessage(hwnd, message, wParam, lParam); }
            finally { SetMessageExtraInfo(previous); }
        }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern nint NativeSendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] static extern nint SetMessageExtraInfo(nint value);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    }
}
