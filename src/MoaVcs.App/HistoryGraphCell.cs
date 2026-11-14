using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MoaVcs.Core;

namespace MoaVcs.App;

/// <summary>Compact, parent-based commit graph cell for virtualized history rows.</summary>
public sealed class HistoryGraphCell : FrameworkElement
{
    private const double Left = 8;
    private const double LanePitch = 13;
    private static readonly ConditionalWeakTable<object, GraphLayout> Layouts = new();
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(63, 112, 171),
        Color.FromRgb(214, 115, 66),
        Color.FromRgb(59, 143, 111),
        Color.FromRgb(133, 100, 165),
        Color.FromRgb(179, 135, 48),
        Color.FromRgb(57, 143, 158),
        Color.FromRgb(173, 91, 109)
    ];

    public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(
        nameof(Entries), typeof(IEnumerable<HistoryEntry>), typeof(HistoryGraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(HistoryEntry), typeof(HistoryGraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<HistoryEntry>? Entries
    {
        get => (IEnumerable<HistoryEntry>?)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    public HistoryEntry? Entry
    {
        get => (HistoryEntry?)GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Entry is not { } entry || Entries is not { } entries || ActualHeight <= 0 || ActualWidth <= 0)
            return;

        // DataGrid virtualization creates cells repeatedly; cache the topology by the exact
        // immutable list instance supplied by the history snapshot.
        var key = (object)entries;
        var layout = Layouts.GetValue(key, _ => GraphLayout.Create(entries));
        if (!layout.Rows.TryGetValue(entry.Id, out var row)) return;

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var midY = ActualHeight / 2;
        var bottomY = ActualHeight - 1;

        if (row.HasIncoming)
            DrawLine(drawingContext, row.NodeColor, PointAt(row.NodeLane, 0), PointAt(row.NodeLane, midY), 1.7);

        // Existing lanes retain their identity while their column changes as lanes merge.
        foreach (var segment in row.Segments)
            DrawLine(drawingContext, segment.Color,
                PointAt(segment.From, 0), PointAt(segment.To, bottomY), 1.5);

        // New parent lanes leave this commit at the bottom edge. A hollow endpoint represents
        // history beyond the selected/bounded list; it never invents a connection to another row.
        foreach (var edge in row.ParentEdges)
        {
            var node = PointAt(row.NodeLane, midY);
            var target = PointAt(edge.Lane, bottomY);
            DrawLine(drawingContext, edge.Color, node, target, 1.7);
            if (edge.IsOutside)
                drawingContext.DrawEllipse(Brushes.White, new Pen(edge.Brush, 1.4), target, 2.2, 2.2);
        }

        if (row.HasHiddenLanes && ActualWidth > 18)
        {
            var label = new FormattedText("…", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
                new SolidColorBrush(Color.FromRgb(125, 139, 157)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(label, new Point(ActualWidth - label.Width - 1, 0));
        }

        var nodePoint = PointAt(row.NodeLane, midY);
        var nodeBrush = row.IsHead
            ? new SolidColorBrush(Color.FromRgb(226, 95, 60))
            : row.NodeBrush;
        if (row.IsHead)
            drawingContext.DrawEllipse(Brushes.White, new Pen(nodeBrush, 2), nodePoint, 5.4, 5.4);
        else
            drawingContext.DrawEllipse(nodeBrush, new Pen(Brushes.White, 1.2), nodePoint, 4.2, 4.2);

        drawingContext.Pop();
    }

    private Point PointAt(int lane, double y) => new(Left + Math.Max(0, lane) * LanePitch, y);

    private static void DrawLine(DrawingContext context, Color color, Point start, Point end, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        context.DrawLine(pen, start, end);
    }

    private sealed class GraphLayout
    {
        public Dictionary<string, RowLayout> Rows { get; } = new(StringComparer.Ordinal);

        public static GraphLayout Create(IEnumerable<HistoryEntry> source)
        {
            var entries = source as IReadOnlyList<HistoryEntry> ?? source.ToArray();
            var indices = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++) indices.TryAdd(entries[i].Id, i);

            var result = new GraphLayout();
            var active = new List<Lane>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var old = active.ToArray();
                var nodeLane = Array.FindIndex(old, lane => lane.Id == entry.Id);
                var wasExpected = nodeLane >= 0;
                var nodeColor = wasExpected ? old[nodeLane].Color : ColorForLane(active.Count);
                if (wasExpected) active.RemoveAt(nodeLane);
                else nodeLane = active.Count;

                // Collapse the consumed commit into its first not-yet-seen parent. Additional
                // parents start independent lanes, while parents already carried by a lane join it.
                var edgeTargets = new List<(string? ParentId, int OutsideLane)>();
                var newParentCount = 0;
                var outsideParentCount = 0;
                foreach (var parent in entry.ParentIds.Distinct(StringComparer.Ordinal))
                {
                    if (seen.Contains(parent)) continue;
                    var existingLane = active.FindIndex(lane => lane.Id == parent);
                    if (existingLane >= 0)
                    {
                        edgeTargets.Add((parent, -1));
                        continue;
                    }

                    var parentIsListedBelow = indices.TryGetValue(parent, out var parentIndex) && parentIndex > indices[entry.Id];
                    if (!parentIsListedBelow)
                    {
                        // Missing from the visible range: indicate the known continuation only.
                        edgeTargets.Add((null, outsideParentCount++));
                        newParentCount++;
                        continue;
                    }

                    var insertAt = newParentCount == 0 ? Math.Min(nodeLane, active.Count) : active.Count;
                    var parentColor = newParentCount == 0 ? nodeColor : ColorForLane(active.Count);
                    active.Insert(insertAt, new Lane(parent, parentColor));
                    edgeTargets.Add((parent, -1));
                    newParentCount++;
                }

                var positions = new Dictionary<string, int>(active.Count, StringComparer.Ordinal);
                for (var lane = 0; lane < active.Count; lane++) positions.TryAdd(active[lane].Id, lane);
                var edges = edgeTargets.Select(target =>
                {
                    var lane = target.ParentId is { } parentId && positions.TryGetValue(parentId, out var index)
                        ? index : Math.Max(active.Count, nodeLane + 1) + Math.Max(0, target.OutsideLane);
                    var color = target.ParentId is { } id && positions.TryGetValue(id, out var targetLane)
                        ? active[targetLane].Color : ColorForLane(lane);
                    return new ParentEdge(lane, color, target.ParentId is null);
                }).ToArray();
                var segments = new List<LaneSegment>();
                for (var lane = 0; lane < old.Length; lane++)
                {
                    var activeLane = old[lane];
                    if (activeLane.Id == entry.Id || !positions.TryGetValue(activeLane.Id, out var targetLane)) continue;
                    segments.Add(new LaneSegment(lane, targetLane, activeLane.Color));
                }

                // A row whose earlier lanes were clipped shows an ellipsis. Lines remain clipped
                // at the actual cell boundary rather than being merged into a misleading lane.
                var maxLane = Math.Max(Math.Max(nodeLane, active.Count == 0 ? 0 : active.Count - 1),
                    edges.Length == 0 ? 0 : edges.Max(edge => edge.Lane));
                result.Rows[entry.Id] = new RowLayout(
                    nodeLane, segments, edges, wasExpected,
                    entry.References.Any(reference => reference.Kind == HistoryReferenceKind.Head),
                    nodeColor,
                    maxLane >= 7);
                seen.Add(entry.Id);
            }
            return result;
        }

        private static Color ColorForLane(int lane) => LaneColors[Math.Abs(lane % LaneColors.Length)];
    }

    private sealed record RowLayout(int NodeLane, IReadOnlyList<LaneSegment> Segments,
        IReadOnlyList<ParentEdge> ParentEdges, bool HasIncoming, bool IsHead, Color NodeColor, bool HasHiddenLanes)
    {
        public Brush NodeBrush { get; } = new SolidColorBrush(NodeColor);
    }

    private sealed record LaneSegment(int From, int To, Color Color);
    private sealed record Lane(string Id, Color Color);
    private sealed record ParentEdge(int Lane, Color Color, bool IsOutside)
    {
        public Brush Brush { get; } = new SolidColorBrush(Color);
    }
}
