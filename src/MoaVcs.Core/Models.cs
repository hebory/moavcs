namespace MoaVcs.Core;

public enum VcsKind { Git, Svn, Perforce, Lore }

[Flags]
public enum VcsCapabilities
{
    None = 0, Diff = 1, History = 2, Stage = 4, Unstage = 8,
    Commit = 16, Update = 32, Push = 64, Initialize = 128, Clone = 256
}

public enum VcsOperation { Stage, Unstage, Commit, Update, Push, Initialize, Clone }

public sealed record RepositoryInfo(VcsKind Kind, string RootPath, string Branch, string Summary = "");

public sealed record FileChange(string Path, string Status, string IndexStatus = "", string WorkTreeStatus = "", string? OriginalPath = null)
{
    public bool IsStaged => IndexStatus.Length > 0 && IndexStatus != " " && IndexStatus != "?";
}

public sealed record HistoryEntry(string Id, string Author, string Date, string Message);

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
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default);
    Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default);
}
