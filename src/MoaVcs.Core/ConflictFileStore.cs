using System.Security.Cryptography;
using System.Text;

namespace MoaVcs.Core;

/// <summary>Bounded UTF-8 edits with optimistic concurrency and atomic replacement.</summary>
public static class ConflictFileStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<ConflictFile> ReadAsync(string root, string path, string? baseText,
        string leftText, string rightText, string stateToken, string leftLabel, string rightLabel,
        CancellationToken cancellationToken = default)
    {
        root = System.IO.Path.GetFullPath(root);
        var fullPath = RequireRegularFilePath(root, path);
        var bytes = await ReadBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var text = TextSnapshot.DecodeUtf8(bom ? bytes[3..] : bytes, path);
        var newLine = GetNewLine(text);
        return new ConflictFile(root, System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/'),
            baseText, leftText, rightText, text, stateToken, Hash(bytes), bom, newLine, leftLabel, rightLabel);
    }

    public static string RequireRegularFilePath(string root, string path)
    {
        root = System.IO.Path.GetFullPath(root);
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        var relative = System.IO.Path.GetRelativePath(root, fullPath);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            System.IO.Path.IsPathRooted(relative))
            throw new VcsException("충돌 편집 대상은 현재 저장소 내부의 파일이어야 합니다.");
        var segments = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]);
        if (segments.Any(s => s.Contains(':')))
            throw new VcsException("대체 데이터 스트림은 충돌 편집 대상으로 사용할 수 없습니다.");
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(".svn", StringComparison.OrdinalIgnoreCase) || s.Equals(".lore", StringComparison.OrdinalIgnoreCase)))
            throw new VcsException("버전 관리 내부 파일은 충돌 편집 대상으로 사용할 수 없습니다.");
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new VcsException("링크 또는 연결된 폴더의 파일은 내장 충돌 편집을 지원하지 않습니다.");
        foreach (var segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new VcsException("링크 또는 연결된 폴더의 파일은 내장 충돌 편집을 지원하지 않습니다.");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
            throw new VcsException("폴더 충돌은 전용 VCS 도구에서 해결하세요.");
        return fullPath;
    }

    public static async Task EnsureUnchangedAsync(ConflictFile expected, CancellationToken cancellationToken = default)
    {
        var path = RequireRegularFilePath(expected.RootPath, expected.Path);
        var current = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Hash(current), expected.FileHash, StringComparison.Ordinal))
            throw new VcsException("파일이 다른 곳에서 변경되었습니다. 덮어쓰지 않았습니다. 편집 내용을 복사해 보관한 뒤 비교 창을 다시 여세요.");
    }

    public static string NormalizeNewLines(string text, string newLine) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newLine);

    public static async Task<ConflictFile> SaveAsync(ConflictFile expected, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        text = NormalizeNewLines(text, expected.NewLine);
        TextSnapshot.ValidateText(text, expected.Path);
        byte[] body;
        try { body = Utf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new VcsException("올바르지 않은 유니코드 문자가 포함되어 저장하지 못했습니다."); }
        var bytes = expected.HasUtf8Bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(body).ToArray() : body;
        TextSnapshot.EnsureWithinLimit(bytes.Length, expected.Path);
        await EnsureUnchangedAsync(expected, cancellationToken).ConfigureAwait(false);
        var path = RequireRegularFilePath(expected.RootPath, expected.Path);
        var id = Guid.NewGuid().ToString("N");
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".moavcs-merge-" + id + ".tmp");
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".moavcs-merge-" + id + ".bak");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            await EnsureUnchangedAsync(expected, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve the exact file replaced, including an edit in the final check/replace interval.
            File.Replace(temp, path, backup);
            try
            {
                var backupDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MoaVcs", "ConflictBackups", Hash(Encoding.UTF8.GetBytes(expected.RootPath)));
                Directory.CreateDirectory(backupDirectory);
                var storedBackup = System.IO.Path.Combine(backupDirectory, id + "-" + System.IO.Path.GetFileName(path) + ".bak");
                File.Move(backup, storedBackup);
                backup = storedBackup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The save succeeded. Keep and report the sibling backup if relocating it fails.
            }
            return expected with { WorkingText = text, FileHash = Hash(bytes), BackupPath = backup };
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string GetNewLine(string text)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var remaining = text.Replace("\r\n", "");
        var lf = remaining.Contains('\n');
        var cr = remaining.Contains('\r');
        if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1)
            throw new VcsException("줄바꿈 방식이 섞인 파일은 원문 보존을 위해 내장 충돌 편집을 지원하지 않습니다. 외부 도구를 사용하세요.");
        return crlf ? "\r\n" : cr ? "\r" : "\n";
    }

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        TextSnapshot.EnsureWithinLimit(stream.Length, path);
        var buffer = new byte[TextSnapshot.MaxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        TextSnapshot.EnsureWithinLimit(length, path);
        return buffer[..length];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
