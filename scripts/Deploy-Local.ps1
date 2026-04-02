[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TargetDir = "C:\FolderSync",
    [string]$ServiceName = "FolderSync",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoRestart
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
        [Parameter(Mandatory = $true)][string]$Destination
    )

    & robocopy $Source $Destination /E /R:2 /W:2 /NFL /NDL /NP /XF appsettings.json /XD logs
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    (Get-Location).Path
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
$solutionPath = Join-Path $repoRoot "FolderSync.slnx"
$projectPath = Join-Path $repoRoot "src\FolderSync\FolderSync.csproj"
$targetDir = Assert-SafeTargetDirectory -Path $TargetDir
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("foldersync-publish-" + [guid]::NewGuid().ToString("N"))
$wasRunning = $false
$isAdministrator = Test-IsAdministrator

try {
    if (-not (Test-Path $projectPath)) {
        throw "Project file not found: $projectPath"
    }

    if (-not (Test-Path $targetDir)) {
        throw "Target directory not found: $targetDir"
    }

    $targetConfigPath = Join-Path $targetDir "appsettings.json"
    if (-not (Test-Path $targetConfigPath)) {
        throw "Target config not found: $targetConfigPath"
    }

    if (-not $SkipTests) {
        Write-Host "Running test suite..."
        Invoke-DotNet -Arguments @("test", $solutionPath, "--nologo") -WorkingDirectory $repoRoot
    }

    Write-Host "Validating target configuration..."
    Invoke-DotNet -Arguments @("run", "--project", $projectPath, "--", "validate-config", "--config", $targetConfigPath) -WorkingDirectory $repoRoot

    Write-Host "Publishing FolderSync ($Configuration)..."
    New-Item -ItemType Directory -Path $publishDir | Out-Null
    Invoke-DotNet -Arguments @("publish", $projectPath, "-c", $Configuration, "-o", $publishDir) -WorkingDirectory $repoRoot

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

    if ($PSCmdlet.ShouldProcess($targetDir, "Copy published output")) {
        Write-Host "Updating files in $targetDir..."
        Invoke-RobocopyCopy -Source $publishDir -Destination $targetDir
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
}
finally {
    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
}
