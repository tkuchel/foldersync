namespace FolderSync.Models;

public sealed class StatusReport
{
    public required string ServiceName { get; set; }
    public string? RawState { get; set; }
    public required string DisplayState { get; set; }
    public string? BinaryPath { get; set; }
    public string? InstallDirectory { get; set; }
    public string? Version { get; set; }
    public string? ConfigPath { get; set; }
    public List<string> Profiles { get; set; } = [];
    public string? LogsDirectory { get; set; }
    public LogFileReport? RecentLog { get; set; }
    public RuntimeHealthSnapshot? Runtime { get; set; }
    public RecentActivityReport? RecentActivity { get; set; }
}

public sealed class LogFileReport
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
}

public sealed class RecentActivityReport
{
    public string? LastReconcile { get; set; }
    public string? LastSync { get; set; }
    public string? LastLifecycle { get; set; }
    public string? LastWarning { get; set; }
    public string? LastError { get; set; }
}
