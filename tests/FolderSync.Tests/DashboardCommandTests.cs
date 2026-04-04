using FolderSync.Commands;
using FolderSync.Models;

namespace FolderSync.Tests;

public sealed class DashboardCommandTests
{
    [Fact]
    public void ApplyProfileFilter_Keeps_Only_Selected_Runtime_Profile_And_Config_Name()
    {
        var report = new StatusReport
        {
            ServiceName = "FolderSync",
            DisplayState = "Running",
            Profiles = ["alpha", "beta"],
            Runtime = new RuntimeHealthSnapshot
            {
                ServiceName = "FolderSync",
                ServiceState = "Running",
                StartedAtUtc = DateTimeOffset.Parse("2026-04-04T08:00:00+00:00"),
                UpdatedAtUtc = DateTimeOffset.Parse("2026-04-04T08:05:00+00:00"),
                Profiles =
                [
                    new ProfileHealthSnapshot { Name = "alpha", State = "Running" },
                    new ProfileHealthSnapshot { Name = "beta", State = "Paused" }
                ]
            }
        };

        var filtered = DashboardCommand.ApplyProfileFilter(report, "beta");

        Assert.Equal("beta", Assert.Single(filtered.Profiles));
        Assert.NotNull(filtered.Runtime);
        var profile = Assert.Single(filtered.Runtime!.Profiles);
        Assert.Equal("beta", profile.Name);
        Assert.Equal("Paused", profile.State);
    }

    [Fact]
    public void GetTargetProfiles_Prefers_Runtime_Profiles_When_Available()
    {
        var report = new StatusReport
        {
            ServiceName = "FolderSync",
            DisplayState = "Running",
            Profiles = ["config-alpha", "config-beta"],
            Runtime = new RuntimeHealthSnapshot
            {
                ServiceName = "FolderSync",
                ServiceState = "Running",
                StartedAtUtc = DateTimeOffset.Parse("2026-04-04T08:00:00+00:00"),
                UpdatedAtUtc = DateTimeOffset.Parse("2026-04-04T08:05:00+00:00"),
                Profiles =
                [
                    new ProfileHealthSnapshot { Name = "runtime-alpha", State = "Running" },
                    new ProfileHealthSnapshot { Name = "runtime-beta", State = "Running" }
                ]
            }
        };

        var profiles = DashboardCommand.GetTargetProfiles(report);

        Assert.Equal(["runtime-alpha", "runtime-beta"], profiles);
    }

    [Fact]
    public void GetTargetProfiles_Falls_Back_To_Config_Profile_List()
    {
        var report = new StatusReport
        {
            ServiceName = "FolderSync",
            DisplayState = "Running",
            Profiles = ["config-alpha", "config-beta"]
        };

        var profiles = DashboardCommand.GetTargetProfiles(report);

        Assert.Equal(["config-alpha", "config-beta"], profiles);
    }
}
