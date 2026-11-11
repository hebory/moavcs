using System.Text;
using MoaVcs.Core;

namespace MoaVcs.Tests;

internal static class ConflictTextTests
{
    public static async Task RunAsync()
    {
        const string text = "유지\r\n<<<<<<< current\r\n왼쪽\r\n||||||| base\r\n기준\r\n=======\r\n오른쪽\r\n>>>>>>> incoming\r\n끝\r\n";
        var block = ConflictMarkers.Parse(text).Single();
        Assert(block.LeftText == "왼쪽\r\n" && block.RightText == "오른쪽\r\n" && block.BaseText == "기준\r\n", "diff3 sides");
        Assert(text[..block.Start] + block.RightText + text[(block.Start + block.Length)..] == "유지\r\n오른쪽\r\n끝\r\n", "only selected block changes");
        var multiple = text + text.Replace("current", "second");
        var blocks = ConflictMarkers.Parse(multiple);
        Assert(blocks.Count == 2 && blocks[1].Start > blocks[0].Start + blocks[0].Length, "independent block positions");
        Assert(ConflictMarkers.Parse("<<<<<<<<<< a\nx\n==========\ny\n>>>>>>>>>> b").Single().RightText == "y\n", "custom marker width and no final newline");
        Assert(ConflictMarkers.Parse("<<<<<<< a\n=======\ny\n>>>>>>> b\n").Single().LeftText.Length == 0, "empty side");
        Assert(ConflictMarkers.Parse("ordinary\n<<< code\n").Count == 0 && !ConflictMarkers.ContainsMarkers("x <<<<<< y"), "ordinary text");
        Assert(ConflictMarkers.ContainsMarkers("\uFEFF<<<<<<< unfinished"), "BOM must not hide an unfinished first marker");
        foreach (var malformed in new[] { "<<<<<<< a\nx", ">>>>>>> b\n", "=======\n", "<<<<<<< a\n<<<<<<< b\nx\n=======\ny\n>>>>>>> b\n", "<<<<<<< a\nx\n========\ny\n>>>>>>> b\n" })
        {
            Assert(ConflictMarkers.ContainsMarkers(malformed), "unfinished markers cannot resolve");
            try { ConflictMarkers.Parse(malformed); throw new Exception("Malformed markers accepted"); }
            catch (VcsException) { }
        }

        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local", "conflict-text-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "한글 공백.txt");
            var encoding = new UTF8Encoding(true);
            await File.WriteAllTextAsync(path, text, encoding);
            var snapshot = await Read();
            Assert(snapshot.HasUtf8Bom && snapshot.NewLine == "\r\n" && snapshot.WorkingText == text, "UTF8 BOM and CRLF detection");
            var saved = await ConflictFileStore.SaveAsync(snapshot, "유지\n해결함\n끝\n");
            Assert(saved.BackupPath is not null && (await File.ReadAllBytesAsync(saved.BackupPath)).SequenceEqual(
                new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text))), "exact replaced bytes are recoverable from backup");
            var bytes = await File.ReadAllBytesAsync(path);
            Assert(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) &&
                Encoding.UTF8.GetString(bytes[3..]) == "유지\r\n해결함\r\n끝\r\n", "BOM and original newlines preserved on save");
            await ConflictFileStore.EnsureUnchangedAsync(saved);
            await Throws(() => ConflictFileStore.SaveAsync(snapshot, "stale"), "outdated file snapshot rejected");
            await File.AppendAllTextAsync(path, "외부 수정");
            var outsideEdit = await File.ReadAllBytesAsync(path);
            await Throws(() => ConflictFileStore.SaveAsync(saved, "overwrite"), "external edit rejected");
            Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(outsideEdit), "external edit preserved");
            Assert(!Directory.EnumerateFiles(root, ".moavcs-merge-*.tmp").Any(), "no temporary files remain");
            await File.WriteAllTextAsync(path, "one\ntwo\r\n", new UTF8Encoding(false));
            await Throws(async () => { await Read(); }, "mixed newlines rejected before editing");
            await File.WriteAllBytesAsync(path, [0x61, 0, 0x62]);
            await Throws(async () => { await Read(); }, "binary files rejected");
            await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0x41, 0]);
            await Throws(async () => { await Read(); }, "non-UTF8 files rejected");
            await File.WriteAllTextAsync(path, new string('x', TextSnapshot.MaxBytes + 1));
            await Throws(async () => { await Read(); }, "large files bounded");
            await Throws(async () => { await ConflictFileStore.ReadAsync(root, "../outside.txt", null, "", "", "", "", ""); }, "outside paths rejected");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await File.WriteAllTextAsync(Path.Combine(root, ".git", "config"), "metadata");
            await Throws(async () => { await ConflictFileStore.ReadAsync(root, ".git/config", null, "", "", "", "", ""); }, "metadata rejected");

            Task<ConflictFile> Read() => ConflictFileStore.ReadAsync(root, "한글 공백.txt", "base", "left", "right", "token", "왼쪽", "오른쪽");
        }
        finally
        {
            // Only this randomly named fixture beneath the workspace .local is owned by this test.
            Directory.Delete(root, true);
        }
    }

    private static async Task Throws(Func<Task> action, string message)
    {
        try { await action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Conflict text: " + message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Conflict text: " + message);
    }
}
