using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Frd;

internal readonly record struct SecureHelperStatus(bool Installed, bool Running, string Message);

internal static class SecureDesktopService
{
    const string Name = "FRDSecureCaptureProbe";
    const uint ScManagerConnect = 1, ScManagerCreateService = 2, ServiceAllAccess = 0xF01FF;
    const uint ServiceWin32OwnProcess = 0x10, ServiceDemandStart = 3, ServiceErrorNormal = 1;
    const uint ServiceStopped = 1, ServiceStartPending = 2, ServiceStopPending = 3, ServiceRunning = 4;
    const uint ServiceAcceptStop = 1, ServiceControlStop = 1;
    const uint TokenAllAccess = 0xF01FF, SePrivilegeEnabled = 2;
    const int TokenSessionId = 12, ErrorServiceExists = 1073, ErrorServiceAlreadyRunning = 1056;
    static readonly ServiceMainCallback serviceMain = ServiceMain;
    static readonly ServiceControlCallback serviceControl = HandleControl;
    static readonly ManualResetEventSlim stop = new(false);
    static nint serviceHandle;
    static ProcessInformation worker;
    static uint workerSession = uint.MaxValue;
    static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRD", "SecureDesktopProbe");

    internal static SecureHelperStatus Status()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) return new(false, false, new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.").Message);
        try
        {
            var service = OpenService(manager, Name, 4);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                return error == 1060 ? new(false, false, "安全桌面辅助服务尚未安装") : new(false, false, new Win32Exception(error).Message);
            }
            try
            {
                var status = Query(service);
                return status.CurrentState switch
                {
                    ServiceRunning => new(true, true, "安全桌面辅助服务正在运行"),
                    ServiceStartPending => new(true, false, "安全桌面辅助服务正在启动"),
                    _ => new(true, false, "安全桌面辅助服务未运行")
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static async Task InstallViaUacAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("FRD executable path is unavailable.");
        using var process = Process.Start(new ProcessStartInfo(executable, "--install-secure-helper")
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
        }) ?? throw new InvalidOperationException("Could not start the elevated FRD installer.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"安全桌面辅助服务安装失败，退出码 {process.ExitCode}。");
        var status = Status();
        if (!status.Running) throw new InvalidOperationException(status.Message);
    }

    internal static int Install()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("FRD executable path is unavailable.");
        PrepareStorage();
        var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == 0) throw Win32("OpenSCManager");
        try
        {
            var command = $"\"{executable}\" --secure-service";
            var service = CreateService(manager, Name, "FRD secure-desktop capture", ServiceAllAccess,
                ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal, command, null, 0, null, null, null);
            if (service == 0)
            {
                if (Marshal.GetLastWin32Error() != ErrorServiceExists) throw Win32("CreateService");
                service = OpenService(manager, Name, ServiceAllAccess);
                if (service == 0) throw Win32("OpenService");
            }
            try
            {
                var current = Query(service).CurrentState;
                if (current is ServiceRunning or ServiceStartPending)
                {
                    if (!ControlService(service, ServiceControlStop, out _)) throw Win32("ControlService(stop)");
                    WaitForState(service, ServiceStopped, TimeSpan.FromSeconds(15));
                }
                if (!ChangeServiceConfig(service, ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal, command,
                    null, 0, null, null, null, "FRD secure-desktop capture")) throw Win32("ChangeServiceConfig");
                if (!StartService(service, 0, null) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning) throw Win32("StartService");
                WaitForState(service, ServiceRunning, TimeSpan.FromSeconds(15));
                Console.Error.WriteLine($"[secure helper] Installed and started from {executable}.");
                return 0;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    static void PrepareStorage()
    {
        Directory.CreateDirectory(DataDirectory);
        var key = SecureDesktopUdp.KeyPath;
        if (!File.Exists(key)) File.WriteAllBytes(key, RandomNumberGenerator.GetBytes(32));
        var framePath = SecureDesktopShared.PathName;
        using (var frame = new FileStream(framePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) frame.SetLength(SecureDesktopShared.Capacity);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
        Restrict(key, sid, "R");
        Restrict(framePath, sid, "F");
    }

    static void Restrict(string path, string sid, string userAccess)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{path}\" /inheritance:r /grant:r \"SYSTEM:(F)\" \"{sid}:({userAccess})\"")
        { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Could not start icacls.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Could not protect {Path.GetFileName(path)} (icacls exit {process.ExitCode}).");
    }

    internal static int RunService()
    {
        ServiceTableEntry[] table = [new() { Name = Name, Main = serviceMain }, new()];
        if (!StartServiceCtrlDispatcher(table)) throw Win32("StartServiceCtrlDispatcher");
        return 0;
    }

    static void ServiceMain(uint argc, nint argv)
    {
        try
        {
            serviceHandle = RegisterServiceCtrlHandlerEx(Name, serviceControl, 0);
            if (serviceHandle == 0) throw Win32("RegisterServiceCtrlHandlerEx");
            Report(ServiceStartPending);
            Console.Error.WriteLine($"[secure helper] Service started as {WindowsIdentity.GetCurrent().Name}.");
            Report(ServiceRunning);
            while (!stop.Wait(1000))
            {
                var session = WTSGetActiveConsoleSessionId();
                if (session == uint.MaxValue) continue;
                if (worker.Process != 0 && workerSession == session && WaitForSingleObject(worker.Process, 0) == 0x102) continue;
                StopWorker();
                try { worker = StartWorker(session); workerSession = session; }
                catch (Exception error) { Console.Error.WriteLine("[secure helper] Worker launch failed: " + error); }
            }
        }
        catch (Exception error) { Console.Error.WriteLine("[secure helper] Service failed: " + error); }
        finally { StopWorker(); if (serviceHandle != 0) Report(ServiceStopped); }
    }

    static uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        if (control == ServiceControlStop) { Report(ServiceStopPending); stop.Set(); }
        return 0;
    }

    static void Report(uint state)
    {
        var status = new ServiceStatus { ServiceType = ServiceWin32OwnProcess, CurrentState = state,
            ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop : 0, WaitHint = state is ServiceStartPending or ServiceStopPending ? 10000u : 0 };
        if (!SetServiceStatus(serviceHandle, ref status)) Console.Error.WriteLine("[secure helper] " + Win32("SetServiceStatus"));
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
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("FRD executable path is unavailable.");
                var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"WinSta0\Winlogon" };
                var command = new StringBuilder($"\"{executable}\" --secure-worker");
                if (!CreateProcessAsUser(token, executable, command, 0, 0, false, 0x08000000, 0, Path.GetDirectoryName(executable), ref startup, out var result))
                    throw Win32("CreateProcessAsUser");
                CloseHandle(result.Thread); result.Thread = 0;
                return result;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(source); }
    }

    static void StopWorker()
    {
        if (worker.Process == 0) return;
        if (WaitForSingleObject(worker.Process, 0) == 0x102 && !TerminateProcess(worker.Process, 0))
            Console.Error.WriteLine("[secure helper] " + Win32("TerminateProcess"));
        CloseHandle(worker.Process); worker = default; workerSession = uint.MaxValue;
    }

    internal static void RunWorker()
    {
        if (!SetProcessDpiAwarenessContext(new nint(-4))) Console.Error.WriteLine($"[secure helper] Per-monitor DPI unavailable: {Marshal.GetLastWin32Error()}.");
        var key = SecureDesktopUdp.ReadKey();
        using var shared = SecureDesktopShared.TryOpen(writer: true) ?? throw new InvalidOperationException("Secure frame buffer is unavailable.");
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        new Thread(() => RunInputWorker(key)) { IsBackground = true, Name = "FRD secure input" }.Start();
        uint frameId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        string? previousDesktop = null, lastHash = null;
        while (true)
        {
            try
            {
                var active = OpenInputDesktop(0, false, 1);
                if (active == 0) throw Win32("OpenInputDesktop");
                string desktop;
                try { desktop = DesktopName(active); }
                finally { CloseDesktop(active); }
                if (!desktop.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                {
                    if (previousDesktop == "Winlogon") { shared.SetInactive(); SecureDesktopUdp.Send(udp, key, ++frameId, 0, 0, null); }
                    previousDesktop = desktop; lastHash = null; Thread.Sleep(250); continue;
                }
                previousDesktop = desktop;
                var sample = CaptureDesktop();
                if (sample.Hash != lastHash)
                {
                    if (!shared.Publish(sample.Width, sample.Height, sample.Pixels))
                        SecureDesktopUdp.Send(udp, key, ++frameId, sample.Width, sample.Height, sample.Pixels);
                    lastHash = sample.Hash;
                }
            }
            catch (Exception error) { Console.Error.WriteLine("[secure helper] Capture failed: " + error.Message); lastHash = null; }
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
                if (!SetThreadDesktop(desktop)) throw Win32("SetThreadDesktop");
                using var injector = new Win32InputInjector();
                SecureDesktopInputWorker.Run(key, injector, IsWinlogonActive, message => Console.Error.WriteLine("[secure helper] " + message), CancellationToken.None);
            }
            finally { CloseDesktop(desktop); }
        }
        catch (Exception error) { Console.Error.WriteLine("[secure helper] Input worker failed: " + error); }
    }

    static bool IsWinlogonActive()
    {
        var desktop = OpenInputDesktop(0, false, 1);
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

    static (int Width, int Height, string Hash, byte[] Pixels) CaptureDesktop()
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
                var info = new BitmapInfo { Header = new() { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
                var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
                if (bitmap == 0 || bits == 0) throw Win32("CreateDIBSection");
                try
                {
                    var previous = SelectObject(memory, bitmap);
                    if (previous == 0) throw Win32("SelectObject");
                    try
                    {
                        if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x40CC0020)) throw Win32("BitBlt");
                        var pixels = new byte[checked(width * height * 4)]; Marshal.Copy(bits, pixels, 0, pixels.Length);
                        return (width, height, Convert.ToHexString(SHA256.HashData(pixels)), pixels);
                    }
                    finally { SelectObject(memory, previous); }
                }
                finally { DeleteObject(bitmap); }
            }
            finally { DeleteDC(memory); }
        }
        finally { ReleaseDC(0, screen); }
    }

    static ServiceStatusProcess Query(nint service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, size, out _)) throw Win32("QueryServiceStatusEx");
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static void WaitForState(nint service, uint expected, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (Query(service).CurrentState == expected) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Service did not reach state {expected} within {timeout.TotalSeconds:F0} seconds.");
    }

    static void EnablePrivilege(nint token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) throw Win32("LookupPrivilegeValue");
        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
        if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0)) throw Win32("AdjustTokenPrivileges");
        if (Marshal.GetLastWin32Error() == 1300) throw new Win32Exception(1300, name + " was not assigned.");
    }

    static Win32Exception Win32(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ServiceMainCallback(uint argc, nint argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint ServiceControlCallback(uint control, uint eventType, nint eventData, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceTableEntry { public string? Name; public ServiceMainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo { public uint Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2; public nint Reserved2Pointer, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, uint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartService(nint service, uint count, string[]? arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ControlService(nint service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(nint service, int level, nint buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true)] static extern nint RegisterServiceCtrlHandlerEx(string name, ServiceControlCallback callback, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes, int level, int type, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetTokenInformation(nint token, int informationClass, ref uint session, int length);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool LookupPrivilegeValue(string? machine, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AdjustTokenPrivileges(nint token, bool disable, ref TokenPrivileges privileges, uint length, nint previous, nint returned);
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TerminateProcess(nint handle, uint code);
    [DllImport("user32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint id);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetUserObjectInformation(nint desktop, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint SelectObject(nint dc, nint image);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint image);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
