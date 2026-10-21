using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Frd.DesktopProbe;

static class Privilege
{
    static ServiceMain? mainCallback;
    static Handler? handler;
    static nint statusHandle;
    static string? serviceDirectory;
    static volatile bool stopping;

    public static void InstallAndStart(string directory)
    {
        directory = Path.GetFullPath(directory);
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator approval is required for the temporary SYSTEM capture service.");
            var settings = Program.Read<RunSettings>(Path.Combine(directory, "settings.json"));
            if (!Program.OwnerAlive(settings) || Path.GetFullPath(settings.Directory) != directory) throw new InvalidDataException("Invalid demo owner or directory");
            var serviceName = "FrdDesktopProbe_" + Guid.NewGuid().ToString("N");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRD-DesktopProbe", serviceName);
            Directory.CreateDirectory(root);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(security);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(root, "ffmpeg"));
            foreach (var file in Directory.GetFiles(Program.NativeDirectory)) File.Copy(file, Path.Combine(root, "ffmpeg", Path.GetFileName(file)));
            Program.Save(Path.Combine(root, "settings.json"), settings with { NativeDirectory = Path.Combine(root, "ffmpeg") });
            File.WriteAllText(Path.Combine(root, "service-name"), serviceName);
            var created = false;
            try
            {
                RunSc("create", serviceName, "binPath=", $"\"{Path.Combine(root, "DesktopProbe.exe")}\" --service \"{root}\"", "start=", "demand"); created = true;
                RunSc("start", serviceName);
            }
            finally { if (created) RunSc("delete", serviceName); }
            Program.Save(Path.Combine(directory, "privilege.json"), new { Started = true, Service = serviceName, ProtectedDirectory = root,
                Note = "Demand service registration deleted immediately; running worker exits on demo close or after 20 minutes. Protected binaries retained for inspection." });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error); Program.Save(Path.Combine(directory, "privilege-error.json"), new { Error = error.ToString() }); throw;
        }
    }

    static void RunSc(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Service Control Manager client");
        var output = process.StandardOutput.ReadToEnd(); var errors = process.StandardError.ReadToEnd(); process.WaitForExit();
        Console.WriteLine(output);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Service operation failed ({process.ExitCode}): {output} {errors}");
    }

    public static void RunService(string directory)
    {
        serviceDirectory = directory;
        mainCallback = ServiceEntry;
        var entries = new[] { new ServiceEntryPoint { Name = File.ReadAllText(Path.Combine(directory, "service-name")), Main = mainCallback }, new ServiceEntryPoint() };
        if (!StartServiceCtrlDispatcher(entries)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Service dispatcher");
    }
    static void ServiceEntry(uint count, nint arguments)
    {
        var directory = serviceDirectory!;
        var settings = Program.Read<RunSettings>(Path.Combine(directory, "settings.json"));
        handler = control => { if (control is 1 or 5) { stopping = true; File.WriteAllText(Path.Combine(settings.Directory, "stop"), "Service stopping"); } };
        statusHandle = RegisterServiceCtrlHandler(File.ReadAllText(Path.Combine(directory, "service-name")), handler);
        if (statusHandle == 0) return;
        Status(4);
        var code = 0;
        try { RunBroker(directory); }
        catch (Exception error) { code = 1; Program.Save(Path.Combine(settings.Directory, "broker-error.json"), new { Error = error.ToString() }); }
        finally { Status(1, code); }
    }
    static void Status(uint state, int error = 0)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u, Win32Exit = (uint)error };
        if (!SetServiceStatus(statusHandle, ref status)) Console.Error.WriteLine("SetServiceStatus: " + new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static void RunBroker(string directory)
    {
        var settings = Program.Read<RunSettings>(Path.Combine(directory, "settings.json"));
        if (!WindowsIdentity.GetCurrent().IsSystem) throw new UnauthorizedAccessException("Broker must run as SYSTEM");
        nint token = 0, primary = 0, environment = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x000F01FF, out token)) Fail("OpenProcessToken");
            EnablePrivilege(token, "SeTcbPrivilege"); EnablePrivilege(token, "SeAssignPrimaryTokenPrivilege"); EnablePrivilege(token, "SeIncreaseQuotaPrivilege");
            if (!DuplicateTokenEx(token, 0x000F01FF, 0, 2, 1, out primary)) Fail("DuplicateTokenEx");
            var session = settings.SessionId;
            if (!SetTokenInformation(primary, 12, ref session, sizeof(int))) Fail("SetTokenInformation session");
            if (!CreateEnvironmentBlock(out environment, primary, false)) Fail("CreateEnvironmentBlock");
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
            var exe = Path.Combine(AppContext.BaseDirectory, "DesktopProbe.exe");
            var command = new StringBuilder($"\"{exe}\" --worker \"{directory}\"");
            if (!CreateProcessAsUser(primary, exe, command, 0, 0, false, 0x08000400, environment, AppContext.BaseDirectory, ref start, out var process)) Fail("CreateProcessAsUser");
            CloseHandle(process.Thread);
            Program.Save(Path.Combine(settings.Directory, "broker.json"), new { WorkerPid = process.ProcessId, SessionId = session, System = true });
            try
            {
                var watch = Stopwatch.StartNew();
                while (WaitForSingleObject(process.Process, 250) == 258)
                {
                    if (stopping || !Program.OwnerAlive(settings) || watch.Elapsed.TotalSeconds > settings.Seconds + 10)
                    {
                        File.WriteAllText(Path.Combine(settings.Directory, "stop"), "Owner closed or test expired");
                        if (WaitForSingleObject(process.Process, 8000) == 258)
                        {
                            Console.Error.WriteLine("Worker did not stop in 8 seconds; terminating test child");
                            if (!TerminateProcess(process.Process, 1)) Fail("TerminateProcess");
                        }
                        break;
                    }
                }
            }
            finally { CloseHandle(process.Process); }
        }
        finally { if (environment != 0) DestroyEnvironmentBlock(environment); if (primary != 0) CloseHandle(primary); if (token != 0) CloseHandle(token); }
    }
    static void EnablePrivilege(nint token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) Fail("LookupPrivilegeValue");
        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
        if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0) || Marshal.GetLastWin32Error() != 0) Fail("AdjustTokenPrivileges " + name);
    }
    static void Fail(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    delegate void ServiceMain(uint count, nint arguments);
    delegate void Handler(uint control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceEntryPoint { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize;
        public nint ReservedData, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool StartServiceCtrlDispatcher([In] ServiceEntryPoint[] entries);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint RegisterServiceCtrlHandler(string name, Handler handler);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonation, int type, out nint duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(nint token, int information, ref int value, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(nint token, bool disable, ref TokenPrivileges privileges, uint length, nint previous, nint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessAsUser(nint token, string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(nint handle, uint code);
}
