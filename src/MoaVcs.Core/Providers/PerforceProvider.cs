namespace MoaVcs.Core.Providers;

/// <summary>Helix Core adapter. It operates on the P4 client configured for the selected directory.</summary>
public sealed class PerforceProvider : IVcsProvider
{
    private readonly ICommandRunner _runner;
    public PerforceProvider(ICommandRunner? runner = null, string executable = "p4")
    { _runner = runner ?? new ProcessCommandRunner(); Executable = executable; }

    public VcsKind Kind => VcsKind.Perforce;
    public string Name => "Perforce";
    public string Executable { get; }
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage |
        VcsCapabilities.Unstage | VcsCapabilities.Update;

    public async Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var selected = Path.GetFullPath(path);
        if (!Directory.Exists(selected)) throw new VcsException($"폴더를 찾을 수 없습니다: {selected}");
        var result = await Run(selected, ["-ztag", "info"], cancellationToken);
        var fields = ParseTagged(result.StandardOutput).FirstOrDefault() ?? new Dictionary<string, string>();
        var root = Find(fields, "clientRoot", "client root", "root");
        var client = Find(fields, "clientName", "client name", "client");
        if (string.IsNullOrWhiteSpace(root) || root == "*unknown*")
            throw new VcsException("P4 did not report a client root. Configure a Perforce workspace for this directory.");
        root = Path.GetFullPath(root);
        if (!IsWithin(root, selected)) throw new VcsException("The selected directory is outside the active Perforce workspace root.");
        if (!selected.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            // P4CONFIG is selected from the process working directory. The UI later
            // invokes commands from the returned workspace root, so verify that this
            // directory resolves to the same configured client/server.
            var rootInfo = await Run(root, ["-ztag", "info"], cancellationToken);
            var rootFields = ParseTagged(rootInfo.StandardOutput).FirstOrDefault() ?? new Dictionary<string, string>();
            var rootConfiguredClientRoot = Find(rootFields, "clientRoot", "client root", "root");
            if (!string.Equals(Find(rootFields, "clientName", "client name", "client"), client, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Find(rootFields, "serverAddress", "server address"), Find(fields, "serverAddress", "server address"), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(rootConfiguredClientRoot) ||
                !string.Equals(Path.GetFullPath(rootConfiguredClientRoot), root, StringComparison.OrdinalIgnoreCase))
                throw new VcsException("The Perforce workspace configuration changes between the selected directory and its workspace root.");
        }
        var server = Find(fields, "serverAddress", "server address");
        return new RepositoryInfo(Kind, root, string.IsNullOrWhiteSpace(client) ? "Perforce workspace" : client, server ?? string.Empty);
    }

