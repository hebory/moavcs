use crate::{
    models::*,
    process::{CancelToken, CommandRunner, CommandSpec},
    providers::VcsProvider,
    text,
};
use serde_json::Value;
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    path::{Path, PathBuf},
    sync::Arc,
};

pub struct PerforceProvider {
    executable: String,
    runner: Arc<dyn CommandRunner>,
}
impl PerforceProvider {
    pub fn new(executable: String, runner: Arc<dyn CommandRunner>) -> Self {
        Self { executable, runner }
    }
    fn run(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        cancel.check()?;
        self.runner.run(
            &CommandSpec::new(self.executable.clone(), root, args),
            cancel,
        )
    }
    fn req(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        self.run(root, args, cancel)?.ensure()
    }
    fn info(&self, path: &Path, cancel: &CancelToken) -> Result<HashMap<String, String>> {
        let r = self.req(path, vec!["-ztag".into(), "info".into()], cancel)?;
        tagged(&r.stdout)
            .into_iter()
            .next()
            .ok_or_else(|| VcsError("Perforce 작업 공간 정보를 읽을 수 없습니다.".into()))
    }
    fn root(&self, path: &Path, cancel: &CancelToken) -> Result<PathBuf> {
        let selected = absolute(path)?;
        let fields = self.info(&selected, cancel)?;
        let root = find(&fields, &["clientRoot", "client root", "root"]).ok_or_else(|| {
            VcsError("Perforce client root 정보가 없습니다. 작업 공간을 설정하세요.".into())
        })?;
        let root = PathBuf::from(root);
        if !root.is_absolute() || !within(&root, &selected) {
            return Err(VcsError(
                "선택 폴더가 활성 Perforce 작업 공간 밖에 있습니다.".into(),
            ));
        }
        if !same_path(&selected, &root) {
            let from_root = self.info(&root, cancel)?;
            for key in ["clientName", "serverAddress"] {
                if find(&fields, &[key]) != find(&from_root, &[key]) {
                    return Err(VcsError(
                        "Perforce 작업 공간 설정이 선택 폴더와 루트에서 달라집니다.".into(),
                    ));
                }
            }
        }
        Ok(root)
    }
    fn path(&self, root: &Path, path: &str) -> Result<PathBuf> {
        if path.contains(['@', '#', '%', '*']) || path.contains("...") {
            return Err(VcsError(
                "Perforce 경로에 revision 또는 wildcard 문자가 있습니다.".into(),
            ));
        }
        let relative = text::validate_relative(root, path)?;
        let full = absolute(&root.join(relative))?;
        if !within(root, &full) {
            return Err(VcsError(
                "선택 경로가 Perforce 작업 공간 밖에 있습니다.".into(),
            ));
        }
        Ok(full)
    }
    fn metadata(
        &self,
        root: &Path,
        local: &Path,
        display: &str,
        cancel: &CancelToken,
    ) -> Result<HashMap<String, String>> {
        let r = self.req(
            root,
            vec![
                "-ztag".into(),
                "fstat".into(),
                "-T".into(),
                "depotFile,clientFile,path,haveRev,headType,type".into(),
                local.to_string_lossy().into_owned(),
            ],
            cancel,
        )?;
        let records = tagged(&r.stdout);
        let rec = records.first().ok_or_else(|| {
            VcsError(format!(
                "Perforce에서 '{display}' depot mapping을 찾지 못했습니다."
            ))
        })?;
        if let Some(mapped) = find(rec, &["clientFile", "path"]) {
            if Path::new(&mapped).is_absolute() && !same_path(Path::new(&mapped), local) {
                return Err(VcsError(
                    "Perforce selected file mapping이 다른 경로를 가리킵니다.".into(),
                ));
            }
        }
        Ok(rec.clone())
    }
}
impl VcsProvider for PerforceProvider {
    fn kind(&self) -> VcsKind {
        VcsKind::Perforce
    }
    fn executable(&self) -> &str {
        &self.executable
    }
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            stage: true,
            unstage: true,
            update: true,
            blame: true,
            comparison: true,
            history: true,
            ..Default::default()
        }
    }
    fn repository(&self, path: &Path, cancel: &CancelToken) -> Result<RepositoryInfo> {
        let selected = absolute(path)?;
        let fields = self.info(&selected, cancel)?;
        let root = PathBuf::from(
            find(&fields, &["clientRoot", "client root", "root"])
                .filter(|s| s != "*unknown*")
                .ok_or_else(|| VcsError("P4 client root을 반환하지 않았습니다.".into()))?,
        );
        if !within(&root, &selected) {
            return Err(VcsError(
                "선택 폴더가 Perforce workspace root 밖입니다.".into(),
            ));
        }
        let client = find(&fields, &["clientName", "client name", "client"])
            .unwrap_or_else(|| "Perforce workspace".into());
        let server = find(&fields, &["serverAddress", "server address"]).unwrap_or_default();
        if !same_path(&root, &selected) {
            let r = self.info(&root, cancel)?;
            if find(&r, &["clientName", "client name", "client"]).as_deref() != Some(&client)
                || find(&r, &["serverAddress", "server address"]).unwrap_or_default() != server
            {
                return Err(VcsError(
                    "선택 경로와 workspace root에서 Perforce 설정이 달라집니다.".into(),
                ));
            }
        }
        Ok(RepositoryInfo {
            kind: VcsKind::Perforce,
            root,
            branch: client,
            summary: server,
        })
    }
    fn status(&self, root: &Path, cancel: &CancelToken) -> Result<Vec<FileChange>> {
        let root = self.root(root, cancel)?;
        let r = self.req(&root, vec!["-ztag".into(), "status".into()], cancel)?;
        let recs = tagged(&r.stdout);
        if r.stdout.trim().is_empty() && !r.success() {
            return Err(VcsError(r.output()));
        }
        if !r.stdout.trim().is_empty() && recs.is_empty() {
            return Err(VcsError(
                "Perforce status 출력 형식을 인식할 수 없어 clean으로 표시하지 않았습니다.".into(),
            ));
        }
        let mut rows = BTreeMap::new();
        for rec in recs {
            let f = find(&rec, &["clientFile", "localFile", "path", "depotFile"])
                .ok_or_else(|| VcsError("Perforce status 경로 필드가 없습니다.".into()))?;
            let action = find(&rec, &["action", "status"])
                .ok_or_else(|| VcsError("Perforce status 상태 필드가 없습니다.".into()))?;
            let full = PathBuf::from(&f);
            let full = if full.is_absolute() {
                full
            } else {
                root.join(full)
            };
            if !within(&root, &full) {
                return Err(VcsError(
                    "Perforce status가 workspace 밖 경로를 반환했습니다.".into(),
                ));
            }
            let path = full
                .strip_prefix(&root)
                .map_err(|_| VcsError("Perforce 경로가 workspace 밖입니다.".into()))?
                .to_string_lossy()
                .replace('\\', "/");
            rows.insert(
                path.clone(),
                FileChange {
                    path,
                    status: action.clone(),
                    worktree_status: action,
                    ..Default::default()
                },
            );
        }
        Ok(rows.into_values().collect())
    }
    fn diff(
        &self,
        root: &Path,
        file: Option<&FileChange>,
        _staged: bool,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root, cancel)?;
        let mut args = vec!["diff".into(), "-f".into(), "-du".into()];
        if let Some(f) = file {
            args.push(self.path(&root, &f.path)?.to_string_lossy().into_owned())
        }
        Ok(self.req(&root, args, cancel)?.stdout)
    }
    fn comparison(
        &self,
        root: &Path,
        file: &FileChange,
        _staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        let root = self.root(root, cancel)?;
        let local = self.path(&root, &file.path)?;
        let mut left = None;
        let mut label = "P4 (파일 없음)".to_string();
        if !file.status.eq_ignore_ascii_case("add") {
            let m = self.metadata(&root, &local, &file.path, cancel)?;
            let depot = find(&m, &["depotFile"])
                .ok_or_else(|| VcsError("P4 depot path가 없습니다.".into()))?;
            let rev = require_rev(find(&m, &["haveRev"]))?;
            ensure_text(
                find(&m, &["headType", "type"]).as_deref().unwrap_or(""),
                &file.path,
            )?;
            let p = self.req(
                &root,
                vec!["print".into(), "-q".into(), format!("{depot}#{rev}")],
                cancel,
            )?;
            left = Some(text::decode_snapshot(p.stdout.as_bytes(), &file.path)?);
            label = format!("P4 #{rev}")
        }
        let full = root.join(&self.path(&root, &file.path)?);
        let right = if full.is_file() {
            Some(text::read_snapshot(&full)?.text)
        } else {
            None
        };
        Ok(FileComparison {
            path: file.path.clone(),
            left_label: label,
            right_label: if right.is_some() {
                "작업 공간".into()
            } else {
                "작업 공간 (삭제됨)".into()
            },
            left_text: left.unwrap_or_default(),
            right_text: right.unwrap_or_default(),
        })
    }
    fn blame(&self, root: &Path, file: &FileChange, cancel: &CancelToken) -> Result<BlameResult> {
        let root = self.root(root, cancel)?;
        let local = self.path(&root, &file.path)?;
        let m = self.metadata(&root, &local, &file.path, cancel)?;
        let depot =
            find(&m, &["depotFile"]).ok_or_else(|| VcsError("P4 depot path가 없습니다.".into()))?;
        let rev = require_rev(find(&m, &["haveRev"]))?;
        ensure_text(
            find(&m, &["headType", "type"]).as_deref().unwrap_or(""),
            &file.path,
        )?;
        let p = self.req(
            &root,
            vec!["print".into(), "-q".into(), format!("{depot}#{rev}")],
            cancel,
        )?;
        let content = text::decode_snapshot(p.stdout.as_bytes(), &file.path)?;
        let source: Vec<_> = content.lines().collect();
        if source.len() > 50_000 {
            return Err(VcsError(
                "50,000줄을 초과해 Blame을 표시할 수 없습니다.".into(),
            ));
        }
        let a = self.req(
            &root,
            vec![
                "annotate".into(),
                "-u".into(),
                "-q".into(),
                format!("{depot}#{rev}"),
            ],
            cancel,
        )?;
        let lines: Vec<_> = a.stdout.lines().collect();
        if lines.len() != source.len() {
            return Err(VcsError(
                "Perforce annotate 줄 수와 have 파일이 일치하지 않습니다.".into(),
            ));
        }
        let mut out = Vec::new();
        for (i, line) in lines.iter().enumerate() {
            let Some((revision, annotation)) = line.split_once(':') else {
                return Err(VcsError(format!(
                    "Perforce annotate 형식을 인식할 수 없습니다: {line}"
                )));
            };
            let mut fields = annotation.split_whitespace();
            let author = fields.next().unwrap_or_default();
            let date = fields.next().unwrap_or_default();
            if revision.is_empty()
                || !revision.bytes().all(|b| b.is_ascii_digit())
                || date.len() != 10
                || date.as_bytes()[4] != b'/'
                || date.as_bytes()[7] != b'/'
                || !date
                    .bytes()
                    .enumerate()
                    .all(|(n, b)| matches!(n, 4 | 7) || b.is_ascii_digit())
            {
                return Err(VcsError(format!(
                    "Perforce annotate 형식을 인식할 수 없습니다: {line}"
                )));
            }
            out.push(BlameLine {
                line_number: i + 1,
                revision: revision.into(),
                author: author.into(),
                date: date.into(),
                text: source[i].into(),
            })
        }
        Ok(BlameResult {
            path: file.path.clone(),
            revision_label: format!("P4 #{rev}"),
            lines: out,
        })
    }
    fn history(
        &self,
        root: &Path,
        _scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        let root = self.root(root, cancel)?;
        let pattern = root.join("...").to_string_lossy().into_owned();
        let r = self.req(
            &root,
            vec![
                "-ztag".into(),
                "changes".into(),
                "-s".into(),
                "submitted".into(),
                "-l".into(),
                "-m".into(),
                "50".into(),
                pattern,
            ],
            cancel,
        )?;
        let mut entries = Vec::new();
        for f in tagged(&r.stdout) {
            let id = find(&f, &["change", "change number"]).unwrap_or_default();
            if id.is_empty() {
                continue;
            }
            let date = find(&f, &["time", "date"])
                .and_then(|s| s.parse::<i64>().ok())
                .and_then(chrono_unix)
                .unwrap_or_default();
            entries.push(HistoryEntry {
                id,
                author: find(&f, &["user"]).unwrap_or_default(),
                date,
                message: find(&f, &["desc", "description"]).unwrap_or_default(),
                ..Default::default()
            })
        }
        Ok(HistorySnapshot {
            entries,
            scope_label: "현재 Perforce 작업 공간 경로의 제출된 변경 목록".into(),
            ..Default::default()
        })
    }
    fn history_details(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        let root = self.root(root, cancel)?;
        let rev = require_rev(Some(revision.into()))?;
        let result = self.req(
            &root,
            vec![
                "-Mj".into(),
                "-ztag".into(),
                "describe".into(),
                "-s".into(),
                rev.clone(),
            ],
            cancel,
        )?;
        let data = parse_p4_describe(&result.stdout, &rev)?;
        let seconds = scalar(&data, "time")
            .and_then(|s| s.parse::<i64>().ok())
            .ok_or_else(history_format)?;
        let date = chrono_unix(seconds).ok_or_else(history_format)?;
        let user = data
            .get("user")
            .and_then(Value::as_str)
            .ok_or_else(history_format)?
            .to_owned();
        let message = data
            .get("desc")
            .and_then(Value::as_str)
            .ok_or_else(history_format)?
            .to_owned();
        let object = data.as_object().ok_or_else(history_format)?;
        let mut files = Vec::new();
        let mut seen = HashSet::new();
        for (key, value) in object {
            let Some(suffix) = key.strip_prefix("depotFile") else {
                continue;
            };
            if suffix.is_empty() || !suffix.bytes().all(|b| b.is_ascii_digit()) {
                return Err(history_format());
            }
            let path = value.as_str().ok_or_else(history_format)?;
            validate_depot(path)?;
            if !seen.insert(path.to_owned()) {
                return Err(history_format());
            }
            let file_revision = data
                .get(&format!("rev{suffix}"))
                .and_then(scalar_value)
                .ok_or_else(history_format)?;
            require_rev(Some(file_revision))?;
            let action = data
                .get(&format!("action{suffix}"))
                .and_then(Value::as_str)
                .ok_or_else(history_format)?;
            let status = match action {
                "add" | "move/add" => "added",
                "delete" | "move/delete" => "deleted",
                "branch" => "copied",
                "purge" => "purged",
                "edit" | "integrate" => "modified",
                _ => return Err(history_format()),
            };
            data.get(&format!("type{suffix}"))
                .and_then(Value::as_str)
                .ok_or_else(history_format)?;
            files.push(FileChange {
                path: path.into(),
                status: status.into(),
                ..Default::default()
            });
        }
        let entry = HistoryEntry {
            id: rev,
            author: user,
            date,
            message,
            ..Default::default()
        };
        Ok(HistoryDetails {
            entry,
            files,
            comparison_label: "제출된 변경 목록 · 파일별 이전 리비전과 비교".into(),
        })
    }
    fn history_diff(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root, cancel)?;
        let rev = require_rev(Some(revision.into()))?;
        let result = self.req(
            &root,
            vec![
                "-Mj".into(),
                "-ztag".into(),
                "describe".into(),
                "-s".into(),
                rev.clone(),
            ],
            cancel,
        )?;
        let data = parse_p4_describe(&result.stdout, &rev)?;
        let Some(selected) = file else {
            return self
                .req(
                    &root,
                    vec!["describe".into(), "-du".into(), "-a".into(), rev],
                    cancel,
                )
                .map(|x| x.stdout);
        };
        validate_depot(&selected.path)?;
        for (key, value) in data.as_object().ok_or_else(history_format)? {
            let Some(suffix) = key.strip_prefix("depotFile") else {
                continue;
            };
            if value.as_str() != Some(&selected.path) {
                continue;
            }
            let file_revision = data
                .get(&format!("rev{suffix}"))
                .and_then(scalar_value)
                .ok_or_else(history_format)?;
            let current = require_rev(Some(file_revision))?;
            let action = data
                .get(&format!("action{suffix}"))
                .and_then(Value::as_str)
                .ok_or_else(history_format)?;
            let file_type = data
                .get(&format!("type{suffix}"))
                .and_then(Value::as_str)
                .ok_or_else(history_format)?;
            if action == "purge" {
                return Ok(
                    "이 리비전의 파일 내용은 Perforce 서버에서 제거되어 비교할 수 없습니다.".into(),
                );
            }
            ensure_text(file_type, &selected.path)?;
            let added = matches!(action, "add" | "move/add" | "branch");
            let deleted = matches!(action, "delete" | "move/delete");
            if added || deleted {
                let previous = current.parse::<u64>().unwrap_or(0).saturating_sub(1);
                if deleted && previous == 0 {
                    return Err(history_format());
                }
                let spec = format!(
                    "{}#{}",
                    selected.path,
                    if deleted {
                        previous.to_string()
                    } else {
                        current.clone()
                    }
                );
                let printed = self.req(
                    &root,
                    vec!["print".into(), "-q".into(), spec.clone()],
                    cancel,
                )?;
                let content = text::decode_snapshot(printed.stdout.as_bytes(), &selected.path)?;
                let lines: Vec<_> = content.lines().collect();
                let old_count = if added { 0 } else { lines.len() };
                let new_count = if deleted { 0 } else { lines.len() };
                let left = if added { "/dev/null" } else { &spec };
                let right = if deleted { "/dev/null" } else { &spec };
                let mut patch = format!(
                    "--- {left}\n+++ {right}\n@@ -{},{} +{},{} @@\n",
                    if old_count == 0 { 0 } else { 1 },
                    old_count,
                    if new_count == 0 { 0 } else { 1 },
                    new_count
                );
                for line in lines {
                    patch.push(if added { '+' } else { '-' });
                    patch.push_str(line);
                    patch.push('\n');
                }
                if !content.is_empty() && !content.ends_with(['\n', '\r']) {
                    patch.push_str("\\ No newline at end of file\n")
                }
                return Ok(patch);
            }
            if !matches!(action, "edit" | "integrate") {
                return Err(history_format());
            }
            let previous = current.parse::<u64>().unwrap_or(0).saturating_sub(1);
            if previous == 0 {
                return Err(history_format());
            }
            return self
                .req(
                    &root,
                    vec![
                        "diff2".into(),
                        "-du".into(),
                        format!("{}#{previous}", selected.path),
                        format!("{}#{current}", selected.path),
                    ],
                    cancel,
                )
                .map(|x| x.stdout);
        }
        Err(VcsError(
            "선택한 파일은 이 Perforce 변경 목록에 없습니다.".into(),
        ))
    }
    fn execute(&self, request: &VcsRequest, cancel: &CancelToken) -> Result<CommandResult> {
        let root = self.root(&request.working_directory, cancel)?;
        let paths = if request.paths.is_empty() {
            vec![format!("{}...", root.display())]
        } else {
            request
                .paths
                .iter()
                .map(|p| {
                    self.path(&root, p)
                        .map(|p| p.to_string_lossy().into_owned())
                })
                .collect::<Result<Vec<_>>>()?
        };
        match request.operation {
            Operation::Stage => {
                if request.paths.is_empty() {
                    return Err(VcsError("P4 reconcile할 경로를 선택하세요.".into()));
                }
                let mut a = vec!["reconcile".into()];
                a.extend(paths);
                self.req(&root, a, cancel)
            }
            Operation::Unstage => {
                if request.paths.is_empty() {
                    return Err(VcsError("P4 revert할 경로를 선택하세요.".into()));
                }
                let mut a = vec!["revert".into(), "-k".into()];
                a.extend(paths);
                self.req(&root, a, cancel)
            }
            Operation::Update => {
                let mut a = vec!["sync".into()];
                a.extend(paths);
                self.req(&root, a, cancel)
            }
            _ => Err(VcsError(format!(
                "Perforce에서는 {:?} 작업을 지원하지 않습니다.",
                request.operation
            ))),
        }
    }
}
fn absolute(p: &Path) -> Result<PathBuf> {
    let input = if p.is_absolute() {
        p.to_path_buf()
    } else {
        std::env::current_dir()?.join(p)
    };
    let mut normalized = PathBuf::new();
    for component in input.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                if !normalized.pop() {
                    normalized.push(component.as_os_str())
                }
            }
            _ => normalized.push(component.as_os_str()),
        }
    }
    Ok(normalized)
}
fn within(root: &Path, p: &Path) -> bool {
    let r = root
        .to_string_lossy()
        .replace('/', "\\")
        .trim_end_matches('\\')
        .to_lowercase();
    let p = p.to_string_lossy().replace('/', "\\").to_lowercase();
    p == r || p.starts_with(&(r + "\\"))
}
fn same_path(a: &Path, b: &Path) -> bool {
    a.to_string_lossy()
        .replace('/', "\\")
        .eq_ignore_ascii_case(&b.to_string_lossy().replace('/', "\\"))
}
fn find(m: &HashMap<String, String>, keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|k| m.get(*k).cloned())
}
fn tagged(s: &str) -> Vec<HashMap<String, String>> {
    let mut rows: Vec<HashMap<String, String>> = vec![];
    for l in s.lines().filter_map(|l| l.strip_prefix("... ")) {
        let Some((k, v)) = l.split_once(' ') else {
            continue;
        };
        let new = matches!(
            k,
            "change" | "clientFile" | "depotFile" | "path" | "localFile"
        ) && rows.last().is_some_and(|m| m.contains_key(k));
        if rows.is_empty() || new {
            rows.push(HashMap::new())
        }
        rows.last_mut()
            .unwrap()
            .entry(k.into())
            .and_modify(|x| {
                x.push('\n');
                x.push_str(v)
            })
            .or_insert(v.into());
    }
    rows
}
fn require_rev(v: Option<String>) -> Result<String> {
    let s = v.unwrap_or_default();
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_digit()) || s.parse::<u64>().unwrap_or(0) == 0
    {
        return Err(VcsError(
            "Perforce는 양의 숫자 revision을 반환해야 합니다.".into(),
        ));
    }
    Ok(s)
}
fn ensure_text(filetype: &str, path: &str) -> Result<()> {
    if ["binary", "ubinary", "apple", "resource", "symlink"]
        .iter()
        .any(|t| filetype.to_ascii_lowercase().starts_with(t))
    {
        return Err(VcsError(format!(
            "'{path}'은 Perforce 텍스트 파일이 아닙니다."
        )));
    }
    Ok(())
}
fn validate_depot(path: &str) -> Result<()> {
    if !path.starts_with("//")
        || path.len() < 4
        || path.contains(['@', '#', '*', '\\'])
        || path.contains("...")
        || path.split('/').any(|x| x == ".." || x == ".")
    {
        return Err(VcsError("Perforce depot 경로가 올바르지 않습니다.".into()));
    }
    Ok(())
}
fn parse_p4_describe(s: &str, revision: &str) -> Result<Value> {
    let mut found = None;
    for line in s.lines().filter(|l| !l.trim().is_empty()) {
        let v: Value = serde_json::from_str(line).map_err(|_| history_format())?;
        if v.get("code").and_then(Value::as_str) == Some("error") {
            return Err(VcsError(
                v.get("data")
                    .and_then(Value::as_str)
                    .unwrap_or("P4 describe 실패")
                    .into(),
            ));
        }
        if found.is_some()
            || scalar(&v, "change").as_deref() != Some(revision)
            || scalar(&v, "status").as_deref() != Some("submitted")
        {
            return Err(history_format());
        }
        found = Some(v)
    }
    found.ok_or_else(history_format)
}
fn history_format() -> VcsError {
    VcsError("Perforce 커밋 상세 JSON 형식을 인식할 수 없습니다.".into())
}
fn chrono_unix(t: i64) -> Option<String> {
    let days = t.div_euclid(86_400);
    let day_seconds = t.rem_euclid(86_400);
    let z = days.checked_add(719_468)?;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp + if mp < 10 { 3 } else { -9 };
    if month <= 2 {
        year += 1
    }
    Some(format!(
        "{year:04}-{month:02}-{day:02} {:02}:{:02}:{:02} UTC",
        day_seconds / 3600,
        (day_seconds % 3600) / 60,
        day_seconds % 60
    ))
}

fn scalar(v: &Value, key: &str) -> Option<String> {
    scalar_value(v.get(key)?)
}
fn scalar_value(value: &Value) -> Option<String> {
    match value {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}
