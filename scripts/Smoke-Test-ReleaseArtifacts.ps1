[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServiceZip,
    [Parameter(Mandatory = $true)][string]$TrayZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Process {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [string]$WorkingDirectory = (Get-Location).Path
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Process failed: $FilePath $($ArgumentList -join ' ') (exit code $LASTEXITCODE)"
    }
}

function Expand-ArchiveToTemp {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $tempDir = Join-Path $env:TEMP ($Prefix + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tempDir -Force
    return $tempDir
}

$serviceExtractDir = $null
$trayExtractDir = $null
$configRoot = $null

try {
    $serviceExtractDir = Expand-ArchiveToTemp -ZipPath $ServiceZip -Prefix "foldersync-service-smoke-"
    $trayExtractDir = Expand-ArchiveToTemp -ZipPath $TrayZip -Prefix "foldersync-tray-smoke-"

    $serviceExe = Join-Path $serviceExtractDir "foldersync.exe"
    $trayExe = Join-Path $trayExtractDir "foldersync-tray.exe"

    if (-not (Test-Path -LiteralPath $serviceExe)) {
        throw "Published service executable not found after extracting archive: $serviceExe"
    }

    if (-not (Test-Path -LiteralPath $trayExe)) {
        throw "Published tray executable not found after extracting archive: $trayExe"
    }

    $configRoot = Join-Path $env:TEMP ("foldersync-config-smoke-" + [guid]::NewGuid().ToString("N"))
    $sourceDir = Join-Path $configRoot "source"
    $destDir = Join-Path $configRoot "dest"
    New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    $configPath = Join-Path $configRoot "appsettings.smoke.json"
    @"
{
  "FolderSync": {
    "Profiles": [
      {
        "Name": "smoke-test",
        "SourcePath": "$($sourceDir.Replace('\', '\\'))",
        "DestinationPath": "$($destDir.Replace('\', '\\'))"
      }
    ]
  }
}
"@ | Set-Content -LiteralPath $configPath

    Invoke-Process -FilePath $serviceExe -ArgumentList @("validate-config", "--config", $configPath)
    Invoke-Process -FilePath $trayExe -ArgumentList @("--smoke-test")

    Write-Host "Packaged binaries smoke-tested successfully."
}
finally {
    foreach ($path in @($serviceExtractDir, $trayExtractDir, $configRoot)) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
