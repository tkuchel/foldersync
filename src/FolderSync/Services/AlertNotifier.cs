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
                var payload = CreatePayload(notification, _options);

                using var response = await _httpClient.PostAsJsonAsync(_options.WebhookUrl, payload);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send alert notification for profile {ProfileName}", notification.ProfileName);
            }
        });
    }

    internal static object CreatePayload(AlertNotification notification, NotificationOptions options)
    {
        var title = string.IsNullOrWhiteSpace(options.TitlePrefix)
            ? $"{notification.ServiceName} alert"
            : $"{options.TitlePrefix}: {notification.ServiceName}";

        return NormalizeProvider(options.Provider) switch
        {
            "slack" => new
            {
                text = $"{title} [{notification.Level}] {notification.ProfileName}: {notification.Message}",
                blocks = new object[]
                {
                    new
                    {
                        type = "header",
                        text = new
                        {
                            type = "plain_text",
                            text = $"{title} [{notification.Level}]"
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = $"*Profile:* `{notification.ProfileName}`\n*Message:* {notification.Message}\n*Timestamp:* {notification.TimestampUtc:O}"
                        }
                    }
                }
            },
            "teams" => new
            {
                @type = "MessageCard",
                @context = "http://schema.org/extensions",
                summary = $"{title} [{notification.Level}]",
                themeColor = string.Equals(notification.Level, "warning", StringComparison.OrdinalIgnoreCase) ? "D97706" : "0D8B7D",
                title = $"{title} [{notification.Level}]",
                sections = new object[]
                {
                    new
                    {
                        activityTitle = notification.ProfileName,
                        text = notification.Message,
                        facts = new object[]
                        {
                            new { name = "Service", value = notification.ServiceName },
                            new { name = "Profile", value = notification.ProfileName },
                            new { name = "Timestamp", value = notification.TimestampUtc.ToString("O") }
                        }
                    }
                }
            },
            _ => new
            {
                notification.ServiceName,
                notification.ProfileName,
                notification.Level,
                notification.Message,
                TimestampUtc = notification.TimestampUtc
            }
        };
    }

    private static string NormalizeProvider(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider)
            ? "generic"
            : provider.Trim().ToLowerInvariant();
    }
}
