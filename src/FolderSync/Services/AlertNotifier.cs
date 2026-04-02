using System.Collections.Concurrent;
using System.Net.Http.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IAlertNotifier
{
    void Publish(AlertNotification notification);
}

public sealed class AlertNotifier : IAlertNotifier
{
    private readonly HttpClient _httpClient;
    private readonly NotificationOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<AlertNotifier> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSentAtUtc = new();

    public AlertNotifier(HttpClient httpClient, IOptions<FolderSyncConfig> config, IClock clock, ILogger<AlertNotifier> logger)
    {
        _httpClient = httpClient;
        _options = config.Value.Notifications;
        _clock = clock;
        _logger = logger;
    }

    public void Publish(AlertNotification notification)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookUrl))
            return;

        var key = $"{notification.ProfileName}:{notification.Message}";
        var now = _clock.UtcNow;
        if (_lastSentAtUtc.TryGetValue(key, out var lastSentUtc) &&
            now - lastSentUtc < TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes)))
        {
            return;
        }

        _lastSentAtUtc[key] = now;

        _ = Task.Run(async () =>
        {
            try
            {
                var payload = new
                {
                    notification.ServiceName,
                    notification.ProfileName,
                    notification.Level,
                    notification.Message,
                    TimestampUtc = notification.TimestampUtc
                };

                using var response = await _httpClient.PostAsJsonAsync(_options.WebhookUrl, payload);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send alert notification for profile {ProfileName}", notification.ProfileName);
            }
        });
    }
}
