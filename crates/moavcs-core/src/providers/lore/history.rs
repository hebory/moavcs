use super::inspection::string;
use super::*;
use std::time::{Duration, UNIX_EPOCH};
const ZERO: &str = "0000000000000000000000000000000000000000000000000000000000000000";
#[derive(Clone)]
struct RevisionInfo {
    signature: String,
    number: u64,
    parent: String,
    merge_parent: String,
    repository: String,
    entry: HistoryEntry,
    files: Vec<FileChange>,
}
impl LoreProvider {
    pub(super) fn history_snapshot(
        &self,
        root: &Path,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        let root = self.root(root)?;
        let events = self.json_events(
            &root,
            &["--local".into(), "history".into(), "50".into()],
            true,
            cancel,
        )?;
        let mut entries = Vec::new();
        let (mut current, mut metadata) = (None::<HistoryEntry>, BTreeMap::<String, Value>::new());
        for e in events {
            cancel.check()?;
            match e.get("tagName").and_then(Value::as_str).unwrap_or("") {
                "revisionHistoryEntry" => {
                    if let Some(mut old) = current.take() {
                        apply_meta(&mut old, &metadata);
                        entries.push(old)
                    }
                    metadata.clear();
                    let d = &e["data"];
                    let id = d["revisionNumber"]
                        .as_u64()
                        .filter(|x| *x > 0)
                        .ok_or_else(|| VcsError("Lore 이력 번호가 잘못되었습니다.".into()))?;
                    let signature = string(d, "revision")?;
                    check_sig(&signature)?;
                    current = Some(HistoryEntry {
                        id: id.to_string(),
                        ..Default::default()
                    })
                }
                "metadata" if current.is_some() => {
                    let d = &e["data"];
                    metadata.insert(
                        string(d, "key")?,
                        d.get("value")
                            .cloned()
                            .ok_or_else(|| VcsError("Lore metadata value가 없습니다.".into()))?,
                    );
                }
                _ => {}
            }
        }
        if let Some(mut old) = current {
            apply_meta(&mut old, &metadata);
            entries.push(old)
        }
        Ok(HistorySnapshot {
            entries,
            scope_label: "Lore 로컬 리비전 이력 (최대 50개)".into(),
            ..Default::default()
        })
    }
    pub(super) fn history_details_impl(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        let i = self.revision_info(root, revision, cancel)?;
        Ok(HistoryDetails {
            entry: i.entry,
            files: i.files,
            comparison_label: if i.parent == ZERO {
                "최초 커밋 · 빈 저장소 기준".into()
            } else if i.merge_parent != ZERO {
                "첫 번째 부모와 비교 · 병합 커밋".into()
            } else {
                "이전 커밋과 비교".into()
            },
        })
    }
    pub(super) fn history_diff_impl(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root)?;
        let i = self.revision_info(&root, revision, cancel)?;
        let selected: Vec<String> = if let Some(f) = file {
            let path = self.safe_path(&root, &f.path)?;
            let h = i.files.iter().find(|x| x.path == path).ok_or_else(|| {
                VcsError("선택한 파일은 이 Lore 리비전에 포함되지 않습니다.".into())
            })?;
            let mut v = Vec::new();
            if let Some(old) = &h.original_path {
                v.push(self.safe_path(&root, old)?)
            }
            v.push(h.path.clone());
            v
        } else {
            i.files
                .iter()
                .flat_map(|f| {
                    f.original_path
                        .iter()
                        .cloned()
                        .chain(std::iter::once(f.path.clone()))
                })
                .map(|p| self.safe_path(&root, &p))
                .collect::<Result<Vec<_>>>()?
        };
        let mut unique = selected;
        unique.sort();
        unique.dedup();
        if unique.is_empty() {
            return Ok("이 커밋에는 변경된 파일이 없습니다.".into());
        }
        if i.parent == ZERO {
            let rev = super::inspection::Revision::new(i.signature, i.number, i.repository);
            let mut out = String::new();
            for p in unique {
                let Some(content) = self.snapshot(&root, &p, &rev, cancel)? else {
                    continue;
                };
                out.push_str(&format_initial(&p, &content));
                if out.len() > 4 * 1024 * 1024 {
                    return Err("Lore history diff가 4 MiB 제한을 초과했습니다.".into());
                }
            }
            return Ok(out);
        }
        let mut a = vec![
            "--local".into(),
            "--no-pager".into(),
            "diff".into(),
            "--source".into(),
            i.parent,
            "--target".into(),
            i.signature,
            "--".into(),
        ];
        a.extend(unique.into_iter().map(|p| format!("./{p}")));
        Ok(self.req(&root, a, cancel)?.stdout)
    }
    fn revision_info(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<RevisionInfo> {
        let root = self.root(root)?;
        let n = revision
            .parse::<u64>()
            .ok()
            .filter(|n| *n > 0)
            .ok_or_else(|| VcsError("Lore 리비전 번호 형식이 잘못되었습니다.".into()))?;
        let events = self.json_events(
            &root,
            &[
                "--local".into(),
                "revision".into(),
                "info".into(),
                n.to_string(),
                "--delta".into(),
            ],
            true,
            cancel,
        )?;
        let (mut info, mut meta, mut files) =
            (None::<Value>, BTreeMap::<String, Value>::new(), Vec::new());
        for e in events {
            cancel.check()?;
            let tag = e["tagName"].as_str().unwrap_or("");
            let d = &e["data"];
            match tag {
                "revisionInfo" => {
                    if info.replace(d.clone()).is_some() {
                        return Err("Lore revisionInfo 중복 이벤트".into());
                    }
                }
                "metadata" => {
                    meta.insert(string(d, "key")?, d["value"].clone());
                }
                "revisionInfoDelta" => {
                    if d["flagFile"].as_bool() != Some(true) {
                        continue;
                    }
                    let path = self.safe_path(&root, &string(d, "path")?)?;
                    let action = string(d, "action")?;
                    let original = d
                        .get("fromPath")
                        .and_then(Value::as_str)
                        .filter(|s| !s.is_empty())
                        .map(|s| self.safe_path(&root, s))
                        .transpose()?;
                    let modified = d["flagModify"].as_bool() == Some(true);
                    let (status, index) = match action.as_str() {
                        "add" => ("added", "A"),
                        "delete" => ("deleted", "D"),
                        "move" => ("moved", "V"),
                        "copy" => ("copied", "C"),
                        "keep" if modified => ("modified", "M"),
                        "keep" => ("changed", "M"),
                        _ => {
                            return Err("Lore revision delta 동작 형식을 인식할 수 없습니다.".into())
                        }
                    };
                    files.push(FileChange {
                        path,
                        status: status.into(),
                        index_status: index.into(),
                        original_path: original,
                        ..Default::default()
                    })
                }
                _ => {}
            }
        }
        let d = info.ok_or_else(|| VcsError("Lore 리비전 상세 정보를 찾을 수 없습니다.".into()))?;
        let sig = string(&d, "revision")?;
        check_sig(&sig)?;
        if d["revisionNumber"].as_u64() != Some(n) {
            return Err("Lore 반환 리비전 번호가 요청과 다릅니다.".into());
        }
        let parents = d["parent"]
            .as_array()
            .filter(|x| x.len() == 2)
            .ok_or_else(|| VcsError("Lore parent 배열 형식이 잘못되었습니다.".into()))?;
        let parent = parents[0].as_str().unwrap_or("").to_owned();
        let merge = parents[1].as_str().unwrap_or("").to_owned();
        check_sig(&parent)?;
        check_sig(&merge)?;
        let repo = string(&d, "repository")?;
        if repo.len() != 32 || !repo.bytes().all(|c| c.is_ascii_hexdigit()) {
            return Err("Lore repository ID가 잘못되었습니다.".into());
        }
        let mut entry = HistoryEntry {
            id: n.to_string(),
            ..Default::default()
        };
        apply_meta(&mut entry, &meta);
        Ok(RevisionInfo {
            signature: sig,
            number: n,
            parent,
            merge_parent: merge,
            repository: repo,
            entry,
            files,
        })
    }
}

