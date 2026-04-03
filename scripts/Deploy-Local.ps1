[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TargetDir = "C:\FolderSync",
    [string]$ServiceName = "FolderSync",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoRestart,
    [switch]$SkipTray
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-SafeTargetDirectory {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')

    if ([string]::IsNullOrWhiteSpace($fullPath)) {
        throw "Target directory must not be empty."
    }

    if ($fullPath -eq $root) {
        throw "Refusing to deploy to a drive root: $fullPath"
    }

    return $fullPath
}

function Test-IsAdministrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-RobocopyCopy {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$PreserveLiveConfig
    )

    $arguments = @($Source, $Destination, "/E", "/R:2", "/W:2", "/NFL", "/NDL", "/NP")
    if ($PreserveLiveConfig) {
        $arguments += @("/XF", "appsettings.json", "/XD", "logs")
    }

    & robocopy @arguments
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE."
    }
}

function Remove-StalePublishDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [Parameter(Mandatory = $true)][string]$CurrentPublishDir
    )

    $staleDirectories = Get-ChildItem -Path $TempRoot -Directory -Filter "foldersync-publish-*" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $CurrentPublishDir }

    foreach ($directory in $staleDirectories) {
        try {
            if ($PSCmdlet.ShouldProcess($directory.FullName, "Remove stale publish directory")) {
                Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction Stop
                Write-Host "Removed stale publish directory $($directory.FullName)"
            }
        }
        catch {
            Write-Warning "Could not remove stale publish directory '$($directory.FullName)': $($_.Exception.Message)"
        }
    }
}

function New-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [string]$Arguments = "",
        [string]$WorkingDirectory = "",
        [string]$Description = "",
        [string]$IconLocation = ""
    )

    $shortcutDirectory = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path $shortcutDirectory)) {
        New-Item -ItemType Directory -Path $shortcutDirectory | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $shortcut.Arguments = $Arguments
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $shortcut.WorkingDirectory = $WorkingDirectory
    }
    if (-not [string]::IsNullOrWhiteSpace($Description)) {
        $shortcut.Description = $Description
    }
    if (-not [string]::IsNullOrWhiteSpace($IconLocation)) {
        $shortcut.IconLocation = $IconLocation
    }
    $shortcut.Save()
}

function Get-RunningProcessesForExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    $matches = @()

    foreach ($process in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            continue
        }

        try {
            $candidatePath = [System.IO.Path]::GetFullPath($process.ExecutablePath)
            if ($candidatePath.Equals($resolvedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $matches += $process
            }
        }
        catch {
        }
    }

    return $matches
}

function Stop-RunningExecutableProcesses {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    if (-not (Test-Path $ExecutablePath)) {
        return $false
    }

    $processes = @(Get-RunningProcessesForExecutable -ExecutablePath $ExecutablePath)
    if ($processes.Count -eq 0) {
        return $false
    }

    foreach ($process in $processes) {
        if ($PSCmdlet.ShouldProcess("$DisplayName PID $($process.ProcessId)", "Stop process")) {
            Write-Host "Stopping $DisplayName process $($process.ProcessId)..."
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }
    }

    if ($WhatIfPreference) {
        return $true
    }

    Start-Sleep -Seconds 1

    $remaining = @(Get-RunningProcessesForExecutable -ExecutablePath $ExecutablePath)
    if ($remaining.Count -gt 0) {
        throw "Failed to stop all running '$DisplayName' processes using $ExecutablePath."
    }

    return $true
}

$repoRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    (Get-Location).Path
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
$solutionPath = Join-Path $repoRoot "FolderSync.slnx"
$projectPath = Join-Path $repoRoot "src\FolderSync\FolderSync.csproj"
$trayProjectPath = Join-Path $repoRoot "src\FolderSync.Tray\FolderSync.Tray.csproj"
$targetDir = Assert-SafeTargetDirectory -Path $TargetDir
$trayTargetDir = Join-Path $targetDir "Tray"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("Programs")) "FolderSync"
$tempRoot = [System.IO.Path]::GetTempPath()
$publishDir = Join-Path $tempRoot ("foldersync-publish-" + [guid]::NewGuid().ToString("N"))
$trayPublishDir = Join-Path $tempRoot ("foldersync-tray-publish-" + [guid]::NewGuid().ToString("N"))
$serviceExePath = Join-Path $targetDir "foldersync.exe"
$trayExePath = Join-Path $trayTargetDir "foldersync-tray.exe"
$wasRunning = $false
$trayWasRunning = $false
$isAdministrator = Test-IsAdministrator

