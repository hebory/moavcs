use super::*;
use std::collections::BTreeMap;

#[derive(Clone)]
struct Stage {
    mode: String,
    oid: String,
    stage: u8,
}
impl GitProvider {
    pub(super) fn conflict_file(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        if !file.conflicted() {
            return Err(VcsError("선택한 파일은 Git 충돌 상태가 아닙니다.".into()));
        }
        let root = self.root(root, cancel)?;
        let path = self.path(&root, &file.path)?;
        let all = self.unmerged(&root, cancel)?;
        let stages = all
            .get(&path)
            .ok_or_else(|| VcsError("선택 파일의 충돌 인덱스 항목을 찾을 수 없습니다.".into()))?;
        let base = stages
            .get(&1)
            .map(|s| self.stage_text(&root, &path, s, cancel))
            .transpose()?;
        let ours = stages
            .get(&2)
            .map(|s| self.stage_text(&root, &path, s, cancel))
            .transpose()?
            .unwrap_or_default();
        let theirs = stages
            .get(&3)
            .map(|s| self.stage_text(&root, &path, s, cancel))
            .transpose()?
            .unwrap_or_default();
        let full = root.join(&path);
        let (working_text, file_hash, bom, newline) = if full.is_file() {
            let snapshot = text::read_snapshot(&full)?;
            (snapshot.text, snapshot.hash, snapshot.bom, snapshot.newline)
        } else {
            (String::new(), String::new(), false, "\n".into())
        };
        let token = stage_token(stages);
        Ok(ConflictFile {
            root,
            path,
            base_text: base,
            left_text: ours,
            right_text: theirs,
            working_text,
            state_token: token,
            file_hash,
            has_utf8_bom: bom,
            newline,
            left_label: "내 변경".into(),
            right_label: "받은 변경".into(),
            backup_path: None,
        })
    }
    pub(super) fn save_conflict_file(
        &self,
        expected: &ConflictFile,
        content: &str,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        let root = self.root(&expected.root, cancel)?;
        let path = self.path(&root, &expected.path)?;
        let stages = self.unmerged(&root, cancel)?;
        let current = stages
            .get(&path)
            .ok_or_else(|| VcsError("충돌 인덱스가 변경되었습니다. 파일을 다시 여세요.".into()))?;
        if stage_token(current) != expected.state_token {
            return Err(VcsError(
                "충돌 인덱스가 변경되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        let full = root.join(&path);
        if full.is_file() && text::read_snapshot(&full)?.hash != expected.file_hash {
            return Err(VcsError(
                "작업 파일이 충돌 편집기 밖에서 변경되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        if !full.exists() && !expected.file_hash.is_empty() {
            return Err(VcsError(
                "충돌 파일이 삭제되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        text::save_conflict_file(expected, content)
    }
    pub(super) fn resolve_conflict_file(
        &self,
        expected: &ConflictFile,
        cancel: &CancelToken,
    ) -> Result<()> {
        let root = self.root(&expected.root, cancel)?;
        let path = self.path(&root, &expected.path)?;
        let all = self.unmerged(&root, cancel)?;
        let current = all.get(&path).ok_or_else(|| {
            VcsError("선택한 파일은 이미 해결되었거나 충돌 상태가 변경되었습니다.".into())
        })?;
        if stage_token(current) != expected.state_token {
            return Err(VcsError(
                "충돌 상태가 편집 도중 변경되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        let full = root.join(&path);
        if !full.is_file() {
            return Err(VcsError("충돌 해결 파일이 없습니다.".into()));
        }
        let content = text::read_snapshot(&full)?;
        if content.hash != expected.file_hash {
            return Err(VcsError(
                "충돌 해결 파일이 저장된 뒤 다시 변경되었습니다. 다시 열어 확인하세요.".into(),
            ));
        }
        if text::contains_markers(&content.text) {
            return Err(VcsError(
                "충돌 표식이 남아 있습니다. 내용을 해결하고 다시 시도하세요.".into(),
            ));
        }
        self.required(
            &root,
            vec![
                "--literal-pathspecs".into(),
                "add".into(),
                "--".into(),
                path,
            ],
            cancel,
        )?;
        Ok(())
    }
    fn unmerged(
        &self,
        root: &Path,
        cancel: &CancelToken,
    ) -> Result<BTreeMap<String, BTreeMap<u8, Stage>>> {
        let result = self.required(
            root,
            vec![
                "--literal-pathspecs".into(),
                "ls-files".into(),
                "--unmerged".into(),
                "-z".into(),
            ],
            cancel,
        )?;
        let mut out = BTreeMap::new();
        for record in result.stdout.split('\0').filter(|s| !s.is_empty()) {
            let (meta, path) = record
                .split_once('\t')
                .ok_or_else(|| VcsError("Git 충돌 인덱스 형식을 인식할 수 없습니다.".into()))?;
            let f: Vec<_> = meta.split_whitespace().collect();
            if f.len() != 3 || !f[1].bytes().all(|b| b.is_ascii_hexdigit()) {
                return Err(VcsError("Git 충돌 인덱스 필드가 잘못되었습니다.".into()));
            }
            let stage = f[2]
                .parse::<u8>()
                .map_err(|_| VcsError("Git 충돌 stage 형식이 잘못되었습니다.".into()))?;
            if !(1..=3).contains(&stage) {
                return Err(VcsError("Git 충돌 stage가 범위를 벗어났습니다.".into()));
            }
            let path = self.path(root, path)?;
            out.entry(path).or_insert_with(BTreeMap::new).insert(
                stage,
                Stage {
                    mode: f[0].into(),
                    oid: f[1].into(),
                    stage,
                },
            );
        }
        Ok(out)
    }
    fn stage_text(
        &self,
        root: &Path,
        path: &str,
        stage: &Stage,
        cancel: &CancelToken,
    ) -> Result<String> {
        if stage.mode == "160000" {
            return Err(VcsError(format!(
                "'{path}'은 submodule 충돌이므로 텍스트 편집을 지원하지 않습니다."
            )));
        }
        let blob = self.required(
            root,
            vec!["cat-file".into(), "blob".into(), stage.oid.clone()],
            cancel,
        )?;
        text::decode_snapshot(blob.stdout.as_bytes(), path)
    }
}
fn stage_token(stages: &BTreeMap<u8, Stage>) -> String {
    stages
        .values()
        .map(|s| format!("{}:{}:{}", s.stage, s.mode, s.oid))
        .collect::<Vec<_>>()
        .join("|")
}
