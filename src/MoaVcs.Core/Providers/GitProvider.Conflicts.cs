using System.Security.Cryptography;
using System.Text;

namespace MoaVcs.Core.Providers;

public sealed partial class GitProvider
{
    private sealed record UnmergedEntry(string Path, int Stage, string Mode, string ObjectId);

    public async Task<ConflictFile> GetConflictAsync(string root, FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.IsConflicted) throw new VcsException("선택한 파일은 Git 충돌 상태가 아닙니다.");

        root = await RequireRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var path = ValidateRelativePath(root, file.Path);
        var entries = await ReadAllUnmergedEntriesAsync(root, cancellationToken).ConfigureAwait(false);
        var selected = entries.Where(entry => PathsEqual(entry.Path, path)).ToArray();
        if (selected.Length == 0)
            throw new VcsException($"'{path}'의 충돌 항목을 Git 인덱스에서 찾지 못했습니다. 상태를 새로 고치세요.");

        var stages = selected.ToDictionary(entry => entry.Stage);
        foreach (var entry in selected)
        {
            if (entry.Mode is not ("100644" or "100755"))
                throw new VcsException($"'{path}'은 일반 텍스트 파일의 충돌이 아닙니다 (Git mode {entry.Mode}). 심볼릭 링크, 서브모듈과 트리 충돌은 내장 편집에서 지원하지 않습니다.");
        }
        if (!stages.TryGetValue(2, out var left) || !stages.TryGetValue(3, out var right))
            throw new VcsException($"'{path}'은 한쪽 파일이 삭제되었거나 트리 충돌 상태입니다. Git에서 충돌 내용을 확인하세요.");
        if (stages.Keys.Any(stage => stage is < 1 or > 3))
            throw new VcsException($"'{path}'에 알 수 없는 Git 충돌 단계가 있습니다.");

        var baseText = stages.TryGetValue(1, out var baseEntry)
            ? await ReadConflictBlobAsync(root, path, baseEntry, cancellationToken).ConfigureAwait(false)
            : null;
        var leftText = await ReadConflictBlobAsync(root, path, left, cancellationToken).ConfigureAwait(false);
        var rightText = await ReadConflictBlobAsync(root, path, right, cancellationToken).ConfigureAwait(false);

