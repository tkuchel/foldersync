using System.CommandLine;
using FolderSync.Commands;

var rootCommand = new RootCommand("FolderSync - One-way folder synchronisation tool");
rootCommand.Subcommands.Add(RunCommand.Create());
rootCommand.Subcommands.Add(InstallCommand.Create());
rootCommand.Subcommands.Add(UninstallCommand.Create());
rootCommand.Subcommands.Add(StatusCommand.Create());
rootCommand.Subcommands.Add(HealthCommand.Create());
rootCommand.Subcommands.Add(ReconcileCommand.Create());
rootCommand.Subcommands.Add(ValidateConfigCommand.Create());

// Default (no subcommand): run in service/console mode
rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    await RunCommand.ExecuteAsync();
});

return rootCommand.Parse(args).Invoke();
