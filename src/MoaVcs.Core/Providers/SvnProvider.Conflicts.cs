using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MoaVcs.Core.Providers;

public sealed partial class SvnProvider
{
    private sealed record SvnConflictState(string Path, string Token, string BasePath, string MinePath, string TheirPath,
        string BaseText, string MineText, string TheirText, string LeftLabel, string RightLabel);

    public async Task<ConflictFile> GetConflictAsync(string root, FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.IsConflicted) throw new VcsException("선택한 파일은 SVN 충돌 상태가 아닙니다.");
        root = (await GetRepositoryAsync(root, cancellationToken).ConfigureAwait(false)).RootPath;
        var path = NormalizeConflictPath(root, file.Path);
        var state = await ReadTextConflictAsync(root, path, cancellationToken).ConfigureAwait(false);
        return await ConflictFileStore.ReadAsync(root, path, state.BaseText, state.MineText, state.TheirText,
            state.Token, state.LeftLabel, state.RightLabel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConflictFile> SaveConflictAsync(ConflictFile expected, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = (await GetRepositoryAsync(expected.RootPath, cancellationToken).ConfigureAwait(false)).RootPath;
        var path = NormalizeConflictPath(root, expected.Path);
        var state = await ReadTextConflictAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.Token, expected.StateToken, StringComparison.Ordinal))
            throw ConflictChanged(path);
        var pinned = expected with { RootPath = root, Path = path };
        await ConflictFileStore.EnsureUnchangedAsync(pinned, cancellationToken).ConfigureAwait(false);
        return await ConflictFileStore.SaveAsync(pinned, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkConflictResolvedAsync(ConflictFile expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = (await GetRepositoryAsync(expected.RootPath, cancellationToken).ConfigureAwait(false)).RootPath;
        var path = NormalizeConflictPath(root, expected.Path);
        var state = await ReadTextConflictAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.Token, expected.StateToken, StringComparison.Ordinal))
            throw ConflictChanged(path);
        var pinned = expected with { RootPath = root, Path = path };
        await ConflictFileStore.EnsureUnchangedAsync(pinned, cancellationToken).ConfigureAwait(false);

        var workingPath = ConflictFileStore.RequireRegularFilePath(root, path);
        var working = await TextSnapshot.ReadFileAsync(workingPath, path, cancellationToken).ConfigureAwait(false);
        if (ConflictMarkers.ContainsMarkers(working))
            throw new VcsException($"'{path}'에 충돌 표시가 남아 있습니다. 모두 정리하고 먼저 ‘결과 저장’을 누르세요.");

        // `working` explicitly keeps the user's edited file; mine-full/theirs-full would discard it.
        await Run(["resolve", "--accept", "working", "--depth", "empty", "--", EscapePegPath(workingPath)],
            root, cancellationToken).ConfigureAwait(false);

