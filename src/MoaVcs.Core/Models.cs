namespace MoaVcs.Core;

public enum VcsKind { Git, Svn, Perforce, Lore }

[Flags]
public enum VcsCapabilities
{
    None = 0, Diff = 1, History = 2, Stage = 4, Unstage = 8,
    Commit = 16, Update = 32, Push = 64, Initialize = 128, Clone = 256,
    Blame = 512, FileComparison = 1024, RemoteUrl = 2048, ConflictResolution = 4096
}

public enum VcsOperation { Stage, Unstage, Commit, Update, Push, Initialize, Clone }

public sealed record RepositoryInfo(VcsKind Kind, string RootPath, string Branch, string Summary = "");

public sealed record FileChange(string Path, string Status, string IndexStatus = "", string WorkTreeStatus = "", string? OriginalPath = null)
{
    public bool IsConflicted => Status.Equals("conflict", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("conflicted", StringComparison.OrdinalIgnoreCase);
    public bool IsStaged => !IsConflicted && IndexStatus.Length > 0 && IndexStatus != " " && IndexStatus != "?";
}

public sealed record HistoryEntry(string Id, string Author, string Date, string Message)
{
    public string Summary => Message.IndexOfAny(['\r', '\n']) is var end && end >= 0 ? Message[..end] : Message;
}

public sealed record HistoryDetails(HistoryEntry Entry, IReadOnlyList<FileChange> Files, string ComparisonLabel);

public sealed record FileComparison(string Path, string LeftLabel, string RightLabel, string LeftText, string RightText);

public sealed record BlameLine(int LineNumber, string Revision, string Author, string Date, string Text);

public sealed record BlameResult(string Path, string RevisionLabel, IReadOnlyList<BlameLine> Lines);

public sealed record VcsRequest(VcsOperation Operation, string WorkingDirectory,
    IReadOnlyList<string>? Paths = null, string? Message = null, string? Remote = null, string? Destination = null);

public sealed record CommandSpec(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    string? StandardInput = null, TimeSpan? Timeout = null);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public string Output => string.Join(Environment.NewLine,
        new[] { StandardOutput.TrimEnd(), StandardError.TrimEnd() }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public CommandResult EnsureSuccess()
    {
        if (!Success) throw new VcsException(string.IsNullOrWhiteSpace(Output) ? $"명령이 종료 코드 {ExitCode}로 실패했습니다." : Output);
        return this;
    }
}

public sealed class VcsException(string message) : Exception(message);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default);
}

public interface IVcsProvider
{
    VcsKind Kind { get; }
    string Name { get; }
    string Executable { get; }
    VcsCapabilities Capabilities { get; }
    Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileChange>> GetStatusAsync(string root, CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(string root, FileChange? file, bool staged = false, CancellationToken cancellationToken = default);
    Task<FileComparison> GetComparisonAsync(string root, FileChange file, bool staged = false, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 파일 비교를 지원하지 않습니다.");
    Task<BlameResult> GetBlameAsync(string root, FileChange file, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 작성자 이력 보기를 지원하지 않습니다.");
    Task<ConflictFile> GetConflictAsync(string root, FileChange file, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}의 충돌 해결은 전용 VCS 도구를 사용하세요. 내장 충돌 편집은 Git과 SVN의 텍스트 충돌을 지원합니다.");
    Task<ConflictFile> SaveConflictAsync(ConflictFile expected, string text, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}의 내장 충돌 편집을 지원하지 않습니다.");
    Task MarkConflictResolvedAsync(ConflictFile expected, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}의 내장 충돌 해결 표시를 지원하지 않습니다.");
    Task<RemoteInfo> GetRemoteAsync(string root, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 원격 주소 보기를 지원하지 않습니다.");
    Task SetRemoteUrlAsync(string root, string url, RemoteInfo expected, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 원격 주소 변경을 지원하지 않습니다.");
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default);
    Task<HistoryDetails> GetHistoryDetailsAsync(string root, string revision, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 커밋 상세 보기를 지원하지 않습니다.");
    Task<string> GetHistoryDiffAsync(string root, string revision, FileChange? file = null, CancellationToken cancellationToken = default) =>
        throw new VcsException($"{Name}에서는 아직 커밋별 변경 내용 보기를 지원하지 않습니다.");
    Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default);
}