fn apply_meta(e: &mut HistoryEntry, m: &BTreeMap<String, Value>) {
    let get = |k: &str| {
        m.get(k)
            .and_then(|v| {
                if v["tagName"] == "string" {
                    v["data"].as_str()
                } else {
                    None
                }
            })
            .unwrap_or("")
    };
    e.message = get("message").to_owned();
    e.author = {
        let c = get("committed-by");
        if c.is_empty() {
            get("created-by")
        } else {
            c
        }
    }
    .to_owned();
    if let Some(ms) = m.get("timestamp").and_then(|v| {
        if v["tagName"] == "numeric" {
            v["data"].as_i64()
        } else {
            None
        }
    }) {
        e.date = UNIX_EPOCH
            .checked_add(Duration::from_millis(ms.max(0) as u64))
            .map(|t| format!("{:?}", t.duration_since(UNIX_EPOCH).ok()))
            .unwrap_or_default();
    }
}
fn check_sig(s: &str) -> Result<()> {
    if s.len() != 64 || !s.bytes().all(|b| b.is_ascii_hexdigit()) {
        Err("Lore SHA-256 리비전 서명이 잘못되었습니다.".into())
    } else {
        Ok(())
    }
}
fn format_initial(path: &str, content: &str) -> String {
    let mut out = format!(
        "--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,{} @@\n",
        content.lines().count()
    );
    for l in content.lines() {
        out.push('+');
        out.push_str(l);
        out.push('\n')
    }
    if !content.is_empty() && !content.ends_with(['\n', '\r']) {
        out.push_str("\\ No newline at end of file\n")
    }
    out
}
