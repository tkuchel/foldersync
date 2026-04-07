namespace FolderSync.Models;

public sealed class TwoWayApplyResult
{
    public int CopiedLeftToRight { get; set; }
    public int CopiedRightToLeft { get; set; }
    public int SkippedConflicts { get; set; }
    public int SkippedDeletes { get; set; }
    public int Failed { get; set; }
    public List<TwoWayApplyError> Errors { get; init; } = [];
}

public sealed class TwoWayApplyError
{
    public string RelativePath { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
