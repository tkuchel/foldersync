using System.CommandLine;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FolderSync.Models;

namespace FolderSync.Commands;

public static class DashboardCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to query",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var portOption = new Option<int>("--port")
        {
            Description = "Local dashboard port",
            DefaultValueFactory = _ => 8941
        };

        var openOption = new Option<bool>("--open")
        {
            Description = "Open the dashboard in the default browser",
            DefaultValueFactory = _ => true
        };

        var command = new Command("dashboard", "Serve a lightweight local dashboard for FolderSync");
        command.Options.Add(nameOption);
        command.Options.Add(portOption);
        command.Options.Add(openOption);

        command.SetAction(async parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("dashboard"))
                return;

            await ExecuteAsync(
                parseResult.GetValue(nameOption)!,
                parseResult.GetValue(portOption),
                parseResult.GetValue(openOption));
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static async Task ExecuteAsync(string serviceName, int port, bool openBrowser)
    {
        using var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine($"Dashboard listening on {prefix}");

        if (openBrowser)
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = prefix;
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch
            {
                Console.WriteLine("Could not open the default browser automatically.");
            }
        }

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(context, serviceName));
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task HandleRequestAsync(HttpListenerContext context, string serviceName)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(path, "/api/status", StringComparison.OrdinalIgnoreCase))
            {
                var report = StatusCommand.TryBuildStatusReport(serviceName, out var errorMessage);
                if (report is null)
                {
                    context.Response.StatusCode = 500;
                    await WriteJsonAsync(context.Response, new { error = errorMessage ?? "Failed to build status report." });
                    return;
                }

                await WriteJsonAsync(context.Response, report);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            var html = GetDashboardHtml(serviceName);
            var bytes = Encoding.UTF8.GetBytes(html);
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
        await response.OutputStream.WriteAsync(bytes);
    }

    private static string GetDashboardHtml(string serviceName)
    {
        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>FolderSync Dashboard</title>
  <style>
    :root {
      --bg: #f4efe7;
      --panel: #fffdf9;
      --ink: #1d2a35;
      --muted: #62727f;
      --accent: #0d8b7d;
      --warn: #b26b00;
      --border: #d8d1c7;
    }
    body { margin: 0; font-family: "Segoe UI", sans-serif; background: linear-gradient(180deg, #f8f3eb, #efe7db); color: var(--ink); }
    .wrap { max-width: 1100px; margin: 0 auto; padding: 32px 20px 48px; }
    .hero { display: flex; justify-content: space-between; gap: 16px; align-items: end; margin-bottom: 20px; }
    .hero h1 { margin: 0; font-size: 2rem; }
    .hero p { margin: 6px 0 0; color: var(--muted); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; }
    .card { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; box-shadow: 0 8px 24px rgba(29,42,53,.06); }
    .label { font-size: .8rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: 6px; }
    .value { font-size: 1.4rem; font-weight: 700; }
    .profiles { margin-top: 16px; display: grid; gap: 12px; }
    .profile { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; }
    .pill { display: inline-block; padding: 4px 10px; border-radius: 999px; background: rgba(13,139,125,.12); color: var(--accent); font-size: .8rem; font-weight: 600; }
    .pill.warn { background: rgba(178,107,0,.14); color: var(--warn); }
    dl { margin: 12px 0 0; display: grid; grid-template-columns: max-content 1fr; gap: 6px 12px; }
    dt { color: var(--muted); }
    .error { color: #9b2c2c; }
    pre { white-space: pre-wrap; background: #f6f1e8; border-radius: 12px; padding: 12px; font-size: .85rem; }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <div>
        <h1>{{serviceName}} Dashboard</h1>
        <p>Live local status, health, and profile activity. Refreshes every 5 seconds.</p>
      </div>
      <div id="updated" class="pill">Loading…</div>
    </div>

    <div class="grid">
      <div class="card">
        <div class="label">Service</div>
        <div class="value" id="service-status">Loading…</div>
      </div>
      <div class="card">
        <div class="label">Paused</div>
        <div class="value" id="paused-status">Loading…</div>
      </div>
      <div class="card">
        <div class="label">Profiles</div>
        <div class="value" id="profile-count">0</div>
      </div>
    </div>

    <div class="profiles" id="profiles"></div>
    <div class="card" id="error-card" style="display:none; margin-top: 16px;">
      <div class="label">Error</div>
      <div class="error" id="error-text"></div>
    </div>
  </div>

  <script>
    async function refresh() {
      try {
        const response = await fetch('/api/status', { cache: 'no-store' });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Failed to load status');

        document.getElementById('service-status').textContent = data.DisplayState;
        document.getElementById('paused-status').textContent = data.Control?.IsPaused ? `Paused (${data.Control.Reason || 'no reason'})` : 'Active';
        document.getElementById('profile-count').textContent = (data.Runtime?.Profiles || []).length;
        document.getElementById('updated').textContent = data.Runtime?.UpdatedAtUtc ? `Updated ${new Date(data.Runtime.UpdatedAtUtc).toLocaleString()}` : 'No runtime snapshot';

        const host = document.getElementById('profiles');
        host.innerHTML = '';
        for (const profile of (data.Runtime?.Profiles || [])) {
          const div = document.createElement('div');
          div.className = 'profile';
          const pillClass = profile.AlertMessage ? 'pill warn' : 'pill';
          div.innerHTML = `
            <div style="display:flex; justify-content:space-between; gap:12px; align-items:center;">
              <strong>${profile.Name}</strong>
              <span class="${pillClass}">${profile.AlertMessage ? profile.AlertLevel : profile.State}</span>
            </div>
            <dl>
              <dt>Processed</dt><dd>${profile.ProcessedCount}</dd>
              <dt>Failed</dt><dd>${profile.FailedCount}</dd>
              <dt>Overflows</dt><dd>${profile.WatcherOverflowCount}</dd>
              <dt>Last Sync</dt><dd>${profile.LastSuccessfulSyncUtc ? new Date(profile.LastSuccessfulSyncUtc).toLocaleString() : 'n/a'}</dd>
              <dt>Last Failure</dt><dd>${profile.LastFailure || 'n/a'}</dd>
              <dt>Reconcile</dt><dd>${profile.Reconciliation?.LastExitDescription || 'n/a'}</dd>
            </dl>
            ${profile.AlertMessage ? `<pre>${profile.AlertMessage}</pre>` : ''}
          `;
          host.appendChild(div);
        }

        document.getElementById('error-card').style.display = 'none';
      } catch (error) {
        document.getElementById('error-card').style.display = 'block';
        document.getElementById('error-text').textContent = error.message;
      }
    }

    refresh();
    setInterval(refresh, 5000);
  </script>
</body>
</html>
""";
    }
}
