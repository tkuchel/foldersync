using FolderSync.Tray;

if (args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
{
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new TrayApplicationContext());
