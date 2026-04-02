using System.Text.Json;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class AlertNotifierTests
{
    [Fact]
    public void CreatePayload_Uses_Generic_Format_By_Default()
    {
        var payload = AlertNotifier.CreatePayload(CreateNotification(), new NotificationOptions());
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"ServiceName\":\"FolderSync\"", json);
        Assert.Contains("\"ProfileName\":\"alpha\"", json);
        Assert.Contains("\"Level\":\"warning\"", json);
    }

    [Fact]
    public void CreatePayload_Uses_Slack_Format()
    {
        var payload = AlertNotifier.CreatePayload(CreateNotification(), new NotificationOptions
        {
            Provider = "Slack",
            TitlePrefix = "FolderSync Alerts"
        });
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"text\":\"FolderSync Alerts: FolderSync [warning] alpha: Example failure\"", json);
        Assert.Contains("\"blocks\"", json);
    }

    [Fact]
    public void CreatePayload_Uses_Teams_Format()
    {
        var payload = AlertNotifier.CreatePayload(CreateNotification(), new NotificationOptions
        {
            Provider = "Teams",
            TitlePrefix = "FolderSync Alerts"
        });
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"summary\":\"FolderSync Alerts: FolderSync [warning]\"", json);
        Assert.Contains("\"MessageCard\"", json);
        Assert.Contains("\"activityTitle\":\"alpha\"", json);
    }

    private static AlertNotification CreateNotification()
    {
        return new AlertNotification
        {
            ServiceName = "FolderSync",
            ProfileName = "alpha",
            Level = "warning",
            Message = "Example failure",
            TimestampUtc = DateTimeOffset.Parse("2026-04-02T10:00:00+00:00")
        };
    }
}
