using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

public sealed partial class LoreProvider
{
    private sealed record InspectionRevision(string Signature, ulong Number, string Repository);

    public async Task<FileComparison> GetComparisonAsync(string root, FileChange file, bool staged = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (staged) throw new VcsException("Lore는 현재 커밋과 작업 파일을 비교합니다. Git 전용 스테이지 비교는 지원하지 않습니다.");
        root = Path.GetFullPath(root);
        var path = ResolveInspectionPath(root, file.Path);
        var baselinePath = ResolveBaselinePath(root, file);
        var revision = await ReadInspectionRevisionAsync(root, cancellationToken).ConfigureAwait(false);
        var baseline = await ReadInspectionSnapshotAsync(root, baselinePath, revision, cancellationToken).ConfigureAwait(false);
        var fullPath = Path.Combine(root, path);
        if (Directory.Exists(fullPath)) throw new VcsException("폴더 대신 비교할 파일을 선택하세요.");
        var working = File.Exists(fullPath)
            ? await TextSnapshot.ReadFileAsync(fullPath, path, cancellationToken).ConfigureAwait(false) : null;
        if (baseline is null && working is null) throw new VcsException("선택한 파일이 Lore 기준 리비전과 작업 폴더에 없습니다.");
        return new FileComparison(file.Path,
            $"Lore r{revision.Number}" + (baseline is null ? " (파일 없음)" : ""),
            working is null ? "작업 파일 (삭제됨)" : "작업 파일", baseline ?? "", working ?? "");
    }

    private static string ResolveInspectionPath(string root, string path)
    {
        var relative = ResolveSelected(root, path);
        if (relative == ".") throw new VcsException("폴더 대신 파일을 선택하세요.");
        return relative;
    }

    private static string ResolveBaselinePath(string root, FileChange file)
    {
        var original = file.OriginalPath is not null && (file.IndexStatus is "V" or "C" || file.WorkTreeStatus is "V" or "C");
        return ResolveInspectionPath(root, original ? file.OriginalPath! : file.Path);
    }

