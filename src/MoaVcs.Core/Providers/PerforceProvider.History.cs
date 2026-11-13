using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

public sealed partial class PerforceProvider
{
    private sealed record SubmittedFile(FileChange Change, string Revision, string Type);
    private sealed record SubmittedChange(HistoryDetails Details, IReadOnlyList<SubmittedFile> Files);

    public async Task<HistoryDetails> GetHistoryDetailsAsync(string root, string revision,
        CancellationToken cancellationToken = default) =>
        (await ReadSubmittedChangeAsync(root, revision, cancellationToken).ConfigureAwait(false)).Details;

    public async Task<string> GetHistoryDiffAsync(string root, string revision, FileChange? file = null,
        CancellationToken cancellationToken = default)
    {
        var change = await ReadSubmittedChangeAsync(root, revision, cancellationToken).ConfigureAwait(false);
        if (file is null)
            return (await Run(root, ["describe", "-du", "-a", change.Details.Entry.Id], cancellationToken).ConfigureAwait(false)).StandardOutput;
        var selected = change.Files.FirstOrDefault(item => item.Change.Path == file.Path)
            ?? throw new VcsException("선택한 파일은 이 Perforce 변경 목록에 없습니다.");
        var path = selected.Change.Path;
        if (selected.Change.Status == "purged")
            return "이 리비전의 파일 내용은 Perforce 서버에서 제거되어 비교할 수 없습니다.";
        EnsureTextFileType(selected.Type, path);
        var previous = (long.Parse(selected.Revision, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
        var added = selected.Change.Status is "added" or "copied";
        var deleted = selected.Change.Status == "deleted";
        if (added || deleted)
        {
            // diff2 reports <none> instead of content when one side is absent.
            var spec = path + "#" + (deleted ? previous : selected.Revision);
            var print = await Run(root, ["print", "-q", spec], cancellationToken).ConfigureAwait(false);
            var content = TextSnapshot.ValidateText(print.StandardOutput, path);
            var lines = SplitLines(content);
            var left = added ? "/dev/null" : spec;
            var right = deleted ? "/dev/null" : spec;
            return $"--- {left}\n+++ {right}\n@@ -{(added ? "0,0" : "1," + lines.Count)} +{(deleted ? "0,0" : "1," + lines.Count)} @@\n" +
                string.Join('\n', lines.Select(line => (added ? "+" : "-") + line)) +
                (lines.Count == 0 ? "" : "\n") + (content.Length > 0 && !content.EndsWith('\n') && !content.EndsWith('\r') ? "\\ No newline at end of file\n" : "");
        }
        return (await Run(root, ["diff2", "-du", path + "#" + previous, path + "#" + selected.Revision], cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    private async Task<SubmittedChange> ReadSubmittedChangeAsync(string root, string revision, CancellationToken ct)
    {
        revision = RequireHaveRevision(revision);
        var result = await Run(root, ["-Mj", "-ztag", "describe", "-s", revision], ct).ConfigureAwait(false);
        try
        {
            JsonElement? entry = null;
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                var item = json.RootElement;
                if (item.ValueKind != JsonValueKind.Object) throw HistoryFormatError();
                if (item.TryGetProperty("code", out var code) && code.GetString() == "error")
                    throw new VcsException(ReadHistoryField(item, "data"));
                if (entry is not null || ReadHistoryField(item, "change") != revision) throw HistoryFormatError();
                entry = item.Clone();
            }
            if (entry is not { } data || ReadHistoryField(data, "status") != "submitted")
                throw new VcsException("제출된 Perforce 변경 목록만 상세 정보를 볼 수 있습니다.");
            var timestamp = ReadHistoryField(data, "time");
            if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) throw HistoryFormatError();
            var files = new List<SubmittedFile>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in data.EnumerateObject().Where(property => property.Name.StartsWith("depotFile", StringComparison.Ordinal)))
            {
                var suffix = property.Name[9..];
                if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit)) throw HistoryFormatError();
                var path = property.Value.GetString() ?? throw HistoryFormatError();
                ValidateHistoryDepotPath(path);
                if (!names.Add(path)) throw HistoryFormatError();
                var fileRevision = RequireHaveRevision(ReadHistoryField(data, "rev" + suffix));
                var action = ReadHistoryField(data, "action" + suffix) switch
                {
                    "add" or "move/add" => "added", "delete" or "move/delete" => "deleted",
                    "branch" => "copied", "edit" or "integrate" => "modified", "purge" => "purged",
                    _ => throw HistoryFormatError()
                };
                files.Add(new SubmittedFile(new FileChange(path, action), fileRevision, ReadHistoryField(data, "type" + suffix)));
            }
            var details = new HistoryDetails(new HistoryEntry(revision, ReadHistoryField(data, "user"),
                DateTimeOffset.FromUnixTimeSeconds(unix).ToString("O"), ReadHistoryField(data, "desc")),
                files.Select(file => file.Change).ToArray(), $"제출된 변경 목록 {revision} · 각 파일의 이전 리비전과 비교");
            return new SubmittedChange(details, files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw HistoryFormatError(); }
    }

    private static string ReadHistoryField(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) throw HistoryFormatError();
        return value.ValueKind switch { JsonValueKind.String => value.GetString()!, JsonValueKind.Number => value.GetRawText(), _ => throw HistoryFormatError() };
    }

    private static void ValidateHistoryDepotPath(string path)
    {
        if (!path.StartsWith("//", StringComparison.Ordinal) || path.Length < 4 || path.Any(char.IsControl) ||
            path.IndexOfAny(['@', '#', '*', '\\']) >= 0 || path.Contains("...", StringComparison.Ordinal) ||
            Regex.IsMatch(path, "%(?!23|25|2[Aa]|40)") || path.Split('/').Any(segment => segment is "." or ".."))
            throw new VcsException("Perforce 변경 목록에 올바르지 않은 depot 경로가 있습니다.");
    }

    private static VcsException HistoryFormatError() => new("Perforce 커밋 상세 JSON 형식을 해석할 수 없습니다.");
}
