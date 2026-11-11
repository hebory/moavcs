namespace MoaVcs.Core;

public sealed record ConflictBlock(int Start, int Length, string LeftText, string RightText,
    string? BaseText, string LeftLabel, string RightLabel);

/// <summary>Parses standard merge/diff3 markers without changing surrounding text or line endings.</summary>
public static class ConflictMarkers
{
    public static bool ContainsMarkers(string text) => Lines(text).Any(line => Marker(line.Text).Kind != '\0');

    public static IReadOnlyList<ConflictBlock> Parse(string text)
    {
        var lines = Lines(text).ToArray();
        var result = new List<ConflictBlock>();
        for (var i = 0; i < lines.Length; i++)
        {
            var first = Marker(lines[i].Text);
            if (first.Kind == '\0') continue;
            if (first.Kind != '<') throw Malformed();
            var start = lines[i].Start;
            var leftStart = lines[i].End;
            var leftEnd = -1;
            var baseStart = -1;
            var baseEnd = -1;
            var rightStart = -1;
            var foundEnd = false;
            for (i++; i < lines.Length; i++)
            {
                var marker = Marker(lines[i].Text);
                if (marker.Kind == '\0') continue;
                if (marker.Width != first.Width) throw Malformed();
                if (marker.Kind == '|' && leftEnd < 0)
                {
                    leftEnd = lines[i].Start;
                    baseStart = lines[i].End;
                }
                else if (marker.Kind == '=' && rightStart < 0)
                {
                    if (baseStart >= 0) baseEnd = lines[i].Start;
                    else leftEnd = lines[i].Start;
                    rightStart = lines[i].End;
                }
                else if (marker.Kind == '>' && rightStart >= 0)
                {
                    result.Add(new ConflictBlock(start, lines[i].End - start,
                        text[leftStart..leftEnd], text[rightStart..lines[i].Start],
                        baseStart >= 0 ? text[baseStart..baseEnd] : null, first.Label, marker.Label));
                    foundEnd = true;
                    break;
                }
                else throw Malformed();
            }
            if (!foundEnd) throw Malformed();
        }
        return result;
    }

    private static (char Kind, int Width, string Label) Marker(string text)
    {
        if (text.Length < 7 || text[0] is not ('<' or '>' or '=' or '|')) return default;
        var kind = text[0];
        var width = 1;
        while (width < text.Length && text[width] == kind) width++;
        if (width < 7 || (width < text.Length && !char.IsWhiteSpace(text[width]))) return default;
        var label = text[width..].Trim();
        if (kind == '=' && label.Length != 0) return default;
        return (kind, width, label);
    }

    private static IEnumerable<(int Start, int End, string Text)> Lines(string text)
    {
        for (var start = text.StartsWith('\uFEFF') ? 1 : 0; start < text.Length;)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            var content = text[start..end];
            if (end < text.Length && text[end++] == '\r' && end < text.Length && text[end] == '\n') end++;
            yield return (start, end, content);
            start = end;
        }
    }

    private static VcsException Malformed() => new("충돌 표식이 일부 없거나 중첩되어 구간을 선택할 수 없습니다. 결과를 직접 편집해 표식을 모두 정리하세요.");
}