try {
    if (-not (Test-Path $projectPath)) {
        throw "Project file not found: $projectPath"
    }

    if (-not $SkipTray -and -not (Test-Path $trayProjectPath)) {
        throw "Tray project file not found: $trayProjectPath"
    }

    if (-not (Test-Path $targetDir)) {
        throw "Target directory not found: $targetDir"
    }

    $targetConfigPath = Join-Path $targetDir "appsettings.json"
    if (-not (Test-Path $targetConfigPath)) {
        throw "Target config not found: $targetConfigPath"
    }

    Remove-StalePublishDirectories -TempRoot $tempRoot -CurrentPublishDir $publishDir

    if (-not $SkipTests) {
        Write-Host "Running test suite..."
        Invoke-DotNet -Arguments @("test", $solutionPath, "--nologo") -WorkingDirectory $repoRoot
    }

    Write-Host "Validating target configuration..."
    Invoke-DotNet -Arguments @("run", "--project", $projectPath, "--", "validate-config", "--config", $targetConfigPath) -WorkingDirectory $repoRoot

    Write-Host "Publishing FolderSync ($Configuration)..."
    New-Item -ItemType Directory -Path $publishDir | Out-Null
    Invoke-DotNet -Arguments @("publish", $projectPath, "-c", $Configuration, "-o", $publishDir) -WorkingDirectory $repoRoot

    if (-not $SkipTray) {
        Write-Host "Publishing FolderSync.Tray ($Configuration)..."
        New-Item -ItemType Directory -Path $trayPublishDir | Out-Null
        Invoke-DotNet -Arguments @("publish", $trayProjectPath, "-c", $Configuration, "-o", $trayPublishDir) -WorkingDirectory $repoRoot
    }

    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    $wasRunning = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running

    if ($wasRunning -and -not $NoRestart -and -not $isAdministrator) {
        throw "Deployment needs an elevated PowerShell session to stop and restart the '$ServiceName' service. Re-run this script as Administrator."
    }

    if ($wasRunning -and $PSCmdlet.ShouldProcess($ServiceName, "Stop Windows service")) {
        try {
            Write-Host "Stopping service $ServiceName..."
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        catch {
            throw "Failed to stop service '$ServiceName'. Re-run this script from an elevated PowerShell session. Original error: $($_.Exception.Message)"
        }
    }

    Stop-RunningExecutableProcesses -ExecutablePath $serviceExePath -DisplayName "FolderSync dashboard/command host" | Out-Null

    if (-not $SkipTray) {
        $trayWasRunning = Stop-RunningExecutableProcesses -ExecutablePath $trayExePath -DisplayName "FolderSync tray companion"
    }

    if ($PSCmdlet.ShouldProcess($targetDir, "Copy published output")) {
        Write-Host "Updating files in $targetDir..."
        Invoke-RobocopyCopy -Source $publishDir -Destination $targetDir -PreserveLiveConfig
    }

    if (-not $SkipTray) {
        if ($PSCmdlet.ShouldProcess($trayTargetDir, "Copy tray companion output")) {
            if (-not (Test-Path $trayTargetDir)) {
                New-Item -ItemType Directory -Path $trayTargetDir | Out-Null
            }

            Write-Host "Updating tray companion in $trayTargetDir..."
            Invoke-RobocopyCopy -Source $trayPublishDir -Destination $trayTargetDir
        }

        $trayShortcutPath = Join-Path $startMenuDir "FolderSync Tray.lnk"
        $dashboardShortcutPath = Join-Path $startMenuDir "FolderSync Dashboard.lnk"
        if ($PSCmdlet.ShouldProcess($startMenuDir, "Create Start Menu shortcuts")) {
            Write-Host "Updating Start Menu shortcuts in $startMenuDir..."
            New-StartMenuShortcut `
                -ShortcutPath $trayShortcutPath `
                -TargetPath $trayExePath `
                -WorkingDirectory $trayTargetDir `
                -Description "FolderSync tray companion" `
                -IconLocation $trayExePath
            New-StartMenuShortcut `
                -ShortcutPath $dashboardShortcutPath `
                -TargetPath $serviceExePath `
                -Arguments "dashboard" `
                -WorkingDirectory $targetDir `
                -Description "FolderSync dashboard" `
                -IconLocation $trayExePath
        }

        if ($trayWasRunning -and (Test-Path $trayExePath) -and $PSCmdlet.ShouldProcess($trayExePath, "Restart tray companion")) {
            Write-Host "Restarting tray companion..."
            Start-Process -FilePath $trayExePath -WorkingDirectory $trayTargetDir
        }
    }

    if (-not $NoRestart -and $wasRunning -and $PSCmdlet.ShouldProcess($ServiceName, "Start Windows service")) {
        try {
            Write-Host "Starting service $ServiceName..."
            Start-Service -Name $ServiceName -ErrorAction Stop
        }
        catch {
            throw "Deployment copied files but failed to restart service '$ServiceName'. Start it manually from an elevated PowerShell session. Original error: $($_.Exception.Message)"
        }
    }

    Write-Host "Deployment completed successfully."
    Write-Host "Live config preserved at $targetConfigPath"
    if (-not $SkipTray) {
        Write-Host "Tray companion published to $trayTargetDir"
        Write-Host "Launch it with $([System.IO.Path]::Combine($trayTargetDir, 'foldersync-tray.exe'))"
        Write-Host "Start Menu shortcuts updated in $startMenuDir"
    }
}
finally {
    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    if (Test-Path $trayPublishDir) {
        Remove-Item -LiteralPath $trayPublishDir -Recurse -Force
    }
}
