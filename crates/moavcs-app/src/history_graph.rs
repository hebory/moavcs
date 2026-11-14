//! Parent-aware, precomputed lane layout for compact Git history graphs.
//!
//! Construct one [`GraphLayout`] for a history snapshot and retain it while painting
//! individual rows. This keeps graph work out of the per-row paint path.

use std::collections::{HashMap, HashSet};

use eframe::egui::{self, Color32, Pos2, Response, Stroke, Vec2};
use moavcs_core::{HistoryEntry, HistoryReferenceKind};

const LEFT: f32 = 8.0;
const LANE_PITCH: f32 = 13.0;
const VISIBLE_LANES: usize = 7;
const PALETTE: [Color32; 7] = [
    Color32::from_rgb(63, 112, 171),
    Color32::from_rgb(214, 115, 66),
    Color32::from_rgb(59, 143, 111),
    Color32::from_rgb(133, 100, 165),
    Color32::from_rgb(179, 135, 48),
    Color32::from_rgb(57, 143, 158),
    Color32::from_rgb(173, 91, 109),
];

/// Width reserved for the history graph column.
pub const GRAPH_WIDTH: f32 = 104.0;

/// Precomputed parent topology for a bounded, newest-first history snapshot.
#[derive(Debug, Clone, Default)]
pub struct GraphLayout {
    rows: HashMap<String, RowLayout>,
}

impl GraphLayout {
    /// Build graph rows once for the displayed history entries (normally at most 200).
    /// Entries are expected newest first, as returned by `git log`.
    pub fn new(entries: &[HistoryEntry]) -> Self {
        let mut indices = HashMap::with_capacity(entries.len());
        for (index, entry) in entries.iter().enumerate() {
            indices.entry(entry.id.as_str()).or_insert(index);
        }

        let mut rows = HashMap::with_capacity(entries.len());
        let mut active: Vec<Lane> = Vec::new();
        let mut seen = HashSet::with_capacity(entries.len());

        for (entry_index, entry) in entries.iter().enumerate() {
            let old = active.clone();
            let mut node_lane = old.iter().position(|lane| lane.id == entry.id);
            let was_expected = node_lane.is_some();
            let node_color = node_lane
                .map(|index| old[index].color)
                .unwrap_or_else(|| color_for_identity(&entry.id));
            if let Some(index) = node_lane {
                active.remove(index);
            } else {
                node_lane = Some(active.len());
            }
            let node_lane = node_lane.unwrap_or(0);

            let mut edge_targets = Vec::new();
            let mut new_parent_count = 0usize;
            let mut outside_parent_count = 0usize;
            let mut unique_parents = HashSet::new();
            for parent in &entry.parent_ids {
                if !unique_parents.insert(parent.as_str()) || seen.contains(parent) {
                    continue;
                }

                if let Some(lane) = active
                    .iter()
                    .position(|candidate| candidate.id.as_str() == parent.as_str())
                {
                    // The parent already has a live lane; connect to that lane so branches join.
                    edge_targets.push(EdgeTarget::Parent(parent.clone()));
                    debug_assert_eq!(active[lane].id, *parent);
                    continue;
                }

                let listed_below = indices
                    .get(parent.as_str())
                    .is_some_and(|parent_index| *parent_index > entry_index);
                if !listed_below {
                    edge_targets.push(EdgeTarget::Outside(outside_parent_count));
                    outside_parent_count += 1;
                    new_parent_count += 1;
                    continue;
                }

                // The first parent continues this lane; additional merge parents start
                // independently colored lanes. Identity-based colors survive column shifts.
                let insert_at = if new_parent_count == 0 {
                    node_lane.min(active.len())
                } else {
                    active.len()
                };
                let color = if new_parent_count == 0 {
                    node_color
                } else {
                    color_for_identity(parent)
                };
                active.insert(
                    insert_at,
                    Lane {
                        id: parent.clone(),
                        color,
                    },
                );
                edge_targets.push(EdgeTarget::Parent(parent.clone()));
                new_parent_count += 1;
            }

            let mut positions = HashMap::with_capacity(active.len());
            for (lane, value) in active.iter().enumerate() {
                positions.entry(value.id.as_str()).or_insert(lane);
            }
            let parent_edges = edge_targets
                .into_iter()
                .map(|target| match target {
                    EdgeTarget::Parent(id) => {
                        let lane = positions.get(id.as_str()).copied().unwrap_or(node_lane);
                        ParentEdge {
                            lane,
                            color: active.get(lane).map_or(node_color, |value| value.color),
                            outside: false,
                        }
                    }
                    EdgeTarget::Outside(offset) => ParentEdge {
                        lane: active.len().max(node_lane + 1) + offset,
                        color: node_color,
                        outside: true,
                    },
                })
                .collect::<Vec<_>>();

            let mut segments = Vec::new();
            for (from, lane) in old.iter().enumerate() {
                if lane.id == entry.id {
                    continue;
                }
                if let Some(to) = positions.get(lane.id.as_str()).copied() {
                    segments.push(LaneSegment {
                        from,
                        to,
                        color: lane.color,
                    });
                }
            }

            let max_lane = parent_edges
                .iter()
                .map(|edge| edge.lane)
                .chain(std::iter::once(node_lane))
                .chain(active.len().checked_sub(1))
                .max()
                .unwrap_or(0);
            rows.insert(
                entry.id.clone(),
                RowLayout {
                    node_lane,
                    node_color,
                    has_incoming: was_expected,
                    is_head: entry
                        .references
                        .iter()
                        .any(|reference| reference.kind == HistoryReferenceKind::Head),
                    segments,
                    parent_edges,
                    has_hidden_lanes: max_lane >= VISIBLE_LANES,
                },
            );
            seen.insert(entry.id.clone());
        }

        Self { rows }
    }

