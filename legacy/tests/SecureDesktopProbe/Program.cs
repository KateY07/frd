using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Frd;

namespace Frd.SecureDesktopProbe;

static class Program
{
    const string ServiceName = "FRDSecureCaptureProbe";
    const uint ServiceWin32OwnProcess = 0x10, ServiceDemandStart = 3, ServiceErrorNormal = 1;
    const uint ServiceStartPending = 2, ServiceRunning = 4, ServiceStopPending = 3, ServiceStopped = 1;
    const uint ServiceAcceptStop = 1, ServiceControlStop = 1, ServiceAllAccess = 0xF01FF;
    const uint TokenAllAccess = 0xF01FF, SePrivilegeEnabled = 2;
    const int TokenSessionId = 12;
    static readonly ServiceMainCallback serviceMain = RunService;
    static readonly ServiceControlCallback serviceControl = HandleControl;
    static readonly ManualResetEventSlim stop = new(false);
    static nint serviceHandle;
    static ProcessInformation worker;
    static uint workerSession = uint.MaxValue;
    static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRD", "SecureDesktopProbe");

    static int Main(string[] args)
    {
        try
        {
            if (args is ["--service"]) RunDispatcher();
            else if (args is ["--worker"]) RunWorker();
            else if (args is ["--install"]) Install();
            else throw new ArgumentException("Usage: --service | --worker | --install");
            return 0;
        }
        catch (Exception error)
        {
            Log("launcher", error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    static void Install()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var manager = OpenSCManager(null, null, 0x0002);
        if (manager == 0) throw Win32("OpenSCManager");
        try
        {
            var service = CreateService(manager, ServiceName, "FRD secure-desktop capture probe", ServiceAllAccess,
                ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal, $"\"{executable}\" --service",
                null, 0, null, null, null);
            if (service == 0) throw Win32("CreateService");
            try
            {
                if (!StartService(service, 0, null)) throw Win32("StartService");
                Log("install", $"Installed and started on-demand LocalSystem service. Executable={executable}");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    static void RunDispatcher()
    {
        ServiceTableEntry[] table = [new() { Name = ServiceName, Main = serviceMain }, new()];
        if (!StartServiceCtrlDispatcher(table)) throw Win32("StartServiceCtrlDispatcher");
    }

    static void RunService(uint argc, nint argv)
    {
        try
        {
            serviceHandle = RegisterServiceCtrlHandlerEx(ServiceName, serviceControl, 0);
            if (serviceHandle == 0) throw Win32("RegisterServiceCtrlHandlerEx");
            Report(ServiceStartPending);
            Log("service", $"Started as {WindowsIdentity.GetCurrent().Name}, Session={Process.GetCurrentProcess().SessionId}.");
            Report(ServiceRunning);
            while (!stop.Wait(1000))
            {
                var session = WTSGetActiveConsoleSessionId();
                if (session == uint.MaxValue) continue;
                if (worker.Process != 0 && workerSession == session && WaitForSingleObject(worker.Process, 0) == 0x102) continue;
                StopWorker();
                try { worker = StartWorker(session); workerSession = session; }
                catch (Exception error) { Log("service", "Worker launch failed: " + error); }
            }
        }
        catch (Exception error) { Log("service", error.ToString()); }
        finally { StopWorker(); if (serviceHandle != 0) Report(ServiceStopped); }
    }

    static uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        if (control == ServiceControlStop) { Report(ServiceStopPending); stop.Set(); }
        return 0;
    }

    static void Report(uint state)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess, CurrentState = state,
            ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop : 0,
            WaitHint = state is ServiceStartPending or ServiceStopPending ? 10000u : 0
        };
        if (!SetServiceStatus(serviceHandle, ref status)) Log("service", Win32("SetServiceStatus").ToString());
    }

    static ProcessInformation StartWorker(uint session)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAllAccess, out var source)) throw Win32("OpenProcessToken");
        try
        {
            EnablePrivilege(source, "SeTcbPrivilege");
            if (!DuplicateTokenEx(source, TokenAllAccess, 0, 2, 1, out var token)) throw Win32("DuplicateTokenEx");
            try
            {
                if (!SetTokenInformation(token, TokenSessionId, ref session, sizeof(uint))) throw Win32("SetTokenInformation(TokenSessionId)");
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
                var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"WinSta0\Winlogon" };
                var command = new StringBuilder($"\"{executable}\" --worker");
                if (!CreateProcessAsUser(token, executable, command, 0, 0, false, 0x08000000,
                    0, Path.GetDirectoryName(executable), ref startup, out var result)) throw Win32("CreateProcessAsUser");
                CloseHandle(result.Thread);
                Log("service", $"SYSTEM worker PID={result.ProcessId}, Session={session}, desktop=WinSta0\\Winlogon.");
                result.Thread = 0;
                return result;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(source); }
    }

    static void EnablePrivilege(nint token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) throw Win32("LookupPrivilegeValue");
        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
        if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0)) throw Win32("AdjustTokenPrivileges");
        if (Marshal.GetLastWin32Error() == 1300) throw new Win32Exception(1300, name + " was not assigned.");
    }

    static void StopWorker()
    {
        if (worker.Process == 0) return;
        if (WaitForSingleObject(worker.Process, 0) == 0x102 && !TerminateProcess(worker.Process, 0))
            Log("service", Win32("TerminateProcess").ToString());
        CloseHandle(worker.Process);
        worker = default; workerSession = uint.MaxValue;
    }

    static void RunWorker()
    {
        if (!SetProcessDpiAwarenessContext(new nint(-4)))
            Log("worker", $"Per-monitor DPI awareness unavailable: Win32 error {Marshal.GetLastWin32Error()}.");
        var session = Process.GetCurrentProcess().SessionId;
        Log("worker", $"Started as {WindowsIdentity.GetCurrent().Name}, Session={session}, boundDesktop={DesktopName(GetThreadDesktop(GetCurrentThreadId()))}.");
        string? previousDesktop = null, previousHash = null, previousError = null;
        string? lastSentHash = null;
        var key = SecureDesktopUdp.ReadKey();
        using var shared = SecureDesktopShared.TryOpen(writer: true);
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        new Thread(() => RunInputWorker(key)) { IsBackground = true, Name = "FRD secure input" }.Start();
        uint frameId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        while (true)
        {
            try
            {
                var active = OpenInputDesktop(0, false, 0x0001);
                if (active == 0) throw Win32("OpenInputDesktop");
                string name;
                try { name = DesktopName(active); }
                finally { CloseDesktop(active); }
                if (name != previousDesktop)
                {
                    Log("worker", "Active input desktop=" + name);
                    if (previousDesktop == "Winlogon")
                    {
                        shared?.SetInactive();
                        SecureDesktopUdp.Send(udp, key, ++frameId, 0, 0, null);
                        lastSentHash = null;
                    }
                    previousDesktop = name;
                }
                if (name.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                {
                    var captureStarted = Stopwatch.GetTimestamp();
                    var sample = CaptureDesktop();
                    var captureMs = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
                    if (sample.Hash != previousHash)
                    {
                        Log("worker", $"CAPTURE_OK desktop={name} size={sample.Width}x{sample.Height} distinctSampleColors={sample.DistinctColors} sha256Prefix={sample.Hash[..16]}");
                        previousHash = sample.Hash;
                    }
                    if (sample.Hash != lastSentHash)
                    {
                        var sendStarted = Stopwatch.GetTimestamp();
                        if (shared?.Publish(sample.Width, sample.Height, sample.Pixels) == true)
                            Log("worker", $"SHARED_SENT capture={captureMs:F1}ms publish={Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds:F1}ms size={sample.Width}x{sample.Height}.");
                        else
                        {
                            shared?.SetInactive();
                            var sent = SecureDesktopUdp.Send(udp, key, ++frameId, sample.Width, sample.Height, sample.Pixels);
                            Log("worker", $"UDP_SENT frame={frameId} capture={captureMs:F1}ms encode+send={Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds:F1}ms compressed={sent.CompressedBytes} bytes packets={sent.Packets}x2.");
                        }
                        lastSentHash = sample.Hash;
                    }
                }
                previousError = null;
            }
            catch (Exception error)
            {
                var message = error.GetType().Name + ": " + error.Message;
                if (message != previousError) { Log("worker", "CAPTURE_ERROR " + message); previousError = message; }
                lastSentHash = null;
            }
            Thread.Sleep(250);
        }
    }

    static void RunInputWorker(byte[] key)
    {
        try
        {
            var desktop = OpenDesktop("Winlogon", 0, false, 0x01FF);
            if (desktop == 0) throw Win32("OpenDesktop(Winlogon)");
            try
            {
                if (!SetThreadDesktop(desktop)) throw Win32("SetThreadDesktop(Winlogon)");
                Log("input", $"Input worker desktop={DesktopName(GetThreadDesktop(GetCurrentThreadId()))}.");
                using var injector = new Win32InputInjector();
                SecureDesktopInputWorker.Run(key, injector, IsWinlogonActive, message => Log("input", message), CancellationToken.None);
            }
            finally { CloseDesktop(desktop); }
        }
        catch (Exception error) { Log("input", "Input worker failed: " + error); }
    }

    static bool IsWinlogonActive()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) throw Win32("OpenInputDesktop(input)");
        try { return DesktopName(desktop).Equals("Winlogon", StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }

    static string DesktopName(nint desktop)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, text, text.Capacity * sizeof(char), out _)) throw Win32("GetUserObjectInformation");
        return text.ToString();
    }

    static (int Width, int Height, int DistinctColors, string Hash, byte[] Pixels) CaptureDesktop()
    {
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (width is < 64 or > 7680 || height is < 64 or > 4320) throw new InvalidOperationException("Invalid desktop dimensions.");
        var screen = GetDC(0);
        if (screen == 0) throw Win32("GetDC");
        try
        {
            var memory = CreateCompatibleDC(screen);
            if (memory == 0) throw Win32("CreateCompatibleDC");
            try
            {
                var info = new BitmapInfo { Header = new() { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width,
                    Height = -height, Planes = 1, BitCount = 32 } };
                var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
                if (bitmap == 0 || bits == 0) throw Win32("CreateDIBSection");
                try
                {
                    var previous = SelectObject(memory, bitmap);
                    if (previous == 0) throw Win32("SelectObject");
                    try
                    {
                        if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x40CC0020)) throw Win32("BitBlt");
                        var pixels = new byte[checked(width * height * 4)];
                        Marshal.Copy(bits, pixels, 0, pixels.Length);
                        var colors = new HashSet<int>();
                        for (var index = 0; index < pixels.Length; index += Math.Max(4, pixels.Length / 4096 & ~3))
                            colors.Add(pixels[index] | pixels[index + 1] << 8 | pixels[index + 2] << 16);
                        return (width, height, colors.Count, Convert.ToHexString(SHA256.HashData(pixels)), pixels);
                    }
                    finally { SelectObject(memory, previous); }
                }
                finally { DeleteObject(bitmap); }
            }
            finally { DeleteDC(memory); }
        }
        finally { ReleaseDC(0, screen); }
    }

    static void Log(string component, string text)
    {
        Directory.CreateDirectory(LogDirectory);
        File.AppendAllText(Path.Combine(LogDirectory, component + ".log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
    }

    static Win32Exception Win32(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ServiceMainCallback(uint argc, nint argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint ServiceControlCallback(uint control, uint eventType, nint eventData, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.FunctionPtr)] public ServiceMainCallback? Main;
    }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public uint Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public nint Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start,
        uint error, string binary, string? group, uint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartService(nint service, uint count, string[]? arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint RegisterServiceCtrlHandlerEx(string name, ServiceControlCallback callback, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes, int level, int type, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetTokenInformation(nint token, int informationClass, ref uint information, int length);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool LookupPrivilegeValue(string? machine, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disable, ref TokenPrivileges privileges, uint length, nint previous, nint returned);
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TerminateProcess(nint handle, uint code);
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint id);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenDesktop(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetUserObjectInformation(nint objectHandle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint SelectObject(nint dc, nint image);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint image);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rasterOperation);
}
