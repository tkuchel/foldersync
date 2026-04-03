using FolderSync.Tray;
using System.Threading;

const string mutexName = "Local\\FolderSyncTraySingleton";
var waitForSingleton = args.Any(arg => string.Equals(arg, "--wait-for-singleton", StringComparison.OrdinalIgnoreCase));

Mutex? singleInstanceMutex = null;

try
{
    singleInstanceMutex = AcquireSingletonMutex(mutexName, waitForSingleton);
    if (singleInstanceMutex is null)
    {
        MessageBox.Show(
            "FolderSync Tray is already running.",
            "FolderSync Tray",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return;
    }

    ApplicationConfiguration.Initialize();
    Application.Run(new TrayApplicationContext());
}
finally
{
    singleInstanceMutex?.Dispose();
}

static Mutex? AcquireSingletonMutex(string mutexName, bool waitForSingleton)
{
    const int maxAttempts = 20;

    for (var attempt = 0; attempt < (waitForSingleton ? maxAttempts : 1); attempt++)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (createdNew)
            return mutex;

        mutex.Dispose();
        if (!waitForSingleton)
            return null;

        Thread.Sleep(250);
    }

    return null;
}