        var workingFullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(workingFullPath))
            throw new VcsException($"'{path}'의 작업 파일이 없어 내장 충돌 편집을 열 수 없습니다.");

        var token = await ReadConflictStateTokenAsync(root, path, entries, cancellationToken).ConfigureAwait(false);
        return await ConflictFileStore.ReadAsync(root, path, baseText, leftText, rightText, token,
            "stage 2 · 현재 기준 (rebase에서는 적용 대상 쪽)",
            "stage 3 · 합칠 변경 (rebase에서는 적용 중인 쪽)", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConflictFile> SaveConflictAsync(ConflictFile expected, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = await RequireRepositoryRootAsync(expected.RootPath, cancellationToken).ConfigureAwait(false);
        var path = ValidateRelativePath(root, expected.Path);
        var entries = await ReadAllUnmergedEntriesAsync(root, cancellationToken).ConfigureAwait(false);
        var token = await ReadConflictStateTokenAsync(root, path, entries, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected.StateToken, token, StringComparison.Ordinal))
            throw new VcsException($"'{path}'의 Git 충돌 상태가 비교 화면을 연 뒤 바뀌었습니다. 새로 고친 뒤 다시 여세요.");
        await ConflictFileStore.EnsureUnchangedAsync(expected with { RootPath = root, Path = path }, cancellationToken).ConfigureAwait(false);
        if (!entries.Any(entry => PathsEqual(entry.Path, path)))
            throw new VcsException($"'{path}'의 Git 충돌이 이미 바뀌었습니다. 상태를 새로 고치고 다시 여세요.");
        return await ConflictFileStore.SaveAsync(expected with { RootPath = root, Path = path, StateToken = token }, text, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkConflictResolvedAsync(ConflictFile expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = await RequireRepositoryRootAsync(expected.RootPath, cancellationToken).ConfigureAwait(false);
        var path = ValidateRelativePath(root, expected.Path);
        var entries = await ReadAllUnmergedEntriesAsync(root, cancellationToken).ConfigureAwait(false);
        var token = await ReadConflictStateTokenAsync(root, path, entries, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected.StateToken, token, StringComparison.Ordinal))
            throw new VcsException($"'{path}'의 Git 충돌 상태가 비교 화면을 연 뒤 바뀌었습니다. 새로 고친 뒤 다시 여세요.");
        var pinned = expected with { RootPath = root, Path = path };
        await ConflictFileStore.EnsureUnchangedAsync(pinned, cancellationToken).ConfigureAwait(false);

        var selectedEntries = entries.Where(entry => PathsEqual(entry.Path, path)).ToArray();
        if (selectedEntries.Length == 0)
            throw new VcsException($"'{path}'의 Git 충돌 항목이 이미 해결되었거나 바뀌었습니다. 상태를 새로 고치세요.");
        if (selectedEntries.Any(entry => entry.Mode is not ("100644" or "100755")) ||
            selectedEntries.All(entry => entry.Stage != 2) || selectedEntries.All(entry => entry.Stage != 3))
            throw new VcsException($"'{path}'은 내장 편집에서 지원하는 일반 텍스트 양쪽 충돌이 아닙니다.");

        var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new VcsException($"'{path}' 작업 파일을 찾지 못해 충돌을 해결 상태로 표시할 수 없습니다.");
        var workingText = await TextSnapshot.ReadFileAsync(fullPath, path, cancellationToken).ConfigureAwait(false);
        if (ConflictMarkers.ContainsMarkers(workingText))
            throw new VcsException($"'{path}'에 남은 충돌 표시를 먼저 해결한 뒤 다시 시도하세요.");
        if (!FileHashMatches(expected.FileHash, await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false)))
            throw new VcsException($"'{path}' 작업 파일이 비교 화면을 연 뒤 바뀌었습니다. 다시 열어 확인하세요.");

        var stage = await Run(root,
            ["--literal-pathspecs", "add", "--", path], cancellationToken).ConfigureAwait(false);
        stage.EnsureSuccess();

        var remaining = await ReadAllUnmergedEntriesAsync(root, cancellationToken).ConfigureAwait(false);
        if (remaining.Any(entry => PathsEqual(entry.Path, path)))
            throw new VcsException($"'{path}'을 Git 인덱스에서 해결 표시하지 못했습니다. 작업 파일은 보존했으니 상태를 새로 고치세요.");
    }

    private async Task<IReadOnlyList<UnmergedEntry>> ReadAllUnmergedEntriesAsync(string root,
        CancellationToken cancellationToken)
    {
        var listing = (await Run(root, ["-c", "core.quotepath=false", "ls-files", "--unmerged", "-z"], cancellationToken)
            .ConfigureAwait(false)).EnsureSuccess();
        var entries = new List<UnmergedEntry>();
        foreach (var record in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            var fields = tab < 0 ? [] : record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || !int.TryParse(fields[2], out var stage) || stage is < 1 or > 3 || tab + 1 >= record.Length)
                throw new VcsException("Git 인덱스의 충돌 항목 형식을 읽을 수 없습니다. 상태를 새로 고치세요.");
            var path = record[(tab + 1)..];
            ValidateRelativePath(root, path);
            entries.Add(new UnmergedEntry(path, stage, fields[0], fields[1]));
        }
        return entries;
    }

    private async Task<string> ReadConflictStateTokenAsync(string root, string path, IReadOnlyList<UnmergedEntry> entries,
        CancellationToken cancellationToken)
    {
        var headResult = await Run(root, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        var head = headResult.Success ? headResult.StandardOutput.Trim() : "(unborn)";
        var attributes = await ReadConflictMarkerSizeAsync(root, path, cancellationToken).ConfigureAwait(false);
        var state = string.Join('\n', entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Stage)
            .Select(entry => $"{entry.Path}\0{entry.Stage}\0{entry.Mode}\0{entry.ObjectId}"));
        var bytes = Encoding.UTF8.GetBytes(head + "\0" + attributes.Value + "\0" + state);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private async Task<(string Value, int Size)> ReadConflictMarkerSizeAsync(string root, string path,
        CancellationToken cancellationToken)
    {
        var result = (await Run(root,
            ["--literal-pathspecs", "check-attr", "-z", "conflict-marker-size", "--", path], cancellationToken)
            .ConfigureAwait(false)).EnsureSuccess();
        var fields = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !PathsEqual(fields[0], path) || fields[1] != "conflict-marker-size")
            throw new VcsException($"'{path}'의 Git conflict-marker-size 속성을 확인하지 못했습니다. 내장 충돌 편집을 열지 않았습니다.");

        // Git's default is seven markers. It documents that only positive integers affect
        // merge output; unset and unspecified therefore keep that default. A custom value
        // below seven cannot be parsed by our marker reader and must never be marked resolved.
        if (fields[2] is "unspecified" or "unset") return (fields[2], 7);
        if (!int.TryParse(fields[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0)
            throw new VcsException($"'{path}'의 conflict-marker-size 값 '{fields[2]}'이 양의 정수가 아니어서 내장 충돌 편집을 지원하지 않습니다.");
        if (size < 7)
            throw new VcsException($"'{path}'의 conflict-marker-size 값 {size}은(는) 7자보다 짧아 충돌 표식을 안전하게 구별할 수 없습니다. 외부 도구를 사용하세요.");
        return (fields[2], size);
    }

    private async Task<string> ReadConflictBlobAsync(string root, string path, UnmergedEntry entry,
        CancellationToken cancellationToken)
    {
        var size = (await Run(root, ["cat-file", "-s", entry.ObjectId], cancellationToken).ConfigureAwait(false))
            .EnsureSuccess();
        if (!long.TryParse(size.StandardOutput.Trim(), out var byteLength) || byteLength < 0)
            throw new VcsException($"'{path}'의 Git 충돌 blob 크기를 읽을 수 없습니다.");
        TextSnapshot.EnsureWithinLimit(byteLength, path);
        var blob = await Run(root, ["cat-file", "blob", entry.ObjectId], cancellationToken).ConfigureAwait(false);
        blob.EnsureSuccess();
        return TextSnapshot.ValidateText(blob.StandardOutput, path);
    }

    private async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool FileHashMatches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
