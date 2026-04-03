using System.Diagnostics;
using System.Text.Json;

namespace FolderSync.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "FolderSync";
    private const string DashboardUrl = "http://127.0.0.1:8941/";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _profilesItem;
    private readonly ToolStripMenuItem _pauseAllItem;
    private readonly ToolStripMenuItem _resumeAllItem;
    private readonly ToolStripMenuItem _reconcileAllItem;
    private readonly ToolStripMenuItem _openDashboardItem;
    private readonly ToolStripMenuItem _openInstallItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private string? _installDirectory;
    private string? _executablePath;
    private RuntimeHealthSnapshot? _healthSnapshot;
    private RuntimeControlSnapshot? _controlSnapshot;
    private string? _lastAlertKey;
    private Process? _dashboardProcess;

    public TrayApplicationContext()
    {
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Loading FolderSync status...") { Enabled = false };
        _profilesItem = new ToolStripMenuItem("Profiles");
        _pauseAllItem = new ToolStripMenuItem("Pause all", null, (_, _) => PauseAll());
        _resumeAllItem = new ToolStripMenuItem("Resume all", null, (_, _) => ResumeAll());
        _reconcileAllItem = new ToolStripMenuItem("Reconcile all", null, (_, _) => ReconcileAll());
        _openDashboardItem = new ToolStripMenuItem("Open dashboard", null, async (_, _) => await OpenDashboardAsync());
        _openInstallItem = new ToolStripMenuItem("Open install folder", null, (_, _) => OpenInstallFolder());
        _refreshItem = new ToolStripMenuItem("Refresh now", null, (_, _) => RefreshState(showErrors: true));
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        _menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            _openDashboardItem,
            _openInstallItem,
            new ToolStripSeparator(),
            _pauseAllItem,
            _resumeAllItem,
            _reconcileAllItem,
            _profilesItem,
            new ToolStripSeparator(),
            _refreshItem,
            exitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Text = "FolderSync Tray",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = SystemIcons.Application
        };
        _notifyIcon.DoubleClick += async (_, _) => await OpenDashboardAsync();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 5000
        };
        _timer.Tick += (_, _) => RefreshState(showErrors: false);
        _timer.Start();

        RefreshState(showErrors: true);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        if (_dashboardProcess is { HasExited: false })
            _dashboardProcess.Dispose();

        base.ExitThreadCore();
    }

    private void RefreshState(bool showErrors)
    {
        try
        {
            ResolveInstallLocation();
            _healthSnapshot = TryReadJson<RuntimeHealthSnapshot>(GetPath("foldersync-health.json"));
            _controlSnapshot = TryReadJson<RuntimeControlSnapshot>(GetPath("foldersync-control.json")) ?? new RuntimeControlSnapshot();
            UpdateMenu();
            ShowAlertIfNeeded();
        }
        catch (Exception ex)
        {
            UpdateUnavailableState(ex.Message);
            if (showErrors)
                ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void UpdateMenu()
    {
        if (string.IsNullOrWhiteSpace(_installDirectory))
        {
            UpdateUnavailableState("FolderSync install not found.");
            return;
        }

        var overallState = _healthSnapshot?.ServiceState ?? "Unknown";
        var pausedText = _controlSnapshot?.IsPaused is true ? $"Paused ({_controlSnapshot.Reason ?? "no reason"})" : overallState;
        _statusItem.Text = $"FolderSync: {pausedText}";

        var highestSeverity = GetHighestSeverityProfile();
        _notifyIcon.Icon = highestSeverity switch
        {
            "error" => SystemIcons.Error,
            "warning" => SystemIcons.Warning,
            _ => SystemIcons.Application
        };

        _notifyIcon.Text = TrimNotifyText(BuildNotifyText(overallState));

        _pauseAllItem.Enabled = _controlSnapshot?.IsPaused is not true;
        _resumeAllItem.Enabled = _controlSnapshot?.IsPaused is true || (_controlSnapshot?.Profiles.Count ?? 0) > 0;
        _reconcileAllItem.Enabled = !string.IsNullOrWhiteSpace(_executablePath);
        _openInstallItem.Enabled = Directory.Exists(_installDirectory);

        _profilesItem.DropDownItems.Clear();
        var profiles = _healthSnapshot?.Profiles ?? [];
        if (profiles.Count == 0)
        {
            _profilesItem.Enabled = false;
            _profilesItem.DropDownItems.Add(new ToolStripMenuItem("No profiles detected") { Enabled = false });
        }
        else
        {
            _profilesItem.Enabled = true;
            foreach (var profile in profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                _profilesItem.DropDownItems.Add(BuildProfileMenu(profile));
            }
        }
    }

    private void UpdateUnavailableState(string message)
    {
        _statusItem.Text = message;
        _notifyIcon.Icon = SystemIcons.Warning;
        _notifyIcon.Text = TrimNotifyText(message);
        _profilesItem.Enabled = false;
        _profilesItem.DropDownItems.Clear();
        _profilesItem.DropDownItems.Add(new ToolStripMenuItem("Unavailable") { Enabled = false });
        _pauseAllItem.Enabled = false;
        _resumeAllItem.Enabled = false;
        _reconcileAllItem.Enabled = false;
        _openInstallItem.Enabled = false;
    }

    private ToolStripMenuItem BuildProfileMenu(ProfileHealthSnapshot profile)
    {
        var stateLabel = profile.IsPaused
            ? $"Paused: {profile.PauseReason ?? "operator"}"
            : profile.AlertMessage ?? profile.State;

        var root = new ToolStripMenuItem($"{profile.Name} ({stateLabel})");
        root.DropDownItems.Add(new ToolStripMenuItem($"Processed: {profile.ProcessedCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Failed: {profile.FailedCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Overflows: {profile.WatcherOverflowCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem("Pause profile", null, (_, _) => PauseProfile(profile.Name))
        {
            Enabled = !profile.IsPaused
        });
        root.DropDownItems.Add(new ToolStripMenuItem("Resume profile", null, (_, _) => ResumeProfile(profile.Name))
        {
            Enabled = profile.IsPaused
        });
        root.DropDownItems.Add(new ToolStripMenuItem("Reconcile now", null, (_, _) => ReconcileProfile(profile.Name))
        {
            Enabled = !string.IsNullOrWhiteSpace(_executablePath)
        });

        if (profile.RecentActivities.Count > 0)
        {
            root.DropDownItems.Add(new ToolStripSeparator());
            foreach (var activity in profile.RecentActivities.Take(5))
            {
                root.DropDownItems.Add(new ToolStripMenuItem($"{activity.Kind}: {activity.Summary}") { Enabled = false });
            }
        }

        return root;
    }

    private void ShowAlertIfNeeded()
    {
        var profile = _healthSnapshot?.Profiles
            .Where(item => !string.IsNullOrWhiteSpace(item.AlertMessage))
            .OrderByDescending(item => item.LastAlertUtc)
            .FirstOrDefault();

        if (profile?.AlertMessage is null || profile.LastAlertUtc is null)
            return;

        var key = $"{profile.Name}:{profile.LastAlertUtc:O}:{profile.AlertMessage}";
        if (string.Equals(_lastAlertKey, key, StringComparison.Ordinal))
            return;

        _lastAlertKey = key;
        ShowBalloon($"FolderSync: {profile.Name}", profile.AlertMessage, ToolTipIcon.Warning);
    }

    private async Task OpenDashboardAsync()
    {
        try
        {
            if (!await IsDashboardResponsiveAsync())
                StartDashboardProcess();

            Process.Start(new ProcessStartInfo
            {
                FileName = DashboardUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to open dashboard: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private async Task<bool> IsDashboardResponsiveAsync()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var response = await client.GetAsync(DashboardUrl);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void StartDashboardProcess()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
            throw new InvalidOperationException("Installed foldersync.exe not found.");

        if (_dashboardProcess is { HasExited: false })
            return;

        _dashboardProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = "dashboard --open false",
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private void OpenInstallFolder()
    {
        if (string.IsNullOrWhiteSpace(_installDirectory) || !Directory.Exists(_installDirectory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _installDirectory,
            UseShellExecute = true
        });
    }

    private void PauseAll()
    {
        WriteControl(store => store.IsPaused = true, "Paused by tray app", "FolderSync paused");
    }

    private void ResumeAll()
    {
        WriteControl(store =>
        {
            store.IsPaused = false;
            store.Reason = null;
            store.ChangedAtUtc = DateTimeOffset.UtcNow;
            store.Profiles.Clear();
        }, null, "FolderSync resumed");
    }

    private void PauseProfile(string profileName)
    {
        WriteControl(store =>
        {
            var profile = store.Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
            profile ??= AddProfile(store, profileName);
            profile.IsPaused = true;
            profile.Reason = "Paused by tray app";
            profile.ChangedAtUtc = DateTimeOffset.UtcNow;
        }, null, $"Paused {profileName}");
    }

    private void ResumeProfile(string profileName)
    {
        WriteControl(store =>
        {
            var profile = store.Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
                store.Profiles.Remove(profile);
        }, null, $"Resumed {profileName}");
    }

    private void ReconcileAll()
    {
        StartReconcile(null);
    }

    private void ReconcileProfile(string profileName)
    {
        StartReconcile(profileName);
    }

    private void StartReconcile(string? profileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
                throw new InvalidOperationException("Installed foldersync.exe not found.");

            var configPath = GetPath("appsettings.json");
            if (!File.Exists(configPath))
                throw new InvalidOperationException("Installed appsettings.json not found.");

            var arguments = $"reconcile --config \"{configPath}\"";
            if (!string.IsNullOrWhiteSpace(profileName))
                arguments += $" --profile \"{profileName}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            ShowBalloon("FolderSync Tray", string.IsNullOrWhiteSpace(profileName) ? "Started reconciliation for all profiles." : $"Started reconciliation for {profileName}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Error);
        }
    }

    private void WriteControl(Action<RuntimeControlSnapshot> update, string? reason, string successMessage)
    {
        try
        {
            var path = GetPath("foldersync-control.json");
            var snapshot = TryReadJson<RuntimeControlSnapshot>(path) ?? new RuntimeControlSnapshot();

            if (!string.IsNullOrWhiteSpace(reason))
            {
                snapshot.IsPaused = true;
                snapshot.Reason = reason;
                snapshot.ChangedAtUtc = DateTimeOffset.UtcNow;
            }

            update(snapshot);
            PersistJson(path, snapshot);
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", successMessage, ToolTipIcon.Info);
        }
        catch (UnauthorizedAccessException)
        {
            ShowBalloon("FolderSync Tray", "Access denied writing FolderSync control state. Run the tray app elevated for control actions.", ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ResolveInstallLocation()
    {
        var (exitCode, output, _) = RunSc($"qc \"{ServiceName}\"");
        if (exitCode == 0)
        {
            var binPath = ParseBinPath(output);
            var executablePath = NormalizeExecutablePath(binPath);
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                _executablePath = executablePath;
                _installDirectory = Path.GetDirectoryName(executablePath);
                return;
            }
        }

        var fallbackDir = @"C:\FolderSync";
        var fallbackExe = Path.Combine(fallbackDir, "foldersync.exe");
        if (File.Exists(fallbackExe))
        {
            _executablePath = fallbackExe;
            _installDirectory = fallbackDir;
            return;
        }

        _installDirectory = null;
        _executablePath = null;
    }

    private string GetPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_installDirectory))
            throw new InvalidOperationException("FolderSync install directory is not available.");

        return Path.Combine(_installDirectory, fileName);
    }

    private T? TryReadJson<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, _jsonOptions);
    }

    private void PersistJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    private static ProfileRuntimeControlSnapshot AddProfile(RuntimeControlSnapshot snapshot, string profileName)
    {
        var profile = new ProfileRuntimeControlSnapshot
        {
            Name = profileName
        };
        snapshot.Profiles.Add(profile);
        return profile;
    }

    private static (int ExitCode, string Output, string Error) RunSc(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string? ParseBinPath(string scOutput)
    {
        foreach (var line in scOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0 && colonIndex + 1 < trimmed.Length)
                    return trimmed[(colonIndex + 1)..].Trim();
            }
        }

        return null;
    }

    private static string NormalizeExecutablePath(string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath))
            return string.Empty;

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

    private string BuildNotifyText(string overallState)
    {
        var profile = _healthSnapshot?.Profiles
            .Where(item => !string.IsNullOrWhiteSpace(item.AlertMessage))
            .OrderByDescending(item => item.LastAlertUtc)
            .FirstOrDefault();

        return profile?.AlertMessage is not null
            ? $"FolderSync: {profile.Name} warning"
            : $"FolderSync: {overallState}";
    }

    private string? GetHighestSeverityProfile()
    {
        if (_healthSnapshot?.Profiles.Any(item => !string.IsNullOrWhiteSpace(item.AlertMessage)) is true)
            return "warning";

        return null;
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private static string TrimNotifyText(string value)
    {
        return value.Length <= 63 ? value : value[..60] + "...";
    }
}