    /// Paint one precomputed history row and return its allocated egui response.
    pub fn paint_row(&self, ui: &mut egui::Ui, id: &str, height: f32) -> Response {
        let height = height.max(1.0);
        let (rect, response) =
            ui.allocate_exact_size(Vec2::new(GRAPH_WIDTH, height), egui::Sense::hover());
        let Some(row) = self.rows.get(id) else {
            return response;
        };

        let painter = ui.painter().with_clip_rect(rect);
        let mid_y = rect.center().y;
        let bottom_y = rect.bottom() - 1.0;
        let point =
            |lane: usize, y: f32| Pos2::new(rect.left() + LEFT + lane as f32 * LANE_PITCH, y);

        if row.has_incoming {
            line(
                &painter,
                row.node_color,
                point(row.node_lane, rect.top()),
                point(row.node_lane, mid_y),
                1.7,
            );
        }
        for segment in &row.segments {
            line(
                &painter,
                segment.color,
                point(segment.from, rect.top()),
                point(segment.to, bottom_y),
                1.5,
            );
        }
        for edge in &row.parent_edges {
            let target = point(edge.lane, bottom_y);
            line(
                &painter,
                edge.color,
                point(row.node_lane, mid_y),
                target,
                1.7,
            );
            if edge.outside {
                painter.circle_stroke(target, 2.4, Stroke::new(1.4_f32, edge.color));
                painter.circle_filled(target, 1.15, rect_fill(ui));
            }
        }

        if row.has_hidden_lanes && height >= 13.0 {
            let origin = Pos2::new(rect.right() - 8.0, rect.top() + 1.0);
            painter.text(
                origin,
                egui::Align2::RIGHT_TOP,
                "…",
                egui::FontId::proportional(12.0),
                ui.visuals().text_color(),
            );
        }

        let center = point(row.node_lane, mid_y);
        if row.is_head {
            painter.circle_filled(center, 4.0, rect_fill(ui));
            painter.circle_stroke(
                center,
                5.5,
                Stroke::new(2.0_f32, Color32::from_rgb(226, 95, 60)),
            );
        } else {
            painter.circle_filled(center, 4.1, row.node_color);
            painter.circle_stroke(center, 4.1, Stroke::new(1.1_f32, rect_fill(ui)));
        }
        response
    }
}

fn line(painter: &egui::Painter, color: Color32, from: Pos2, to: Pos2, width: f32) {
    painter.line_segment([from, to], Stroke::new(width, color));
}

fn rect_fill(ui: &egui::Ui) -> Color32 {
    ui.visuals().panel_fill
}

fn color_for_identity(identity: &str) -> Color32 {
    let hash = identity.bytes().fold(0xcbf29ce484222325u64, |hash, byte| {
        (hash ^ u64::from(byte)).wrapping_mul(0x100000001b3)
    });
    PALETTE[(hash as usize) % PALETTE.len()]
}

#[derive(Debug, Clone)]
struct Lane {
    id: String,
    color: Color32,
}

