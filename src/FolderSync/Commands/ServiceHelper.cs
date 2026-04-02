using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace FolderSync.Commands;

[SupportedOSPlatform("windows")]
public static class ServiceHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static (int ExitCode, string Output, string Error) RunSc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    public static string GetExePath()
    {
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine executable path");
    }
}