    public async Task<IReadOnlyList<FileChange>> GetStatusAsync(string root, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(root);
        var result = await Run(fullRoot, ["-ztag", "status"], cancellationToken);
        ValidateTaggedOutput(result.StandardOutput, "status");
        var changes = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
        var records = ParseTagged(result.StandardOutput);
        foreach (var record in records)
        {
            var file = Find(record, "clientFile", "localFile", "path", "depotFile");
            var action = Find(record, "action", "status");
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(action))
                throw new VcsException("Perforce status returned an unrecognized tagged record; refusing to show a clean workspace.");
            if (Path.IsPathRooted(file) && !IsWithin(fullRoot, file))
                throw new VcsException("Perforce status reported a path outside the active workspace.");
            var normalized = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(fullRoot, file));
            var relative = Path.GetRelativePath(fullRoot, normalized);
            if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == "..")
                throw new VcsException("Perforce status reported a path outside the active workspace.");
            changes[relative] = new FileChange(relative, action, string.Empty, action);
        }
        return changes.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> GetDiffAsync(string root, FileChange? file, bool staged = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "diff", "-f", "-du" };
        if (file is not null) args.Add(ResolveSelected(root, file.Path));
        var result = await Run(root, args, cancellationToken);
        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-ztag", "changes", "-s", "submitted", "-l", "-m", "50", Path.GetFullPath(root) + Path.DirectorySeparatorChar + "..." };
        var result = await Run(root, args, cancellationToken);
        return ParseTagged(result.StandardOutput).Select(record =>
        {
            var stamp = Find(record, "time", "date") ?? string.Empty;
            if (long.TryParse(stamp, out var unix))
                stamp = DateTimeOffset.FromUnixTimeSeconds(unix).ToString("O");
            return new HistoryEntry(Find(record, "change", "change number") ?? string.Empty,
                Find(record, "user") ?? string.Empty, stamp, Find(record, "desc", "description") ?? string.Empty);
        }).Where(entry => entry.Id.Length > 0).ToArray();
    }

    public async Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(request.WorkingDirectory);
        var selected = request.Paths is { Count: > 0 }
            ? request.Paths.Select(path => ResolveSelected(root, path)).ToArray()
            : new[] { root + Path.DirectorySeparatorChar + "..." };
        switch (request.Operation)
        {
            case VcsOperation.Stage:
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Choose files or folders to reconcile in Perforce.");
                return await Run(root, ["reconcile", .. selected], cancellationToken);
            case VcsOperation.Unstage:
                if (request.Paths is not { Count: > 0 }) throw new VcsException("Choose files or folders to revert from the Perforce changelist.");
                return await Run(root, ["revert", "-k", .. selected], cancellationToken);
            case VcsOperation.Update:
                return await Run(root, ["sync", .. selected], cancellationToken);
            default:
                throw new VcsException($"{Name} does not support {request.Operation}.");
        }
    }

    private async Task<CommandResult> Run(string cwd, IReadOnlyList<string> args, CancellationToken ct) =>
        (await _runner.RunAsync(new CommandSpec(Executable, args, Path.GetFullPath(cwd)), ct)).EnsureSuccess();

    private static string ResolveSelected(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new VcsException("A selected path cannot be empty.");
        if (path.IndexOfAny(['@', '#', '%', '*']) >= 0) throw new VcsException("This Perforce path contains a wildcard or revision character and cannot be selected safely.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(fullRoot, path));
        if (!IsWithin(fullRoot, full)) throw new VcsException("The selected path must stay inside the Perforce workspace.");
        return full;
    }

    private static bool IsWithin(string root, string path)
    {
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        return target.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Dictionary<string, string>> ParseTagged(string output)
    {
        var result = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("... ", StringComparison.Ordinal)) continue;
            var body = line[4..];
            var split = body.IndexOf(' ');
            if (split < 0) continue;
            var key = body[..split];
            var value = body[(split + 1)..];
            // P4 tagged command output groups records; a repeated primary field starts the next record.
            if ((key.Equals("change", StringComparison.OrdinalIgnoreCase) || key.Equals("clientFile", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("depotFile", StringComparison.OrdinalIgnoreCase) || key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("localFile", StringComparison.OrdinalIgnoreCase)) && current?.ContainsKey(key) == true)
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result.Add(current);
            }
            current ??= NewRecord(result);
            if (current.TryGetValue(key, out var previous) && (key.Equals("desc", StringComparison.OrdinalIgnoreCase) || key.Equals("Description", StringComparison.OrdinalIgnoreCase)))
                current[key] = previous + Environment.NewLine + value;
            else current[key] = value;
        }
        return result;

        static Dictionary<string, string> NewRecord(List<Dictionary<string, string>> records)
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            records.Add(record);
            return record;
        }
    }

    private static void ValidateTaggedOutput(string output, string command)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (!line.StartsWith("... ", StringComparison.Ordinal) || line.Length <= 4 || line.IndexOf(' ', 4) < 0)
                throw new VcsException($"Perforce {command} output was not in the expected tagged format; refusing to infer a clean workspace.");
    }

    private static string? Find(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value)) return value;
            var compact = key.Replace(" ", "", StringComparison.Ordinal);
            foreach (var pair in fields)
                if (pair.Key.Replace(" ", "", StringComparison.Ordinal).Equals(compact, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }
        return null;
    }
}