#[derive(Debug, Clone)]
enum EdgeTarget {
    Parent(String),
    Outside(usize),
}

#[derive(Debug, Clone)]
struct LaneSegment {
    from: usize,
    to: usize,
    color: Color32,
}

#[derive(Debug, Clone)]
struct ParentEdge {
    lane: usize,
    color: Color32,
    outside: bool,
}

#[derive(Debug, Clone)]
struct RowLayout {
    node_lane: usize,
    node_color: Color32,
    has_incoming: bool,
    is_head: bool,
    segments: Vec<LaneSegment>,
    parent_edges: Vec<ParentEdge>,
    has_hidden_lanes: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    use moavcs_core::HistoryReference;

    fn entry(id: &str, parents: &[&str]) -> HistoryEntry {
        HistoryEntry {
            id: id.to_owned(),
            parent_ids: parents.iter().map(|value| (*value).to_owned()).collect(),
            ..HistoryEntry::default()
        }
    }

    fn row<'a>(layout: &'a GraphLayout, id: &str) -> &'a RowLayout {
        layout.rows.get(id).expect("test row exists")
    }

    #[test]
    fn linear_three_commit_chain_keeps_one_continuous_lane() {
        let layout = GraphLayout::new(&[entry("a", &["b"]), entry("b", &["c"]), entry("c", &[])]);
        let a = row(&layout, "a");
        let b = row(&layout, "b");
        let c = row(&layout, "c");
        assert_eq!(a.parent_edges.len(), 1);
        assert_eq!(a.parent_edges[0].lane, 0);
        assert!(b.has_incoming);
        assert!(b.segments.is_empty());
        assert_eq!(b.parent_edges[0].lane, 0);
        assert!(c.has_incoming);
        assert!(c.parent_edges.is_empty());
        assert_eq!(a.node_color, b.node_color);
        assert_eq!(b.node_color, c.node_color);
    }

    #[test]
    fn merge_fork_rejoins_at_shared_ancestor() {
        let entries = [
            entry("M", &["A", "B"]),
            entry("A", &["C"]),
            entry("B", &["C"]),
            entry("C", &[]),
        ];
        let layout = GraphLayout::new(&entries);
        let merge = row(&layout, "M");
        assert_eq!(merge.parent_edges.len(), 2);
        let a = row(&layout, "A");
        assert!(a.has_incoming);
        assert_eq!(a.parent_edges.len(), 1);
        let b = row(&layout, "B");
        assert!(b.has_incoming);
        assert_eq!(b.parent_edges.len(), 1);
        let c = row(&layout, "C");
        assert!(c.has_incoming);
        assert!(c.parent_edges.is_empty());
    }

    #[test]
    fn merge_parent_that_already_has_a_lane_joins_without_duplicate_lane() {
        let entries = [
            entry("X", &["B"]),
            entry("M", &["A", "B"]),
            entry("A", &["B"]),
            entry("B", &[]),
        ];
        let layout = GraphLayout::new(&entries);
        let merge = row(&layout, "M");
        assert_eq!(merge.parent_edges.len(), 2);
        assert_ne!(merge.parent_edges[0].lane, merge.parent_edges[1].lane);
        assert_eq!(row(&layout, "A").parent_edges.len(), 1);
    }

    #[test]
    fn missing_parent_is_a_hollow_outside_tail_and_head_gets_ring() {
        let mut head = entry("tip", &["parent-outside-range"]);
        head.references.push(HistoryReference {
            name: "HEAD".to_owned(),
            kind: HistoryReferenceKind::Head,
        });
        let layout = GraphLayout::new(&[head]);
        let tip = row(&layout, "tip");
        assert!(tip.is_head);
        assert_eq!(tip.parent_edges.len(), 1);
        assert!(tip.parent_edges[0].outside);
    }

    #[test]
    fn lane_color_follows_lane_identity_after_a_column_shift() {
        let entries = [
            entry("tip", &["side", "main"]),
            entry("side", &[]),
            entry("main", &[]),
        ];
        let layout = GraphLayout::new(&entries);
        let tip = row(&layout, "tip");
        let side_color = tip.parent_edges[0].color;
        let main_color = tip.parent_edges[1].color;
        let side = row(&layout, "side");
        let main = row(&layout, "main");
        assert_eq!(side.node_color, tip.node_color);
        assert_eq!(main.node_color, main_color);
        assert_ne!(side_color, main_color);
    }
}
