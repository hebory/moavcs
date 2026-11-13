using System.Globalization;
using System.Xml.Linq;

namespace MoaVcs.Core.Providers;

public sealed partial class SvnProvider
{
    public async Task<HistoryDetails> GetHistoryDetailsAsync(string root, string revision,
        CancellationToken cancellationToken = default)
    {
        revision = ValidateHistoryRevision(revision);
        var repositoryUrl = await GetHistoryRepositoryUrlAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadHistoryDetailsAsync(root, repositoryUrl, revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetHistoryDiffAsync(string root, string revision, FileChange? file = null,
        CancellationToken cancellationToken = default)
    {
        revision = ValidateHistoryRevision(revision);
        var repositoryUrl = await GetHistoryRepositoryUrlAsync(root, cancellationToken).ConfigureAwait(false);
        var target = repositoryUrl;
        var peg = revision;
        if (file is not null)
        {
            var details = await ReadHistoryDetailsAsync(root, repositoryUrl, revision, cancellationToken).ConfigureAwait(false);
            var selected = details.Files.FirstOrDefault(item => item.Path == file.Path)
                ?? throw new VcsException("선택한 파일은 이 SVN 리비전의 변경 목록에 없습니다.");
            // SVN log paths are repository paths, not paths in the local working copy.
            target += string.Join('/', selected.Path.Split('/').Select(Uri.EscapeDataString));
            if (selected.Status == "deleted") peg = (long.Parse(revision, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
        }
        var result = await Run(["diff", "--internal-diff", "--notice-ancestry", "-c", revision,
            "--", target + "@" + peg], root, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<string> GetHistoryRepositoryUrlAsync(string root, CancellationToken ct)
    {
        var info = await Run(["info", "--xml", "--", EscapePegPath(Path.GetFullPath(root))], root, ct).ConfigureAwait(false);
        var url = XDocument.Parse(info.StandardOutput).Root?.Element("entry")?.Element("repository")?.Element("root")?.Value;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _) || url.Any(char.IsControl))
            throw new VcsException("SVN 저장소의 기준 URL을 확인할 수 없습니다.");
        return url.TrimEnd('/');
    }

    private async Task<HistoryDetails> ReadHistoryDetailsAsync(string root, string repositoryUrl, string revision, CancellationToken ct)
    {
        var result = await Run(["log", "--xml", "--verbose", "-r", revision, "--", repositoryUrl + "@" + revision], root, ct).ConfigureAwait(false);
        var entries = XDocument.Parse(result.StandardOutput).Descendants("logentry").ToArray();
        if (entries.Length != 1 || (string?)entries[0].Attribute("revision") != revision)
            throw new VcsException("선택한 SVN 리비전의 상세 정보를 찾을 수 없습니다.");
        var entry = entries[0];
        var files = new List<FileChange>();
        foreach (var path in entry.Element("paths")?.Elements("path") ?? [])
        {
            var name = ValidateHistoryPath(path.Value);
            var original = (string?)path.Attribute("copyfrom-path");
            if (original is not null) original = ValidateHistoryPath(original);
            var status = (string?)path.Attribute("action") switch
            {
                "A" => original is null ? "added" : "copied", "D" => "deleted", "M" => "modified", "R" => "replaced",
                _ => throw new VcsException("SVN 리비전의 변경 상태를 해석할 수 없습니다.")
            };
            files.Add(new FileChange(name, status, OriginalPath: original));
        }
        return new HistoryDetails(new HistoryEntry(revision, entry.Element("author")?.Value ?? "",
            entry.Element("date")?.Value ?? "", entry.Element("msg")?.Value ?? ""), files,
            $"저장소 전체 리비전 · r{long.Parse(revision, CultureInfo.InvariantCulture) - 1} → r{revision}");
    }

    private static string ValidateHistoryRevision(string revision)
    {
        if (!long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new VcsException("SVN 리비전은 양의 정수여야 합니다.");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string ValidateHistoryPath(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsControl) ||
            path.Contains('\\') || path.Split('/').Any(segment => segment is "." or ".."))
            throw new VcsException("SVN 리비전에 올바르지 않은 저장소 경로가 있습니다.");
        return path;
    }
}
