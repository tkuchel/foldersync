using System.Runtime.Versioning;

namespace FolderSync.Commands;

internal static class CommandPlatformGuard
{
    [SupportedOSPlatformGuard("windows")]
    public static bool EnsureWindows(string commandName)
    {
        if (OperatingSystem.IsWindows())
            return true;

        Console.Error.WriteLine($"Error: '{commandName}' is only supported on Windows.");
        Environment.ExitCode = 1;
        return false;
    }
}
