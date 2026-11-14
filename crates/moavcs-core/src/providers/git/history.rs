use super::*;

impl GitProvider {
    pub(super) fn history_details_impl(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        let root = self.root(root, cancel)?;
        let id = resolve_commit(self, &root, revision, cancel)?;
        let entry = read_entry(self, &root, &id, cancel)?;
        let files = read_files(self, &root, &entry, cancel)?;
        let label = if entry.parent_ids.len() > 1 {
            format!(
                "첫 번째 부모 기준 · {} → {}",
                short(&entry.parent_ids[0]),
                short(&entry.id)
            )
        } else if entry.parent_ids.is_empty() {
            format!("첫 커밋 · 빈 저장소 → {}", short(&entry.id))
        } else {
            format!("{} → {}", short(&entry.parent_ids[0]), short(&entry.id))
        };
        Ok(HistoryDetails {
            entry,
            files,
            comparison_label: label,
        })
    }
    pub(super) fn history_diff_impl(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root, cancel)?;
        let id = resolve_commit(self, &root, revision, cancel)?;
        let entry = read_entry(self, &root, &id, cancel)?;
        let mut args = vec![
            "-c".into(),
            "core.quotepath=false".into(),
            "--literal-pathspecs".into(),
            "--no-pager".into(),
            "diff-tree".into(),
            "--patch".into(),
            "--no-commit-id".into(),
            "-r".into(),
            "-M".into(),
            "--no-ext-diff".into(),
            "--no-textconv".into(),
            "--no-color".into(),
        ];
        if entry.parent_ids.is_empty() {
            args.push("--root".into())
        } else {
            args.push(entry.parent_ids[0].clone())
        }
        args.push(id.clone());
        if let Some(file) = file {
            let valid = self.path(&root, &file.path)?;
            let changes = read_files(self, &root, &entry, cancel)?;
            let found = changes
                .iter()
                .find(|c| c.path == valid || c.original_path.as_deref() == Some(&valid))
                .ok_or_else(|| {
                    VcsError(format!(
                        "'{valid}'은 선택한 커밋에서 변경한 파일이 아닙니다."
                    ))
                })?;
            args.push("--".into());
            if let Some(old) = &found.original_path {
                args.push(old.clone())
            }
            args.push(found.path.clone())
        }
        Ok(self.required(&root, args, cancel)?.stdout)
    }
    pub(super) fn history_snapshot(
        &self,
        root: &Path,
        scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        self.snapshot_impl(root, scope, cancel)
    }
}
pub(super) fn short(id: &str) -> String {
    id.chars().take(12).collect()
}
pub(super) fn resolve_commit(
    provider: &GitProvider,
    root: &Path,
    revision: &str,
    cancel: &CancelToken,
) -> Result<String> {
    if !(revision.len() == 40 || revision.len() == 64)
        || !revision.bytes().all(|b| b.is_ascii_hexdigit())
    {
        return Err(VcsError("Git 커밋 ID가 올바르지 않습니다.".into()));
    }
    let out = provider
        .run(
            root,
            vec![
                "rev-parse".into(),
                "--verify".into(),
                format!("{revision}^{{commit}}"),
            ],
            cancel,
        )?
        .ensure()?;
    let id = out.stdout.trim();
    if id.eq_ignore_ascii_case(revision) {
        Ok(id.into())
    } else {
        Err(VcsError(
            "Git 커밋 ID를 안전하게 확인하지 못했습니다.".into(),
        ))
    }
}
pub(super) fn read_entry(
    provider: &GitProvider,
    root: &Path,
    id: &str,
    cancel: &CancelToken,
) -> Result<HistoryEntry> {
    let out = provider.required(
        root,
        vec![
            "show".into(),
            "-s".into(),
            "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B%x00".into(),
            id.into(),
        ],
        cancel,
    )?;
    let f: Vec<_> = out.stdout.split('\0').collect();
    if f.len() < 9 || f[0].trim() != id {
        return Err(VcsError("Git 커밋 세부 정보를 읽을 수 없습니다.".into()));
    }
    let parents = f[1].split_whitespace().map(str::to_owned).collect();
    Ok(HistoryEntry {
        id: id.into(),
        author: f[2].into(),
        date: f[4].into(),
        message: f[8].trim_end_matches(['\r', '\n']).into(),
        parent_ids: parents,
        references: Vec::new(),
        relation: HistoryRelation::Unknown,
        author_email: f[3].into(),
        committer: f[5].into(),
        committer_email: f[6].into(),
        commit_date: f[7].into(),
    })
}
pub(super) fn read_files(
    provider: &GitProvider,
    root: &Path,
    entry: &HistoryEntry,
    cancel: &CancelToken,
) -> Result<Vec<FileChange>> {
    let mut args = vec![
        "--literal-pathspecs".into(),
        "--no-pager".into(),
        "diff-tree".into(),
        "--no-commit-id".into(),
        "--name-status".into(),
        "-r".into(),
        "-M".into(),
        "-z".into(),
    ];
    if entry.parent_ids.is_empty() {
        args.push("--root".into())
    } else {
        args.push(entry.parent_ids[0].clone())
    }
    args.push(entry.id.clone());
    let output = provider.required(root, args, cancel)?.stdout;
    let fields: Vec<_> = output.split('\0').filter(|x| !x.is_empty()).collect();
    let mut result = Vec::new();
    let mut i = 0;
    while i < fields.len() {
        let status = fields[i];
        i += 1;
        let rename = status.starts_with('R') || status.starts_with('C');
        let old = if rename {
            let p = fields
                .get(i)
                .ok_or_else(|| VcsError("Git diff-tree rename 경로가 누락되었습니다.".into()))?;
            i += 1;
            Some((*p).to_owned())
        } else {
            None
        };
        let path = fields
            .get(i)
            .ok_or_else(|| VcsError("Git diff-tree 파일 경로가 누락되었습니다.".into()))?
            .to_string();
        i += 1;
        for p in old.iter().chain(std::iter::once(&path)) {
            provider.path(root, p)?;
        }
        let label = match status.as_bytes().first().copied().unwrap_or(b'M') {
            b'A' => "added",
            b'D' => "deleted",
            b'R' => "renamed",
            b'C' => "copied",
            b'T' => "type changed",
            _ => "modified",
        };
        result.push(FileChange {
            path,
            status: label.into(),
            index_status: String::new(),
            worktree_status: String::new(),
            original_path: old,
        });
    }
    Ok(result)
}
