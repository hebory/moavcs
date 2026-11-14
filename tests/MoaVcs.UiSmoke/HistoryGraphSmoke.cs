using System.Collections;
using System.Reflection;
using MoaVcs.App;
using MoaVcs.Core;

namespace MoaVcs.UiSmoke;

internal static partial class Program
{
    private static void RunHistoryGraphSmoke()
    {
        var linear = new[]
        {
            new HistoryEntry("c3", "A", "", "tip") { ParentIds = ["c2"], References = [new HistoryReference("HEAD", HistoryReferenceKind.Head)] },
            new HistoryEntry("c2", "A", "", "middle") { ParentIds = ["c1"] },
            new HistoryEntry("c1", "A", "", "root")
        };
        var linearLayout = BuildLayout(linear);
        AssertRow(linearLayout, "c3", nodeLane: 0, hasIncoming: false, parentLanes: [0]);
        AssertRow(linearLayout, "c2", nodeLane: 0, hasIncoming: true, parentLanes: [0]);
        AssertRow(linearLayout, "c1", nodeLane: 0, hasIncoming: true, parentLanes: []);

        var merge = new[]
        {
            new HistoryEntry("M", "A", "", "merge") { ParentIds = ["A", "B"] },
            new HistoryEntry("A", "A", "", "first branch") { ParentIds = ["C"] },
            new HistoryEntry("B", "B", "", "second branch") { ParentIds = ["C"] },
            new HistoryEntry("C", "A", "", "shared base")
        };
        var mergeLayout = BuildLayout(merge);
        AssertRow(mergeLayout, "M", nodeLane: 0, hasIncoming: false, parentLanes: [0, 1]);
        AssertRow(mergeLayout, "A", nodeLane: 0, hasIncoming: true, parentLanes: [0]);
        AssertRow(mergeLayout, "B", nodeLane: 1, hasIncoming: true, parentLanes: [0]);
        AssertRow(mergeLayout, "C", nodeLane: 0, hasIncoming: true, parentLanes: []);

        var clipped = BuildLayout([new HistoryEntry("short", "A", "", "bounded") { ParentIds = ["outside"] }]);
        AssertRow(clipped, "short", nodeLane: 0, hasIncoming: false, parentLanes: [1], outsideParents: 1);
    }

    private static object BuildLayout(IReadOnlyList<HistoryEntry> entries)
    {
        var layoutType = typeof(HistoryGraphCell).GetNestedType("GraphLayout", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History graph layout type was not found.");
        var create = layoutType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("History graph layout builder was not found.");
        return create.Invoke(null, [entries]) ?? throw new InvalidOperationException("History graph layout was empty.");
    }

    private static void AssertRow(object layout, string id, int nodeLane, bool hasIncoming,
        int[] parentLanes, int outsideParents = 0)
    {
        var rows = layout.GetType().GetProperty("Rows")!.GetValue(layout) as IDictionary
            ?? throw new InvalidOperationException("History graph rows were not built.");
        var row = rows[id] ?? throw new InvalidOperationException($"History graph row {id} was missing.");
        var rowType = row.GetType();
        if ((int)rowType.GetProperty("NodeLane")!.GetValue(row)! != nodeLane ||
            (bool)rowType.GetProperty("HasIncoming")!.GetValue(row)! != hasIncoming)
            throw new InvalidOperationException($"History graph continuity was wrong for {id}.");

        var edges = rowType.GetProperty("ParentEdges")!.GetValue(row) as IEnumerable
            ?? throw new InvalidOperationException($"History graph parent lanes were missing for {id}.");
        var lanes = new List<int>();
        var outside = 0;
        foreach (var edge in edges)
        {
            var type = edge!.GetType();
            lanes.Add((int)type.GetProperty("Lane")!.GetValue(edge)!);
            if ((bool)type.GetProperty("IsOutside")!.GetValue(edge)!) outside++;
        }
        if (!lanes.SequenceEqual(parentLanes) || outside != outsideParents)
            throw new InvalidOperationException($"History graph parent connections were wrong for {id}.");

        var segments = rowType.GetProperty("Segments")!.GetValue(row) as IEnumerable
            ?? throw new InvalidOperationException($"History graph branch lanes were missing for {id}.");
        if (id == "A")
        {
            var segment = segments.Cast<object>().Single();
            if ((int)segment.GetType().GetProperty("From")!.GetValue(segment)! != 1 ||
                (int)segment.GetType().GetProperty("To")!.GetValue(segment)! != 1)
                throw new InvalidOperationException("The other merge branch did not continue in its own lane.");
        }
    }
}
