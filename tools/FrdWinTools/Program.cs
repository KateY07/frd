using System.Reflection;

namespace Frd;

/// <summary>
/// frd-wintools.exe: the Windows-only helper that FRD.exe keeps out of its own process. It installs and runs the SYSTEM service that captures
/// and injects input on the secure desktop (UAC, lock screen). FRD.exe launches it elevated for the install and never loads this code.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args is ["--version"])
            {
                Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                return 0;
            }
            if (args is ["--install-secure-helper"]) return SecureDesktopService.Install();
            if (args is ["--install-secure-helper", var userSid]) return SecureDesktopService.Install(userSid);
            if (args is ["--secure-service"]) return SecureDesktopService.RunService();
            if (args is ["--secure-worker"]) { SecureDesktopService.RunWorker(); return 0; }
            Console.Error.WriteLine("frd-wintools.exe --install-secure-helper [用户SID] | --secure-service | --secure-worker | --version");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
