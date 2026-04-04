using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderSync.Models;
using Microsoft.Win32;

namespace FolderSync.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "FolderSync";
    private const string DashboardUrl = "http://127.0.0.1:8941/";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "FolderSyncTray";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _profilesItem;
    private readonly ToolStripMenuItem _pauseAllItem;
    private readonly ToolStripMenuItem _resumeAllItem;
    private readonly ToolStripMenuItem _reconcileAllItem;
    private readonly ToolStripMenuItem _openDashboardItem;
    private readonly ToolStripMenuItem _openInstallItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly ToolStripMenuItem _restartElevatedItem;
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
    private Icon? _currentTrayIcon;

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
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows())
        {
            CheckOnClick = true
        };
        _restartElevatedItem = new ToolStripMenuItem("Restart as administrator", null, (_, _) => RestartElevated());
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
            _startWithWindowsItem,
            _restartElevatedItem,
            new ToolStripSeparator(),
            _refreshItem,
            exitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Text = "FolderSync Tray",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = BuildStatusIcon("running")
        };
        _currentTrayIcon = _notifyIcon.Icon;
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
        _currentTrayIcon?.Dispose();
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
            ApplyControlOverlay();
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
        var pendingRequestCount = GetPendingReconcileRequests().Count;
        var queueSuffix = pendingRequestCount > 0 ? $" | queued reconciles: {pendingRequestCount}" : string.Empty;
        _statusItem.Text = $"FolderSync: {pausedText}{queueSuffix}";
        _startWithWindowsItem.Checked = IsStartWithWindowsEnabled();
        _restartElevatedItem.Visible = !IsProcessElevated();

        var highestSeverity = GetHighestSeverityProfile();
        var iconState = highestSeverity switch
        {
            "error" => "error",
            "warning" => "warning",
            _ when _controlSnapshot?.IsPaused is true => "paused",
            _ => "running"
        };
        UpdateTrayIcon(iconState);

        _notifyIcon.Text = TrimNotifyText(BuildNotifyText(overallState));

        _pauseAllItem.Enabled = _controlSnapshot?.IsPaused is not true;
        _resumeAllItem.Enabled = _controlSnapshot?.IsPaused is true || (_controlSnapshot?.Profiles.Count ?? 0) > 0;
        _reconcileAllItem.Enabled = !string.IsNullOrWhiteSpace(_executablePath);
        _openInstallItem.Enabled = Directory.Exists(_installDirectory);
        _startWithWindowsItem.Enabled = !string.IsNullOrWhiteSpace(GetTrayExecutablePath());

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
        UpdateTrayIcon("warning");
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
        var pendingRequests = GetPendingReconcileRequests(profile.Name);
        var stateLabel = profile.IsPaused
            ? $"Paused: {profile.PauseReason ?? "operator"}"
            : pendingRequests.Count > 0
                ? $"Queued reconcile ({pendingRequests.Count})"
                : profile.AlertMessage ?? profile.State;

        var root = new ToolStripMenuItem(profile.Name);
        root.DropDownItems.Add(new ToolStripMenuItem($"Status: {stateLabel}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Processed: {profile.ProcessedCount} | Failed: {profile.FailedCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Overflows: {profile.WatcherOverflowCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Queued reconciles: {pendingRequests.Count}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Last sync: {FormatTimestamp(profile.LastSuccessfulSyncUtc)}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Last reconcile: {SummarizeReconciliation(profile)}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem("Open in dashboard", null, async (_, _) => await OpenDashboardAsync(profile.Name)));
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

    private async Task OpenDashboardAsync(string? profileName = null)
    {
        try
        {
            if (!await IsDashboardResponsiveAsync())
                StartDashboardProcess();

            Process.Start(new ProcessStartInfo
            {
                FileName = BuildDashboardUrl(profileName),
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
        WriteControl(store => SetPaused(store, true, "Paused by tray app"), "FolderSync paused");
    }

    private void ResumeAll()
    {
        WriteControl(store =>
        {
            SetPaused(store, false, null);
            store.Profiles.Clear();
        }, "FolderSync resumed");
    }

    private void PauseProfile(string profileName)
    {
        WriteControl(store =>
        {
            SetProfilePaused(store, profileName, true, "Paused by tray app");
        }, $"Paused {profileName}");
    }

    private void ResumeProfile(string profileName)
    {
        WriteControl(store =>
        {
            SetProfilePaused(store, profileName, false, null);
        }, $"Resumed {profileName}");
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
            if (!string.Equals(_healthSnapshot?.ServiceState, "Running", StringComparison.OrdinalIgnoreCase) &&
                !(_controlSnapshot?.IsPaused is true))
            {
                throw new InvalidOperationException("FolderSync service must be running to accept tray reconciliation requests.");
            }

            var controlPath = GetPath("foldersync-control.json");
            WithControlFileLock(controlPath, () =>
            {
                var snapshot = TryReadJson<RuntimeControlSnapshot>(controlPath) ?? new RuntimeControlSnapshot();
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    foreach (var targetProfile in GetKnownProfiles())
                    {
                        snapshot.ReconcileRequests.Add(new ReconcileRequestSnapshot
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            ProfileName = targetProfile,
                            Trigger = "Tray",
                            RequestedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                }
                else
                {
                    snapshot.ReconcileRequests.Add(new ReconcileRequestSnapshot
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ProfileName = profileName,
                        Trigger = "Tray",
                        RequestedAtUtc = DateTimeOffset.UtcNow
                    });
                }

                PersistJson(controlPath, snapshot);
            });

            RefreshState(showErrors: false);

            ShowBalloon("FolderSync Tray", string.IsNullOrWhiteSpace(profileName) ? "Started reconciliation for all profiles." : $"Started reconciliation for {profileName}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Error);
        }
    }

    private void WriteControl(Action<RuntimeControlSnapshot> update, string successMessage)
    {
        try
        {
            var path = GetPath("foldersync-control.json");
            WithControlFileLock(path, () =>
            {
                var snapshot = TryReadJson<RuntimeControlSnapshot>(path) ?? new RuntimeControlSnapshot();
                update(snapshot);
                PersistJson(path, snapshot);
            });
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", successMessage, ToolTipIcon.Info);
        }
        catch (UnauthorizedAccessException)
        {
            HandleControlRequiresElevation();
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            var trayExecutable = GetTrayExecutablePath();
            if (string.IsNullOrWhiteSpace(trayExecutable))
                throw new InvalidOperationException("Tray executable path is unavailable.");

            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (_startWithWindowsItem.Checked)
            {
                runKey.SetValue(StartupValueName, $"\"{trayExecutable}\"");
                ShowBalloon("FolderSync Tray", "Tray app will now start with Windows.", ToolTipIcon.Info);
            }
            else
            {
                runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
                ShowBalloon("FolderSync Tray", "Tray app will no longer start with Windows.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _startWithWindowsItem.Checked = IsStartWithWindowsEnabled();
            ShowBalloon("FolderSync Tray", $"Unable to update startup setting: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void RestartElevated()
    {
        try
        {
            var trayExecutable = GetTrayExecutablePath();
            if (string.IsNullOrWhiteSpace(trayExecutable))
                throw new InvalidOperationException("Tray executable path is unavailable.");

            Process.Start(new ProcessStartInfo
            {
                FileName = trayExecutable,
                UseShellExecute = true,
                Verb = "runas"
            });

            ExitThread();
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to restart as administrator: {ex.Message}", ToolTipIcon.Warning);
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
        if (_controlSnapshot?.IsPaused is true)
            return $"FolderSync: paused ({_controlSnapshot.Reason ?? "operator"})";

        var pendingRequestCount = GetPendingReconcileRequests().Count;
        if (pendingRequestCount > 0)
            return $"FolderSync: {pendingRequestCount} reconcile queued";

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
        if (_controlSnapshot?.IsPaused is true || _healthSnapshot?.Profiles.Any(item => item.IsPaused) is true)
            return "warning";

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

    private void HandleControlRequiresElevation()
    {
        ShowBalloon("FolderSync Tray", "Pause and resume need administrator access. Use 'Restart as administrator' in the tray menu.", ToolTipIcon.Warning);

        var result = MessageBox.Show(
            "FolderSync pause and resume actions need administrator access.\n\nRestart the tray app as administrator now?",
            "FolderSync Tray",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (result == DialogResult.Yes)
            RestartElevated();
    }

    private void ApplyControlOverlay()
    {
        if (_healthSnapshot is null || _controlSnapshot is null)
            return;

        if (_controlSnapshot.IsPaused)
        {
            _healthSnapshot.ServiceState = "Paused";
        }

        foreach (var profile in _healthSnapshot.Profiles)
        {
            var effectivePause = _controlSnapshot.GetEffectivePause(profile.Name);
            profile.IsPaused = effectivePause?.IsPaused is true;
            profile.PauseReason = effectivePause?.Reason;
            profile.PausedAtUtc = effectivePause?.ChangedAtUtc;
            if (effectivePause?.IsPaused is true)
                profile.State = "Paused";
        }
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("g") ?? "n/a";
    }

    private static string SummarizeReconciliation(ProfileHealthSnapshot profile)
    {
        if (profile.RecentActivities.FirstOrDefault(activity => string.Equals(activity.Kind, "reconcile", StringComparison.OrdinalIgnoreCase)) is { } activity)
        {
            var detail = string.IsNullOrWhiteSpace(activity.Details)
                ? string.Empty
                : $" - {Truncate(activity.Details, 28)}";
            return $"{Truncate(activity.Summary, 32)}{detail}";
        }

        return "n/a";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string BuildDashboardUrl(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return DashboardUrl;

        return $"{DashboardUrl}?profile={Uri.EscapeDataString(profileName)}";
    }

    private static void WithControlFileLock(string path, Action action)
    {
        using var mutex = new Mutex(false, BuildControlMutexName(path));
        mutex.WaitOne();
        try
        {
            action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string BuildControlMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $@"Global\FolderSync-Control-{hash}";
    }

    private static void SetPaused(RuntimeControlSnapshot snapshot, bool paused, string? reason)
    {
        snapshot.IsPaused = paused;
        snapshot.Reason = paused ? reason : null;
        snapshot.ChangedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void SetProfilePaused(RuntimeControlSnapshot snapshot, string profileName, bool paused, string? reason)
    {
        var profile = snapshot.Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (paused)
        {
            profile ??= AddProfile(snapshot, profileName);
            profile.IsPaused = true;
            profile.Reason = reason;
            profile.ChangedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (profile is not null)
            snapshot.Profiles.Remove(profile);
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

    private List<string> GetKnownProfiles()
    {
        return (_healthSnapshot?.Profiles ?? [])
            .Select(profile => profile.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ReconcileRequestSnapshot> GetPendingReconcileRequests(string? profileName = null)
    {
        var requests = _controlSnapshot?.ReconcileRequests ?? [];
        if (string.IsNullOrWhiteSpace(profileName))
            return requests
                .OrderBy(request => request.RequestedAtUtc)
                .ToList();

        return requests
            .Where(request => string.Equals(request.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(request => request.RequestedAtUtc)
            .ToList();
    }

    private void UpdateTrayIcon(string state)
    {
        var icon = BuildStatusIcon(state);
        var oldIcon = _currentTrayIcon;
        _currentTrayIcon = icon;
        _notifyIcon.Icon = icon;
        oldIcon?.Dispose();
    }

    private static Icon BuildStatusIcon(string state)
    {
        var palette = state switch
        {
            "error" => (Background: Color.FromArgb(143, 38, 53), Foreground: Color.White, Accent: Color.FromArgb(255, 189, 189)),
            "warning" => (Background: Color.FromArgb(188, 116, 25), Foreground: Color.White, Accent: Color.FromArgb(255, 225, 168)),
            "paused" => (Background: Color.FromArgb(74, 88, 107), Foreground: Color.White, Accent: Color.FromArgb(255, 210, 118)),
            _ => (Background: Color.FromArgb(13, 139, 125), Foreground: Color.White, Accent: Color.FromArgb(181, 250, 238))
        };

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var shadowBrush = new SolidBrush(Color.FromArgb(36, 15, 21, 27));
            graphics.FillEllipse(shadowBrush, 3, 4, 26, 26);

            using var panelBrush = new SolidBrush(palette.Background);
            using var accentBrush = new SolidBrush(palette.Accent);
            using var path = CreateRoundedRect(new RectangleF(2, 2, 26, 26), 8f);
            graphics.FillPath(panelBrush, path);
            graphics.FillEllipse(accentBrush, 19, 5, 6, 6);

            using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(palette.Foreground);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("FS", font, textBrush, new RectangleF(2, 5, 26, 22), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string? GetTrayExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        return null;
    }

    private static bool IsStartWithWindowsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var currentValue = runKey?.GetValue(StartupValueName) as string;
        return !string.IsNullOrWhiteSpace(currentValue);
    }

    private static bool IsProcessElevated()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
