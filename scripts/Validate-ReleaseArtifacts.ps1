[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServiceZip,
    [Parameter(Mandatory = $true)][string]$TrayZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-ArchiveContains {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $normalizedEntryPath = $EntryPath.Replace('\', '/')
    $entry = $Archive.Entries | Where-Object {
        $_.FullName.TrimStart('/').Replace('\', '/') -eq $normalizedEntryPath
    } | Select-Object -First 1

    if ($null -eq $entry) {
        throw "$Description not found in archive: $EntryPath"
    }

    return $entry
}

function Test-ArchiveEntryExists {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryPath
    )

    $normalizedEntryPath = $EntryPath.Replace('\', '/')
    return $null -ne ($Archive.Entries | Where-Object {
        $_.FullName.TrimStart('/').Replace('\', '/') -eq $normalizedEntryPath
    } | Select-Object -First 1)
}

function Validate-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$PrimaryExecutable,
        [Parameter(Mandatory = $true)][string[]]$RequiredEntries,
        [string[]]$OptionalEntries = @()
    )

    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "Archive not found: $ArchivePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath))
    try {
        if ($archive.Entries.Count -eq 0) {
            throw "Archive is empty: $ArchivePath"
        }

        foreach ($entry in $RequiredEntries) {
            Assert-ArchiveContains -Archive $archive -EntryPath $entry -Description "Required release asset" | Out-Null
        }

        $exeEntry = Assert-ArchiveContains -Archive $archive -EntryPath $PrimaryExecutable -Description "Primary executable"
        if ($exeEntry.Length -le 0) {
            throw "Primary executable is empty in archive: $PrimaryExecutable"
        }

        foreach ($entry in $OptionalEntries) {
            if (Test-ArchiveEntryExists -Archive $archive -EntryPath $entry) {
                Write-Host "Found optional entry: $entry"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

Validate-ReleaseArchive `
    -ArchivePath $ServiceZip `
    -PrimaryExecutable "foldersync.exe" `
    -RequiredEntries @(
        "foldersync.exe",
        "foldersync.dll",
        "foldersync.runtimeconfig.json",
        "appsettings.example.json"
    )

Validate-ReleaseArchive `
    -ArchivePath $TrayZip `
    -PrimaryExecutable "foldersync-tray.exe" `
    -RequiredEntries @(
        "foldersync-tray.exe",
        "foldersync-tray.dll",
        "foldersync-tray.runtimeconfig.json"
    ) `
    -OptionalEntries @(
        "appsettings.example.json"
    )

Write-Host "Release artifacts validated successfully."
