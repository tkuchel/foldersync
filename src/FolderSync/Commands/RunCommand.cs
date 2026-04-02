using System.CommandLine;
using Serilog;

namespace FolderSync.Commands;

public static class RunCommand
{
    public static Command Create()
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to custom appsettings.json file"
        };

        var command = new Command("run", "Run FolderSync interactively (console mode)");
        command.Options.Add(configOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(configOption);
            await ExecuteAsync(configPath);
        });

        return command;
    }

    public static async Task ExecuteAsync(string? configPath = null)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Starting FolderSync...");

            var host = HostBuilderHelper.BuildHost([], configPath);
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            Environment.ExitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
