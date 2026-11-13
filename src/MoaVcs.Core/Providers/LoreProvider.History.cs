using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

public sealed partial class LoreProvider
{
    private sealed record LoreRevisionInfo(
        string Signature,
        ulong Number,
        string Parent,
        string MergeParent,
        string Repository,
        HistoryEntry Entry,
        IReadOnlyList<FileChange> Files);

    private const string ZeroSignature = "0000000000000000000000000000000000000000000000000000000000000000";

    private async Task<IReadOnlyList<HistoryEntry>> GetHistoryEntriesAsync(string root, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        var events = await RunHistoryJsonAsync(root, ["--local", "history", "50"], ct).ConfigureAwait(false);
        var result = new List<HistoryEntry>();
        var messages = new List<string>();
        string author = "";
        string date = "";
        string signature = "";

        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            var tag = item.GetProperty("tagName").GetString();
            var data = item.GetProperty("data");
            if (tag == "revisionHistoryEntry")
            {
                if (signature.Length > 0) AddEntry();
                signature = GetRequiredString(data, "revision");
                if (!Regex.IsMatch(signature, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
                    throw HistoryFormatError("리비전 서명 형식이 잘못되었습니다.");
                if (!data.TryGetProperty("revisionNumber", out var n) || n.ValueKind != JsonValueKind.Number ||
                    !n.TryGetUInt64(out var number) || number == 0)
                    throw HistoryFormatError("리비전 번호를 확인할 수 없습니다.");
                result.Add(new HistoryEntry(number.ToString(CultureInfo.InvariantCulture), "", "", ""));
                messages.Clear();
                author = "";
                date = "";
            }
            else if (tag == "metadata" && result.Count > 0)
            {
                var key = GetRequiredString(data, "key");
                if (!data.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("tagName", out var valueTag) || valueTag.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty("data", out var valueData))
                    throw HistoryFormatError("이력 메타데이터 형식이 잘못되었습니다.");
                switch (key)
                {
                    case "message" when valueTag.GetString() == "string" && valueData.ValueKind == JsonValueKind.String:
                        messages.Add(valueData.GetString()!);
                        break;
                    case "committed-by" or "created-by" when valueTag.GetString() == "string" && valueData.ValueKind == JsonValueKind.String:
                        if (key == "committed-by" || author.Length == 0) author = valueData.GetString()!;
                        break;
                    case "timestamp" when valueTag.GetString() == "numeric" && valueData.ValueKind == JsonValueKind.Number &&
                                               valueData.TryGetInt64(out var milliseconds):
                        try { date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); }
                        catch (ArgumentOutOfRangeException) { throw HistoryFormatError("날짜 범위를 확인할 수 없습니다."); }
                        break;
                }
            }
        }

        if (signature.Length > 0) AddEntry();
        return result;

        void AddEntry()
        {
            var entry = result[^1];
            result[^1] = entry with { Author = author, Date = date, Message = string.Join("", messages) };
            signature = "";
        }
    }

    public async Task<HistoryDetails> GetHistoryDetailsAsync(string root, string revision, CancellationToken cancellationToken = default)
    {
        var info = await ReadLoreRevisionInfoAsync(root, revision, cancellationToken).ConfigureAwait(false);
        var comparisonLabel = info.Parent == ZeroSignature
            ? "최초 커밋 · 빈 저장소 기준"
            : info.MergeParent != ZeroSignature ? "첫 번째 부모와 비교 · 병합 커밋" : "이전 커밋과 비교";
        return new HistoryDetails(info.Entry, info.Files, comparisonLabel);
    }

