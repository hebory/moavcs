use super::*;

impl GitProvider {
    pub(super) fn untracked_diff(
        &self,
        root: &Path,
        path: &str,
        cancel: &CancelToken,
    ) -> Result<String> {
        let full = root.join(path);
        if !full.is_file() {
            return Ok(format!("새 파일을 찾을 수 없습니다: {path}\n"));
        }
        let _snap = text::read_snapshot(&full)?;
        let mut args = vec![
            "--literal-pathspecs".into(),
            "--no-pager".into(),
            "diff".into(),
            "--no-index".into(),
            "--no-ext-diff".into(),
            "--no-textconv".into(),
            "--no-color".into(),
            "--".into(),
            "/dev/null".into(),
            format!("./{path}"),
        ];
        let result = self.run(root, args.drain(..), cancel)?;
        if !matches!(result.exit_code, 0 | 1) {
            return result.ensure().map(|x| x.stdout);
        }
        Ok(result.stdout)
    }
    pub(super) fn comparison_impl(
        &self,
        root: &Path,
        file: &FileChange,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        let root = self.root(root, cancel)?;
        let path = self.path(&root, &file.path)?;
        if file.conflicted() {
            if staged {
                return Err(VcsError(
                    "충돌 상태에서는 일반 스테이지 비교를 할 수 없습니다. 충돌 비교를 여세요."
                        .into(),
                ));
            }
            let conflict = self.conflict_file(&root, file, cancel)?;
            return Ok(FileComparison {
                path,
                left_label: conflict.left_label,
                right_label: conflict.right_label,
                left_text: conflict.left_text,
                right_text: conflict.right_text,
            });
        }
        let (left, right, left_label, right_label) = if staged {
            let hp = if file.index_status.starts_with('R') {
                file.original_path
                    .as_deref()
                    .map(|p| self.path(&root, p))
                    .transpose()?
                    .unwrap_or(path.clone())
            } else {
                path.clone()
            };
            (
                self.read_commit_path(&root, "HEAD", &hp, cancel)?,
                self.read_index_path(&root, &path, cancel)?,
                "HEAD".into(),
                "인덱스".into(),
            )
        } else {
            let ip = if file.worktree_status.starts_with('R') {
                file.original_path
                    .as_deref()
                    .map(|p| self.path(&root, p))
                    .transpose()?
                    .unwrap_or(path.clone())
            } else {
                path.clone()
            };
            (
                self.read_index_path(&root, &ip, cancel)?,
                self.read_worktree_path(&root, &path)?,
                "인덱스".into(),
                "작업 트리".into(),
            )
        };
        Ok(FileComparison {
            path,
            left_label: if left.is_none() {
                format!("{left_label} (파일 없음)")
            } else {
                left_label
            },
            right_label: if right.is_none() {
                format!("{right_label} (삭제됨)")
            } else {
                right_label
            },
            left_text: left.unwrap_or_default(),
            right_text: right.unwrap_or_default(),
        })
    }
    fn read_worktree_path(&self, root: &Path, path: &str) -> Result<Option<String>> {
        let full = root.join(path);
        if !full.exists() {
            return Ok(None);
        }
        if !full.is_file() {
            return Err(VcsError(
                "폴더 전체를 텍스트로 비교할 수 없습니다. 파일을 선택하세요.".into(),
            ));
        }
        Ok(Some(text::read_snapshot(&full)?.text))
    }
    fn read_index_path(
        &self,
        root: &Path,
        path: &str,
        cancel: &CancelToken,
    ) -> Result<Option<String>> {
        let out = self.required(
            root,
            vec![
                "--literal-pathspecs".into(),
                "ls-files".into(),
                "--stage".into(),
                "-z".into(),
                "--".into(),
                path.into(),
            ],
            cancel,
        )?;
        let Some(record) = out.stdout.split('\0').find(|s| !s.is_empty()) else {
            return Ok(None);
        };
        let mut halves = record.splitn(2, '\t');
        let metadata = halves.next().unwrap_or_default();
        let _name = halves.next();
        let fields: Vec<_> = metadata.split_whitespace().collect();
        if fields.len() < 3 || fields[2] != "0" {
            return Err(VcsError(format!(
                "'{path}' 파일은 인덱스 충돌 상태여서 비교할 수 없습니다."
            )));
        }
        let id = fields[1];
        if !id.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(VcsError(
                "Git 인덱스의 blob ID 형식이 잘못되었습니다.".into(),
            ));
        }
        let blob = self.required(
            root,
            vec!["cat-file".into(), "blob".into(), id.into()],
            cancel,
        )?;
        Ok(Some(text::decode_snapshot(blob.stdout.as_bytes(), path)?))
    }
    pub(super) fn blame_impl(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<BlameResult> {
        let root = self.root(root, cancel)?;
        let mut path = self.path(&root, &file.path)?;
        if file.index_status.starts_with('R') {
            if let Some(original) = &file.original_path {
                path = self.path(&root, original)?
            }
        }
        let head = self.run(
            &root,
            vec!["rev-parse".into(), "--verify".into(), "HEAD".into()],
            cancel,
        )?;
        if !head.success() {
            return Err(VcsError(
                "작성자 이력을 보려면 먼저 저장소에 첫 커밋이 필요합니다.".into(),
            ));
        }
        let revision = head.stdout.trim();
        let content = self
            .read_commit_path(&root, revision, &path, cancel)?
            .ok_or_else(|| {
                VcsError("선택한 파일은 현재 커밋에 없어 작성자 이력을 표시할 수 없습니다.".into())
            })?;
        let expected = content.lines().count();
        if expected > 50_000 {
            return Err(VcsError(
                "파일이 50,000줄을 초과하여 작성자 이력을 표시할 수 없습니다.".into(),
            ));
        }
        let result = self.required(
            &root,
            vec![
                "--literal-pathspecs".into(),
                "--no-pager".into(),
                "blame".into(),
                "--line-porcelain".into(),
                "--encoding=UTF-8".into(),
                revision.into(),
                "--".into(),
                path.clone(),
            ],
            cancel,
        )?;
        let lines = parse_blame(&result.stdout)?;
        if lines.len() != expected
            || lines
                .iter()
                .enumerate()
                .any(|(i, l)| l.line_number != i + 1)
        {
            return Err(VcsError(
                "Git blame 메타데이터의 줄 수와 파일 내용이 일치하지 않습니다.".into(),
            ));
        }
        let short = self
            .run(
                &root,
                vec!["rev-parse".into(), "--short".into(), revision.into()],
                cancel,
            )
            .ok()
            .filter(|x| x.success())
            .map(|x| x.stdout.trim().to_owned())
            .unwrap_or_default();
        Ok(BlameResult {
            path: file.path.clone(),
            revision_label: if short.is_empty() {
                "HEAD".into()
            } else {
                format!("HEAD ({short})")
            },
            lines,
        })
    }
}
fn parse_blame(text: &str) -> Result<Vec<BlameLine>> {
    let lines: Vec<_> = text.lines().collect();
    let mut result = Vec::new();
    let mut i = 0;
    while i < lines.len() {
        let header = lines[i].split_whitespace().collect::<Vec<_>>();
        if header.len() < 3
            || !(header[0].strip_prefix('^').unwrap_or(header[0]).len() == 40
                || header[0].strip_prefix('^').unwrap_or(header[0]).len() == 64)
            || !header[0]
                .strip_prefix('^')
                .unwrap_or(header[0])
                .bytes()
                .all(|b| b.is_ascii_hexdigit())
        {
            return Err(VcsError(format!(
                "Git blame 줄 형식을 인식할 수 없습니다: {}",
                lines[i]
            )));
        }
        let _: usize = header[1]
            .parse()
            .map_err(|_| VcsError("Git blame 원본 줄 번호 형식이 잘못되었습니다.".into()))?;
        let line_number = header[2]
            .parse::<usize>()
            .map_err(|_| VcsError("Git blame 줄 번호 형식이 잘못되었습니다.".into()))?;
        i += 1;
        let mut author = String::new();
        let mut date = String::new();
        while i < lines.len() && !lines[i].starts_with('\t') {
            if let Some(v) = lines[i].strip_prefix("author ") {
                author = v.into()
            }
            if let Some(v) = lines[i].strip_prefix("author-time ") {
                date = v
                    .parse::<i64>()
                    .ok()
                    .and_then(format_unix)
                    .unwrap_or_default()
            }
            i += 1
        }
        if i >= lines.len() {
            return Err(VcsError("Git blame 본문이 누락되었습니다.".into()));
        }
        let body = lines[i].strip_prefix('\t').unwrap_or_default();
        i += 1;
        result.push(BlameLine {
            line_number,
            revision: header[0].to_owned(),
            author,
            date,
            text: body.into(),
        });
    }
    Ok(result)
}
fn format_unix(t: i64) -> Option<String> {
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
