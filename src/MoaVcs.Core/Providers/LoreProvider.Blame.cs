using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

public sealed partial class LoreProvider
{
    private sealed record LoreAuditRange(int First, int Last, string Repository, string Revision, string Outcome, bool Local);
    private sealed record LoreAttribution(string Author, string Date);

    public async Task<BlameResult> GetBlameAsync(string root, FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        root = Path.GetFullPath(root);
        var baselinePath = ResolveBaselinePath(root, file);
        var revision = await ReadInspectionRevisionAsync(root, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadInspectionSnapshotAsync(root, baselinePath, revision, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            throw new VcsException($"'{file.Path}'은 현재 Lore 리비전에 없어 작성자 이력을 표시할 수 없습니다.");

        var sourceLines = LineDiff.SplitLines(snapshot);
        if (sourceLines.Length > LineDiff.MaxLines)
            throw new VcsException($"'{file.Path}'은 {LineDiff.MaxLines:N0}줄을 초과하여 작성자 이력을 표시할 수 없습니다.");

        var auditPath = "./" + baselinePath.Replace('\\', '/');
        var events = await RunInspectionJsonAsync(root,
            ["file", "audit", "--revision", revision.Signature, "--max-revisions", "0",
             "--no-ignore-space-at-eol", "--", auditPath], cancellationToken).ConfigureAwait(false);
        var ranges = ParseAuditRanges(events, file.Path, sourceLines.Length, revision.Repository);

        var attributions = new Dictionary<string, LoreAttribution>(StringComparer.Ordinal);
        foreach (var signature in ranges.Where(range => range.Outcome == "attributed")
                     .Where(range => range.Local)
                     .Select(range => range.Revision).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataEvents = await RunInspectionJsonAsync(root,
                ["revision", "metadata", "get", "--revision", signature], cancellationToken).ConfigureAwait(false);
            attributions.Add(signature, ParseAttribution(metadataEvents));
        }

        var lines = new BlameLine[sourceLines.Length];
        foreach (var range in ranges)
        {
            LoreAttribution? attribution = range.Outcome == "attributed" && range.Local
                ? attributions[range.Revision]
                : null;
            var author = range.Outcome == "attributed" && !range.Local
                ? "연결 저장소 이력 (작성자 조회 미지원)"
                : attribution?.Author ?? $"이력 확인 불가 ({range.Outcome})";
            var revisionLabel = range.Outcome == "attributed" ? range.Revision : string.Empty;
            var date = attribution?.Date ?? string.Empty;
            for (var lineNumber = range.First; lineNumber <= range.Last; lineNumber++)
                lines[lineNumber - 1] = new BlameLine(lineNumber, revisionLabel, author, date, sourceLines[lineNumber - 1]);
        }

        return new BlameResult(file.Path, $"Lore revision {revision.Number} ({revision.Signature})", lines);
    }

    private static IReadOnlyList<LoreAuditRange> ParseAuditRanges(
        IReadOnlyList<JsonElement> events, string path, int expectedLineCount, string localRepository)
    {
        var ranges = new List<LoreAuditRange>();
        foreach (var item in events)
        {
            var tag = ReadStringProperty(item, "tagName", "감사 이벤트");
            if (tag != "fileAudit") continue;
            if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw AuditFormatError(path, "fileAudit 이벤트에 data 개체가 없습니다");
            var first = ReadPositiveInt(data, "lineFirst", path);
            var last = ReadPositiveInt(data, "lineLast", path);
            var outcome = ReadStringProperty(data, "outcome", "감사 결과");
            var repository = ReadStringProperty(data, "repository", "감사 저장소");
            if (!Regex.IsMatch(repository, "^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant))
                throw AuditFormatError(path, "감사 범위의 저장소 식별자가 올바르지 않습니다");
            if (last < first || last > expectedLineCount)
                throw AuditFormatError(path, $"줄 범위 {first}-{last}이(가) 올바르지 않습니다");
            var revision = ReadStringProperty(data, "revision", "감사 리비전");
            if (outcome == "attributed")
            {
                if (!Regex.IsMatch(revision, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                    revision.All(character => character == '0'))
                    throw AuditFormatError(path, "작성된 범위의 리비전 서명이 올바르지 않습니다");
            }
            else if (outcome is not ("uncommitted" or "budget-exhausted" or "obliterated" or
                                     "binary-in-history" or "link-unavailable" or "content-unavailable" or "unresolved"))
            {
                throw AuditFormatError(path, $"알 수 없는 결과 '{outcome}'입니다");
            }
            ranges.Add(new LoreAuditRange(first, last, repository, revision, outcome,
                repository.Equals(localRepository, StringComparison.OrdinalIgnoreCase)));
        }

        ranges.Sort((left, right) => left.First.CompareTo(right.First));
        var nextLine = 1;
        foreach (var range in ranges)
        {
            if (range.First != nextLine)
                throw AuditFormatError(path, $"감사 범위가 {nextLine}번째 줄을 포함하지 않거나 겹칩니다");
            nextLine = range.Last + 1;
        }
        if (nextLine != expectedLineCount + 1)
            throw AuditFormatError(path, $"감사 결과가 전체 {expectedLineCount}줄을 포함하지 않습니다");
        return ranges;
    }

    private static LoreAttribution ParseAttribution(IReadOnlyList<JsonElement> events)
    {
        string? committedBy = null;
        string? mergedBy = null;
        ulong? timestamp = null;
        foreach (var item in events)
        {
            if (!item.TryGetProperty("tagName", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
                throw new VcsException("Lore 리비전 메타데이터 이벤트 형식을 인식할 수 없습니다.");
            var tag = tagElement.GetString() ?? throw new VcsException("Lore 리비전 메타데이터 이벤트 형식을 인식할 수 없습니다.");
            if (tag != "metadata") continue;
            if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String ||
                !data.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("tagName", out var valueTypeElement) || valueTypeElement.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("data", out var valueData))
                throw new VcsException("Lore 리비전 메타데이터 JSON 형식을 인식할 수 없습니다.");

            var key = keyElement.GetString();
            var valueType = valueTypeElement.GetString();
            if (key is "merged-by" or "committed-by")
            {
                if (valueType != "string" || valueData.ValueKind != JsonValueKind.String)
                    throw new VcsException($"Lore 리비전 메타데이터 '{key}' 값은 문자열이어야 합니다.");
                if (key == "merged-by") mergedBy = valueData.GetString();
                else committedBy = valueData.GetString();
            }
            else if (key == "timestamp")
            {
                if (valueType != "numeric" || valueData.ValueKind != JsonValueKind.Number || !valueData.TryGetUInt64(out var valueNumber))
                    throw new VcsException("Lore 리비전 timestamp 메타데이터는 0 이상의 정수여야 합니다.");
                timestamp = valueNumber;
            }
        }

        var author = !string.IsNullOrWhiteSpace(mergedBy) ? mergedBy : committedBy;
        var date = "";
        if (timestamp is > 0 and <= long.MaxValue)
        {
            try { date = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp.Value).ToString("O", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { date = ""; }
        }
        return new LoreAttribution(string.IsNullOrWhiteSpace(author) ? "작성자 정보 없음" : author, date);
    }

    private static int ReadPositiveInt(JsonElement data, string propertyName, string path)
    {
        if (!data.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) || number < 1)
            throw AuditFormatError(path, $"'{propertyName}' 값은 양의 정수여야 합니다");
        return number;
    }

    private static string ReadStringProperty(JsonElement data, string propertyName, string context)
    {
        if (!data.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new VcsException($"Lore {context} JSON 형식이 올바르지 않습니다. '{propertyName}' 값은 문자열이어야 합니다.");
        return value.GetString() ?? string.Empty;
    }

    private static VcsException AuditFormatError(string path, string reason) =>
        new($"'{path}'의 Lore 감사 결과를 해석할 수 없습니다. {reason}.");
}
