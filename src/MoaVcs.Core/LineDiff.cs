namespace MoaVcs.Core;

public sealed record ComparisonRow(int? LeftNumber, string LeftText, int? RightNumber, string RightText, string Kind);
public sealed record LineDiffResult(IReadOnlyList<ComparisonRow> Rows, bool Simplified);

/// <summary>A bounded Myers line comparison. Large edit sets retain every line in a replacement block.</summary>
public static class LineDiff
{
    public const int MaxLines = 50_000;
    private const int MaxDistance = 512;

    public static LineDiffResult Compare(string leftText, string rightText, CancellationToken cancellationToken = default)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);
        if (left.Length > MaxLines || right.Length > MaxLines)
            throw new VcsException("좌우 비교는 파일당 50,000줄까지 지원합니다. 외부 도구를 사용하세요.");
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var edits = FindEdits(left[prefix..(left.Length - suffix)], right[prefix..(right.Length - suffix)], cancellationToken);
        var simplified = edits is null;
        edits ??= Enumerable.Repeat('-', left.Length - prefix - suffix)
            .Concat(Enumerable.Repeat('+', right.Length - prefix - suffix)).ToList();
        var rows = new List<ComparisonRow>();
        var l = 0; var r = 0;
        for (var i = 0; i < prefix; i++) Equal();
        for (var i = 0; i < edits.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edits[i] == '=') { Equal(); i++; continue; }
            var removed = 0; var added = 0;
            while (i < edits.Count && edits[i] != '=') { if (edits[i++] == '-') removed++; else added++; }
            for (var j = 0; j < Math.Max(removed, added); j++)
            {
                var hasLeft = j < removed; var hasRight = j < added;
                rows.Add(new(hasLeft ? l + 1 : null, hasLeft ? left[l++] : "",
                    hasRight ? r + 1 : null, hasRight ? right[r++] : "",
                    hasLeft && hasRight ? "modified" : hasLeft ? "removed" : "added"));
            }
        }
        for (var i = 0; i < suffix; i++) Equal();
        return new(rows, simplified);

        void Equal() => rows.Add(new(l + 1, left[l++], r + 1, right[r++], "unchanged"));
    }

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public static string LineEndingLabel(string text)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var remaining = text.Replace("\r\n", "");
        var lf = remaining.Contains('\n'); var cr = remaining.Contains('\r');
        var kind = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1 ? "혼합 줄바꿈" : crlf ? "CRLF" : lf ? "LF" : cr ? "CR" : "줄바꿈 없음";
        return kind + (text.EndsWith('\n') || text.EndsWith('\r') ? " · 끝 줄바꿈 있음" : " · 끝 줄바꿈 없음");
    }

    private static List<char>? FindEdits(string[] left, string[] right, CancellationToken ct)
    {
        var limit = Math.Min(left.Length + right.Length, MaxDistance);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();
        var work = 0;
        for (var d = 0; d <= limit; d++)
        {
            ct.ThrowIfCancellationRequested();
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1]
                    ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < left.Length && y < right.Length && left[x] == right[y]) { x++; y++; if (++work > 4_000_000) return null; }
                v[offset + k] = x;
                if (x >= left.Length && y >= right.Length)
                {
                    var result = new List<char>();
                    for (var depth = d; depth >= 0; depth--)
                    {
                        var previous = trace[depth]; var diagonal = x - y;
                        var prevK = diagonal == -depth || diagonal != depth && previous[offset + diagonal - 1] < previous[offset + diagonal + 1]
                            ? diagonal + 1 : diagonal - 1;
                        var prevX = previous[offset + prevK]; var prevY = prevX - prevK;
                        while (x > prevX && y > prevY) { result.Add('='); x--; y--; }
                        if (depth == 0) break;
                        if (x == prevX) { result.Add('+'); y--; } else { result.Add('-'); x--; }
                    }
                    result.Reverse();
                    return result;
                }
            }
        }
        return null;
    }
}
