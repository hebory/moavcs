namespace MoaVcs.Core;

/// <summary>A pinned conflict and the exact working file version that the user reviewed.</summary>
public sealed record ConflictFile(
    string RootPath, string Path, string? BaseText, string LeftText, string RightText,
    string WorkingText, string StateToken, string FileHash, bool HasUtf8Bom, string NewLine,
    string LeftLabel, string RightLabel, string? BackupPath = null);
