using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Frd;

internal readonly record struct SecureHelperStatus(bool Installed, bool Running, string Message);

/// <summary>
/// The FRD.exe side of the secure-desktop helper: asks the service manager whether it runs, and launches the separate
/// <c>frd-wintools.exe</c> elevated to install it. The service itself lives only in frd-wintools.exe.
/// </summary>
internal static class SecureDesktopService
{
    internal const string ToolsFileName = "frd-wintools.exe";
    const string Name = "FRDSecureCaptureProbe";
    const uint ScManagerConnect = 1, ServiceStartPending = 2, ServiceRunning = 4;
    static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRD", "SecureDesktopProbe");
    static string InstallErrorPath => Path.Combine(DataDirectory, "install-error.txt");

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
                return error == 1060 ? new(false, false, Loc.T("安全桌面辅助服务尚未安装")) : new(false, false, new Win32Exception(error).Message);
            }
            try
            {
                var state = Query(service);
                return state switch
                {
                    ServiceRunning => new(true, true, Loc.T("安全桌面辅助服务正在运行")),
                    ServiceStartPending => new(true, false, Loc.T("安全桌面辅助服务正在启动")),
                    _ => new(true, false, Loc.T("安全桌面辅助服务未运行"))
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static async Task InstallViaUacAsync()
    {
        // The service ships as its own installer (frd-service-<version>-setup.exe): the user picks install/overwrite or uninstall there and confirms UAC.
        var setup = Directory.EnumerateFiles(AppContext.BaseDirectory, "frd-service-*-setup.exe").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (setup != null)
        {
            SessionTrace.Event("secure-helper", "setup-requested");
            using var installer = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })
                ?? throw new InvalidOperationException("Could not start the FRD service installer.");
            await installer.WaitForExitAsync();
            SessionTrace.Event("secure-helper", $"setup-exited-{installer.ExitCode}");
            var after = Status();
            if (!after.Running) throw new InvalidOperationException(after.Message);
            return;
        }
        var tools = Path.Combine(AppContext.BaseDirectory, ToolsFileName);
        if (!File.Exists(tools)) throw new FileNotFoundException(Loc.F("找不到 {0}，请把它放在 FRD.exe 同一目录。", ToolsFileName), tools);
        // The elevated process may run as a different (administrator) account, so name the interactive user explicitly.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
        SessionTrace.Event("secure-helper", "install-requested");
        try { File.Delete(InstallErrorPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        using var process = Process.Start(new ProcessStartInfo(tools, $"--install-secure-helper {sid}")
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
        }) ?? throw new InvalidOperationException("Could not start the elevated frd-wintools installer.");
        SessionTrace.Event("secure-helper", "elevated-installer-started");
        await process.WaitForExitAsync();
        SessionTrace.Event("secure-helper", $"elevated-installer-exited-{process.ExitCode}");
        if (process.ExitCode != 0)
        {
            string? reason = null;
            try { reason = File.ReadLines(InstallErrorPath).FirstOrDefault(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new InvalidOperationException(Loc.F("安全桌面辅助服务安装失败，退出码 {0}。", process.ExitCode) + (reason == null ? "" : " " + reason));
        }
        var status = Status();
        SessionTrace.Event("secure-helper", status.Running ? "service-running" : "service-not-running");
        if (!status.Running) throw new InvalidOperationException(status.Message);
    }

    static uint Query(nint service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatusEx failed.");
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer).CurrentState;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(nint service, int level, nint buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(nint handle);
}
