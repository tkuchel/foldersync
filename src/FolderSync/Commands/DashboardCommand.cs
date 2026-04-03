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

            if (string.Equals(path, "/api/config", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConfigRequestAsync(context, serviceName);
                return;
            }

            if (string.Equals(path, "/api/config/profile", StringComparison.OrdinalIgnoreCase))
            {
                await HandleProfileConfigRequestAsync(context, serviceName);
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
    private static async Task HandleConfigRequestAsync(HttpListenerContext context, string serviceName)
    {
        if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 405;
            await WriteJsonAsync(context.Response, new { error = "GET required." });
            return;
        }

        var report = StatusCommand.TryBuildStatusReport(serviceName, out var error);
        if (report is null || string.IsNullOrWhiteSpace(report.ConfigPath))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = error ?? "Installed appsettings.json not found." });
            return;
        }

        try
        {
            var snapshot = DashboardProfileConfigManager.Load(report.ConfigPath);
            await WriteJsonAsync(context.Response, snapshot);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = $"Failed to load configuration: {ex.Message}" });
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task HandleProfileConfigRequestAsync(HttpListenerContext context, string serviceName)
    {
        var report = StatusCommand.TryBuildStatusReport(serviceName, out var error);
        if (report is null || string.IsNullOrWhiteSpace(report.ConfigPath))
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = error ?? "Installed appsettings.json not found." });
            return;
        }

        try
        {
            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var request = await ReadJsonBodyAsync<DashboardProfileEditRequest>(context);
                if (request is null)
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context.Response, new { error = "Invalid request payload." });
                    return;
                }

                var result = DashboardProfileConfigManager.SaveProfile(report.ConfigPath, request);
                context.Response.StatusCode = result.Success ? 200 : 400;
                await WriteJsonAsync(context.Response, new
                {
                    ok = result.Success,
                    snapshot = result.Snapshot,
                    errors = result.Errors,
                    warnings = result.Snapshot.Warnings,
                    restartRequired = true,
                    restartMessage = "Config saved. Restart the FolderSync service to apply profile changes."
                });
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var request = await ReadJsonBodyAsync<DashboardProfileDeleteRequest>(context);
                if (request is null || string.IsNullOrWhiteSpace(request.ProfileName))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context.Response, new { error = "ProfileName is required." });
                    return;
                }

                var result = DashboardProfileConfigManager.DeleteProfile(report.ConfigPath, request.ProfileName);
                context.Response.StatusCode = result.Success ? 200 : 400;
                await WriteJsonAsync(context.Response, new
                {
                    ok = result.Success,
                    snapshot = result.Snapshot,
                    errors = result.Errors,
                    warnings = result.Snapshot.Warnings,
                    restartRequired = true,
                    restartMessage = "Config saved. Restart the FolderSync service to apply profile changes."
                });
                return;
            }

            context.Response.StatusCode = 405;
            await WriteJsonAsync(context.Response, new { error = "POST or DELETE required." });
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = 403;
            await WriteJsonAsync(context.Response, new { error = $"Access denied writing {report.ConfigPath}. Re-run the dashboard from an elevated PowerShell window." });
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { error = $"Failed to update configuration: {ex.Message}" });
        }
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return default;

            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return default;
        }
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
                UpdateRuntimeControlActivity(
                    installDir!,
                    pause ? "pause" : "resume",
                    profileName: null,
                    pause ? NormalizeReason(request.Reason) : null);
            }
            else
            {
                controlStore.SetProfilePaused(request.Profile, pause, pause ? NormalizeReason(request.Reason) : null);
                UpdateRuntimeControlActivity(
                    installDir!,
                    pause ? "pause" : "resume",
                    request.Profile,
                    pause ? NormalizeReason(request.Reason) : null);
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

        var arguments = $"reconcile --config \"{configPath}\" --trigger Dashboard";
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
            UpdateRuntimeControlActivity(
                report.InstallDirectory!,
                "reconcile",
                request.Profile,
                "Requested from dashboard");
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

    private static string GetDashboardIconDataUrl()
    {
        const string svg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="16" fill="#0d8b7d"/>
  <circle cx="48" cy="16" r="7" fill="#b5faee"/>
  <text x="32" y="41" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="26" font-weight="700" fill="#ffffff">FS</text>
</svg>
""";

        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private static string GetDashboardBrandSvg()
    {
        return """
<svg viewBox="0 0 64 64" aria-hidden="true" focusable="false">
  <rect width="64" height="64" rx="16" fill="#0d8b7d"></rect>
  <circle cx="48" cy="16" r="7" fill="#b5faee"></circle>
  <text x="32" y="41" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="26" font-weight="700" fill="#ffffff">FS</text>
</svg>
""";
    }

    private static void UpdateRuntimeControlActivity(string installDir, string action, string? profileName, string? details)
    {
        try
        {
            var healthPath = Path.Combine(installDir, "foldersync-health.json");
            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(healthPath);
            if (snapshot is null)
                return;

            snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(profileName))
            {
                foreach (var profile in snapshot.Profiles)
                    ApplyActivity(profile, action, details);
            }
            else
            {
                var profile = snapshot.Profiles.FirstOrDefault(item =>
                    string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                    return;

                ApplyActivity(profile, action, details);
            }

            PersistRuntimeHealthSnapshot(healthPath, snapshot);
        }
        catch
        {
        }
    }

    private static void ApplyActivity(ProfileHealthSnapshot profile, string action, string? details)
    {
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "pause":
                profile.IsPaused = true;
                profile.PauseReason = details;
                profile.PausedAtUtc = now;
                profile.State = "Paused";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "control",
                    Summary = "Paused from dashboard",
                    TimestampUtc = now,
                    Details = details
                });
                break;
            case "resume":
                profile.IsPaused = false;
                profile.PauseReason = null;
                profile.PausedAtUtc = null;
                if (string.Equals(profile.State, "Paused", StringComparison.OrdinalIgnoreCase))
                    profile.State = "Running";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "control",
                    Summary = "Resumed from dashboard",
                    TimestampUtc = now,
                    Details = details
                });
                break;
            case "reconcile":
                profile.Reconciliation.LastTrigger = "Dashboard";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "reconcile",
                    Summary = "Reconciliation requested from dashboard",
                    TimestampUtc = now,
                    Details = details
                });
                break;
        }
    }

    private static void PersistRuntimeHealthSnapshot(string path, RuntimeHealthSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
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
  <link rel="icon" href="{{GetDashboardIconDataUrl()}}">
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
    .hero-brand { display:flex; gap:16px; align-items:center; }
    .brand-mark { width: 56px; height: 56px; border-radius: 18px; box-shadow: var(--shadow); flex: 0 0 auto; }
    .brand-mark svg { width: 100%; height: 100%; display:block; }
    .hero h1 { margin: 0; font-size: 2rem; }
    .hero p { margin: 6px 0 0; color: var(--muted); }
    .hero-actions { display:flex; gap:10px; align-items:center; flex-wrap:wrap; justify-content:flex-end; }
    .toolbar { display:flex; flex-wrap:wrap; gap:12px; align-items:end; margin: 20px 0 10px; }
    .toolbar label { display:grid; gap:6px; font-size:.85rem; color: var(--muted); }
    .toolbar input, .toolbar select, .modal input, .modal select, .modal textarea { min-width: 220px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--panel); color: var(--ink); font: inherit; }
    .modal textarea { min-height: 88px; resize: vertical; }
    .toolbar button, .actions button, .toggle, .theme-toggle { border: 0; border-radius: 999px; padding: 10px 14px; background: var(--accent); color: white; font-weight: 600; cursor: pointer; transition: transform .15s ease, opacity .15s ease; }
    .toolbar button:hover, .actions button:hover, .toggle:hover, .theme-toggle:hover { transform: translateY(-1px); }
    .toolbar button.secondary, .actions button.secondary, .toggle.secondary, .theme-toggle.secondary { background: var(--button-secondary-bg); color: var(--button-secondary-ink); }
    .toolbar .spacer { flex: 1 1 auto; }
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
    .toast.warn { border-color: color-mix(in srgb, var(--warn) 35%, var(--border)); color: var(--warn); background: var(--warn-bg); }
    .error { color: var(--danger); }
    .config-note { margin-top: 14px; padding: 12px 14px; border-radius: 14px; background: var(--warn-bg); color: var(--warn); border: 1px solid color-mix(in srgb, var(--warn) 35%, var(--border)); }
    .modal-shell { position: fixed; inset: 0; display:none; align-items:center; justify-content:center; background: rgba(8, 12, 18, .55); padding: 20px; z-index: 20; }
    .modal-shell.open { display:flex; }
    .modal { width: min(920px, 100%); max-height: calc(100vh - 40px); overflow:auto; background: var(--panel); border: 1px solid var(--border); border-radius: 22px; box-shadow: var(--shadow); padding: 20px; }
    .modal-head { display:flex; justify-content:space-between; align-items:start; gap:12px; margin-bottom: 16px; }
    .modal-title { margin:0; font-size: 1.3rem; }
    .modal-subtitle { color: var(--muted); margin-top: 6px; }
    .modal-grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
    .modal-grid label, .modal-stack label { display:grid; gap:6px; font-size:.85rem; color: var(--muted); }
    .modal-stack { display:grid; gap: 12px; margin-top: 12px; }
    .modal-section { margin-top: 18px; padding-top: 16px; border-top: 1px solid var(--border); }
    .modal-section h3 { margin: 0 0 10px; font-size: 1rem; }
    .checkbox-row { display:flex; gap:16px; flex-wrap:wrap; }
    .checkbox-row label { display:flex; gap:8px; align-items:center; font-size:.95rem; color: var(--ink); }
    .modal-actions { display:flex; gap:10px; justify-content:flex-end; margin-top: 18px; }
    .profile-empty { padding: 18px; border: 1px dashed var(--border); border-radius: 18px; color: var(--muted); text-align:center; background: color-mix(in srgb, var(--panel) 88%, var(--subtle)); }
    pre { white-space: pre-wrap; background: var(--subtle); border: 1px solid var(--border); border-radius: 12px; padding: 12px; font-size: .85rem; }
    @media (max-width: 720px) {
      .hero { align-items: start; }
      .hero-actions { justify-content:flex-start; }
      .toolbar { flex-direction: column; align-items: stretch; }
      .toolbar input { min-width: 0; width: 100%; }
      .toolbar .spacer { display:none; }
      .modal-actions { justify-content:stretch; flex-direction:column; }
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <div class="hero-brand">
        <div class="brand-mark">{{GetDashboardBrandSvg()}}</div>
        <div>
          <h1>{{serviceName}} Dashboard</h1>
          <p>Live local status, health, and profile activity. Refreshes every 5 seconds.</p>
        </div>
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
      <div class="spacer"></div>
      <button id="add-profile" class="secondary" type="button">Add profile</button>
    </div>

    <div class="profiles" id="profiles"></div>
    <div class="toast" id="action-toast"></div>
    <div class="config-note" id="config-note" style="display:none;"></div>
    <div class="card" id="error-card" style="display:none; margin-top: 16px;">
      <div class="label">Error</div>
      <div class="error" id="error-text"></div>
    </div>
  </div>

  <div class="modal-shell" id="profile-modal-shell" aria-hidden="true">
    <div class="modal">
      <div class="modal-head">
        <div>
          <h2 class="modal-title" id="profile-modal-title">Add profile</h2>
          <div class="modal-subtitle">Changes are saved to <code>appsettings.json</code>. Restart the FolderSync service afterward to apply pipeline changes.</div>
        </div>
        <button id="profile-modal-close" class="theme-toggle secondary" type="button">Close</button>
      </div>
      <div class="modal-grid">
        <label>Name<input id="profile-name" type="text" placeholder="workspace-name"></label>
        <label>Source path<input id="profile-source" type="text" placeholder="C:\\Path\\To\\Source"></label>
        <label>Destination path<input id="profile-destination" type="text" placeholder="D:\\Path\\To\\Destination"></label>
        <label>Delete mode
          <select id="profile-delete-mode">
            <option value="">Inherit defaults</option>
            <option value="Archive">Archive</option>
            <option value="Delete">Delete</option>
          </select>
        </label>
        <label>Delete archive path<input id="profile-delete-archive" type="text" placeholder="Optional archive path"></label>
        <label>Reconciliation interval (minutes)<input id="profile-reconcile-interval" type="number" min="1" step="1" placeholder="15"></label>
      </div>
      <div class="modal-section">
        <h3>Flags</h3>
        <div class="checkbox-row">
          <label><input id="profile-include-subdirs" type="checkbox"> Include subdirectories</label>
          <label><input id="profile-sync-deletions" type="checkbox"> Sync deletions</label>
          <label><input id="profile-dry-run" type="checkbox"> Dry run</label>
          <label><input id="profile-reconcile-enabled" type="checkbox"> Reconciliation enabled</label>
          <label><input id="profile-reconcile-startup" type="checkbox"> Run reconciliation on startup</label>
          <label><input id="profile-use-robocopy" type="checkbox"> Use robocopy</label>
        </div>
      </div>
      <div class="modal-section">
        <h3>Reconciliation options</h3>
        <div class="modal-stack">
          <label>Robocopy options<textarea id="profile-robocopy-options" placeholder="/E /FFT /Z /R:2 /W:5 /XO /NFL /NDL /NP /XJ"></textarea></label>
        </div>
      </div>
      <div class="modal-section">
        <h3>Exclusions</h3>
        <div class="modal-grid">
          <label>Directory names<textarea id="profile-exclusion-dirs" placeholder=".git&#10;node_modules"></textarea></label>
          <label>File patterns<textarea id="profile-exclusion-patterns" placeholder="*.tmp&#10;*.partial"></textarea></label>
          <label>Extensions<textarea id="profile-exclusion-extensions" placeholder=".tmp&#10;.bak"></textarea></label>
        </div>
      </div>
      <div class="modal-actions">
        <button id="profile-delete" class="secondary" type="button" style="margin-right:auto; display:none;">Delete profile</button>
        <button id="profile-cancel" class="secondary" type="button">Cancel</button>
        <button id="profile-save" type="button">Save profile</button>
      </div>
    </div>
  </div>

  <script>
    const themeKey = 'foldersync-dashboard-theme';
    const expandedKey = 'foldersync-dashboard-expanded';
    const defaultRobocopyOptions = '/E /FFT /Z /R:2 /W:5 /XO /NFL /NDL /NP /XJ';
    const expandedProfiles = new Set(JSON.parse(localStorage.getItem(expandedKey) || '[]'));
    const defaultIconHref = '{{GetDashboardIconDataUrl()}}';
    let currentData = null;
    let currentConfig = null;
    let lastToastTimeout = null;
    let editingOriginalName = null;

    function escapeHtml(value) {
      return String(value || '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
    }

    function getVisualState(data) {
      if (!data) return 'running';
      const displayState = String(data.DisplayState || '').toLowerCase();
      const profiles = data.Runtime?.Profiles || [];
      if (profiles.some(profile => !!profile.AlertMessage)) return 'warning';
      if (displayState.includes('stopped')) return 'stopped';
      if (displayState.includes('paused')) return 'paused';
      if (data.Control?.IsPaused) return 'paused';
      return 'running';
    }

    function getVisualPalette(state) {
      switch (state) {
        case 'stopped':
          return { background: '#8f2635', accent: '#ffbdbd', text: '#ffffff', glow: 'rgba(143,38,53,.24)' };
        case 'warning':
          return { background: '#bc7419', accent: '#ffe1a8', text: '#ffffff', glow: 'rgba(188,116,25,.24)' };
        case 'paused':
          return { background: '#4a586b', accent: '#ffd276', text: '#ffffff', glow: 'rgba(74,88,107,.22)' };
        default:
          return { background: '#0d8b7d', accent: '#b5faee', text: '#ffffff', glow: 'rgba(13,139,125,.22)' };
      }
    }

    function renderBrandSvg(state) {
      const palette = getVisualPalette(state);
      return `
<svg viewBox="0 0 64 64" aria-hidden="true" focusable="false">
  <rect width="64" height="64" rx="16" fill="${palette.background}"></rect>
  <circle cx="48" cy="16" r="7" fill="${palette.accent}"></circle>
  <text x="32" y="41" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="26" font-weight="700" fill="${palette.text}">FS</text>
</svg>`;
    }

    function updateBrandingState(data) {
      const visualState = getVisualState(data);
      const palette = getVisualPalette(visualState);
      document.body.dataset.state = visualState;
      const brandMark = document.querySelector('.brand-mark');
      if (brandMark) {
        brandMark.innerHTML = renderBrandSvg(visualState);
        brandMark.style.boxShadow = `0 18px 40px ${palette.glow}`;
      }

      const favicon = document.querySelector('link[rel="icon"]');
      if (favicon) {
        favicon.href = 'data:image/svg+xml,' + encodeURIComponent(renderBrandSvg(visualState));
      }
    }

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

    function initializeProfileFilterFromUrl() {
      const params = new URLSearchParams(window.location.search);
      const profile = params.get('profile');
      if (profile) {
        document.getElementById('profile-filter').value = profile;
      }
    }

    function syncFilterToUrl() {
      const value = document.getElementById('profile-filter').value.trim();
      const url = new URL(window.location.href);
      if (value) url.searchParams.set('profile', value);
      else url.searchParams.delete('profile');
      window.history.replaceState({}, '', url);
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

    async function getConfig() {
      const response = await fetch('/api/config', { cache: 'no-store' });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || 'Failed to load configuration');
      currentConfig = data;
      renderConfigNote(data);
      return data;
    }

    async function saveProfile(payload) {
      const response = await fetch('/api/config/profile', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const data = await response.json();
      if (!response.ok) throw new Error((data.errors || [data.error || 'Failed to save profile']).join('\n'));
      currentConfig = data.snapshot;
      renderConfigNote(currentConfig, data.restartMessage);
      return data;
    }

    async function deleteProfile(profileName) {
      const response = await fetch('/api/config/profile', {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profileName })
      });
      const data = await response.json();
      if (!response.ok) throw new Error((data.errors || [data.error || 'Failed to delete profile']).join('\n'));
      currentConfig = data.snapshot;
      renderConfigNote(currentConfig, data.restartMessage);
      return data;
    }

    function renderConfigNote(snapshot, restartMessage) {
      const note = document.getElementById('config-note');
      if (!snapshot && !restartMessage) {
        note.style.display = 'none';
        note.textContent = '';
        return;
      }

      const warnings = snapshot?.Warnings || [];
      const parts = [];
      if (restartMessage) parts.push(restartMessage);
      if (warnings.length) parts.push(`Validation warnings: ${warnings.join(' | ')}`);
      note.textContent = parts.join(' ');
      note.style.display = parts.length ? 'block' : 'none';
    }

    function splitLines(value) {
      return String(value || '')
        .split(/\r?\n|,/)
        .map(item => item.trim())
        .filter(Boolean);
    }

    function setCheckbox(id, value, fallback = false) {
      document.getElementById(id).checked = value ?? fallback;
    }

    function setValue(id, value) {
      document.getElementById(id).value = value ?? '';
    }

    function getNullableText(id) {
      const value = document.getElementById(id).value.trim();
      return value ? value : null;
    }

    function getNullableInt(id) {
      const value = document.getElementById(id).value.trim();
      if (!value) return null;
      const parsed = Number.parseInt(value, 10);
      return Number.isFinite(parsed) ? parsed : null;
    }

    function openProfileModal(profile) {
      const isEdit = !!profile;
      editingOriginalName = profile?.Name || null;
      document.getElementById('profile-modal-title').textContent = isEdit ? `Edit ${profile.Name}` : 'Add profile';
      document.getElementById('profile-delete').style.display = isEdit ? 'inline-block' : 'none';

      setValue('profile-name', profile?.Name);
      setValue('profile-source', profile?.SourcePath);
      setValue('profile-destination', profile?.DestinationPath);
      setValue('profile-delete-mode', profile?.DeleteMode);
      setValue('profile-delete-archive', profile?.DeleteArchivePath);
      setValue('profile-reconcile-interval', profile?.Reconciliation?.IntervalMinutes);
      setCheckbox('profile-include-subdirs', profile?.IncludeSubdirectories, true);
      setCheckbox('profile-sync-deletions', profile?.SyncDeletions, false);
      setCheckbox('profile-dry-run', profile?.DryRun, false);
      setCheckbox('profile-reconcile-enabled', profile?.Reconciliation?.Enabled, true);
      setCheckbox('profile-reconcile-startup', profile?.Reconciliation?.RunOnStartup, true);
      setCheckbox('profile-use-robocopy', profile?.Reconciliation?.UseRobocopy, true);
      setValue('profile-robocopy-options', profile?.Reconciliation?.RobocopyOptions);
      setValue('profile-exclusion-dirs', (profile?.Exclusions?.DirectoryNames || []).join('\n'));
      setValue('profile-exclusion-patterns', (profile?.Exclusions?.FilePatterns || []).join('\n'));
      setValue('profile-exclusion-extensions', (profile?.Exclusions?.Extensions || []).join('\n'));

      const shell = document.getElementById('profile-modal-shell');
      shell.classList.add('open');
      shell.setAttribute('aria-hidden', 'false');
    }

    function closeProfileModal() {
      editingOriginalName = null;
      const shell = document.getElementById('profile-modal-shell');
      shell.classList.remove('open');
      shell.setAttribute('aria-hidden', 'true');
    }

    function collectProfileForm() {
      const exclusions = {
        DirectoryNames: splitLines(document.getElementById('profile-exclusion-dirs').value),
        FilePatterns: splitLines(document.getElementById('profile-exclusion-patterns').value),
        Extensions: splitLines(document.getElementById('profile-exclusion-extensions').value)
      };

      const reconciliationEnabled = document.getElementById('profile-reconcile-enabled').checked;
      const reconciliationInterval = getNullableInt('profile-reconcile-interval') ?? 15;
      const runReconciliationOnStartup = document.getElementById('profile-reconcile-startup').checked;
      const useRobocopy = document.getElementById('profile-use-robocopy').checked;
      const robocopyOptions = document.getElementById('profile-robocopy-options').value.trim();
      const reconciliation = {
        Enabled: reconciliationEnabled,
        IntervalMinutes: reconciliationInterval,
        RunOnStartup: runReconciliationOnStartup,
        UseRobocopy: useRobocopy,
        RobocopyOptions: useRobocopy ? (robocopyOptions || defaultRobocopyOptions) : robocopyOptions
      };

      return {
        Name: document.getElementById('profile-name').value.trim(),
        SourcePath: document.getElementById('profile-source').value.trim(),
        DestinationPath: document.getElementById('profile-destination').value.trim(),
        IncludeSubdirectories: document.getElementById('profile-include-subdirs').checked,
        SyncDeletions: document.getElementById('profile-sync-deletions').checked,
        DeleteMode: getNullableText('profile-delete-mode'),
        DeleteArchivePath: getNullableText('profile-delete-archive'),
        DryRun: document.getElementById('profile-dry-run').checked,
        Reconciliation: reconciliation,
        Exclusions: (exclusions.DirectoryNames.length || exclusions.FilePatterns.length || exclusions.Extensions.length)
          ? exclusions
          : null
      };
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
        updateBrandingState(data);

        document.getElementById('service-status').textContent = data.DisplayState;
        document.getElementById('paused-status').textContent = data.Control?.IsPaused ? `Paused (${data.Control.Reason || 'no reason'})` : 'Active';
        document.getElementById('profile-count').textContent = (data.Runtime?.Profiles || []).length;
        document.getElementById('updated').textContent = data.Runtime?.UpdatedAtUtc ? `Updated ${new Date(data.Runtime.UpdatedAtUtc).toLocaleString()}` : 'No runtime snapshot';

        const host = document.getElementById('profiles');
        host.innerHTML = '';
        const filterText = document.getElementById('profile-filter').value.trim();
        const runtimeProfiles = data.Runtime?.Profiles || [];
        const configuredProfiles = currentConfig?.Profiles || [];
        const visibleProfiles = runtimeProfiles.filter(item => matchesFilter(item, filterText));
        if (visibleProfiles.length === 0 && !filterText && configuredProfiles.length === 0) {
          host.innerHTML = '<div class="profile-empty">No profiles are configured yet. Use <strong>Add profile</strong> to create one.</div>';
        }
        for (const profile of visibleProfiles) {
          const configuredProfile = configuredProfiles.find(item => item.Name.toLowerCase() === profile.Name.toLowerCase()) || null;
          const div = document.createElement('div');
          div.className = 'profile';
          const pillClass = profileStatusClass(profile);
          const profileStatus = profile.IsPaused ? `Paused${profile.PauseReason ? `: ${profile.PauseReason}` : ''}` : (profile.AlertMessage ? profile.AlertLevel : profile.State);
          const historyOpen = expandedProfiles.has(profile.Name) ? 'open' : '';
          const safeName = escapeHtml(profile.Name);
          const safeSubtitle = escapeHtml(profile.Reconciliation?.LastTrigger ? `Last trigger: ${profile.Reconciliation.LastTrigger}` : 'Watching for changes');
          const safeStatus = escapeHtml(profileStatus || 'Unknown');
          const safeLastFailure = escapeHtml(profile.LastFailure || 'n/a');
          const safeReconcile = escapeHtml(profile.Reconciliation?.LastExitDescription || 'n/a');
          div.innerHTML = `
            <div class="profile-head">
              <div class="profile-title">
                <strong>${safeName}</strong>
                <div class="profile-subtitle">${safeSubtitle}</div>
              </div>
              <span class="${pillClass}">${safeStatus}</span>
            </div>
            <div class="stats">
              <div class="stat"><div class="stat-label">Processed</div><div class="stat-value">${profile.ProcessedCount}</div></div>
              <div class="stat"><div class="stat-label">Failed</div><div class="stat-value">${profile.FailedCount}</div></div>
              <div class="stat"><div class="stat-label">Overflows</div><div class="stat-value">${profile.WatcherOverflowCount}</div></div>
              <div class="stat"><div class="stat-label">Last Sync</div><div class="stat-value">${profile.LastSuccessfulSyncUtc ? new Date(profile.LastSuccessfulSyncUtc).toLocaleString() : 'n/a'}</div></div>
              <div class="stat"><div class="stat-label">Last Failure</div><div class="stat-value">${safeLastFailure}</div></div>
              <div class="stat"><div class="stat-label">Reconcile</div><div class="stat-value">${safeReconcile}</div></div>
            </div>
            <div class="actions">
              <button data-action="pause-profile" data-profile="${safeName}">Pause profile</button>
              <button data-action="resume-profile" data-profile="${safeName}" class="secondary">Resume profile</button>
              <button data-action="reconcile-profile" data-profile="${safeName}" class="secondary">Reconcile now</button>
              <button data-action="edit-profile" data-profile="${safeName}" class="secondary">Edit profile</button>
            </div>
            <details class="history" data-profile="${safeName}" ${historyOpen}>
              <summary><span class="toggle secondary">Recent activity</span></summary>
              <div class="history-body">
                ${(profile.RecentActivities || []).length === 0
                  ? '<div class="history-item"><strong>No recent activity</strong><div class="history-meta">This profile has not written recent history yet.</div></div>'
                  : (profile.RecentActivities || []).map(item => `
                    <div class="history-item">
                      <strong>${escapeHtml(item.Summary)}</strong>
                      <div class="history-meta">${new Date(item.TimestampUtc).toLocaleString()}${item.RelativePath ? ` • ${escapeHtml(item.RelativePath)}` : ''}</div>
                      ${item.Details ? `<div>${escapeHtml(item.Details)}</div>` : ''}
                    </div>
                    `).join('')}
              </div>
            </details>
            ${profile.AlertMessage ? `<pre>${escapeHtml(profile.AlertMessage)}</pre>` : ''}
          `;
          host.appendChild(div);
        }

        if (!filterText) {
          for (const profile of configuredProfiles.filter(item => !runtimeProfiles.some(runtime => runtime.Name.toLowerCase() === item.Name.toLowerCase()))) {
            const div = document.createElement('div');
            div.className = 'profile';
            const safeName = escapeHtml(profile.Name);
            div.innerHTML = `
              <div class="profile-head">
                <div class="profile-title">
                  <strong>${safeName}</strong>
                  <div class="profile-subtitle">Configured but not active in the current runtime snapshot yet.</div>
                </div>
                <span class="pill warn">Configured only</span>
              </div>
              <div class="stats">
                <div class="stat"><div class="stat-label">Source</div><div class="stat-value">${escapeHtml(profile.SourcePath || 'n/a')}</div></div>
                <div class="stat"><div class="stat-label">Destination</div><div class="stat-value">${escapeHtml(profile.DestinationPath || 'n/a')}</div></div>
              </div>
              <div class="actions">
                <button data-action="edit-profile" data-profile="${safeName}" class="secondary">Edit profile</button>
              </div>`;
            host.appendChild(div);
          }
        }

        document.getElementById('error-card').style.display = 'none';
      } catch (error) {
        updateBrandingState(null);
        document.getElementById('error-card').style.display = 'block';
        document.getElementById('error-text').textContent = error.message;
      }
    }

    document.getElementById('profile-filter').addEventListener('input', () => {
      syncFilterToUrl();
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

    document.getElementById('add-profile').addEventListener('click', async () => {
      try {
        if (!currentConfig) await getConfig();
        openProfileModal(null);
      } catch (error) {
        showToast(error.message, 'error');
      }
    });

    document.getElementById('profile-modal-close').addEventListener('click', closeProfileModal);
    document.getElementById('profile-cancel').addEventListener('click', closeProfileModal);
    document.getElementById('profile-modal-shell').addEventListener('click', event => {
      if (event.target.id === 'profile-modal-shell') closeProfileModal();
    });

    document.getElementById('profile-save').addEventListener('click', async () => {
      try {
        const payload = { originalName: editingOriginalName, profile: collectProfileForm() };
        const result = await saveProfile(payload);
        closeProfileModal();
        showToast(result.restartMessage || 'Profile saved. Restart the service to apply changes.', result.snapshot?.Warnings?.length ? 'warn' : 'success');
        await getConfig();
        await refresh();
      } catch (error) {
        showToast(error.message, 'error');
      }
    });

    document.getElementById('profile-delete').addEventListener('click', async () => {
      if (!editingOriginalName) return;
      if (!confirm(`Delete profile "${editingOriginalName}"?`)) return;
      try {
        const result = await deleteProfile(editingOriginalName);
        closeProfileModal();
        showToast(result.restartMessage || `Deleted ${editingOriginalName}. Restart the service to apply changes.`, result.snapshot?.Warnings?.length ? 'warn' : 'success');
        await getConfig();
        await refresh();
      } catch (error) {
        showToast(error.message, 'error');
      }
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
      if (action === 'edit-profile') {
        try {
          if (!currentConfig) await getConfig();
          const configuredProfile = (currentConfig?.Profiles || []).find(item => item.Name.toLowerCase() === String(profile).toLowerCase());
          openProfileModal(configuredProfile || {
            Name: profile,
            SourcePath: '',
            DestinationPath: '',
            IncludeSubdirectories: true,
            Reconciliation: { Enabled: true, IntervalMinutes: 15, RunOnStartup: true, UseRobocopy: true, RobocopyOptions: '' }
          });
        } catch (error) {
          showToast(error.message, 'error');
        }
        return;
      }

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
    initializeProfileFilterFromUrl();
    getConfig().catch(error => showToast(error.message, 'error'));
    refresh();
    setInterval(refresh, 5000);
  </script>
</body>
</html>
""";
    }
}