internal sealed class RuntimeControlSnapshot
{
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
    public List<ProfileRuntimeControlSnapshot> Profiles { get; set; } = [];
}

internal sealed class ProfileRuntimeControlSnapshot
{
    public required string Name { get; set; }
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
}

internal sealed class RuntimeHealthSnapshot
{
    public required string ServiceName { get; init; }
    public required string ServiceState { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<ProfileHealthSnapshot> Profiles { get; init; } = [];
}

internal sealed class ProfileHealthSnapshot
{
    public required string Name { get; init; }
    public string State { get; set; } = "Starting";
    public bool IsPaused { get; set; }
    public string? PauseReason { get; set; }
    public long ProcessedCount { get; set; }
    public long FailedCount { get; set; }
    public long WatcherOverflowCount { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public string? LastFailure { get; set; }
    public string? AlertMessage { get; set; }
    public DateTimeOffset? LastAlertUtc { get; set; }
    public ReconciliationHealthSnapshot Reconciliation { get; init; } = new();
    public List<ProfileActivitySnapshot> RecentActivities { get; init; } = [];
}

internal sealed class ReconciliationHealthSnapshot
{
    public string? LastTrigger { get; set; }
    public string? LastExitDescription { get; set; }
}

internal sealed class ProfileActivitySnapshot
{
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public string? RelativePath { get; init; }
    public string? Details { get; init; }
}
