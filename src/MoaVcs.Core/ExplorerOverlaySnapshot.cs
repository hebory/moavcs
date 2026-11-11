using System.Buffers.Binary;
using System.Text;

namespace MoaVcs.Core;

public enum ExplorerOverlayState : uint
{
    Conflict = 1,
    Modified = 2,
    Untracked = 3
}

public sealed record ExplorerOverlayEntry(ExplorerOverlayState State, string Path);

/// <summary>Builds and serializes the bounded status snapshot consumed by the Explorer overlay handler.</summary>
public static class ExplorerOverlaySnapshot
{
    public const uint Magic = 0x4D4F4156;
    public const uint Version = 1;
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 100_000;
    public const int MaxPathCodeUnits = 32_767;

    private const int HeaderSize = sizeof(uint) + sizeof(uint) + sizeof(long) + sizeof(uint);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);

    public static IReadOnlyList<ExplorerOverlayEntry> Build(string root, IReadOnlyList<FileChange> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(changes);
        if (!Path.IsPathFullyQualified(root) || !IsSafePathSyntax(root, fullyQualified: true) || HasTraversalComponent(root))
            throw new ArgumentException("The repository root must be a safe fully qualified path.", nameof(root));
        var fullRoot = Path.GetFullPath(root);
        var volumeRoot = Path.GetPathRoot(fullRoot)!;
        if (!fullRoot.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
            fullRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ValidatePathLength(fullRoot);

        var states = new Dictionary<string, ExplorerOverlayState>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change is null) continue;
            var state = GetState(change);
            if (state is null) continue;
            AddPath(change.Path, state.Value);
            if (!string.IsNullOrWhiteSpace(change.OriginalPath)) AddPath(change.OriginalPath, ExplorerOverlayState.Modified);
        }

        return states.Select(pair => new ExplorerOverlayEntry(pair.Value, pair.Key))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        void AddPath(string path, ExplorerOverlayState state)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var isRooted = Path.IsPathRooted(path);
            if (!IsSafePathSyntax(path, isRooted && Path.IsPathFullyQualified(path)) ||
                (isRooted && !Path.IsPathFullyQualified(path)) || HasTraversalComponent(path)) return;
            string full;
            try { full = Path.GetFullPath(isRooted ? path : Path.Combine(fullRoot, path)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return; }
            var pathVolumeRoot = Path.GetPathRoot(full);
            if (!full.Equals(pathVolumeRoot, StringComparison.OrdinalIgnoreCase))
                full = Path.TrimEndingDirectorySeparator(full);
            if (!IsWithin(fullRoot, full) || ContainsMetadataComponent(fullRoot, full)) return;
            ValidatePathLength(full);
            for (var current = full; IsWithin(fullRoot, current); current = Path.GetDirectoryName(current)!)
            {
                AddState(current, state);
                if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        void AddState(string path, ExplorerOverlayState state)
        {
            if (!states.TryGetValue(path, out var previous) || Priority(state) < Priority(previous))
                states[path] = state;
            if (states.Count > MaxEntries) throw new InvalidDataException("Explorer overlay snapshot has too many entries.");
        }
    }

    public static void Write(Stream destination, DateTime utcTimestamp, IReadOnlyList<ExplorerOverlayEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(entries);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        if (entries.Count > MaxEntries) throw new InvalidDataException("Explorer overlay snapshot has too many entries.");

        long fileTime;
        try { fileTime = utcTimestamp.ToUniversalTime().ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Explorer overlay timestamp is invalid.", ex); }

        var encoded = new (ExplorerOverlayState State, string Path, byte[] Bytes)[entries.Count];
        var total = HeaderSize;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new InvalidDataException("Explorer overlay entry is null.");
            ValidateState(entry.State);
            ValidateAbsolutePath(entry.Path);
            if (!seen.Add(entry.Path)) throw new InvalidDataException("Explorer overlay snapshot contains a duplicate path.");
            var bytes = EncodePath(entry.Path);
            total = checked(total + sizeof(uint) * 2 + bytes.Length);
            if (total > MaxBytes) throw new InvalidDataException("Explorer overlay snapshot exceeds 16 MiB.");
            encoded[i] = (entry.State, entry.Path, bytes);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], fileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], checked((uint)encoded.Length));
        destination.Write(header);
        Span<byte> field = stackalloc byte[sizeof(uint)];
        foreach (var entry in encoded)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)entry.State);
            destination.Write(field);
            BinaryPrimitives.WriteUInt32LittleEndian(field, checked((uint)(entry.Bytes.Length / 2)));
            destination.Write(field);
            destination.Write(entry.Bytes);
        }
    }

    public static IReadOnlyList<ExplorerOverlayEntry> Read(Stream source, out DateTime utcTimestamp)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream is not readable.", nameof(source));
        if (source.CanSeek && source.Length - source.Position > MaxBytes) throw new InvalidDataException("Explorer overlay snapshot exceeds 16 MiB.");

        var header = new byte[HeaderSize];
        ReadExactly(source, header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("Explorer overlay snapshot magic is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Version) throw new InvalidDataException("Explorer overlay snapshot version is unsupported.");
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        try { utcTimestamp = DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Explorer overlay timestamp is invalid.", ex); }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        if (count > MaxEntries) throw new InvalidDataException("Explorer overlay snapshot has too many entries.");

        var entries = new List<ExplorerOverlayEntry>(checked((int)count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumed = HeaderSize;
        Span<byte> field = stackalloc byte[sizeof(uint)];
        for (var i = 0; i < count; i++)
        {
            ReadExactly(source, field);
            consumed += sizeof(uint);
            var rawState = BinaryPrimitives.ReadUInt32LittleEndian(field);
            var state = (ExplorerOverlayState)rawState;
            ValidateState(state);
            ReadExactly(source, field);
            consumed += sizeof(uint);
            var codeUnits = BinaryPrimitives.ReadUInt32LittleEndian(field);
            if (codeUnits is 0 or > MaxPathCodeUnits) throw new InvalidDataException("Explorer overlay path length is invalid.");
            var byteCount = checked((int)codeUnits * 2);
            consumed = checked(consumed + byteCount);
            if (consumed > MaxBytes) throw new InvalidDataException("Explorer overlay snapshot exceeds 16 MiB.");
            var bytes = new byte[byteCount];
            ReadExactly(source, bytes);
            string path;
            try { path = StrictUtf16Le.GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Explorer overlay path contains invalid UTF-16.", ex); }
            ValidateAbsolutePath(path);
            if (!seen.Add(path)) throw new InvalidDataException("Explorer overlay snapshot contains a duplicate path.");
            entries.Add(new ExplorerOverlayEntry(state, path));
        }

        if (source.ReadByte() != -1) throw new InvalidDataException("Explorer overlay snapshot has trailing data.");
        return entries;
    }

    private static ExplorerOverlayState? GetState(FileChange change)
    {
        if (change.IsConflicted || IsAny(change, "conflict", "conflicted", "tree-conflicted")) return ExplorerOverlayState.Conflict;
        if (IsAny(change, "untracked", "unversioned", "?")) return ExplorerOverlayState.Untracked;
        if (IsAny(change, "modified", "changed", "added", "deleted", "renamed", "copied", "missing", "replaced",
                "obstructed", "incomplete", "property modified", "add", "edit", "delete", "move/add", "move/delete",
                "branch", "integrate", "import", "retype", "purge", "archive", "A", "D", "M", "R", "C", "T", "U"))
            return ExplorerOverlayState.Modified;
        if (HasGitStatusCode(change.IndexStatus) || HasGitStatusCode(change.WorkTreeStatus)) return ExplorerOverlayState.Modified;
        return null;
    }

    private static bool IsAny(FileChange change, params string[] values) =>
        values.Any(value => change.Status.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            change.IndexStatus.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            change.WorkTreeStatus.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static bool HasGitStatusCode(string value) => value.Length == 1 && value[0] is 'A' or 'D' or 'M' or 'R' or 'C' or 'T' or 'U';

    private static bool ContainsMetadataComponent(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                component.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                component.Equals(".lore", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafePathSyntax(string path, bool fullyQualified)
    {
        if (path.IndexOf('\0') >= 0 || path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (!OperatingSystem.IsWindows()) return true;
        if (path.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0) return false;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == ':' && !(fullyQualified && i == 1 && char.IsAsciiLetter(path[0]))) return false;
        return true;
    }

    private static bool HasTraversalComponent(string path) =>
        path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component == "..");

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static int Priority(ExplorerOverlayState state) => state switch
    {
        ExplorerOverlayState.Conflict => 0,
        ExplorerOverlayState.Modified => 1,
        ExplorerOverlayState.Untracked => 2,
        _ => throw new InvalidDataException("Explorer overlay state is invalid.")
    };

    private static void ValidateState(ExplorerOverlayState state) => _ = Priority(state);

    private static void ValidatePathLength(string path)
    {
        if (path.Length is 0 or > MaxPathCodeUnits) throw new InvalidDataException("Explorer overlay path length is invalid.");
    }

    private static void ValidateAbsolutePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidatePathLength(path);
        if (!Path.IsPathFullyQualified(path) || !IsSafePathSyntax(path, fullyQualified: true) || HasTraversalComponent(path))
            throw new InvalidDataException("Explorer overlay path must be an absolute normalized path.");
        try
        {
            if (!Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Explorer overlay path must be an absolute normalized path.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidDataException("Explorer overlay path is invalid.", ex);
        }
    }

    private static byte[] EncodePath(string path)
    {
        try { return StrictUtf16Le.GetBytes(path); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Explorer overlay path contains invalid UTF-16.", ex); }
    }

    private static void ReadExactly(Stream source, Span<byte> buffer)
    {
        try { source.ReadExactly(buffer); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Explorer overlay snapshot is truncated.", ex); }
    }
}
