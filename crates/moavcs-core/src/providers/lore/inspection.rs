use super::*;

#[derive(Clone)]
pub(super) struct Revision {
    pub(super) signature: String,
    pub(super) number: u64,
    pub(super) repository: String,
}
impl Revision {
    pub(super) fn new(signature: String, number: u64, repository: String) -> Self {
        Self {
            signature,
            number,
            repository,
        }
    }
}
#[derive(Clone)]
struct Range {
    first: usize,
    last: usize,
    repo: String,
    revision: String,
    outcome: String,
}
impl LoreProvider {
    pub(super) fn comparison_impl(
        &self,
        root: &Path,
        file: &FileChange,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        if staged {
            return Err("Lore는 Git 전용 스테이지 비교를 지원하지 않습니다.".into());
        }
        let root = self.root(root)?;
        let path = self.safe_path(&root, &file.path)?;
        let baseline = file
            .original_path
            .as_deref()
            .filter(|_| {
                matches!(file.index_status.as_str(), "V" | "C")
                    || matches!(file.worktree_status.as_str(), "V" | "C")
            })
            .map(|p| self.safe_path(&root, p))
            .transpose()?
            .unwrap_or_else(|| path.clone());
        let revision = self.revision(&root, cancel)?;
        let left = self.snapshot(&root, &baseline, &revision, cancel)?;
        let full = root.join(&path);
        if full.is_dir() {
            return Err("폴더 대신 비교할 파일을 선택하세요.".into());
        }
        let right = if full.exists() {
            Some(text::read_snapshot(&full)?.text)
        } else {
            None
        };
        if left.is_none() && right.is_none() {
            return Err("파일이 현재 Lore 리비전과 작업 폴더에 모두 없습니다.".into());
        }
        Ok(FileComparison {
            path: file.path.clone(),
            left_label: format!(
                "Lore r{}{}",
                revision.number,
                if left.is_none() {
                    " (파일 없음)"
                } else {
                    ""
                }
            ),
            right_label: if right.is_some() {
                "작업 파일".into()
            } else {
                "작업 파일 (삭제됨)".into()
            },
            left_text: left.unwrap_or_default(),
            right_text: right.unwrap_or_default(),
        })
    }
    pub(super) fn blame_impl(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<BlameResult> {
        let root = self.root(root)?;
        let path = file
            .original_path
            .as_deref()
            .filter(|_| {
                matches!(file.index_status.as_str(), "V" | "C")
                    || matches!(file.worktree_status.as_str(), "V" | "C")
            })
            .unwrap_or(&file.path);
        let path = self.safe_path(&root, path)?;
        let revision = self.revision(&root, cancel)?;
        let content = self
            .snapshot(&root, &path, &revision, cancel)?
            .ok_or_else(|| {
                VcsError(format!(
                    "'{path}'은 현재 Lore 리비전에 없어 blame할 수 없습니다."
                ))
            })?;
        let source: Vec<&str> = content.lines().collect();
        if source.len() > text::MAX_LINES {
            return Err("Lore blame은 파일당 50,000줄까지 지원합니다.".into());
        }
        let args = vec![
            "file".into(),
            "audit".into(),
            "--revision".into(),
            revision.signature.clone(),
            "--max-revisions".into(),
            "0".into(),
            "--no-ignore-space-at-eol".into(),
            "--".into(),
            format!("./{path}"),
        ];
        let events = self.json_events(&root, &args, true, cancel)?;
        let mut ranges = Vec::new();
        for e in events
            .iter()
            .filter(|e| e.get("tagName").and_then(Value::as_str) == Some("fileAudit"))
        {
            let d = &e["data"];
            let first = number(d, "lineFirst")?;
            let last = number(d, "lineLast")?;
            let outcome = string(d, "outcome")?;
            let repo = string(d, "repository")?;
            let rev = string(d, "revision")?;
            if repo.len() != 32
                || !repo.bytes().all(|c| c.is_ascii_hexdigit())
                || first == 0
                || last < first
                || last > source.len()
            {
                return Err("Lore audit 줄 범위 또는 저장소 ID가 잘못되었습니다.".into());
            }
            if outcome == "attributed" {
                if rev.len() != 64
                    || !rev.bytes().all(|c| c.is_ascii_hexdigit())
                    || rev.bytes().all(|c| c == b'0')
                {
                    return Err("Lore audit 리비전 서명이 잘못되었습니다.".into());
                }
            } else if ![
                "uncommitted",
                "budget-exhausted",
                "obliterated",
                "binary-in-history",
                "link-unavailable",
                "content-unavailable",
                "unresolved",
            ]
            .contains(&outcome.as_str())
            {
                return Err(format!("알 수 없는 Lore audit 결과: {outcome}").into());
            }
            ranges.push(Range {
                first,
                last,
                repo,
                revision: rev,
                outcome,
            })
        }
        ranges.sort_by_key(|r| r.first);
        let mut next = 1;
        for r in &ranges {
            if r.first != next {
                return Err("Lore audit 결과에 누락되거나 겹치는 줄이 있습니다.".into());
            }
            next = r.last + 1
        }
        if next != source.len() + 1 {
            return Err("Lore audit 결과가 파일의 모든 줄을 포함하지 않습니다.".into());
        }
        let mut attrs = std::collections::HashMap::new();
        for r in ranges.iter().filter(|r| {
            r.outcome == "attributed" && r.repo.eq_ignore_ascii_case(&revision.repository)
        }) {
            cancel.check()?;
            let events = self.json_events(
                &root,
                &[
                    "revision".into(),
                    "metadata".into(),
                    "get".into(),
                    "--revision".into(),
                    r.revision.clone(),
                ],
                true,
                cancel,
            )?;
            attrs.insert(r.revision.clone(), attribution(&events)?);
        }
        let mut lines = vec![None; source.len()];
        for r in ranges {
            let (author, date) = if r.outcome != "attributed" {
                (format!("이력 확인 불가 ({})", r.outcome), String::new())
            } else if !r.repo.eq_ignore_ascii_case(&revision.repository) {
                (
                    "연결 저장소 이력 (작성자 조회 미지원)".into(),
                    String::new(),
                )
            } else {
                attrs
                    .get(&r.revision)
                    .cloned()
                    .unwrap_or(("작성자 정보 없음".into(), String::new()))
            };
            for n in r.first..=r.last {
                lines[n - 1] = Some(BlameLine {
                    line_number: n,
                    revision: if r.outcome == "attributed" {
                        r.revision.clone()
                    } else {
                        String::new()
                    },
                    author: author.clone(),
                    date: date.clone(),
                    text: source[n - 1].into(),
                })
            }
        }
        Ok(BlameResult {
            path: file.path.clone(),
            revision_label: format!("Lore r{} ({})", revision.number, revision.signature),
            lines: lines
                .into_iter()
                .collect::<Option<Vec<_>>>()
                .ok_or_else(|| VcsError("Lore blame 줄이 누락되었습니다.".into()))?,
        })
    }
    fn revision(&self, root: &Path, cancel: &CancelToken) -> Result<Revision> {
        let events = self.json_events(
            root,
            &[
                "--local".into(),
                "repository".into(),
                "status".into(),
                "--revision-only".into(),
            ],
            false,
            cancel,
        )?;
        let found: Vec<_> = events
            .iter()
            .filter(|e| {
                e.get("tagName").and_then(Value::as_str) == Some("repositoryStatusRevision")
            })
            .collect();
        if found.len() != 1 {
            return Err("Lore 현재 리비전 정보가 없거나 중복되었습니다.".into());
        }
        let d = &found[0]["data"];
        let repository = string(d, "repository")?;
        let signature = string(d, "revision")?;
        let number = d["revisionNumber"]
            .as_u64()
            .ok_or_else(|| VcsError("Lore 리비전 번호가 올바르지 않습니다.".into()))?;
        if repository.len() != 32
            || !repository.bytes().all(|c| c.is_ascii_hexdigit())
            || signature.len() != 64
            || !signature.bytes().all(|c| c.is_ascii_hexdigit())
            || (signature.bytes().all(|c| c == b'0') && number != 0)
        {
            return Err("Lore 리비전 서명 또는 저장소 ID가 올바르지 않습니다.".into());
        }
        Ok(Revision {
            signature,
            number,
            repository,
        })
    }
    pub(super) fn snapshot(
        &self,
        root: &Path,
        path: &str,
        revision: &Revision,
        cancel: &CancelToken,
    ) -> Result<Option<String>> {
        if revision.number == 0 && revision.signature.bytes().all(|b| b == b'0') {
            return Ok(None);
        }
        let events = self.json_events(
            root,
            &[
                "file".into(),
                "info".into(),
                "--revision".into(),
                revision.signature.clone(),
                "--".into(),
                format!("./{path}"),
            ],
            false,
            cancel,
        )?;
        let files: Vec<_> = events
            .iter()
            .filter(|e| e.get("tagName").and_then(Value::as_str) == Some("fileInfo"))
            .collect();
        if files.is_empty() {
            return Ok(None);
        }
        if files.len() != 1 {
            return Err("Lore 파일 조회가 다른 경로 정보를 반환했습니다.".into());
        }
        let d = &files[0]["data"];
        if self.safe_path(root, string(d, "path")?.as_str())? != path {
            return Err("Lore 파일 정보가 요청한 경로와 다릅니다.".into());
        }
        if d["flagAdded"]
            .as_bool()
            .ok_or_else(|| VcsError("기준 리비전 파일 여부를 확인하지 못했습니다.".into()))?
        {
            return Ok(None);
        }
        if d["isDir"].as_bool() == Some(true) {
            return Err("Lore 폴더는 텍스트로 표시할 수 없습니다.".into());
        }
        if d["isFile"].as_bool() != Some(true) {
            return Ok(None);
        }
        let size = d["size"]
            .as_u64()
            .ok_or_else(|| VcsError("Lore 파일 크기를 확인할 수 없습니다.".into()))?;
        if size > text::MAX_TEXT_BYTES as u64 {
            return Err("Lore 텍스트 도구는 1 MiB까지 지원합니다.".into());
        }
        cancel.check()?;
        let folder = std::env::temp_dir().join(format!(
            "moavcs-lore-{}-{}",
            std::process::id(),
            TEMP.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        fs::create_dir(&folder)?;
        let output = folder.join("snapshot.txt");
        let result = (|| {
            self.json_events(
                root,
                &[
                    "file".into(),
                    "write".into(),
                    "--revision".into(),
                    revision.signature.clone(),
                    "--path".into(),
                    format!("./{path}"),
                    "--output".into(),
                    output.to_string_lossy().into_owned(),
                ],
                false,
                cancel,
            )?;
            Ok(text::read_snapshot(&output)?.text)
        })();
        let _ = fs::remove_file(&output);
        let _ = fs::remove_dir(&folder);
        result.map(Some)
    }
}
static TEMP: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
pub(super) fn string(v: &Value, k: &str) -> Result<String> {
    v.get(k)
        .and_then(Value::as_str)
        .filter(|x| !x.is_empty())
        .map(str::to_owned)
        .ok_or_else(|| VcsError(format!("Lore JSON '{k}' 문자열 필드가 없습니다.")))
}
fn number(v: &Value, k: &str) -> Result<usize> {
    v.get(k)
        .and_then(Value::as_u64)
        .and_then(|x| usize::try_from(x).ok())
        .filter(|x| *x > 0)
        .ok_or_else(|| VcsError(format!("Lore JSON '{k}'은 양의 정수여야 합니다.")))
}
fn attribution(events: &[Value]) -> Result<(String, String)> {
    let (mut committed, mut merged, mut timestamp) = (None, None, None);
    for e in events
        .iter()
        .filter(|e| e.get("tagName").and_then(Value::as_str) == Some("metadata"))
    {
        let d = &e["data"];
        let key = string(d, "key")?;
        let val = &d["value"];
        let tag = val["tagName"].as_str().unwrap_or("");
        if key == "committed-by" || key == "merged-by" {
            if tag != "string" {
                return Err("Lore 작성자 메타데이터 형식이 잘못되었습니다.".into());
            }
            if key == "merged-by" {
                merged = val["data"].as_str().map(str::to_owned)
            } else {
                committed = val["data"].as_str().map(str::to_owned)
            }
        } else if key == "timestamp" && tag == "numeric" {
            timestamp = val["data"].as_u64()
        }
    }
    let author = merged
        .or(committed)
        .filter(|x| !x.trim().is_empty())
        .unwrap_or_else(|| "작성자 정보 없음".into());
    let date = timestamp
        .and_then(|ms| i64::try_from(ms / 1000).ok())
        .and_then(format_utc)
        .unwrap_or_default();
    Ok((author, date))
}
fn format_utc(t: i64) -> Option<String> {
    let days = t.div_euclid(86_400);
    let sec = t.rem_euclid(86_400);
    let z = days.checked_add(719_468)?;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let mut y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = mp + if mp < 10 { 3 } else { -9 };
    if m <= 2 {
        y += 1
    }
    Some(format!(
        "{y:04}-{m:02}-{d:02} {:02}:{:02}:{:02} UTC",
        sec / 3600,
        (sec % 3600) / 60,
        sec % 60
    ))
}
