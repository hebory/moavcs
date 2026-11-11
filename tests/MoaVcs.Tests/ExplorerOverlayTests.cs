using System.Buffers.Binary;
using MoaVcs.Core;

namespace MoaVcs.Tests;

internal static class ExplorerOverlayTests
{
    public static Task RunAsync()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var changes = new[]
        {
            new FileChange("src/changed.cs", "Modified"),
            new FileChange("src/new.cs", "Untracked"),
            new FileChange("src/conflict.cs", "Conflict"),
            new FileChange("loose/new.txt", "unversioned"),
            new FileChange("ignored.bin", "Ignored"),
            new FileChange("unknown.bin", "other-status"),
            new FileChange(".git/index", "Modified"),
            new FileChange("nested/.git/config", "Modified"),
            new FileChange(".svn/wc.db", "Modified"),
            new FileChange(".lore/config.toml", "Modified"),
            new FileChange("src/renamed.cs", "Renamed", OriginalPath: "old/old-name.cs"),
            new FileChange("../" + Path.GetFileName(root) + "-sibling/escape.txt", "Modified"),
            new FileChange(Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-sibling", "escape.txt"), "Modified")
        };
        if (OperatingSystem.IsWindows())
        {
            changes = [.. changes, new FileChange(Path.GetPathRoot(root)![..2] + "relative-drive.txt", "Modified"),
                new FileChange(Path.Combine(root, "ads.txt:stream"), "Modified")];
        }
        var entries = ExplorerOverlaySnapshot.Build(root, changes);
        var byPath = entries.ToDictionary(entry => entry.Path, entry => entry.State, StringComparer.OrdinalIgnoreCase);
        Check(byPath[Path.Combine(root, "src")] == ExplorerOverlayState.Conflict, "folder shows highest-priority descendant conflict");
        Check(byPath[root] == ExplorerOverlayState.Conflict, "repository root aggregates descendant conflict");
        Check(byPath[Path.Combine(root, "loose")] == ExplorerOverlayState.Untracked, "untracked state propagates to ancestors");
        Check(byPath[Path.Combine(root, "old")] == ExplorerOverlayState.Modified &&
              byPath[Path.Combine(root, "old", "old-name.cs")] == ExplorerOverlayState.Modified,
            "rename original path and its ancestors are included");
        Check(!byPath.ContainsKey(Path.Combine(root, "ignored.bin")) && !byPath.ContainsKey(Path.Combine(root, "unknown.bin")),
            "ignored and unknown statuses produce no badges");
        Check(!entries.Any(entry => entry.Path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
              entry.Path.Contains(Path.DirectorySeparatorChar + ".svn" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
              entry.Path.Contains(Path.DirectorySeparatorChar + ".lore" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
            "VCS metadata directories are omitted");
        Check(!entries.Any(entry => entry.Path.Contains(Path.GetFileName(root) + "-sibling", StringComparison.OrdinalIgnoreCase)),
            "prefix-similar paths outside repository are omitted");

        var timestamp = new DateTime(2026, 11, 12, 3, 4, 5, DateTimeKind.Utc);
        using var stream = new MemoryStream();
        ExplorerOverlaySnapshot.Write(stream, timestamp, entries);
        var bytes = stream.ToArray();
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x4D4F4156 &&
              BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == 1,
            "binary header uses the agreed magic and version");
        Check(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)) == timestamp.ToFileTimeUtc(), "timestamp is UTC Windows FILETIME");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) == entries.Count, "header entry count is exact");
        stream.Position = 0;
        var decoded = ExplorerOverlaySnapshot.Read(stream, out var decodedTimestamp);
        Check(decodedTimestamp == timestamp && decoded.SequenceEqual(entries), "snapshot round-trips exact paths, states and timestamp");

        Rejects(bytes[..^1], "truncated snapshot is rejected");
        Rejects([.. bytes, 0], "trailing bytes are rejected");
        var badMagic = bytes.ToArray();
        badMagic[0] ^= 0xFF;
        Rejects(badMagic, "invalid magic is rejected");
        var badVersion = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badVersion.AsSpan(4), 2);
        Rejects(badVersion, "unsupported version is rejected");
        var badState = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badState.AsSpan(20), 4);
        Rejects(badState, "unknown state is rejected");

        Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
            [new ExplorerOverlayEntry((ExplorerOverlayState)4, Path.Combine(root, "bad"))]), "writer rejects unknown states");
        Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
            [new ExplorerOverlayEntry(ExplorerOverlayState.Modified, "relative.txt")]), "writer rejects relative paths");
        if (OperatingSystem.IsWindows())
        {
            Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
                [new ExplorerOverlayEntry(ExplorerOverlayState.Modified, Path.Combine(root, "ads.txt:stream"))]),
                "writer rejects alternate data stream paths");
            Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
                [new ExplorerOverlayEntry(ExplorerOverlayState.Modified, @"\\?\C:\repo\file.txt")]),
                "writer rejects extended device paths");
        }
        Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
            [new ExplorerOverlayEntry(ExplorerOverlayState.Modified, Path.Combine(root, "same")),
             new ExplorerOverlayEntry(ExplorerOverlayState.Untracked, Path.Combine(root, "same"))]), "writer rejects duplicate paths");
        Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Write(new MemoryStream(), timestamp,
            Enumerable.Repeat(new ExplorerOverlayEntry(ExplorerOverlayState.Modified, Path.Combine(root, "x")), ExplorerOverlaySnapshot.MaxEntries + 1).ToArray()),
            "writer enforces entry count bound");
        return Task.CompletedTask;
    }

    private static void Rejects(byte[] bytes, string label)
    {
        using var stream = new MemoryStream(bytes);
        Throws<InvalidDataException>(() => ExplorerOverlaySnapshot.Read(stream, out _), label);
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Explorer overlay test failed: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Explorer overlay test failed: " + label);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moavcs-overlay-test-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
