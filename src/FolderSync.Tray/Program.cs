using FolderSync.Tray;
using System.Threading;

using var singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\FolderSyncTraySingleton", out var createdNew);
if (!createdNew)
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