    public async Task<string> GetHistoryDiffAsync(string root, string revision, FileChange? file = null,
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var info = await ReadLoreRevisionInfoAsync(root, revision, cancellationToken).ConfigureAwait(false);
        string[] selectedPaths;
        if (file is null)
        {
            selectedPaths = info.Files
                .SelectMany(change => change.OriginalPath is { } original ? new[] { original, change.Path } : new[] { change.Path })
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            var requestedPath = ResolveInspectionPath(root, file.Path);
            var historicalFile = info.Files.SingleOrDefault(change => change.Path.Equals(requestedPath, StringComparison.Ordinal))
                ?? throw new VcsException("선택한 파일은 이 리비전에 포함되지 않습니다.");
            // Derive the old name from the repository result instead of trusting a UI supplied OriginalPath.
            selectedPaths = historicalFile.OriginalPath is { } original
                ? [original, historicalFile.Path]
                : [historicalFile.Path];
        }
        selectedPaths = selectedPaths.Select(path => ResolveInspectionPath(root, path)).Distinct(StringComparer.Ordinal).ToArray();
        if (selectedPaths.Length == 0) return "이 커밋에는 변경된 파일이 없습니다.";

        if (info.Parent == ZeroSignature)
        {
            var snapshot = new InspectionRevision(info.Signature, info.Number, info.Repository);
            var diffs = new StringBuilder();
            foreach (var path in selectedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await ReadInspectionSnapshotAsync(root, path, snapshot, cancellationToken).ConfigureAwait(false);
                if (content is null) continue;
                var patch = FormatInitialFileDiff(path, content);
                if (diffs.Length + patch.Length > ProcessCommandRunner.MaxOutputCharacters)
                    throw new VcsException($"리비전 비교 출력이 {ProcessCommandRunner.MaxOutputCharacters / (1024 * 1024)} MiB 제한을 초과했습니다.");
                diffs.Append(patch);
            }
            return diffs.ToString();
        }

        var args = new List<string> { "--local", "--no-pager", "diff", "--source", info.Parent, "--target", info.Signature, "--" };
        args.AddRange(selectedPaths.Select(path => "./" + path));
        var result = await _runner.RunAsync(new CommandSpec(Executable, args, root), cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
        return result.StandardOutput;
    }

    private async Task<LoreRevisionInfo> ReadLoreRevisionInfoAsync(string root, string revision, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new VcsException($"폴더를 찾을 수 없습니다: {root}");
        if (!ulong.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedNumber) || requestedNumber == 0)
            throw new VcsException("Lore 리비전 번호 형식이 잘못되었습니다.");

        var events = await RunHistoryJsonAsync(root, ["--local", "revision", "info", requestedNumber.ToString(CultureInfo.InvariantCulture), "--delta"], ct)
            .ConfigureAwait(false);
        JsonElement? infoData = null;
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var changes = new List<FileChange>();
        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            var tag = item.GetProperty("tagName").GetString();
            var data = item.GetProperty("data");
            if (tag == "revisionInfo")
            {
                if (infoData is not null) throw HistoryFormatError("리비전 정보가 중복되었습니다.");
                infoData = data;
            }
            else if (tag == "metadata")
            {
                var key = GetRequiredString(data, "key");
                if (!data.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) throw HistoryFormatError("메타데이터 값이 없습니다.");
                metadata[key] = value.Clone();
            }
            else if (tag == "revisionInfoDelta")
            {
                var path = ResolveInspectionPath(root, GetRequiredString(data, "path"));
                if (!data.TryGetProperty("flagFile", out var isFile) || isFile.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw HistoryFormatError("파일 종류를 확인할 수 없습니다.");
                if (!isFile.GetBoolean()) continue;
                var action = GetRequiredString(data, "action");
                var modified = data.TryGetProperty("flagModify", out var flagModify) && flagModify.ValueKind == JsonValueKind.True;
                var original = OptionalString(data, "fromPath");
                original = string.IsNullOrEmpty(original) ? null : ResolveInspectionPath(root, original);
                var status = action switch
                {
                    "add" => "Added",
                    "delete" => "Deleted",
                    "move" => "Moved",
                    "copy" => "Copied",
                    "keep" when modified => "Modified",
                    "keep" => "Changed",
                    _ => throw HistoryFormatError("변경 동작을 알 수 없습니다.")
                };
                var index = action switch { "add" => "A", "delete" => "D", "move" => "V", "copy" => "C", _ => "M" };
                changes.Add(new FileChange(path, status, index, OriginalPath: original));
            }
        }

        if (infoData is null) throw HistoryFormatError("선택한 리비전 정보를 찾을 수 없습니다.");
        var dataInfo = infoData.Value;
        var signature = GetRequiredString(dataInfo, "revision");
        if (!IsSignature(signature)) throw HistoryFormatError("리비전 서명 형식이 잘못되었습니다.");
        if (!dataInfo.TryGetProperty("revisionNumber", out var numberElement) || numberElement.ValueKind != JsonValueKind.Number ||
            !numberElement.TryGetUInt64(out var number) || number == 0 || number != requestedNumber)
            throw HistoryFormatError("요청한 번호와 반환된 리비전이 일치하지 않습니다.");
        if (!dataInfo.TryGetProperty("parent", out var parents) || parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() != 2)
            throw HistoryFormatError("부모 리비전 정보가 잘못되었습니다.");
        var parent = parents[0].GetString() ?? throw HistoryFormatError("첫 번째 부모 리비전을 읽을 수 없습니다.");
        var mergeParent = parents[1].GetString() ?? throw HistoryFormatError("두 번째 부모 리비전을 읽을 수 없습니다.");
        if (!IsSignature(parent) || !IsSignature(mergeParent)) throw HistoryFormatError("부모 리비전 서명 형식이 잘못되었습니다.");
        var repository = GetRequiredString(dataInfo, "repository");
        if (!Regex.IsMatch(repository, "^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)) throw HistoryFormatError("저장소 식별자 형식이 잘못되었습니다.");

        var message = GetMetadataString(metadata, "message");
        var author = GetMetadataString(metadata, "committed-by");
        if (author.Length == 0) author = GetMetadataString(metadata, "created-by");
        var date = "";
        if (metadata.TryGetValue("timestamp", out var timestamp) && timestamp.TryGetProperty("tagName", out var timestampType) &&
            timestampType.GetString() == "numeric" && timestamp.TryGetProperty("data", out var timestampData) &&
            timestampData.ValueKind == JsonValueKind.Number && timestampData.TryGetInt64(out var milliseconds))
        {
            try { date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { throw HistoryFormatError("날짜 범위를 확인할 수 없습니다."); }
        }
        var entry = new HistoryEntry(number.ToString(CultureInfo.InvariantCulture), author, date, message);
        return new LoreRevisionInfo(signature, number, parent, mergeParent, repository, entry,
            changes.DistinctBy(change => change.Path, StringComparer.Ordinal).ToArray());
    }

    private async Task<IReadOnlyList<JsonElement>> RunHistoryJsonAsync(string root, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await _runner.RunAsync(new CommandSpec(Executable,
            ["--repository", Path.GetFullPath(root), "--json", "--no-pager", .. args], Path.GetFullPath(root)), ct).ConfigureAwait(false);
        if (!result.Success) throw new VcsException($"Lore 이력 조회를 실행하지 못했습니다. Lore 0.13.0 이상과 저장소 접근 상태를 확인하세요.\n{result.Output}");
        var events = new List<JsonElement>();
        var complete = 0;
        var succeeded = false;
        var awaitingAuthorLookupFailure = false;
        var authorLookupCompleted = false;
        try
        {
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("tagName", out var tag) || tag.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw HistoryFormatError("이벤트 형식이 잘못되었습니다.");
                var tagName = tag.GetString();

                if (succeeded)
                {
                    if (authorLookupCompleted)
                        throw new VcsException("Lore 작성자 조회 완료 뒤 예상하지 못한 이벤트를 받았습니다.");
                    if (!awaitingAuthorLookupFailure && tagName == "authUserInfo" && IsAuthorLookupEntry(data))
                    {
                        // The JSON formatter also exposes Lore's best-effort user-id/name lookup.
                        // The revision metadata already carries the creator id used by this UI.
                        continue;
                    }
                    if (!awaitingAuthorLookupFailure && tagName == "log" && IsAuthorLookupFailureLog(data))
                    {
                        awaitingAuthorLookupFailure = true;
                        continue;
                    }
                    if (tagName == "complete" && IsAuthorLookupCompletion(data, awaitingAuthorLookupFailure))
                    {
                        awaitingAuthorLookupFailure = false;
                        authorLookupCompleted = true;
                        continue;
                    }
                    throw new VcsException("Lore 이력 조회 완료 뒤 예상하지 못한 이벤트를 받았습니다.");
                }

                if (tagName == "error") throw new VcsException("Lore 이력 조회 오류: " + data.GetRawText());
                if (tagName == "complete")
                {
                    complete++;
                    if (!data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                        !status.TryGetInt64(out var code))
                        throw HistoryFormatError("완료 상태를 확인할 수 없습니다.");
                    if (code == 0) succeeded = true;
                    else throw new VcsException("Lore 이력 조회가 성공적으로 완료되지 않았습니다: " + data.GetRawText());
                }
                events.Add(item.Clone());
            }
        }
        catch (JsonException) { throw HistoryFormatError("JSON을 해석할 수 없습니다."); }
        if (complete != 1 || !succeeded || awaitingAuthorLookupFailure || !authorLookupCompleted)
            throw HistoryFormatError("요청 완료 또는 후속 작성자 조회 이벤트가 누락되었거나 중복되었습니다.");
        return events;
    }

    private static bool IsAuthorLookupEntry(JsonElement data) =>
        data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()) &&
        data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String;

    private static bool IsAuthorLookupCompletion(JsonElement data, bool expectedOfflineFailure)
    {
        if (expectedOfflineFailure) return IsAuthorLookupFailureCompletion(data);
        if (!data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
            !status.TryGetInt32(out var statusCode) || statusCode != 0 ||
            !data.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("errorCode", out var errorCode) || errorCode.ValueKind != JsonValueKind.Number ||
            !errorCode.TryGetInt32(out var code) || code != 0 ||
            !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString() != "") return false;
        return true;
    }

    private static bool IsAuthorLookupFailureLog(JsonElement data)
    {
        if (!data.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.String || level.GetString() != "error" ||
            !data.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.String || location.GetString() != "lore_revision::relay" ||
            !data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;
        var value = message.GetString()!;
        return value.StartsWith("No remote configured\n", StringComparison.Ordinal) &&
               value.Contains("lore\\src\\auth.rs:", StringComparison.Ordinal);
    }

    private static bool IsAuthorLookupFailureCompletion(JsonElement data)
    {
        if (!data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var statusCode) || statusCode != 111 ||
            !data.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("errorCode", out var errorCode) || errorCode.ValueKind != JsonValueKind.Number || !errorCode.TryGetInt32(out var code) || code != 111 ||
            !error.TryGetProperty("message", out var errorMessage) || errorMessage.ValueKind != JsonValueKind.String || errorMessage.GetString() != "No remote configured" ||
            !error.TryGetProperty("traceLocations", out var locations) || locations.ValueKind != JsonValueKind.Array) return false;
        return locations.EnumerateArray().Any(location => location.ValueKind == JsonValueKind.Object &&
            location.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String &&
            file.GetString()!.EndsWith("lore\\src\\auth.rs", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatInitialFileDiff(string path, string content)
    {
        var lines = LineDiff.SplitLines(content);
        var builder = new StringBuilder();
        builder.Append("--- /dev/null\n+++ b/").Append(path).Append("\n@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
        foreach (var line in lines) builder.Append('+').Append(line).Append('\n');
        if (content.Length > 0 && !content.EndsWith('\n') && !content.EndsWith('\r')) builder.Append("\\ No newline at end of file\n");
        return builder.ToString();
    }

    private static bool IsSignature(string value) => Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    private static string GetRequiredString(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            throw HistoryFormatError($"'{property}' 필드를 확인할 수 없습니다.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string GetMetadataString(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || !value.TryGetProperty("tagName", out var type) || type.GetString() != "string" ||
            !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return "";
        return data.GetString() ?? "";
    }

    private static VcsException HistoryFormatError(string detail) =>
        new($"Lore 이력 출력 형식을 인식할 수 없습니다. Lore 0.13.0 이상으로 업데이트하세요. {detail}");
}
