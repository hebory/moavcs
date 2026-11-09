using System.Xml.Linq;

namespace MoaVcs.Core.Providers;

/// <summary>Subversion client adapter. SVN has no index, so commits are always scoped to the selected paths.</summary>
public sealed class SvnProvider : IVcsProvider
{
    private readonly ICommandRunner _runner;
    public SvnProvider(ICommandRunner? runner = null, string executable = "svn")
    { _runner = runner ?? new ProcessCommandRunner(); Executable = executable; }

    public VcsKind Kind => VcsKind.Svn;
    public string Name => "Subversion";
    public string Executable { get; }
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage | VcsCapabilities.Commit | VcsCapabilities.Update | VcsCapabilities.Clone;

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
            var conflicted = wc.Attribute("tree-conflicted")?.Value == "true" || status == "conflicted";
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
