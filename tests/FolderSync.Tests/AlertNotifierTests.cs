using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    [Fact]
    public async Task Publish_Retries_After_Failed_Send_Instead_Of_Entering_Cooldown()
    {
        var clock = new FakeClock();
        clock.Set(DateTimeOffset.Parse("2026-04-02T10:00:00+00:00"));

        var handler = new SequenceHttpMessageHandler(
            [System.Net.HttpStatusCode.InternalServerError, System.Net.HttpStatusCode.OK]);
        var notifier = new AlertNotifier(
            new HttpClient(handler),
            Options.Create(new FolderSyncConfig
            {
                Notifications = new NotificationOptions
                {
                    Enabled = true,
                    WebhookUrl = "https://example.test/hook",
                    CooldownMinutes = 15
                }
            }),
            clock,
            NullLogger<AlertNotifier>.Instance);

        notifier.Publish(CreateNotification());
        await notifier.WaitForPendingPublishAsync();
        await handler.WaitForCountAsync(1);

        notifier.Publish(CreateNotification());
        await notifier.WaitForPendingPublishAsync();
        await handler.WaitForCountAsync(2);

        Assert.Equal(2, handler.RequestCount);
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

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<System.Net.HttpStatusCode> _responses;
        private readonly TaskCompletionSource _requestObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SequenceHttpMessageHandler(IEnumerable<System.Net.HttpStatusCode> responses)
        {
            _responses = new Queue<System.Net.HttpStatusCode>(responses);
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            _requestObserved.TrySetResult();

            var statusCode = _responses.Count > 0
                ? _responses.Dequeue()
                : System.Net.HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request
            });
        }

        public async Task WaitForCountAsync(int expectedCount)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(3);
            while (RequestCount < expectedCount)
            {
                if (DateTime.UtcNow >= timeoutAt)
                    throw new TimeoutException($"Timed out waiting for {expectedCount} requests.");

                await Task.Delay(25);
            }
        }
    }
}
