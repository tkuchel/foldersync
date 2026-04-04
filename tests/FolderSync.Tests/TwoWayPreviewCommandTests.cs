using FolderSync.Commands;
using FolderSync.Models;

namespace FolderSync.Tests;

public sealed class TwoWayPreviewCommandTests
{
    [Fact]
    public void ResolveStateStorePath_UsesExplicitProfilePathWhenConfigured()
    {
        var profile = new ResolvedProfile("alpha", new SyncOptions
        {
            SourcePath = @"C:\Source",
            DestinationPath = @"D:\Dest",
            TwoWay = new TwoWayOptions
            {
                StateStorePath = @"C:\FolderSync\state\alpha.json"
            }
        });

        var path = TwoWayPreviewCommand.ResolveStateStorePath(profile, @"C:\FolderSync\appsettings.json");

        Assert.Equal(Path.GetFullPath(@"C:\FolderSync\state\alpha.json"), path);
    }

    [Fact]
    public void ResolveStateStorePath_FallsBackToConfigDirectoryStateFolder()
    {
        var profile = new ResolvedProfile("alpha", new SyncOptions
        {
            SourcePath = @"C:\Source",
            DestinationPath = @"D:\Dest"
        });

        var path = TwoWayPreviewCommand.ResolveStateStorePath(profile, @"C:\FolderSync\appsettings.json");

        Assert.Equal(Path.Combine(@"C:\FolderSync", "state", "alpha.twoway.json"), path);
    }
}
