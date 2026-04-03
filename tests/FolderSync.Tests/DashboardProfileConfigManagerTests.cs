using System.Text.Json.Nodes;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class DashboardProfileConfigManagerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public DashboardProfileConfigManagerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
    }

    [Fact]
    public void SaveProfile_Adds_New_Profile_And_Preserves_Other_Sections()
    {
        var configPath = Path.Combine(_tempDir.FullName, "appsettings.json");
        var sourceA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a")).FullName;
        var destA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a")).FullName;
        var sourceB = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-b")).FullName;
        var destB = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-b")).FullName;
        File.WriteAllText(configPath, """
        {
          "FolderSync": {
            "Defaults": {
              "IncludeSubdirectories": true
            },
            "Profiles": [
              {
                "Name": "alpha",
                "SourcePath": "__SRC_A__",
                "DestinationPath": "__DST_A__"
              }
            ]
          },
          "Serilog": {
            "MinimumLevel": {
              "Default": "Information"
            }
          }
        }
        """.Replace("__SRC_A__", sourceA.Replace("\\", "\\\\"))
           .Replace("__DST_A__", destA.Replace("\\", "\\\\")));

        var result = DashboardProfileConfigManager.SaveProfile(configPath, new DashboardProfileEditRequest
        {
            Profile = new SyncProfileConfig
            {
                Name = "beta",
                SourcePath = sourceB,
                DestinationPath = destB,
                Reconciliation = new ReconciliationOptions
                {
                    Enabled = true,
                    IntervalMinutes = 30,
                    RunOnStartup = true,
                    UseRobocopy = true,
                    RobocopyOptions = "/E /XJ"
                }
            }
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Snapshot.Profiles.Count);

        var root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        Assert.NotNull(root["Serilog"]);
        var profiles = root["FolderSync"]!["Profiles"]!.AsArray();
        Assert.Equal(2, profiles.Count);
        Assert.Equal("beta", profiles[1]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void SaveProfile_Updates_Existing_Profile_By_Original_Name()
    {
        var configPath = Path.Combine(_tempDir.FullName, "appsettings.json");
        var sourceA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a")).FullName;
        var destA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a")).FullName;
        var sourceA2 = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a2")).FullName;
        var destA2 = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a2")).FullName;
        File.WriteAllText(configPath, """
        {
          "FolderSync": {
            "Profiles": [
              {
                "Name": "alpha",
                "SourcePath": "__SRC_A__",
                "DestinationPath": "__DST_A__"
              }
            ]
          }
        }
        """.Replace("__SRC_A__", sourceA.Replace("\\", "\\\\"))
           .Replace("__DST_A__", destA.Replace("\\", "\\\\")));

        var result = DashboardProfileConfigManager.SaveProfile(configPath, new DashboardProfileEditRequest
        {
            OriginalName = "alpha",
            Profile = new SyncProfileConfig
            {
                Name = "alpha-renamed",
                SourcePath = sourceA2,
                DestinationPath = destA2,
                DeleteMode = DeleteMode.Archive
            }
        });

        Assert.True(result.Success);
        var snapshotProfile = Assert.Single(result.Snapshot.Profiles);
        Assert.Equal("alpha-renamed", snapshotProfile.Name);
        Assert.Equal(sourceA2, snapshotProfile.SourcePath);
    }

    [Fact]
    public void DeleteProfile_Removes_Profile_And_Validates_Remaining_Config()
    {
        var configPath = Path.Combine(_tempDir.FullName, "appsettings.json");
        var sourceA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a")).FullName;
        var destA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a")).FullName;
        var sourceB = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-b")).FullName;
        var destB = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-b")).FullName;
        File.WriteAllText(configPath, """
        {
          "FolderSync": {
            "Profiles": [
              {
                "Name": "alpha",
                "SourcePath": "__SRC_A__",
                "DestinationPath": "__DST_A__"
              },
              {
                "Name": "beta",
                "SourcePath": "__SRC_B__",
                "DestinationPath": "__DST_B__"
              }
            ]
          }
        }
        """.Replace("__SRC_A__", sourceA.Replace("\\", "\\\\"))
           .Replace("__DST_A__", destA.Replace("\\", "\\\\"))
           .Replace("__SRC_B__", sourceB.Replace("\\", "\\\\"))
           .Replace("__DST_B__", destB.Replace("\\", "\\\\")));

        var result = DashboardProfileConfigManager.DeleteProfile(configPath, "beta");

        Assert.True(result.Success);
        var profile = Assert.Single(result.Snapshot.Profiles);
        Assert.Equal("alpha", profile.Name);
    }

    [Fact]
    public void DeleteProfile_Last_Profile_Returns_Validation_Error()
    {
        var configPath = Path.Combine(_tempDir.FullName, "appsettings.json");
        var sourceA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a")).FullName;
        var destA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a")).FullName;
        File.WriteAllText(configPath, """
        {
          "FolderSync": {
            "Profiles": [
              {
                "Name": "alpha",
                "SourcePath": "__SRC_A__",
                "DestinationPath": "__DST_A__"
              }
            ]
          }
        }
        """.Replace("__SRC_A__", sourceA.Replace("\\", "\\\\"))
           .Replace("__DST_A__", destA.Replace("\\", "\\\\")));

        var result = DashboardProfileConfigManager.DeleteProfile(configPath, "alpha");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("No sync profiles configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaveProfile_Normalizes_Blank_Robocopy_Options_To_Default()
    {
        var configPath = Path.Combine(_tempDir.FullName, "appsettings.json");
        var sourceA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "src-a")).FullName;
        var destA = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "dst-a")).FullName;
        File.WriteAllText(configPath, """
        {
          "FolderSync": {
            "Profiles": [
              {
                "Name": "alpha",
                "SourcePath": "__SRC_A__",
                "DestinationPath": "__DST_A__"
              }
            ]
          }
        }
        """.Replace("__SRC_A__", sourceA.Replace("\\", "\\\\"))
           .Replace("__DST_A__", destA.Replace("\\", "\\\\")));

        var result = DashboardProfileConfigManager.SaveProfile(configPath, new DashboardProfileEditRequest
        {
            OriginalName = "alpha",
            Profile = new SyncProfileConfig
            {
                Name = "alpha",
                SourcePath = sourceA,
                DestinationPath = destA,
                Reconciliation = new ReconciliationOptions
                {
                    Enabled = true,
                    IntervalMinutes = 15,
                    RunOnStartup = true,
                    UseRobocopy = true,
                    RobocopyOptions = ""
                }
            }
        });

        Assert.True(result.Success);
        var saved = result.Snapshot.Profiles.Single();
        Assert.NotNull(saved.Reconciliation);
        Assert.Equal(new ReconciliationOptions().RobocopyOptions, saved.Reconciliation!.RobocopyOptions);
        Assert.DoesNotContain(result.Snapshot.Warnings, warning => warning.Contains("without explicit RobocopyOptions", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _tempDir.Delete(recursive: true);
    }
}
