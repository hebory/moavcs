using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

/// <summary>Adapter for Epic Games Lore's CLI.</summary>
public sealed partial class LoreProvider : IVcsProvider
{
    private readonly ICommandRunner _runner;
    public LoreProvider(ICommandRunner? runner = null, string executable = "lore")
    { _runner = runner ?? new ProcessCommandRunner(); Executable = executable; }

    public VcsKind Kind => VcsKind.Lore;
    public string Name => "Lore";
    public string Executable { get; }
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage |
        VcsCapabilities.Unstage | VcsCapabilities.Commit | VcsCapabilities.Update | VcsCapabilities.Push |
        VcsCapabilities.Initialize | VcsCapabilities.Clone | VcsCapabilities.FileComparison | VcsCapabilities.Blame;

    public async Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new VcsException($"폴더를 찾을 수 없습니다: {full}");
        var root = FindRoot(full) ?? throw new VcsException("Epic Games Lore working tree (.lore/config.toml) was not found.");
        var status = await Run(root, ["status", "--scan"], cancellationToken);
        var branch = ParseStatusText(status.StandardOutput, root, requireBranch: true, out _) ?? "(branch not reported)";
        var remote = ReadRemote(root);
        return new RepositoryInfo(Kind, root, branch, remote);
    }

    public async Task<IReadOnlyList<FileChange>> GetStatusAsync(string root, CancellationToken cancellationToken = default)
    {
        var result = await Run(root, ["status", "--scan"], cancellationToken);
        ParseStatusText(result.StandardOutput, root, requireBranch: true, out var changes);
        return changes;
    }

    public async Task<string> GetDiffAsync(string root, FileChange? file, bool staged = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "diff" };
        if (file is not null) { args.Add("--"); args.Add(ResolveSelected(root, file.Path)); }
        var result = await Run(root, args, cancellationToken);
        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default)
        => await GetHistoryEntriesAsync(root, cancellationToken).ConfigureAwait(false);

    public async Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(request.WorkingDirectory);
        var selected = request.Paths is { Count: > 0 }
            ? request.Paths.Select(path => ResolveSelected(root, path)).ToArray()
            : new[] { "." };
        switch (request.Operation)
        {
            case VcsOperation.Stage:
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Choose files or folders to stage in Lore.");
                return await Run(root, ["stage", "--scan", "--", .. selected], cancellationToken);
            case VcsOperation.Unstage:
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Lore unstage requires one or more selected paths.");
                return await Run(root, ["unstage", "--", .. selected], cancellationToken);
            case VcsOperation.Commit:
                if (string.IsNullOrWhiteSpace(request.Message)) throw new VcsException("Lore 커밋 메시지를 입력하세요.");
                // Lore commits the complete staged revision; its CLI does not accept a path selection here.
                return await Run(root, ["commit", "--", request.Message], cancellationToken);
            case VcsOperation.Update:
                return await Run(root, ["sync"], cancellationToken);
            case VcsOperation.Push:
                return await Run(root, ["push"], cancellationToken);
            case VcsOperation.Initialize:
                return await Run(root, ["--offline", "repository", "create"], cancellationToken);
            case VcsOperation.Clone:
                if (string.IsNullOrWhiteSpace(request.Remote) || string.IsNullOrWhiteSpace(request.Destination))
                    throw new VcsException("Lore clone requires a repository URL and destination directory.");
            {
                var clone = await _runner.RunAsync(new CommandSpec(Executable,
                    ["clone", "--", request.Remote, Path.GetFullPath(request.Destination)], root), cancellationToken);
                return clone.EnsureSuccess();
            }
            default:
                throw new VcsException($"{Name} does not support {request.Operation}.");
        }
    }

    private async Task<CommandResult> Run(string root, IReadOnlyList<string> args, CancellationToken ct) =>
        (await _runner.RunAsync(new CommandSpec(Executable, ["--repository", Path.GetFullPath(root), .. args], Path.GetFullPath(root)), ct)).EnsureSuccess();

    private static string? FindRoot(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".lore", "config.toml"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static string ReadRemote(string root)
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(root, ".lore", "config.toml"));
            return Regex.Match(config, "(?m)^\\s*remote_url\\s*=\\s*\"([^\"]*)\"").Groups[1].Value;
        }
        catch (IOException) { return string.Empty; }
    }

    private static string? ParseStatusText(string output, string root, bool requireBranch, out IReadOnlyList<FileChange> changes)
    {
        var result = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
        string? branch = null;
        string? section = null;
        var sawStatusContent = false;
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var contentLine = StripAnsi(rawLine).TrimStart();
            var line = contentLine.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("On branch ", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, @"^On branch (.+) revision \d+ -> \S+$");
                if (!match.Success) throw LoreStatusFormatError();
                branch = match.Groups[1].Value;
                section = null;
                continue;
            }
            if (line.StartsWith("Repository ", StringComparison.Ordinal) ||
                line.StartsWith("Local branch ", StringComparison.Ordinal) ||
                line.StartsWith("Pending merge, incoming revision ", StringComparison.Ordinal) ||
                line == "Remote branch does not exist" || line == "No tracked changes" ||
                Regex.IsMatch(line, @"^Tracked changes:\s+\d+ (?:added|modified|deleted|moved|copied)(?:, \d+ (?:added|modified|deleted|moved|copied))*$", RegexOptions.None) ||
                Regex.IsMatch(line, @"^Repository size: \d+ directories, \d+ files$", RegexOptions.None))
            { section = null; continue; }
            if (line is "Changes staged for commit:" or "Changes in conflict:" or "Changes not staged for commit:" or "Untracked files:")
            {
                section = line;
                sawStatusContent = true;
                continue;
            }
            // These are the other documented status preamble lines emitted by Lore.
            if (line.StartsWith("Remote revision ", StringComparison.Ordinal) ||
                line.StartsWith("Remote reachable but could not read remote revision ", StringComparison.Ordinal))
            { section = null; continue; }
            if (section is null) throw LoreStatusFormatError();

            var entry = Regex.Match(contentLine, @"^(?<action>[A-Z])\s+(?<path>.+)$");
            if (!entry.Success) throw LoreStatusFormatError();
            var action = entry.Groups["action"].Value;
            if (action is not ("A" or "D" or "M" or "V" or "C")) throw LoreStatusFormatError();
            var filePath = entry.Groups["path"].Value;
            // Lore emits a separate metadata column after staged paths. Preserve its
            // trailing separator until markers are removed, so a literal '(M)' filename
            // is not mistaken for the merged flag. The source defines moves as V.
            if (section == "Changes in conflict:")
                filePath = Regex.Replace(filePath, @" (?:\(M\))?!$", "");
            else if (section == "Changes staged for commit:" && filePath.EndsWith(" (M)", StringComparison.Ordinal))
                filePath = filePath[..^4];
            filePath = filePath.TrimEnd();
            string? originalPath = null;
            if (action is "V" or "C")
            {
                var arrow = filePath.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow <= 0 || arrow + 4 >= filePath.Length) throw LoreStatusFormatError();
                originalPath = filePath[..arrow];
                filePath = filePath[(arrow + 4)..];
            }
            if (filePath.Length == 0) throw LoreStatusFormatError();
            try
            {
                var relative = ResolveSelected(root, filePath);
                var relativeOriginal = originalPath is null ? null : ResolveSelected(root, originalPath);
                var staged = section is "Changes staged for commit:" or "Changes in conflict:";
                var conflicted = section == "Changes in conflict:";
                result.TryGetValue(relative, out var existing);
                var indexStatus = staged ? action : existing?.IndexStatus ?? "";
                var workTreeStatus = staged ? existing?.WorkTreeStatus ?? "" : action;
                var displayStatus = conflicted ? "conflicted" : workTreeStatus.Length > 0 ? workTreeStatus : indexStatus;
                result[relative] = new FileChange(relative, displayStatus, indexStatus, workTreeStatus,
                    relativeOriginal ?? existing?.OriginalPath);
            }
            catch (VcsException ex) { throw new VcsException($"Lore status reported an unsafe path: {ex.Message}"); }
        }
        if ((requireBranch && branch is null) || (!sawStatusContent && result.Count > 0)) throw LoreStatusFormatError();
        changes = result.Values.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        return branch;
    }

    private static string StripAnsi(string value) => Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", "");
    private static VcsException LoreStatusFormatError() => new("Lore status output format is not recognized. Update the adapter before using this working copy.");

    private static string ResolveSelected(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new VcsException("A selected path cannot be empty.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new VcsException("The selected path must stay inside the Lore working tree.");
        return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }
}
