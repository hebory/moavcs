using MoaVcs.Core;

namespace MoaVcs.Tests;

internal static class TextDiffTests
{
    public static void Run()
    {
        Check("", ""); Check("a\n", "a\nb\n"); Check("a\nb\n", "b\n");
        var changed = Check("첫 줄\nold\nlast\n", "첫 줄\nnew\nlast\n");
        Assert(changed.Rows.Count == 3 && changed.Rows[1].Kind == "modified", "replacement alignment");
        Assert(Check("a\na\nb\n", "a\nb\nb\n").Rows.Any(r => r.Kind == "unchanged"), "repeated lines");
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            var a = Enumerable.Range(0, random.Next(1, 60)).Select(_ => random.Next(8).ToString()).ToArray();
            var b = Enumerable.Range(0, random.Next(1, 60)).Select(_ => random.Next(8).ToString()).ToArray();
            Check(string.Join('\n', a), string.Join('\n', b));
        }
        Assert(Check(string.Join('\n', Enumerable.Range(0, 600)), string.Join('\n', Enumerable.Range(1000, 600))).Simplified, "bounded fallback");
        Assert(LineDiff.LineEndingLabel("a\r\n") != LineDiff.LineEndingLabel("a\n"), "line ending visibility");
        Assert(LineDiff.LineEndingLabel("a\n") != LineDiff.LineEndingLabel("a"), "final newline visibility");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { LineDiff.Compare("a", "b", cancel.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    private static LineDiffResult Check(string left, string right)
    {
        var result = LineDiff.Compare(left, right);
        Assert(result.Rows.Where(r => r.LeftNumber.HasValue).Select(r => r.LeftText).SequenceEqual(LineDiff.SplitLines(left)), "left text preservation");
        Assert(result.Rows.Where(r => r.RightNumber.HasValue).Select(r => r.RightText).SequenceEqual(LineDiff.SplitLines(right)), "right text preservation");
        Assert(result.Rows.Where(r => r.LeftNumber.HasValue).Select(r => r.LeftNumber!.Value).SequenceEqual(Enumerable.Range(1, LineDiff.SplitLines(left).Length)), "left numbering");
        Assert(result.Rows.Where(r => r.RightNumber.HasValue).Select(r => r.RightNumber!.Value).SequenceEqual(Enumerable.Range(1, LineDiff.SplitLines(right).Length)), "right numbering");
        Assert(result.Rows.Where(r => r.Kind == "unchanged").All(r => r.LeftText == r.RightText), "unchanged content");
        return result;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Line diff: " + message); }
}
