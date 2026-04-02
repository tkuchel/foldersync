using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace FolderSync.Commands;

public static class HostBuilderHelper
{
    public const string DefaultServiceName = "FolderSync";
    public const string DefaultDisplayName = "FolderSync - Folder Synchronisation Service";

    public static IHost BuildHost(string[] args, string? configPath = null)
    {
        // When running as a Windows Service, the working directory is System32.
        // Change it to the exe directory so all relative paths (logs, config) resolve correctly.
        // This must happen before Serilog init — Serilog resolves file sink paths against
        // Environment.CurrentDirectory, not the host's ContentRootPath.
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(appDir);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = appDir
        });

        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile("appsettings.example.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        // Custom config file support
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: true);
        }

        // Serilog
        var logConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext();

        // Add Event Log sink when running as a Windows Service
        if (WindowsServiceHelpers.IsWindowsService())
        {
            logConfig.WriteTo.EventLog(
                DefaultServiceName,
                manageEventSource: false,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information);
        }

        Log.Logger = logConfig.CreateLogger();
        builder.Services.AddSerilog();

        // Windows Service support
        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = DefaultServiceName;
            });
        }

        // Configuration
        builder.Services
            .AddOptions<FolderSyncConfig>()
            .Bind(builder.Configuration.GetSection(FolderSyncConfig.SectionName));

        // Shared infrastructure
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IFileHasher, Sha256FileHasher>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IPathSafetyService, PathSafetyService>();
        builder.Services.AddSingleton<IRuntimeHealthStore, RuntimeHealthStore>();

        // Hosted service
        builder.Services.AddHostedService<FolderSyncService>();

        return builder.Build();
    }
}