    private async Task<InspectionRevision> ReadInspectionRevisionAsync(string root, CancellationToken ct)
    {
        // `revision info --json` in Lore 0.13.0 performs a second author-name lookup after
        // completing the requested operation. The status endpoint supplies the anchor directly.
        var events = await RunInspectionJsonAsync(root, ["--local", "repository", "status", "--revision-only"], ct).ConfigureAwait(false);
        var revisions = events.Where(e => e.GetProperty("tagName").GetString() == "repositoryStatusRevision").ToArray();
        if (revisions.Length != 1) throw InspectionFormatError("현재 리비전 정보가 한 개여야 합니다.");
        var data = revisions[0].GetProperty("data");
        if (!data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(repository.GetString()!, "^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant) ||
            !data.TryGetProperty("revision", out var hash) || hash.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(hash.GetString()!, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
            !data.TryGetProperty("revisionNumber", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetUInt64(out var value))
            throw InspectionFormatError("현재 리비전의 서명 또는 번호가 잘못되었습니다.");
        var signature = hash.GetString()!;
        if (signature.All(character => character == '0') && value != 0)
            throw InspectionFormatError("빈 리비전의 서명과 번호가 일치하지 않습니다.");
        return new InspectionRevision(signature, value, repository.GetString()!);
    }

    private async Task<string?> ReadInspectionSnapshotAsync(string root, string relativePath, InspectionRevision revision, CancellationToken ct)
    {
        relativePath = ResolveInspectionPath(root, relativePath);
        // A freshly created repository has revision 0 with a zero signature, which is
        // not queryable through `file info`. It has no committed file snapshot yet.
        if (revision.Number == 0 && revision.Signature.All(character => character == '0')) return null;
        var events = await RunInspectionJsonAsync(root, ["file", "info", "--revision", revision.Signature, "--", "./" + relativePath], ct).ConfigureAwait(false);
        var files = events.Where(e => e.GetProperty("tagName").GetString() == "fileInfo").ToArray();
        if (files.Length == 0) return null;
        if (files.Length != 1) throw InspectionFormatError("선택한 파일 외의 정보가 반환되었습니다.");
        var data = files[0].GetProperty("data");
        if (!data.TryGetProperty("path", out var returnedPath) || returnedPath.ValueKind != JsonValueKind.String ||
            !ResolveInspectionPath(root, returnedPath.GetString()!).Equals(relativePath, StringComparison.OrdinalIgnoreCase))
            throw InspectionFormatError("선택한 파일과 반환된 경로가 일치하지 않습니다.");
        // Even with --revision, file info also describes untracked filesystem entries.
        // flagAdded distinguishes those from files actually present in the pinned revision.
        if (!data.TryGetProperty("flagAdded", out var added) || added.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InspectionFormatError("기준 리비전에 포함된 파일인지 확인할 수 없습니다.");
        if (added.GetBoolean()) return null;
        if (!data.TryGetProperty("isFile", out var isFile) || !data.TryGetProperty("isDir", out var isDir) ||
            isFile.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || isDir.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InspectionFormatError("파일 종류를 확인할 수 없습니다.");
        if (isDir.GetBoolean()) throw new VcsException("Lore 폴더는 텍스트 파일로 표시할 수 없습니다.");
        if (!isFile.GetBoolean()) return null;
        if (!data.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes < 0)
            throw InspectionFormatError("파일 크기를 확인할 수 없습니다.");
        TextSnapshot.EnsureWithinLimit(bytes, relativePath);
        ct.ThrowIfCancellationRequested();
        var folder = Path.Combine(Path.GetTempPath(), "moavcs-lore-read-" + Guid.NewGuid().ToString("N"));
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(folder).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new VcsException("Lore 파일 조회에는 저장소 밖의 임시 폴더가 필요합니다. Windows 임시 폴더 위치를 확인하세요.");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "snapshot.txt");
        try
        {
            await RunInspectionJsonAsync(root, ["file", "write", "--revision", revision.Signature,
                "--path", "./" + relativePath, "--output", output], ct).ConfigureAwait(false);
            if (!File.Exists(output) || (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
                throw new VcsException("Lore가 비교용 임시 파일을 생성하지 못했습니다.");
            return await TextSnapshot.ReadFileAsync(output, relativePath, ct).ConfigureAwait(false);
        }
        finally
        {
            // Only these two freshly-created paths belong to this operation; never recurse.
            if (File.Exists(output)) File.Delete(output);
            Directory.Delete(folder, recursive: false);
        }
    }

    private async Task<IReadOnlyList<JsonElement>> RunInspectionJsonAsync(string root, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await _runner.RunAsync(new CommandSpec(Executable,
            ["--repository", Path.GetFullPath(root), "--json", "--no-pager", .. args], Path.GetFullPath(root)), ct).ConfigureAwait(false);
        if (!result.Success) throw new VcsException($"Lore 파일 도구를 실행하지 못했습니다. Lore 0.13.0 이상 설치 여부와 저장소 접근 상태를 확인하세요.\n{result.Output}");
        var events = new List<JsonElement>();
        var complete = 0;
        try
        {
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var entry = doc.RootElement;
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("tagName", out var tag) || tag.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw InspectionFormatError("이벤트 형식이 잘못되었습니다.");
                if (tag.GetString() == "error") throw new VcsException("Lore 조회 오류: " + data.GetRawText());
                if (tag.GetString() == "complete")
                {
                    complete++;
                    if (!data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt64(out var code) || code != 0)
                        throw new VcsException("Lore 조회가 성공적으로 완료되지 않았습니다.");
                }
                events.Add(entry.Clone());
            }
        }
        catch (JsonException) { throw InspectionFormatError("JSON을 해석할 수 없습니다."); }
        if (complete != 1) throw InspectionFormatError("완료 이벤트가 누락되었거나 중복되었습니다.");
        return events;
    }

    private static VcsException InspectionFormatError(string detail) =>
        new($"Lore 파일 조회 형식을 인식할 수 없습니다. Lore 0.13.0 이상으로 업데이트하세요. {detail}");
}
