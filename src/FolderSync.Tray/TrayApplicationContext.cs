using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using WindowsToastNotifyApi;

namespace FolderSync.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "FolderSync";
    private const string DashboardUrl = "http://127.0.0.1:8941/";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "FolderSyncTray";
    private const string ToastAppId = "FolderSync.Tray";
    private const string ToastDisplayName = "FolderSync Tray";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _serviceItem;
    private readonly ToolStripMenuItem _serviceStateItem;
    private readonly ToolStripMenuItem _startServiceItem;
    private readonly ToolStripMenuItem _stopServiceItem;
    private readonly ToolStripMenuItem _restartServiceItem;
    private readonly ToolStripMenuItem _dashboardItem;
    private readonly ToolStripMenuItem _dashboardStateItem;
    private readonly ToolStripMenuItem _startDashboardItem;
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
    private readonly Control _uiInvoker;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ServiceControllerStatus? _serviceStatus;
    private ServiceControllerStatus? _lastObservedServiceStatus;
    private bool _dashboardResponsive;
    private bool _toastInitialized;
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
        _serviceStateItem = new ToolStripMenuItem("State: unknown") { Enabled = false };
        _startServiceItem = new ToolStripMenuItem("Start service", null, (_, _) => StartService());
        _stopServiceItem = new ToolStripMenuItem("Stop service", null, (_, _) => StopService());
        _restartServiceItem = new ToolStripMenuItem("Restart service", null, (_, _) => RestartService());
        _serviceItem = new ToolStripMenuItem("Service");
        _serviceItem.DropDownItems.AddRange(
        [
            _serviceStateItem,
            new ToolStripSeparator(),
            _startServiceItem,
            _stopServiceItem,
            _restartServiceItem
        ]);
        _dashboardStateItem = new ToolStripMenuItem("Status: unknown") { Enabled = false };
        _startDashboardItem = new ToolStripMenuItem("Start dashboard host", null, (_, _) => StartDashboardHost());
        _profilesItem = new ToolStripMenuItem("Profiles");
        _pauseAllItem = new ToolStripMenuItem("Pause all", null, (_, _) => PauseAll());
        _resumeAllItem = new ToolStripMenuItem("Resume all", null, (_, _) => ResumeAll());
        _reconcileAllItem = new ToolStripMenuItem("Reconcile all", null, (_, _) => ReconcileAll());
        _openDashboardItem = new ToolStripMenuItem("Open dashboard", null, async (_, _) => await OpenDashboardAsync());
        _dashboardItem = new ToolStripMenuItem("Dashboard");
        _dashboardItem.DropDownItems.AddRange(
        [
            _dashboardStateItem,
            new ToolStripSeparator(),
            _openDashboardItem,
            _startDashboardItem
        ]);
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
            _serviceItem,
            _dashboardItem,
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
        _uiInvoker = new Control();
        _ = _uiInvoker.Handle;
        _currentTrayIcon = _notifyIcon.Icon;
        _notifyIcon.DoubleClick += async (_, _) => await OpenDashboardAsync();
        Toast.Activated += toastArgs =>
        {
            try
            {
                _uiInvoker.BeginInvoke(new MethodInvoker(() => HandleToastActivation(toastArgs.Arguments, toastArgs.Payload)));
            }
            catch
            {
            }
        };

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
        _uiInvoker.Dispose();

        if (_dashboardProcess is { HasExited: false })
            _dashboardProcess.Dispose();

        base.ExitThreadCore();
    }

    private void RefreshState(bool showErrors)
    {
        try
        {
            ResolveInstallLocation();
            EnsureToastInitialized();
            _serviceStatus = TryGetServiceStatus();
            _healthSnapshot = TryReadJson<RuntimeHealthSnapshot>(GetPath("foldersync-health.json"));
            _controlSnapshot = TryReadJson<RuntimeControlSnapshot>(GetPath("foldersync-control.json")) ?? new RuntimeControlSnapshot();
            _dashboardResponsive = IsDashboardResponsive();
            ApplyControlOverlay();
            UpdateMenu();
            ShowAlertIfNeeded();
            ShowServiceStateToastIfNeeded();
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

        var overallState = _serviceStatus.HasValue
            ? FormatServiceStatus(_serviceStatus.Value)
            : _healthSnapshot?.ServiceState ?? "Unknown";
        var pausedText = _controlSnapshot?.IsPaused is true ? $"Paused ({_controlSnapshot.Reason ?? "no reason"})" : overallState;
        _statusItem.Text = $"FolderSync: {pausedText}";
        RepairStartupRegistrationIfNeeded();
        _startWithWindowsItem.Checked = IsStartWithWindowsEnabled();
        _restartElevatedItem.Visible = !IsProcessElevated();
        _serviceStateItem.Text = $"State: {overallState}";
        _dashboardStateItem.Text = $"Status: {(_dashboardResponsive ? "Running" : "Not running")}";
        _startServiceItem.Enabled = _serviceStatus is ServiceControllerStatus.Stopped;
        _stopServiceItem.Enabled = _serviceStatus is ServiceControllerStatus.Running;
        _restartServiceItem.Enabled = _serviceStatus is ServiceControllerStatus.Running;
        _startDashboardItem.Enabled = !_dashboardResponsive && !string.IsNullOrWhiteSpace(_executablePath);

        var highestSeverity = GetHighestSeverityProfile();
        var iconState = highestSeverity switch
        {
            "error" => "error",
            "warning" => "warning",
            _ when _serviceStatus is ServiceControllerStatus.Stopped => "warning",
            _ when _controlSnapshot?.IsPaused is true => "paused",
            _ => "running"
        };
        UpdateTrayIcon(iconState);

        _notifyIcon.Text = TrimNotifyText(BuildNotifyText(overallState));

        var serviceRunning = _serviceStatus is ServiceControllerStatus.Running;
        _pauseAllItem.Enabled = serviceRunning && _controlSnapshot?.IsPaused is not true;
        _resumeAllItem.Enabled = serviceRunning && (_controlSnapshot?.IsPaused is true || (_controlSnapshot?.Profiles.Count ?? 0) > 0);
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
        var stateLabel = profile.IsPaused
            ? $"Paused: {profile.PauseReason ?? "operator"}"
            : profile.AlertMessage ?? profile.State;

        var root = new ToolStripMenuItem(profile.Name);
        root.DropDownItems.Add(new ToolStripMenuItem($"Status: {stateLabel}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Processed: {profile.ProcessedCount} | Failed: {profile.FailedCount}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripMenuItem($"Overflows: {profile.WatcherOverflowCount}") { Enabled = false });
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
        ShowAlertToast(profile);
    }

    private void ShowServiceStateToastIfNeeded()
    {
        if (!_serviceStatus.HasValue)
            return;

        if (!_lastObservedServiceStatus.HasValue)
        {
            _lastObservedServiceStatus = _serviceStatus;
            return;
        }

        if (_lastObservedServiceStatus == _serviceStatus)
            return;

        var previous = _lastObservedServiceStatus.Value;
        var current = _serviceStatus.Value;
        _lastObservedServiceStatus = current;

        if (current == ServiceControllerStatus.Stopped)
        {
            ShowToast(
                "FolderSync service stopped",
                "The Windows service is not running. You can restart it or inspect the dashboard.",
                primaryAction: "start-service",
                primaryLabel: "Start service",
                secondaryAction: "open-dashboard",
                secondaryLabel: "Open dashboard",
                preset: Toast.Warning);
            return;
        }

        if (previous == ServiceControllerStatus.Stopped && current == ServiceControllerStatus.Running)
        {
            ShowToast(
                "FolderSync service running",
                "The Windows service is back online.",
                primaryAction: "open-dashboard",
                primaryLabel: "Open dashboard",
                preset: Toast.Success);
        }
    }

    private void ShowAlertToast(ProfileHealthSnapshot profile)
    {
        if (string.IsNullOrWhiteSpace(profile.AlertMessage))
            return;

        ShowToast(
            $"FolderSync alert: {profile.Name}",
            profile.AlertMessage,
            primaryAction: "open-dashboard",
            primaryLabel: "Open dashboard",
            secondaryAction: _serviceStatus == ServiceControllerStatus.Running ? "reconcile" : "start-service",
            secondaryLabel: _serviceStatus == ServiceControllerStatus.Running ? "Reconcile now" : "Start service",
            payload: new Dictionary<string, string> { ["profile"] = profile.Name },
            preset: Toast.Warning);
    }

    private void ShowToast(
        string title,
        string message,
        string primaryAction,
        string primaryLabel,
        string? secondaryAction = null,
        string? secondaryLabel = null,
        Dictionary<string, string>? payload = null,
        Action<ToastOptions>? configure = null,
        Action<string, string, ToastOptions>? preset = null)
    {
        try
        {
            EnsureToastInitialized();
            if (!_toastInitialized)
                return;

            var options = new ToastOptions
            {
                PrimaryButton = (primaryLabel, primaryAction),
                Payload = payload ?? []
            };

            if (!string.IsNullOrWhiteSpace(secondaryAction) && !string.IsNullOrWhiteSpace(secondaryLabel))
                options.SecondaryButton = (secondaryLabel, secondaryAction);

            configure?.Invoke(options);

            if (preset is not null)
                preset(title, message, options);
            else
                Toast.Show(title, message, options);
        }
        catch
        {
        }
    }

    private void EnsureToastInitialized()
    {
        if (_toastInitialized)
            return;

        try
        {
            Toast.Initialize(ToastAppId, ToastDisplayName, iconPath: null);
            _toastInitialized = true;
        }
        catch
        {
            _toastInitialized = false;
        }
    }

    private void HandleToastActivation(string argument, IReadOnlyDictionary<string, string>? payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argument))
                return;

            payload ??= new Dictionary<string, string>();
            payload.TryGetValue("profile", out var profileName);
            var action = argument;

            switch (action)
            {
                case "open-dashboard":
                    _ = OpenDashboardAsync(profileName);
                    break;
                case "reconcile":
                    if (string.IsNullOrWhiteSpace(profileName))
                        ReconcileAll();
                    else
                        ReconcileProfile(profileName);
                    break;
                case "restart-elevated":
                    RestartElevated();
                    break;
                case "start-service":
                    StartService();
                    break;
            }
        }
        catch
        {
        }
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

    private bool IsDashboardResponsive()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var response = client.GetAsync(DashboardUrl).GetAwaiter().GetResult();
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

    private void StartDashboardHost()
    {
        try
        {
            StartDashboardProcess();
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", "Dashboard host started.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to start dashboard host: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void StartService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", "FolderSync service started.", ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("start");
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to start service: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void StopService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", "FolderSync service stopped.", ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("stop");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("stop");
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to stop service: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void RestartService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            RefreshState(showErrors: false);
            ShowBalloon("FolderSync Tray", "FolderSync service restarted.", ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("restart");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            HandleServiceRequiresElevation("restart");
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", $"Unable to restart service: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void PauseAll()
    {
        WriteControl(store => store.SetPaused(true, "Paused by tray app"), "FolderSync paused", activityAction: "pause", profileName: null, activityDetails: "Paused by tray app");
    }

    private void ResumeAll()
    {
        WriteControl(store =>
        {
            store.SetPaused(false, null);
            store.Profiles.Clear();
        }, "FolderSync resumed", activityAction: "resume", profileName: null, activityDetails: null);
    }

    private void PauseProfile(string profileName)
    {
        WriteControl(store =>
        {
            store.SetProfilePaused(profileName, true, "Paused by tray app");
        }, $"Paused {profileName}", activityAction: "pause", profileName: profileName, activityDetails: "Paused by tray app");
    }

    private void ResumeProfile(string profileName)
    {
        WriteControl(store =>
        {
            store.SetProfilePaused(profileName, false, null);
        }, $"Resumed {profileName}", activityAction: "resume", profileName: profileName, activityDetails: null);
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
            arguments += " --trigger Tray";
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

            WriteRuntimeActivity("reconcile", profileName, "Requested from tray app");
            RefreshState(showErrors: false);

            ShowBalloon("FolderSync Tray", string.IsNullOrWhiteSpace(profileName) ? "Started reconciliation for all profiles." : $"Started reconciliation for {profileName}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("FolderSync Tray", ex.Message, ToolTipIcon.Error);
        }
    }

    private void WriteControl(Action<RuntimeControlSnapshot> update, string successMessage, string activityAction, string? profileName, string? activityDetails)
    {
        try
        {
            var path = GetPath("foldersync-control.json");
            var snapshot = TryReadJson<RuntimeControlSnapshot>(path) ?? new RuntimeControlSnapshot();

            update(snapshot);
            PersistJson(path, snapshot);
            WriteRuntimeActivity(activityAction, profileName, activityDetails);
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
                runKey.SetValue(StartupValueName, FormatStartupCommand(trayExecutable));
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

    private void RepairStartupRegistrationIfNeeded()
    {
        try
        {
            if (!IsStartWithWindowsEnabled())
                return;

            var trayExecutable = GetTrayExecutablePath();
            if (string.IsNullOrWhiteSpace(trayExecutable))
                return;

            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey is null)
                return;

            var currentValue = runKey.GetValue(StartupValueName) as string;
            var desiredValue = FormatStartupCommand(trayExecutable);
            if (!string.Equals(currentValue, desiredValue, StringComparison.OrdinalIgnoreCase))
            {
                runKey.SetValue(StartupValueName, desiredValue);
            }
        }
        catch
        {
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

    private void HandleServiceRequiresElevation(string action)
    {
        ShowBalloon("FolderSync Tray", $"Service {action} needs administrator access. Use 'Restart as administrator' in the tray menu.", ToolTipIcon.Warning);

        var result = MessageBox.Show(
            $"FolderSync service {action} needs administrator access.\n\nRestart the tray app as administrator now?",
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

        if (_serviceStatus.HasValue)
        {
            _healthSnapshot.ServiceState = FormatServiceStatus(_serviceStatus.Value);
        }

        if (_controlSnapshot.IsPaused)
        {
            _healthSnapshot.ServiceState = "Paused";
        }

        foreach (var profile in _healthSnapshot.Profiles)
        {
            var effectivePause = _controlSnapshot.GetEffectivePause(profile.Name);
            profile.IsPaused = effectivePause.IsPaused;
            profile.PauseReason = effectivePause.Reason;
            if (effectivePause.IsPaused)
                profile.State = "Paused";
        }
    }

    private void WriteRuntimeActivity(string action, string? profileName, string? details)
    {
        try
        {
            var path = GetPath("foldersync-health.json");
            var snapshot = TryReadJson<RuntimeHealthSnapshot>(path);
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
                var profile = snapshot.Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                    return;

                ApplyActivity(profile, action, details);
            }

            PersistJson(path, snapshot);
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
                profile.State = "Paused";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "control",
                    Summary = "Paused from tray app",
                    TimestampUtc = now,
                    Details = details
                });
                break;
            case "resume":
                profile.IsPaused = false;
                profile.PauseReason = null;
                if (string.Equals(profile.State, "Paused", StringComparison.OrdinalIgnoreCase))
                    profile.State = "Running";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "control",
                    Summary = "Resumed from tray app",
                    TimestampUtc = now,
                    Details = details
                });
                break;
            case "reconcile":
                profile.Reconciliation.LastTrigger = "Tray";
                profile.AddActivity(new ProfileActivitySnapshot
                {
                    Kind = "reconcile",
                    Summary = "Reconciliation requested from tray app",
                    TimestampUtc = now,
                    Details = details
                });
                break;
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

    private string? GetTrayExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_installDirectory))
        {
            var installPath = Path.Combine(_installDirectory, "Tray", "foldersync-tray.exe");
            if (File.Exists(installPath))
                return installPath;
        }

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

    private static string FormatStartupCommand(string trayExecutable)
    {
        return $"\"{trayExecutable}\"";
    }

    private static ServiceControllerStatus? TryGetServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatServiceStatus(ServiceControllerStatus status)
    {
        return status switch
        {
            ServiceControllerStatus.Running => "Running",
            ServiceControllerStatus.Stopped => "Stopped",
            ServiceControllerStatus.StartPending => "Starting",
            ServiceControllerStatus.StopPending => "Stopping",
            ServiceControllerStatus.PausePending => "Pausing",
            ServiceControllerStatus.Paused => "Paused",
            ServiceControllerStatus.ContinuePending => "Continuing",
            _ => status.ToString()
        };
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

internal sealed class RuntimeControlSnapshot
{
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
    public List<ProfileRuntimeControlSnapshot> Profiles { get; set; } = [];

    public void SetPaused(bool paused, string? reason)
    {
        IsPaused = paused;
        Reason = paused ? reason : null;
        ChangedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetProfilePaused(string profileName, bool paused, string? reason)
    {
        var profile = Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (paused)
        {
            profile ??= AddProfile(profileName);
            profile.IsPaused = true;
            profile.Reason = reason;
            profile.ChangedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (profile is not null)
            Profiles.Remove(profile);
    }

    public ProfileRuntimeControlSnapshot GetEffectivePause(string profileName)
    {
        if (IsPaused)
        {
            return new ProfileRuntimeControlSnapshot
            {
                Name = profileName,
                IsPaused = true,
                Reason = Reason,
                ChangedAtUtc = ChangedAtUtc
            };
        }

        var profile = Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
            return profile;

        return new ProfileRuntimeControlSnapshot
        {
            Name = profileName
        };
    }

    private ProfileRuntimeControlSnapshot AddProfile(string profileName)
    {
        var profile = new ProfileRuntimeControlSnapshot
        {
            Name = profileName
        };
        Profiles.Add(profile);
        return profile;
    }
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
    private const int MaxRecentActivities = 12;

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

    public void AddActivity(ProfileActivitySnapshot activity)
    {
        RecentActivities.Insert(0, activity);
        if (RecentActivities.Count > MaxRecentActivities)
            RecentActivities.RemoveRange(MaxRecentActivities, RecentActivities.Count - MaxRecentActivities);
    }
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
