using System.Text;

namespace MoaVcs.Core;

/// <summary>Shared guard for bounded, UTF-8 text snapshots shown in comparison and blame views.</summary>
public static class TextSnapshot
{
    public const int MaxBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadFileAsync(string filePath, string displayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        var buffer = new byte[MaxBytes + 1];
        var length = 0;
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        EnsureWithinLimit(length, displayPath);
        return DecodeUtf8(buffer.AsSpan(0, length).ToArray(), displayPath);
    }

    public static string DecodeUtf8(byte[] bytes, string path)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        EnsureWithinLimit(bytes.Length, path);
        if (bytes.AsSpan().Contains((byte)0))
            throw new VcsException($"'{path}' 파일은 바이너리 파일이므로 텍스트로 표시할 수 없습니다.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new VcsException($"'{path}' 파일을 UTF-8 텍스트로 읽을 수 없습니다.");
        }
        return ValidateText(text, path);
    }

    public static string ValidateText(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0'))
            throw new VcsException($"'{path}' 파일은 바이너리 파일이므로 텍스트로 표시할 수 없습니다.");
        if (text.Contains('\uFFFD'))
            throw new VcsException($"'{path}' 파일을 UTF-8 텍스트로 읽을 수 없습니다.");
        EnsureWithinLimit(Encoding.UTF8.GetByteCount(text), path);
        return text;
    }

    public static void EnsureWithinLimit(long length, string path)
    {
        if (length > MaxBytes) throw TooLarge(path);
    }

    private static VcsException TooLarge(string path) =>
        new($"'{path}' 파일은 {MaxBytes / 1024} KiB를 초과하여 텍스트로 표시할 수 없습니다.");
}
