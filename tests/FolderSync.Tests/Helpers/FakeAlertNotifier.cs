using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests.Helpers;

public sealed class FakeAlertNotifier : IAlertNotifier
{
    public List<AlertNotification> Notifications { get; } = [];

    public void Publish(AlertNotification notification)
    {
        Notifications.Add(notification);
    }
}
