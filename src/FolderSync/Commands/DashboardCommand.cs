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
      --subtle: #f6f1e8;
      --shadow: 0 8px 24px rgba(29,42,53,.06);
      --success-bg: rgba(13,139,125,.12);
      --warn-bg: rgba(178,107,0,.14);
      --danger: #9b2c2c;
      --danger-bg: rgba(155,44,44,.12);
      --button-secondary-bg: #dde9e7;
      --button-secondary-ink: #1d2a35;
    }
    [data-theme="dark"] {
      --bg: #0f151b;
      --panel: #172129;
      --ink: #edf3f7;
      --muted: #9db0be;
      --accent: #3bc0ad;
      --warn: #f1b24a;
      --border: #2a3a47;
      --subtle: #111920;
      --shadow: 0 18px 40px rgba(0,0,0,.28);
      --success-bg: rgba(59,192,173,.16);
      --warn-bg: rgba(241,178,74,.15);
      --danger: #ff8f8f;
      --danger-bg: rgba(255,143,143,.14);
      --button-secondary-bg: #23313b;
      --button-secondary-ink: #edf3f7;
    }
    body { margin: 0; font-family: "Segoe UI", sans-serif; background: linear-gradient(180deg, color-mix(in srgb, var(--bg) 84%, white), var(--bg)); color: var(--ink); transition: background .2s ease, color .2s ease; }
    .wrap { max-width: 1180px; margin: 0 auto; padding: 32px 20px 48px; }
    .hero { display: flex; justify-content: space-between; gap: 16px; align-items: end; margin-bottom: 20px; }
    .hero h1 { margin: 0; font-size: 2rem; }
    .hero p { margin: 6px 0 0; color: var(--muted); }
    .hero-actions { display:flex; gap:10px; align-items:center; flex-wrap:wrap; justify-content:flex-end; }
    .toolbar { display:flex; flex-wrap:wrap; gap:12px; align-items:end; margin: 20px 0 10px; }
    .toolbar label { display:grid; gap:6px; font-size:.85rem; color: var(--muted); }
    .toolbar input { min-width: 220px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--panel); color: var(--ink); }
    .toolbar button, .actions button, .toggle, .theme-toggle { border: 0; border-radius: 999px; padding: 10px 14px; background: var(--accent); color: white; font-weight: 600; cursor: pointer; transition: transform .15s ease, opacity .15s ease; }
    .toolbar button:hover, .actions button:hover, .toggle:hover, .theme-toggle:hover { transform: translateY(-1px); }
    .toolbar button.secondary, .actions button.secondary, .toggle.secondary, .theme-toggle.secondary { background: var(--button-secondary-bg); color: var(--button-secondary-ink); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; }
    .card { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; box-shadow: var(--shadow); }
    .label { font-size: .8rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: 6px; }
    .value { font-size: 1.4rem; font-weight: 700; }
    .profiles { margin-top: 16px; display: grid; gap: 12px; }
    .profile { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 18px; }
    .profile-head { display:flex; justify-content:space-between; gap:12px; align-items:flex-start; }
    .profile-title { display:grid; gap:4px; }
    .profile-subtitle { font-size:.9rem; color: var(--muted); }
    .pill { display: inline-block; padding: 4px 10px; border-radius: 999px; background: var(--success-bg); color: var(--accent); font-size: .8rem; font-weight: 600; }
    .pill.warn { background: var(--warn-bg); color: var(--warn); }
    .pill.error { background: var(--danger-bg); color: var(--danger); }
    .stats { margin-top: 14px; display:grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; }
    .stat { padding: 10px 12px; border-radius: 14px; background: var(--subtle); border: 1px solid var(--border); }
    .stat-label { font-size: .78rem; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; }
    .stat-value { margin-top: 4px; font-size: 1rem; font-weight: 700; }
    .actions { display:flex; flex-wrap:wrap; gap:8px; margin-top: 14px; }
    .history { margin-top: 14px; border-top: 1px solid var(--border); padding-top: 14px; display:block; }
    .history > summary { list-style: none; cursor: pointer; display:flex; align-items:center; }
    .history > summary::-webkit-details-marker { display: none; }
    .history > summary::before { content: '▸'; margin-right: 8px; color: var(--muted); transition: transform .15s ease; }
    .history[open] > summary::before { transform: rotate(90deg); }
    .history-body { display:grid; gap:10px; margin-top: 12px; }
    .history-item { padding: 10px 12px; border-radius: 12px; background: var(--subtle); border: 1px solid var(--border); }
    .history-item strong { display:block; margin-bottom:4px; }
    .history-meta { color: var(--muted); font-size: .85rem; }
    .toast { display:none; margin-top: 16px; padding: 12px 14px; border-radius: 14px; border: 1px solid var(--border); background: var(--subtle); }
    .toast.error { border-color: color-mix(in srgb, var(--danger) 35%, var(--border)); color: var(--danger); background: var(--danger-bg); }
    .toast.success { border-color: color-mix(in srgb, var(--accent) 35%, var(--border)); color: var(--accent); background: var(--success-bg); }
    .error { color: var(--danger); }
    pre { white-space: pre-wrap; background: var(--subtle); border: 1px solid var(--border); border-radius: 12px; padding: 12px; font-size: .85rem; }
    @media (max-width: 720px) {
      .hero { align-items: start; }
      .hero-actions { justify-content:flex-start; }
      .toolbar { flex-direction: column; align-items: stretch; }
      .toolbar input { min-width: 0; width: 100%; }
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <div>
        <h1>{{serviceName}} Dashboard</h1>
        <p>Live local status, health, and profile activity. Refreshes every 5 seconds.</p>
      </div>
      <div class="hero-actions">
        <button id="theme-toggle" class="theme-toggle secondary" type="button">Dark mode</button>
        <div id="updated" class="pill">Loading…</div>
      </div>
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
    <div class="toast" id="action-toast"></div>
    <div class="card" id="error-card" style="display:none; margin-top: 16px;">
      <div class="label">Error</div>
      <div class="error" id="error-text"></div>
    </div>
  </div>

  <script>
    const themeKey = 'foldersync-dashboard-theme';
    const expandedKey = 'foldersync-dashboard-expanded';
    const expandedProfiles = new Set(JSON.parse(localStorage.getItem(expandedKey) || '[]'));
    let currentData = null;
    let lastToastTimeout = null;

    function saveExpandedProfiles() {
      localStorage.setItem(expandedKey, JSON.stringify([...expandedProfiles]));
    }

    function setTheme(theme) {
      document.documentElement.setAttribute('data-theme', theme);
      localStorage.setItem(themeKey, theme);
      document.getElementById('theme-toggle').textContent = theme === 'dark' ? 'Light mode' : 'Dark mode';
    }

    function initializeTheme() {
      const savedTheme = localStorage.getItem(themeKey);
      if (savedTheme === 'dark' || savedTheme === 'light') {
        setTheme(savedTheme);
        return;
      }

      setTheme(window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    }

    function showToast(message, kind = 'success') {
      const toast = document.getElementById('action-toast');
      toast.className = `toast ${kind}`;
      toast.textContent = message;
      toast.style.display = 'block';
      clearTimeout(lastToastTimeout);
      lastToastTimeout = setTimeout(() => {
        toast.style.display = 'none';
      }, 3000);
    }

    function rememberExpandedPanels() {
      document.querySelectorAll('details.history[data-profile]').forEach(detail => {
        const name = detail.getAttribute('data-profile');
        if (!name) return;
        if (detail.open) expandedProfiles.add(name);
        else expandedProfiles.delete(name);
      });
      saveExpandedProfiles();
    }

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

    function profileStatusClass(profile) {
      if (profile.AlertMessage) return 'pill warn';
      if (profile.FailedCount > 0 && !profile.LastSuccessfulSyncUtc) return 'pill error';
      return 'pill';
    }

    async function refresh() {
      try {
        rememberExpandedPanels();
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
          const pillClass = profileStatusClass(profile);
          const profileStatus = profile.IsPaused ? `Paused${profile.PauseReason ? `: ${profile.PauseReason}` : ''}` : (profile.AlertMessage ? profile.AlertLevel : profile.State);
          const historyOpen = expandedProfiles.has(profile.Name) ? 'open' : '';
          div.innerHTML = `
            <div class="profile-head">
              <div class="profile-title">
                <strong>${profile.Name}</strong>
                <div class="profile-subtitle">${profile.Reconciliation?.LastTrigger ? `Last trigger: ${profile.Reconciliation.LastTrigger}` : 'Watching for changes'}</div>
              </div>
              <span class="${pillClass}">${profileStatus}</span>
            </div>
            <div class="stats">
              <div class="stat"><div class="stat-label">Processed</div><div class="stat-value">${profile.ProcessedCount}</div></div>
              <div class="stat"><div class="stat-label">Failed</div><div class="stat-value">${profile.FailedCount}</div></div>
              <div class="stat"><div class="stat-label">Overflows</div><div class="stat-value">${profile.WatcherOverflowCount}</div></div>
              <div class="stat"><div class="stat-label">Last Sync</div><div class="stat-value">${profile.LastSuccessfulSyncUtc ? new Date(profile.LastSuccessfulSyncUtc).toLocaleString() : 'n/a'}</div></div>
              <div class="stat"><div class="stat-label">Last Failure</div><div class="stat-value">${profile.LastFailure || 'n/a'}</div></div>
              <div class="stat"><div class="stat-label">Reconcile</div><div class="stat-value">${profile.Reconciliation?.LastExitDescription || 'n/a'}</div></div>
            </div>
            <div class="actions">
              <button data-action="pause-profile" data-profile="${profile.Name}">Pause profile</button>
              <button data-action="resume-profile" data-profile="${profile.Name}" class="secondary">Resume profile</button>
              <button data-action="reconcile-profile" data-profile="${profile.Name}" class="secondary">Reconcile now</button>
            </div>
            <details class="history" data-profile="${profile.Name}" ${historyOpen}>
              <summary><span class="toggle secondary">Recent activity</span></summary>
              <div class="history-body">
                ${(profile.RecentActivities || []).length === 0
                  ? '<div class="history-item"><strong>No recent activity</strong><div class="history-meta">This profile has not written recent history yet.</div></div>'
                  : (profile.RecentActivities || []).map(item => `
                    <div class="history-item">
                      <strong>${item.Summary}</strong>
                      <div class="history-meta">${new Date(item.TimestampUtc).toLocaleString()}${item.RelativePath ? ` • ${item.RelativePath}` : ''}</div>
                      ${item.Details ? `<div>${item.Details}</div>` : ''}
                    </div>
                    `).join('')}
              </div>
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
      refresh();
    });

    document.getElementById('theme-toggle').addEventListener('click', () => {
      const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
      setTheme(currentTheme === 'dark' ? 'light' : 'dark');
    });

    document.getElementById('pause-all').addEventListener('click', async () => {
      await postControl('/api/control/pause');
      showToast('Pause request sent.');
      await refresh();
    });

    document.getElementById('resume-all').addEventListener('click', async () => {
      await postControl('/api/control/resume');
      showToast('Resume request sent.');
      await refresh();
    });

    document.addEventListener('click', async (event) => {
      const summary = event.target.closest('details.history > summary');
      if (summary) {
        const details = summary.parentElement;
        const profile = details?.getAttribute('data-profile');
        if (profile) {
          setTimeout(() => {
            if (details.open) expandedProfiles.add(profile);
            else expandedProfiles.delete(profile);
            saveExpandedProfiles();
          }, 0);
        }
        return;
      }

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
        const actionLabel = action === 'pause-profile'
          ? `Paused ${profile}`
          : action === 'resume-profile'
            ? `Resumed ${profile}`
            : `Started reconciliation for ${profile}`;
        showToast(actionLabel);
        await refresh();
      } catch (error) {
        showToast(error.message, 'error');
        document.getElementById('error-card').style.display = 'block';
        document.getElementById('error-text').textContent = error.message;
      }
    });

    initializeTheme();
    refresh();
    setInterval(refresh, 5000);
  </script>
</body>
</html>
""";
    }
}
