using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MoaVcs.Core;

namespace MoaVcs.App.Services;

/// <summary>Values supplied by the application when starting a configured external tool.</summary>
public sealed record ExternalToolContext(
    string RepositoryPath,
    string FilePath,
    string? LeftText = null,
    string? RightText = null,
    string? PatchText = null,
    string? BlameText = null);

/// <summary>Launches configured programs without a shell. Each template becomes one argv item.</summary>
public static partial class ExternalToolLauncher
{
    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.OrdinalIgnoreCase)
        { "left", "right", "patch", "file", "repo", "blame" };

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static bool UsesPlaceholder(ExternalToolSettings? settings, string name)
    {
        if (settings?.ArgumentTemplates is null) return false;
        return settings.ArgumentTemplates.Any(template => PlaceholderRegex().Matches(template ?? "")
            .Cast<Match>().Any(match => string.Equals(match.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Renders argument templates with no command-line or shell parsing.</summary>
    public static IReadOnlyList<string> RenderArguments(
        ExternalToolSettings settings,
        ExternalToolKind kind,
        IReadOnlyDictionary<string, string> values)
    {
        ValidateTemplates(settings, kind);
        var rendered = new List<string>(settings.ArgumentTemplates.Count);
        foreach (var template in settings.ArgumentTemplates)
        {
            var result = PlaceholderRegex().Replace(template, match =>
            {
                var name = match.Groups[1].Value;
                if (!KnownPlaceholders.Contains(name)) throw new VcsException($"알 수 없는 외부 도구 자리표시자입니다: {{{name}}}");
                if (!values.TryGetValue(name, out var value)) throw new VcsException($"외부 도구에 필요한 값이 없습니다: {{{name}}}");
                if (value.Contains('\0')) throw new VcsException("외부 도구 인수에 사용할 수 없는 문자가 포함되어 있습니다.");
                return value;
            });
            rendered.Add(result);
        }
        return rendered;
    }

    /// <summary>Materializes required UTF-8 snapshots and starts the external GUI process.</summary>
    public static Process Launch(ExternalToolSettings settings, ExternalToolKind kind, ExternalToolContext context, string? snapshotRoot = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateExecutable(settings);
        ValidateTemplates(settings, kind);
        var repositoryPath = Path.GetFullPath(context.RepositoryPath);
        var filePath = Path.GetFullPath(context.FilePath, repositoryPath);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["repo"] = repositoryPath,
            ["file"] = filePath
        };
        var needsSnapshot = kind == ExternalToolKind.Diff
            ? UsesPlaceholder(settings, "left") || UsesPlaceholder(settings, "right") || UsesPlaceholder(settings, "patch")
            : UsesPlaceholder(settings, "blame");
        string? snapshotDirectory = null;
        try
        {
            if (needsSnapshot)
            {
                snapshotDirectory = SnapshotStore.CreateDirectory(snapshotRoot);
                if (kind == ExternalToolKind.Diff)
                {
                    if (UsesPlaceholder(settings, "left"))
                        values["left"] = WriteSnapshot(snapshotDirectory, "left", filePath, context.LeftText, "비교의 왼쪽 내용을 받지 못했습니다.");
                    if (UsesPlaceholder(settings, "right"))
                        values["right"] = WriteSnapshot(snapshotDirectory, "right", filePath, context.RightText, "비교의 오른쪽 내용을 받지 못했습니다.");
                    if (UsesPlaceholder(settings, "patch"))
                        values["patch"] = WriteSnapshot(snapshotDirectory, "changes", filePath, context.PatchText, "비교 패치 내용을 받지 못했습니다.", ".patch");
                }
                else if (UsesPlaceholder(settings, "blame"))
                    values["blame"] = WriteSnapshot(snapshotDirectory, "blame", filePath, context.BlameText, "파일 작성 이력 내용을 받지 못했습니다.", ".tsv");
            }

            var arguments = RenderArguments(settings, kind, values);
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(settings.ExecutablePath.Trim()),
                WorkingDirectory = Directory.Exists(repositoryPath) ? repositoryPath : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            try
            {
                return Process.Start(start) ?? throw new VcsException("외부 도구를 시작하지 못했습니다.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new VcsException($"외부 도구를 시작하지 못했습니다: {ex.Message}");
            }
        }
        catch
        {
            if (snapshotDirectory is not null) SnapshotStore.DeleteGeneratedDirectory(snapshotDirectory, snapshotRoot);
            throw;
        }
    }

    public static void Validate(ExternalToolSettings settings, ExternalToolKind kind)
    {
        ValidateExecutable(settings);
        ValidateTemplates(settings, kind);
    }

    private static void ValidateExecutable(ExternalToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !Path.IsPathFullyQualified(settings.ExecutablePath.Trim()))
            throw new VcsException("외부 도구 실행 파일의 절대 경로를 지정하세요.");
        if (!string.Equals(Path.GetExtension(settings.ExecutablePath.Trim()), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new VcsException("외부 도구는 실행 파일(.exe) 경로로 지정하세요.");
        if (!File.Exists(settings.ExecutablePath.Trim()))
            throw new VcsException($"외부 도구 실행 파일을 찾을 수 없습니다: {settings.ExecutablePath.Trim()}");
    }

    private static void ValidateTemplates(ExternalToolSettings settings, ExternalToolKind kind)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ArgumentTemplates is null || settings.ArgumentTemplates.Count == 0)
            throw new VcsException("외부 도구 인수를 한 줄에 하나씩 입력하세요.");
        foreach (var template in settings.ArgumentTemplates)
        {
            if (template is null || template.Contains('\0') || template.Contains('\r') || template.Contains('\n'))
                throw new VcsException("각 외부 도구 인수는 한 줄로 입력하세요.");
            foreach (Match match in PlaceholderRegex().Matches(template))
                if (!KnownPlaceholders.Contains(match.Groups[1].Value))
                    throw new VcsException($"알 수 없는 외부 도구 자리표시자입니다: {{{match.Groups[1].Value}}}");
        }

        bool Has(string name) => settings.ArgumentTemplates.Any(template => PlaceholderRegex().Matches(template)
            .Cast<Match>().Any(match => string.Equals(match.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)));
        if (kind == ExternalToolKind.Diff && !(Has("patch") || (Has("left") && Has("right"))))
            throw new VcsException("차이 도구 인수에 {left}와 {right}, 또는 {patch}를 포함하세요.");
        if (kind == ExternalToolKind.Blame && !(Has("blame") || Has("file")))
            throw new VcsException("작성 이력 도구 인수에 {blame} 또는 {file}을 포함하세요.");
    }

    private static string WriteSnapshot(string directory, string prefix, string sourcePath, string? text, string missingMessage, string? forcedExtension = null)
    {
        if (text is null) throw new VcsException(missingMessage);
        var extension = forcedExtension ?? Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) extension = ".txt";
        var path = Path.Combine(directory, prefix + extension);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

/// <summary>Owns private, generated snapshot folders and removes only old MoaVCS folders.</summary>
public static class SnapshotStore
{
    private const string Prefix = "moavcs-snapshot-";
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(7);
    private static readonly List<string> CleanupWarnings = [];
    public static IReadOnlyList<string> LastCleanupWarnings => CleanupWarnings.ToArray();

    public static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoaVcs", "snapshots");

    public static int CleanupExpired(DateTime? utcNow = null, string? rootPath = null)
    {
        CleanupWarnings.Clear();
        var root = Path.GetFullPath(rootPath ?? RootPath);
        if (!Directory.Exists(root)) return 0;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                CleanupWarnings.Add($"임시 스냅샷 폴더가 재분석 지점이므로 정리하지 않았습니다: {root}");
                return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupWarnings.Add($"임시 스냅샷 폴더를 확인하지 못했습니다: {ex.Message}");
            return 0;
        }
        var now = utcNow ?? DateTime.UtcNow;
        var removed = 0;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(root, Prefix + "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupWarnings.Add($"임시 스냅샷 목록을 읽지 못했습니다: {ex.Message}");
            return 0;
        }
        foreach (var path in candidates)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var name = Path.GetFileName(full);
                if (!IsDirectChildOf(full, root) || !HasGeneratedName(name) ||
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(full) > now - Retention) continue;
                var entries = Directory.EnumerateFileSystemEntries(full).ToArray();
                if (entries.Any(entry => Directory.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                    !IsExpectedSnapshotFile(Path.GetFileName(entry)))) continue;
                foreach (var entry in entries) File.Delete(entry);
                Directory.Delete(full, recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CleanupWarnings.Add($"임시 스냅샷 폴더를 정리하지 못했습니다: {Path.GetFileName(path)} ({ex.Message})");
            }
        }
        return removed;
    }

    internal static string CreateDirectory(string? rootPath = null)
    {
        var root = Path.GetFullPath(rootPath ?? RootPath);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("임시 스냅샷 폴더가 재분석 지점이므로 사용할 수 없습니다.");
        var path = Path.Combine(root, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteGeneratedDirectory(string path, string? rootPath = null)
    {
        var root = Path.GetFullPath(rootPath ?? RootPath);
        var full = Path.GetFullPath(path);
        if (!IsDirectChildOf(full, root) || !HasGeneratedName(Path.GetFileName(full)) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("임시 스냅샷 경로가 안전하지 않아 삭제하지 않았습니다.");
        var entries = Directory.EnumerateFileSystemEntries(full).ToArray();
        if (entries.Any(entry => Directory.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
            !IsExpectedSnapshotFile(Path.GetFileName(entry))))
            throw new IOException("임시 스냅샷 폴더에 예상하지 못한 항목이 있어 삭제하지 않았습니다.");
        foreach (var entry in entries) File.Delete(entry);
        Directory.Delete(full, recursive: false);
    }

    private static bool IsDirectChildOf(string path, string root) =>
        string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool HasGeneratedName(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _);

    private static bool IsExpectedSnapshotFile(string name) =>
        (name.StartsWith("left.", StringComparison.Ordinal) && name.Length > "left.".Length) ||
        (name.StartsWith("right.", StringComparison.Ordinal) && name.Length > "right.".Length) ||
        name is "changes.patch" or "blame.tsv";
}
