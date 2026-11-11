using System.Xml.Linq;

namespace MoaVcs.Core.Providers;

/// <summary>Subversion client adapter. SVN has no index, so commits are always scoped to the selected paths.</summary>
public sealed partial class SvnProvider : IVcsProvider
{
    private readonly ICommandRunner _runner;
    public SvnProvider(ICommandRunner? runner = null, string executable = "svn")
    { _runner = runner ?? new ProcessCommandRunner(); Executable = executable; }

    public VcsKind Kind => VcsKind.Svn;
    public string Name => "Subversion";
    public string Executable { get; }
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage | VcsCapabilities.Commit | VcsCapabilities.Update | VcsCapabilities.Clone | VcsCapabilities.FileComparison | VcsCapabilities.Blame | VcsCapabilities.ConflictResolution;

    public async Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var result = await Run(["info", "--xml", EscapePegPath(full)], full, cancellationToken);
        var entry = XDocument.Parse(result.StandardOutput).Descendants("entry").FirstOrDefault()
            ?? throw new VcsException("svn info did not return a working-copy entry.");
        var root = entry.Element("wc-info")?.Element("wcroot-abspath")?.Value;
        if (string.IsNullOrWhiteSpace(root)) throw new VcsException("The selected path is not an SVN working copy.");
        var url = entry.Element("url")?.Value ?? string.Empty;
        var relativeUrl = (entry.Element("relative-url")?.Value ?? string.Empty).TrimStart('^', '/');
        var parts = relativeUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var branch = parts.Length > 0 && parts[0].Equals("trunk", StringComparison.OrdinalIgnoreCase) ? "trunk"
            : parts.Length > 1 && (parts[0] is "branches" or "tags") ? parts[1]
            : Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Segments.LastOrDefault()?.Trim('/') ?? "working copy"
            : "working copy";
        return new RepositoryInfo(Kind, Path.GetFullPath(root), branch, url);
    }

    public async Task<IReadOnlyList<FileChange>> GetStatusAsync(string root, CancellationToken cancellationToken = default)
    {
        var result = await Run(["status", "--xml", EscapePegPath(Path.GetFullPath(root))], root, cancellationToken);
        var changes = new List<FileChange>();
        foreach (var item in XDocument.Parse(result.StandardOutput).Descendants("entry"))
        {
            var wc = item.Element("wc-status");
            if (wc is null) continue;
            var status = wc.Attribute("item")?.Value ?? "normal";
            var props = wc.Attribute("props")?.Value;
            var conflicted = wc.Attribute("tree-conflicted")?.Value == "true" || status == "conflicted" || props == "conflicted";
            if (status == "normal" && (props is null or "none" or "normal") && !conflicted) continue;
            var path = item.Attribute("path")?.Value ?? string.Empty;
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            if (!IsWithin(root, full)) throw new VcsException("SVN status reported a path outside the working copy.");
            var relative = Path.GetRelativePath(Path.GetFullPath(root), full);
            var display = conflicted ? "conflicted" : status == "normal" && props == "modified" ? "property modified" : status;
            changes.Add(new FileChange(relative, display, string.Empty, display));
        }
        return changes;
    }

    public async Task<string> GetDiffAsync(string root, FileChange? file, bool staged = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "diff" };
        if (file is not null) { args.Add("--"); args.Add(EscapePegPath(ResolveSelected(root, file.Path))); }
        var result = await Run(args, root, cancellationToken);
        return result.StandardOutput;
    }

    public async Task<FileComparison> GetComparisonAsync(string root, FileChange file, bool staged = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        root = Path.GetFullPath(root);
        var fullPath = ResolveSelected(root, file.Path);
        var isNew = file.Status is "unversioned" or "added";
        string? baseline = null;
        if (!isNew)
        {
            var result = await Run(["cat", "--revision", "BASE", "--", EscapePegPath(fullPath)], root, cancellationToken);
            baseline = TextSnapshot.ValidateText(result.StandardOutput, file.Path);
        }

        var working = File.Exists(fullPath)
            ? await TextSnapshot.ReadFileAsync(fullPath, file.Path, cancellationToken)
            : null;

        return new FileComparison(file.Path,
            baseline is null ? "SVN BASE (파일 없음)" : "SVN BASE",
            working is null ? "작업 복사본 (삭제됨)" : "작업 복사본",
            baseline ?? string.Empty, working ?? string.Empty);
    }

    public async Task<BlameResult> GetBlameAsync(string root, FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        root = Path.GetFullPath(root);
        var fullPath = ResolveSelected(root, file.Path);
        if (file.Status is "unversioned" or "added" or "deleted" or "missing")
            throw new VcsException("SVN 작성자 이력은 현재 작업 복사본의 BASE에 존재하는 파일만 표시할 수 있습니다.");

        var baseResult = await Run(["cat", "--revision", "BASE", "--", EscapePegPath(fullPath)], root, cancellationToken);
        var content = TextSnapshot.ValidateText(baseResult.StandardOutput, file.Path);
        if (CountLines(content) > LineDiff.MaxLines)
            throw new VcsException($"'{file.Path}'은 {LineDiff.MaxLines:N0}줄을 초과하여 작성자 이력을 표시할 수 없습니다.");
        var sourceLines = SplitLines(content);
        var blameResult = await Run(["blame", "--xml", "--revision", "BASE", "--", EscapePegPath(fullPath)], root, cancellationToken);
        var xml = XDocument.Parse(blameResult.StandardOutput);
        var entries = xml.Descendants("entry").ToArray();
        if (entries.Length != sourceLines.Count)
            throw new VcsException("SVN blame 메타데이터의 줄 수와 BASE 파일 내용이 일치하지 않습니다.");

        var linesByNumber = new BlameLine[sourceLines.Count];
        foreach (var entry in entries)
        {
            var lineNumberText = (string?)entry.Attribute("line-number");
            if (!int.TryParse(lineNumberText, out var lineNumber) || lineNumber < 1 || lineNumber > sourceLines.Count)
                throw new VcsException("SVN blame XML의 줄 번호 형식을 인식할 수 없습니다.");
            if (linesByNumber[lineNumber - 1] is not null)
                throw new VcsException("SVN blame XML에 중복 줄 번호가 있습니다.");
            var commit = entry.Element("commit");
            var revision = (string?)commit?.Attribute("revision")
                ?? throw new VcsException("SVN blame XML에 커밋 리비전이 없습니다.");
            linesByNumber[lineNumber - 1] = new BlameLine(lineNumber, revision,
                commit!.Element("author")?.Value ?? string.Empty,
                commit!.Element("date")?.Value ?? string.Empty,
                sourceLines[lineNumber - 1]);
        }
        if (linesByNumber.Any(line => line is null)) throw new VcsException("SVN blame XML에 일부 줄 정보가 없습니다.");
        return new BlameResult(file.Path, "SVN BASE", linesByNumber);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default)
    {
        var result = await Run(["log", "--xml", "--verbose", "--limit", "50", EscapePegPath(Path.GetFullPath(root))], root, cancellationToken);
        return XDocument.Parse(result.StandardOutput).Descendants("logentry").Select(entry =>
            new HistoryEntry((string?)entry.Attribute("revision") ?? "", entry.Element("author")?.Value ?? "",
                entry.Element("date")?.Value ?? "", entry.Element("msg")?.Value ?? "")).ToArray();
    }

    public async Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(request.WorkingDirectory);
        switch (request.Operation)
        {
            case VcsOperation.Stage:
            {
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Choose files or folders to add to SVN.");
                var args = new List<string> { "add" };
                AddSelectedPaths(args, root, request.Paths);
                return await Run(args, root, cancellationToken);
            }
            case VcsOperation.Commit:
            {
                if (string.IsNullOrWhiteSpace(request.Message)) throw new VcsException("SVN 커밋 메시지를 입력하세요.");
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Choose files or folders to commit in SVN.");
                var args = new List<string> { "commit", "--message", request.Message };
                args.Add("--depth");
                args.Add("empty");
                AddSelectedPaths(args, root, request.Paths);
                return await Run(args, root, cancellationToken);
            }
            case VcsOperation.Update:
            {
                var args = new List<string> { "update" };
                AddSelectedPaths(args, root, request.Paths);
                return await Run(args, root, cancellationToken);
            }
            case VcsOperation.Clone:
                if (string.IsNullOrWhiteSpace(request.Remote) || string.IsNullOrWhiteSpace(request.Destination))
                    throw new VcsException("SVN checkout requires a repository URL and destination directory.");
                if (request.Remote.StartsWith("-", StringComparison.Ordinal)) throw new VcsException("The SVN repository URL cannot start with '-'.");
                var destination = Path.GetFullPath(request.Destination);
                if (File.Exists(destination)) throw new VcsException("The SVN checkout destination is a file.");
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new VcsException("The SVN checkout destination directory must be empty.");
                return await Run(["checkout", "--", request.Remote, EscapePegPath(destination)], root, cancellationToken);
            default:
                throw new VcsException($"{Name} does not support {request.Operation}.");
        }
    }

    private async Task<CommandResult> Run(IReadOnlyList<string> args, string cwd, CancellationToken ct) =>
        (await _runner.RunAsync(new CommandSpec(Executable, ["--non-interactive", .. args], Path.GetFullPath(cwd)), ct)).EnsureSuccess();

    private static void AddSelectedPaths(List<string> args, string root, IReadOnlyList<string>? paths)
    {
        args.Add("--");
        if (paths is not { Count: > 0 }) { args.Add(EscapePegPath(Path.GetFullPath(root))); return; }
        args.AddRange(paths.Select(p => EscapePegPath(ResolveSelected(root, p))));
    }

    private static string EscapePegPath(string path) => path + "@";

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = System.Text.RegularExpressions.Regex.Split(text, "\\r\\n|\\n|\\r").ToList();
        if (text.EndsWith('\n') || text.EndsWith('\r')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (text[i] == '\n') count++;
        }
        return text[^1] is '\r' or '\n' ? count - 1 : count;
    }

    private static bool IsWithin(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSelected(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new VcsException("A selected path cannot be empty.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new VcsException("The selected path must stay inside the working copy.");
        return full;
    }
}
