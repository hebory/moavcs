use moavcs_core::text::{ComparisonRow, LineDiff};
use moavcs_core::{
    models::{ConflictFile, FileComparison},
    process::CancelToken,
};
use std::sync::Arc;

/// Immutable data derived from a line comparison once on the worker thread.
/// Rendering reads this shared snapshot and visits only rows in the viewport.
pub(crate) struct PreparedDiff {
    source: Arc<FileComparison>,
    pub(super) comparison: LineDiff,
    pub(super) changed_rows: Vec<usize>,
    pub(super) added: usize,
    pub(super) removed: usize,
    pub(super) modified: usize,
    pub(super) max_line_chars: usize,
}

impl PreparedDiff {
    pub(super) fn new(source: Arc<FileComparison>, comparison: LineDiff) -> Arc<Self> {
        let mut changed_rows = Vec::new();
        let (mut added, mut removed, mut modified) = (0, 0, 0);
        let mut max_line_chars = 0;

        for (index, row) in comparison.rows.iter().enumerate() {
            if row.kind != "unchanged" {
                changed_rows.push(index);
            }
            match row.kind.as_str() {
                "added" => added += 1,
                "removed" => removed += 1,
                "modified" => modified += 1,
                _ => {}
            }
            max_line_chars = max_line_chars
                .max(row.left_text.chars().count())
                .max(row.right_text.chars().count());
        }

        Arc::new(Self {
            source,
            comparison,
            changed_rows,
            added,
            removed,
            modified,
            max_line_chars,
        })
    }

    pub(super) fn matches(&self, candidate: &FileComparison) -> bool {
        self.source.left_text == candidate.left_text
            && self.source.right_text == candidate.right_text
    }

    pub(super) fn prepare(
        source: Arc<FileComparison>,
        cached: Option<&Arc<Self>>,
        cancel: &CancelToken,
    ) -> Result<Arc<Self>, String> {
        cancel.check().map_err(|error| error.to_string())?;
        if let Some(cached) = cached.filter(|cached| cached.matches(&source)) {
            return Ok(Arc::clone(cached));
        }
        let comparison =
            moavcs_core::text::compare_lines(&source.left_text, &source.right_text, cancel)
                .map_err(|error| error.to_string())?;
        cancel.check().map_err(|error| error.to_string())?;
        Ok(Self::new(source, comparison))
    }

    pub(super) fn rows(&self) -> &[ComparisonRow] {
        &self.comparison.rows
    }
}

pub(super) struct PreparedConflict {
    pub(super) source: Arc<ConflictFile>,
    pub(super) local: super::theme::PreparedText,
    pub(super) incoming: super::theme::PreparedText,
    pub(super) base: Option<super::theme::PreparedText>,
}

impl PreparedConflict {
    pub(super) fn new(source: Arc<ConflictFile>) -> Self {
        Self {
            local: super::theme::PreparedText::new(&source.left_text),
            incoming: super::theme::PreparedText::new(&source.right_text),
            base: source
                .base_text
                .as_deref()
                .map(super::theme::PreparedText::new),
            source,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn source(path: &str, left_text: &str, right_text: &str) -> Arc<FileComparison> {
        Arc::new(FileComparison {
            path: path.into(),
            left_label: "base".into(),
            right_label: "worktree".into(),
            left_text: left_text.into(),
            right_text: right_text.into(),
        })
    }

    #[test]
    fn cache_reuses_content_and_keeps_current_path_metadata_separate() {
        let original = source("one.txt", "same\nold", "same\nnew");
        let prepared = PreparedDiff::prepare(original, None, &CancelToken::new()).unwrap();
        let reopened = source("two.txt", "same\nold", "same\nnew");

        assert!(prepared.matches(&reopened));
        let hit =
            PreparedDiff::prepare(Arc::clone(&reopened), Some(&prepared), &CancelToken::new())
                .unwrap();
        assert!(Arc::ptr_eq(&prepared, &hit));
        assert_eq!(reopened.path, "two.txt");
    }

    #[test]
    fn cache_rejects_stale_content_and_cancelled_work() {
        let original = source("file.txt", "before", "after");
        let prepared = PreparedDiff::prepare(original, None, &CancelToken::new()).unwrap();
        let edited = source("file.txt", "before", "edited");

        assert!(!prepared.matches(&edited));
        let refreshed =
            PreparedDiff::prepare(Arc::clone(&edited), Some(&prepared), &CancelToken::new())
                .unwrap();
        assert!(!Arc::ptr_eq(&prepared, &refreshed));
        assert!(refreshed.matches(&edited));

        let cancelled = CancelToken::new();
        cancelled.cancel();
        assert!(PreparedDiff::prepare(edited, Some(&refreshed), &cancelled).is_err());
    }
}
