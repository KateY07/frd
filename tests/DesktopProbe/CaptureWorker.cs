using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Frd;

namespace Frd.DesktopProbe;

static class CaptureWorker
{
    public static void Run(string directory)
    {
        var settings = Program.Read<RunSettings>(Path.Combine(directory, "settings.json"));
        using var log = new StreamWriter(Path.Combine(settings.Directory, "worker.log"), true) { AutoFlush = true };
        Console.SetError(TextWriter.Synchronized(log));
        Console.SetOut(TextWriter.Synchronized(log));
        Console.WriteLine($"Worker PID={Environment.ProcessId}; identity={WindowsIdentity.GetCurrent().Name}; session={Process.GetCurrentProcess().SessionId}");
        FfmpegRuntime.Initialize(settings.NativeDirectory);
        using var sender = new UdpVideoSender(new(IPAddress.Loopback, settings.VideoPort));
        sender.SetEncoderBitrateKbps(settings.BitrateKbps);
        using var diagnostics = new UdpClient(AddressFamily.InterNetwork);
        diagnostics.Connect(IPAddress.Loopback, settings.DiagnosticPort);
        Program.Save(Path.Combine(settings.Directory, "ports.json"), new { Video = sender.ActualPort, Diagnostic = ((IPEndPoint)diagnostics.Client.LocalEndPoint!).Port });
        var registrationDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (!File.Exists(Path.Combine(settings.Directory, "registered")))
        {
            if (Stopwatch.GetTimestamp() > registrationDeadline || !Program.OwnerAlive(settings)) throw new TimeoutException("Viewer did not register localhost UDP endpoints");
            Thread.Sleep(10);
        }
        var started = Stopwatch.GetTimestamp();
        var generation = 0;
        long frameId = 0, frames = 0, secureFrames = 0, newImages = 0;
        var failures = 0;
        string? lastError = null;
        var lastStatus = Stopwatch.GetTimestamp();
        bool Running() => Stopwatch.GetElapsedTime(started).TotalSeconds < Math.Clamp(settings.Seconds, 1, 1200) &&
            !File.Exists(Path.Combine(settings.Directory, "stop")) && Program.OwnerAlive(settings);
        while (Running())
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                nint desktop = 0;
                var originalDesktop = GetThreadDesktop(GetCurrentThreadId());
                try
                {
                    desktop = OpenInputDesktop(0, false, 0x02000000);
                    if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenInputDesktop");
                    var name = DesktopName(desktop);
                    if (!SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadDesktop");
                    var currentGeneration = ++generation;
                    var trialPath = Path.Combine(settings.Directory, "trial");
                    var trial = File.Exists(trialPath) ? File.ReadAllText(trialPath).Trim() : "unmarked";
                    Console.WriteLine($"{DateTimeOffset.Now:o} desktop={name}; generation={currentGeneration}");
                    // Encode consumes the pixels synchronously before the next capture overwrites them.
                    using var capture = new DxgiDesktopCapture(settings.Width, settings.Height, reusePixelBuffer: true);
                    using var encoder = new FfmpegEncoder(settings.Candidate.Encoder, settings.Width, settings.Height, settings.Fps, settings.BitrateKbps);
                    using var recording = name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? null :
                        File.Create(Path.Combine(settings.Directory, $"secure-{currentGeneration}.{Extension(settings.Candidate.Id)}"));
                    var first = true;
                    while (Running())
                    {
                        var input = OpenInputDesktop(0, false, 0x0001);
                        if (input == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Poll input desktop");
                        string active;
                        try { active = DesktopName(input); } finally { CloseDesktopChecked(input); }
                        if (!active.Equals(name, StringComparison.Ordinal)) return;
                        var tick = Stopwatch.GetTimestamp();
                        var pixels = capture.Capture();
                        var captured = Stopwatch.GetTimestamp();
                        var id = ++frameId;
                        var packets = encoder.Encode(pixels, id, first); first = false;
                        var encoded = Stopwatch.GetTimestamp();
                        foreach (var packet in packets)
                        {
                            if (packet.Pts != id) throw new InvalidOperationException("Selected low-latency encoder buffered a frame; timestamps cannot be assigned to it.");
                            var stamp = new FrameStamp(id, currentGeneration, name, capture.Statistics?.NewDesktopImage == true, tick, captured, encoded, trial);
                            diagnostics.Send(JsonSerializer.SerializeToUtf8Bytes(stamp));
                            sender.Send(new(currentGeneration, id, packet.KeyFrame, packet.Data), CancellationToken.None);
                            frames++; if (stamp.NewImage) newImages++;
                            if (recording != null)
                            {
                                secureFrames++;
                                if (recording.Position + packet.Data.Length <= 16 * 1024 * 1024) recording.Write(packet.Data);
                            }
                        }
                        if (Stopwatch.GetElapsedTime(lastStatus).TotalSeconds >= 1)
                        {
                            Program.Save(Path.Combine(settings.Directory, "worker-status.json"), new
                            {
                                Pid = Environment.ProcessId, Identity = WindowsIdentity.GetCurrent().Name,
                                System = WindowsIdentity.GetCurrent().IsSystem, Desktop = name, Generation = currentGeneration,
                                Frames = frames, SecureFrames = secureFrames, NewImages = newImages, Failures = failures,
                                LastError = lastError, Capture = capture.Statistics, UpdatedUtc = DateTime.UtcNow
                            });
                            lastStatus = Stopwatch.GetTimestamp();
                        }
                        var remaining = 1000d / settings.Fps - Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                        if (remaining > 1) Thread.Sleep((int)remaining);
                    }
                }
                catch (Exception error) { failure = error; Console.Error.WriteLine($"{DateTimeOffset.Now:o} {error}"); }
                finally
                {
                    if (desktop != 0)
                    {
                        if (!SetThreadDesktop(originalDesktop)) Console.Error.WriteLine("Restore thread desktop: " + new Win32Exception(Marshal.GetLastWin32Error()));
                        CloseDesktopChecked(desktop);
                    }
                }
            }) { IsBackground = true, Name = "FRD capture desktop" };
            thread.Start(); thread.Join();
            if (failure != null)
            {
                failures++; lastError = failure.Message;
                Program.Save(Path.Combine(settings.Directory, "worker-error.json"), new { Error = failure.ToString(), UpdatedUtc = DateTime.UtcNow, Failures = failures });
                Thread.Sleep(200);
                if (failures >= 30) throw new InvalidOperationException("Capture repeatedly failed; inspect worker.log", failure);
            }
        }
        Program.Save(Path.Combine(settings.Directory, "worker-finished.json"), new { Frames = frames, SecureFrames = secureFrames, Failures = failures, LastError = lastError, FinishedUtc = DateTime.UtcNow });
    }

    static string Extension(string id) => id.StartsWith("hevc") ? "hevc" : id.StartsWith("av1") ? "obu" : "h264";
    static string DesktopName(nint desktop)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, text, text.Capacity * 2, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Desktop name");
        return text.ToString();
    }
    static void CloseDesktopChecked(nint desktop) { if (!CloseDesktop(desktop)) Console.Error.WriteLine("CloseDesktop: " + new Win32Exception(Marshal.GetLastWin32Error())); }
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, int length, out int needed);
}
