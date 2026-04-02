using System.CommandLine;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FolderSync.Models;
using FolderSync.Infrastructure;
using FolderSync.Services;

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

                var filtered = ApplyProfileFilter(report, context.Request.QueryString["profile"]);
                await WriteJsonAsync(context.Response, filtered);
                return;
            }

            if (string.Equals(path, "/api/control/pause", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/control/resume", StringComparison.OrdinalIgnoreCase))
            {
                var pause = path.EndsWith("/pause", StringComparison.OrdinalIgnoreCase);
                await HandleControlRequestAsync(context, serviceName, pause);
                return;
            }

            if (string.Equals(path, "/api/control/reconcile", StringComparison.OrdinalIgnoreCase))
            {
                await HandleReconcileRequestAsync(context, serviceName);
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

    [SupportedOSPlatform("windows")]
    private static async Task HandleControlRequestAsync(HttpListenerContext context, string serviceName, bool pause)
    {
        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 405;
            await WriteJsonAsync(context.Response, new { error = "POST required." });
            return;
        }

        if (!PauseCommand.TryResolveInstallDirectory(serviceName, out var installDir, out var error))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = error ?? "Failed to resolve install directory." });
            return;
        }

        DashboardControlRequest? request;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            request = string.IsNullOrWhiteSpace(body)
                ? new DashboardControlRequest()
                : JsonSerializer.Deserialize<DashboardControlRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DashboardControlRequest();
        }
        catch
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context.Response, new { error = "Invalid request payload." });
            return;
        }

        var controlStore = new RuntimeControlStore(Path.Combine(installDir!, "foldersync-control.json"), new SystemClock());

        try
        {
            if (string.IsNullOrWhiteSpace(request.Profile))
            {
                controlStore.SetPaused(pause, pause ? NormalizeReason(request.Reason) : null);
            }
            else
            {
                controlStore.SetProfilePaused(request.Profile, pause, pause ? NormalizeReason(request.Reason) : null);
            }
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = 403;
            await WriteJsonAsync(context.Response, new { error = $"Access denied writing control file in {installDir}. Re-run the dashboard from an elevated PowerShell window." });
            return;
        }

        var report = StatusCommand.TryBuildStatusReport(serviceName, out _);
        await WriteJsonAsync(context.Response, new
        {
            ok = true,
            action = pause ? "pause" : "resume",
            profile = request.Profile,
            report
        });
    }

    [SupportedOSPlatform("windows")]
    private static async Task HandleReconcileRequestAsync(HttpListenerContext context, string serviceName)
    {
        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 405;
            await WriteJsonAsync(context.Response, new { error = "POST required." });
            return;
        }

        var report = StatusCommand.TryBuildStatusReport(serviceName, out var error);
        if (report is null || string.IsNullOrWhiteSpace(report.BinaryPath))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = error ?? "Failed to resolve installed executable." });
            return;
        }

        var executablePath = NormalizeExecutablePath(report.BinaryPath);
        var configPath = report.ConfigPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = "Installed executable not found." });
            return;
        }

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = "Installed appsettings.json not found." });
            return;
        }

        DashboardControlRequest? request;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            request = string.IsNullOrWhiteSpace(body)
                ? new DashboardControlRequest()
                : JsonSerializer.Deserialize<DashboardControlRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DashboardControlRequest();
        }
        catch
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context.Response, new { error = "Invalid request payload." });
            return;
        }

        var arguments = $"reconcile --config \"{configPath}\"";
        if (!string.IsNullOrWhiteSpace(request.Profile))
            arguments += $" --profile \"{request.Profile}\"";

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = $"Failed to start reconciliation: {ex.Message}" });
            return;
        }

        await WriteJsonAsync(context.Response, new
        {
            ok = true,
            action = "reconcile",
            profile = request.Profile
        });
    }

    private static string NormalizeReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "Paused by operator" : reason;
    }

    private static string NormalizeExecutablePath(string binPath)
    {
        var trimmed = binPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
                return trimmed[1..closingQuote];
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return trimmed[..(exeIndex + 4)];

        return trimmed;
    }

    private static StatusReport ApplyProfileFilter(StatusReport report, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) || report.Runtime is null)
            return report;

        var filteredProfiles = report.Runtime.Profiles
            .Where(profile => string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        report.Runtime = new RuntimeHealthSnapshot
        {
            ServiceName = report.Runtime.ServiceName,
            ServiceState = report.Runtime.ServiceState,
            IsPaused = report.Runtime.IsPaused,
            PauseReason = report.Runtime.PauseReason,
            PausedAtUtc = report.Runtime.PausedAtUtc,
            StartedAtUtc = report.Runtime.StartedAtUtc,
            UpdatedAtUtc = report.Runtime.UpdatedAtUtc,
            LastError = report.Runtime.LastError,
            Profiles = filteredProfiles
        };

        report.Profiles = report.Profiles
            .Where(name => string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return report;
    }

    private sealed class DashboardControlRequest
    {
        public string? Profile { get; set; }
        public string? Reason { get; set; }
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
    .toolbar { display:flex; flex-wrap:wrap; gap:12px; align-items:end; margin: 20px 0 10px; }
    .toolbar label { display:grid; gap:6px; font-size:.85rem; color: var(--muted); }
    .toolbar input { min-width: 220px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: #fff; }
    .toolbar button, .actions button, .toggle { border: 0; border-radius: 999px; padding: 10px 14px; background: var(--accent); color: white; font-weight: 600; cursor: pointer; }
    .toolbar button.secondary, .actions button.secondary { background: #dde9e7; color: var(--ink); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; }
    .card { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; box-shadow: 0 8px 24px rgba(29,42,53,.06); }
    .label { font-size: .8rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: 6px; }
    .value { font-size: 1.4rem; font-weight: 700; }
    .profiles { margin-top: 16px; display: grid; gap: 12px; }
    .profile { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; }
    .pill { display: inline-block; padding: 4px 10px; border-radius: 999px; background: rgba(13,139,125,.12); color: var(--accent); font-size: .8rem; font-weight: 600; }
    .pill.warn { background: rgba(178,107,0,.14); color: var(--warn); }
    .actions { display:flex; flex-wrap:wrap; gap:8px; margin-top: 14px; }
    .history { margin-top: 14px; border-top: 1px solid var(--border); padding-top: 14px; display:grid; gap:10px; }
    .history-item { padding: 10px 12px; border-radius: 12px; background: #f6f1e8; }
    .history-item strong { display:block; margin-bottom:4px; }
    .history-meta { color: var(--muted); font-size: .85rem; }
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

    <div class="toolbar">
      <label>
        Filter profile
        <input id="profile-filter" type="text" placeholder="All profiles">
      </label>
      <label>
        Pause reason
        <input id="pause-reason" type="text" placeholder="Maintenance window">
      </label>
      <button id="pause-all">Pause all</button>
      <button id="resume-all" class="secondary">Resume all</button>
    </div>

    <div class="profiles" id="profiles"></div>
    <div class="card" id="error-card" style="display:none; margin-top: 16px;">
      <div class="label">Error</div>
      <div class="error" id="error-text"></div>
    </div>
  </div>

  <script>
    let currentData = null;

    async function postControl(path, profile) {
      const reason = document.getElementById('pause-reason').value;
      const response = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profile, reason })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || 'Control action failed');
      return data;
    }

    function matchesFilter(profile, filterText) {
      if (!filterText) return true;
      return profile.Name.toLowerCase().includes(filterText.toLowerCase());
    }

    async function refresh() {
      try {
        const response = await fetch('/api/status', { cache: 'no-store' });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Failed to load status');
        currentData = data;

        document.getElementById('service-status').textContent = data.DisplayState;
        document.getElementById('paused-status').textContent = data.Control?.IsPaused ? `Paused (${data.Control.Reason || 'no reason'})` : 'Active';
        document.getElementById('profile-count').textContent = (data.Runtime?.Profiles || []).length;
        document.getElementById('updated').textContent = data.Runtime?.UpdatedAtUtc ? `Updated ${new Date(data.Runtime.UpdatedAtUtc).toLocaleString()}` : 'No runtime snapshot';

        const host = document.getElementById('profiles');
        host.innerHTML = '';
        const filterText = document.getElementById('profile-filter').value.trim();
        for (const profile of (data.Runtime?.Profiles || []).filter(item => matchesFilter(item, filterText))) {
          const div = document.createElement('div');
          div.className = 'profile';
          const pillClass = profile.AlertMessage ? 'pill warn' : 'pill';
          const profileStatus = profile.IsPaused ? `Paused${profile.PauseReason ? `: ${profile.PauseReason}` : ''}` : (profile.AlertMessage ? profile.AlertLevel : profile.State);
          div.innerHTML = `
            <div style="display:flex; justify-content:space-between; gap:12px; align-items:center;">
              <strong>${profile.Name}</strong>
              <span class="${pillClass}">${profileStatus}</span>
            </div>
            <dl>
              <dt>Processed</dt><dd>${profile.ProcessedCount}</dd>
              <dt>Failed</dt><dd>${profile.FailedCount}</dd>
              <dt>Overflows</dt><dd>${profile.WatcherOverflowCount}</dd>
              <dt>Last Sync</dt><dd>${profile.LastSuccessfulSyncUtc ? new Date(profile.LastSuccessfulSyncUtc).toLocaleString() : 'n/a'}</dd>
              <dt>Last Failure</dt><dd>${profile.LastFailure || 'n/a'}</dd>
              <dt>Reconcile</dt><dd>${profile.Reconciliation?.LastExitDescription || 'n/a'}</dd>
            </dl>
            <div class="actions">
              <button data-action="pause-profile" data-profile="${profile.Name}">Pause profile</button>
              <button data-action="resume-profile" data-profile="${profile.Name}" class="secondary">Resume profile</button>
              <button data-action="reconcile-profile" data-profile="${profile.Name}" class="secondary">Reconcile now</button>
            </div>
            <details class="history">
              <summary><button type="button" class="toggle secondary">Recent activity</button></summary>
              ${(profile.RecentActivities || []).length === 0
                ? '<div class="history-item"><strong>No recent activity</strong><div class="history-meta">This profile has not written recent history yet.</div></div>'
                : (profile.RecentActivities || []).map(item => `
                    <div class="history-item">
                      <strong>${item.Summary}</strong>
                      <div class="history-meta">${new Date(item.TimestampUtc).toLocaleString()}${item.RelativePath ? ` • ${item.RelativePath}` : ''}</div>
                      ${item.Details ? `<div>${item.Details}</div>` : ''}
                    </div>
                  `).join('')}
            </details>
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

    document.getElementById('profile-filter').addEventListener('input', () => {
      if (currentData) {
        const host = document.getElementById('profiles');
        host.innerHTML = '';
      }
      refresh();
    });

    document.getElementById('pause-all').addEventListener('click', async () => {
      await postControl('/api/control/pause');
      await refresh();
    });

    document.getElementById('resume-all').addEventListener('click', async () => {
      await postControl('/api/control/resume');
      await refresh();
    });

    document.addEventListener('click', async (event) => {
      const button = event.target.closest('button[data-action]');
      if (!button) return;

      const profile = button.getAttribute('data-profile');
      const action = button.getAttribute('data-action');
      const path = action === 'pause-profile'
        ? '/api/control/pause'
        : action === 'resume-profile'
          ? '/api/control/resume'
          : '/api/control/reconcile';
      try {
        await postControl(path, profile);
        await refresh();
      } catch (error) {
        document.getElementById('error-card').style.display = 'block';
        document.getElementById('error-text').textContent = error.message;
      }
    });

    refresh();
    setInterval(refresh, 5000);
  </script>
</body>
</html>
""";
    }
}