        var status = await ReadTargetStatusAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (IsUnresolved(status))
            throw new VcsException($"'{path}'의 작업 파일은 보존했지만 SVN이 해결 상태로 바꾸지 못했습니다. 설정을 새로 고쳐 확인하세요.");
    }

    private async Task<SvnConflictState> ReadTextConflictAsync(string root, string path,
        CancellationToken cancellationToken)
    {
        var entry = await ReadTargetStatusAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (entry is null || !IsUnresolved(entry))
            throw new VcsException($"'{path}'은 더 이상 SVN 충돌 상태가 아닙니다. 화면을 새로 고치세요.");

        var wc = entry.Element("wc-status")!;
        var status = wc.Attribute("item")?.Value;
        var props = wc.Attribute("props")?.Value;
        var tree = string.Equals(wc.Attribute("tree-conflicted")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        if (tree || props == "conflicted")
            throw new VcsException($"'{path}'에는 텍스트 외에 트리 또는 속성 충돌도 있습니다. SVN 전용 도구에서 함께 검토하세요.");
        if (status != "conflicted")
            throw new VcsException($"'{path}'은 일반 파일 텍스트 충돌이 아닙니다. SVN 전용 도구에서 해결하세요.");

        var info = await Run(["info", "--xml", "--", EscapePegPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))],
            root, cancellationToken).ConfigureAwait(false);
        var infoEntries = XDocument.Parse(info.StandardOutput).Descendants("entry").ToArray();
        if (infoEntries.Length != 1 || (string?)infoEntries[0].Attribute("kind") != "file")
            throw new VcsException("SVN info XML에서 선택한 일반 파일 하나를 확인할 수 없습니다.");
        var infoEntry = infoEntries[0];
        var reportedInfoPath = (string?)infoEntry.Attribute("path") ?? "";
        var reportedFullPath = Path.GetFullPath(Path.IsPathRooted(reportedInfoPath) ? reportedInfoPath : Path.Combine(root, reportedInfoPath));
        if (!reportedFullPath.Equals(Path.GetFullPath(Path.Combine(root, path)), StringComparison.OrdinalIgnoreCase))
            throw new VcsException("SVN info가 선택한 파일과 다른 충돌 정보를 반환했습니다.");
        var wcInfo = infoEntry.Element("wc-info") ?? throw new VcsException("SVN info XML에 working-copy 정보가 없습니다.");
        var reportedRoot = wcInfo.Element("wcroot-abspath")?.Value;
        if (string.IsNullOrWhiteSpace(reportedRoot) || !Path.GetFullPath(reportedRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new VcsException("SVN 충돌 파일의 working copy가 현재 저장소와 다릅니다.");
        // The CLI XML schema places <conflict> next to <wc-info> under <entry>.
        var conflicts = infoEntry.Elements("conflict").ToArray();
        if (conflicts.Length != 1 || (string?)conflicts[0].Attribute("type") != "text" ||
            conflicts[0].Element("prop-file") is not null)
            throw new VcsException($"'{path}'의 충돌 정보를 텍스트 충돌 하나로 확인할 수 없습니다. SVN 전용 도구에서 확인하세요.");

        var conflict = conflicts[0];
        // These names come from `svn info --xml`; never infer the .mine/.rREV suffixes.
        var basePath = ResolveConflictSide(root, path, conflict.Element("prev-base-file")?.Value, "기준본");
        var minePath = ResolveConflictSide(root, path, conflict.Element("prev-wc-file")?.Value, "내 변경");
        var theirPath = ResolveConflictSide(root, path, conflict.Element("cur-base-file")?.Value, "받은 변경");
        if (new[] { basePath, minePath, theirPath }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new VcsException($"'{path}'의 SVN 충돌 스냅샷 경로가 서로 달라야 합니다.");

        var baseSnapshot = await ReadConflictSideAsync(root, basePath, path, "기준본", cancellationToken).ConfigureAwait(false);
        var mineSnapshot = await ReadConflictSideAsync(root, minePath, path, "내 변경", cancellationToken).ConfigureAwait(false);
        var theirSnapshot = await ReadConflictSideAsync(root, theirPath, path, "받은 변경", cancellationToken).ConfigureAwait(false);
        var sourceRight = conflict.Elements("version").FirstOrDefault(e => (string?)e.Attribute("side") == "source-right");
        var incomingRevision = (string?)sourceRight?.Attribute("revision");
        var leftLabel = "내 변경 (working copy)";
        var rightLabel = string.IsNullOrWhiteSpace(incomingRevision) ? "받은 변경 (incoming)" : $"받은 변경 (SVN r{incomingRevision})";
        var operation = (string?)conflict.Attribute("operation") ?? "merge";
        var tokenText = string.Join("\n", status, props, tree, operation,
            conflict.ToString(SaveOptions.DisableFormatting),
            baseSnapshot.Hash, mineSnapshot.Hash, theirSnapshot.Hash);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenText)));
        return new SvnConflictState(path, token, basePath, minePath, theirPath,
            baseSnapshot.Text, mineSnapshot.Text, theirSnapshot.Text, leftLabel, rightLabel);
    }

    private async Task<XElement?> ReadTargetStatusAsync(string root, string path, CancellationToken cancellationToken)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        var result = await Run(["status", "--xml", "--", EscapePegPath(full)], root, cancellationToken).ConfigureAwait(false);
        var entries = XDocument.Parse(result.StandardOutput).Descendants("entry")
            .Where(e => e.Element("wc-status") is not null).ToArray();
        if (entries.Length == 0) return null;
        if (entries.Length != 1) throw new VcsException($"'{path}'에 대해 SVN status가 예상하지 않은 여러 항목을 반환했습니다.");
        var reportedPath = (string?)entries[0].Attribute("path") ?? "";
        var reported = Path.GetFullPath(Path.IsPathRooted(reportedPath) ? reportedPath : Path.Combine(root, reportedPath));
        if (!reported.Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new VcsException($"SVN status가 다른 경로를 반환했습니다: {reported}");
        return entries[0];
    }

    private static bool IsUnresolved(XElement? entry)
    {
        var wc = entry?.Element("wc-status");
        if (wc is null) return false;
        var status = (string?)wc.Attribute("item");
        var props = (string?)wc.Attribute("props");
        return status == "conflicted" || props == "conflicted" ||
            string.Equals((string?)wc.Attribute("tree-conflicted"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string Text, string Hash)> ReadConflictSideAsync(string root, string path, string displayPath, string label,
        CancellationToken cancellationToken)
    {
        var full = ConflictFileStore.RequireRegularFilePath(root, path);
        var bytes = await ConflictFileStore.ReadBytesAsync(full, cancellationToken).ConfigureAwait(false);
        TextSnapshot.EnsureWithinLimit(bytes.Length, $"{displayPath} ({label})");
        var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var text = TextSnapshot.DecodeUtf8(hasBom ? bytes[3..] : bytes, $"{displayPath} ({label})");
        return (text, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string ResolveConflictSide(string root, string targetPath, string? reportedPath, string label)
    {
        if (string.IsNullOrWhiteSpace(reportedPath))
            throw new VcsException($"SVN info가 {label} 충돌 스냅샷 경로를 제공하지 않았습니다. 저장소를 안전하게 편집할 수 없습니다.");
        var full = Path.GetFullPath(Path.IsPathRooted(reportedPath)
            ? reportedPath : Path.Combine(root, reportedPath));
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new VcsException($"SVN이 반환한 {label} 스냅샷이 working copy 바깥을 가리킵니다.");
        _ = ConflictFileStore.RequireRegularFilePath(root, Path.GetRelativePath(root, full));
        if (full.Equals(Path.Combine(root, targetPath.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
            throw new VcsException("SVN 충돌 스냅샷이 작업 파일과 겹쳐 원문 비교를 보장할 수 없습니다.");
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    private static string NormalizeConflictPath(string root, string path)
    {
        var full = ResolveSelected(root, path);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
        _ = ConflictFileStore.RequireRegularFilePath(root, relative);
        return relative;
    }

    private static VcsException ConflictChanged(string path) =>
        new($"'{path}'의 SVN 충돌 정보가 비교 창을 연 뒤 바뀌었습니다. 저장하지 않았으니 새로 고쳐 다시 여세요.");
}
