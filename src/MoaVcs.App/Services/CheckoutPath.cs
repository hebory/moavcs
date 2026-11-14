using System.IO;
using System.Text.RegularExpressions;
using MoaVcs.Core;

namespace MoaVcs.App.Services;

/// <summary>Derives and validates checkout directory names from repository addresses.</summary>
public static class CheckoutPath
{
    private static readonly char[] Separators = ['/', '\\'];
    private static readonly char[] InvalidLeafCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly Regex DeviceName = new(
        @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Returns a safe final path component, or an empty string when it cannot be inferred safely.</summary>
    public static string SuggestFolderName(string remote, VcsKind kind)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.Any(char.IsControl)) return string.Empty;
        var value = remote.Trim();
        if (value.StartsWith("-", StringComparison.Ordinal)) return string.Empty;
        string? component;
        string? sourcePath = null;
        var uriPath = false;

        if (HasDrivePrefix(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            sourcePath = value.Length > 2 && value[2] is not ('/' or '\\')
                ? value[2..]
                : value;
            component = LastPathComponent(sourcePath);
        }
        else if (LooksLikeAbsoluteUri(value))
        {
            if (HasMalformedEscape(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
                return string.Empty;
            sourcePath = uri.AbsolutePath;
            uriPath = true;
            component = LastPathComponent(sourcePath);
            if (component is not null)
            {
                try { component = Uri.UnescapeDataString(component); }
                catch (UriFormatException) { return string.Empty; }
            }
        }
        else if (TryGetScpPath(value, out var scpPath))
        {
            sourcePath = scpPath;
            component = LastPathComponent(sourcePath);
        }
        else
        {
            sourcePath = value;
            component = LastPathComponent(sourcePath);
        }

        if (component is null) return string.Empty;
        if (kind == VcsKind.Git && component.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            var parentPath = ParentPath(sourcePath!);
            component = LastPathComponent(parentPath);
            if (component is null) return string.Empty;
            if (uriPath) component = Uri.UnescapeDataString(component);
        }
        if (kind == VcsKind.Git && component.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            component = component[..^4];
        return IsSafeLeaf(component) ? component : string.Empty;
    }

    /// <summary>Combines an absolute parent directory and one safe folder name.</summary>
    public static string GetDestination(string parent, string folderName)
    {
        if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent.Trim()))
            throw new VcsException("상위 폴더에 절대 경로를 입력하세요.");
        if (!IsSafeLeaf(folderName)) throw new VcsException("올바른 로컬 폴더 이름을 입력하세요.");

        var fullParent = Path.GetFullPath(parent.Trim());
        var destination = Path.GetFullPath(Path.Combine(fullParent, folderName));
        var relative = Path.GetRelativePath(fullParent, destination);
        if (Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new VcsException("대상 폴더가 선택한 상위 폴더를 벗어납니다.");
        return destination;
    }

    private static bool IsSafeLeaf(string? leaf)
    {
        if (string.IsNullOrEmpty(leaf) || leaf is "." or ".." || leaf.Length > 255 ||
            leaf.IndexOfAny(InvalidLeafCharacters) >= 0 || leaf.Any(char.IsControl) ||
            leaf[^1] is ' ' or '.')
            return false;

        var deviceBase = leaf.Split('.')[0].TrimEnd(' ');
        return !DeviceName.IsMatch(deviceBase);
    }

    private static string? LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        if (trimmed.Length == 0) return null;
        var separator = trimmed.LastIndexOfAny(Separators);
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }

    private static bool HasDrivePrefix(string value) => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool LooksLikeAbsoluteUri(string value) =>
        value.Contains("://", StringComparison.Ordinal) ||
        value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var separator = trimmed.LastIndexOfAny(Separators);
        return separator < 0 ? string.Empty : trimmed[..separator];
    }

    private static bool TryGetScpPath(string value, out string path)
    {
        path = string.Empty;
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1 || value.Contains('/') && value.IndexOf('/') < colon ||
            value.Contains('\\') && value.IndexOf('\\') < colon)
            return false;

        var host = value[..colon];
        if (host.Any(char.IsWhiteSpace) || host.Any(InvalidLeafCharacters.Contains)) return false;
        path = value[(colon + 1)..];
        return true;
    }

    private static bool HasMalformedEscape(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return true;
            i += 2;
        }
        return false;
    }
}
