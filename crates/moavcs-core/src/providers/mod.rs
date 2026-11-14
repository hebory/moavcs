use crate::{
    models::*,
    process::{CancelToken, CommandRunner, ProcessRunner},
};
use std::{collections::HashMap, path::Path, sync::Arc};
pub mod git;
pub mod lore;
pub mod perforce;
pub mod svn;

pub trait VcsProvider: Send + Sync {
    fn kind(&self) -> VcsKind;
    fn executable(&self) -> &str;
    fn capabilities(&self) -> Capabilities;
    fn repository(&self, path: &Path, cancel: &CancelToken) -> Result<RepositoryInfo>;
    fn status(&self, root: &Path, cancel: &CancelToken) -> Result<Vec<FileChange>>;
    fn diff(
        &self,
        root: &Path,
        file: Option<&FileChange>,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<String>;
    fn history(
        &self,
        root: &Path,
        scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot>;
    fn execute(&self, request: &VcsRequest, cancel: &CancelToken) -> Result<CommandResult>;
    fn comparison(
        &self,
        _root: &Path,
        _file: &FileChange,
        _staged: bool,
        _cancel: &CancelToken,
    ) -> Result<FileComparison> {
        Err(self.unsupported("내장 좌우 비교"))
    }
    fn blame(
        &self,
        _root: &Path,
        _file: &FileChange,
        _cancel: &CancelToken,
    ) -> Result<BlameResult> {
        Err(self.unsupported("Blame"))
    }
    fn history_details(
        &self,
        _root: &Path,
        _revision: &str,
        _cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        Err(self.unsupported("커밋 세부 내역"))
    }
    fn history_diff(
        &self,
        _root: &Path,
        _revision: &str,
        _file: Option<&FileChange>,
        _cancel: &CancelToken,
    ) -> Result<String> {
        Err(self.unsupported("커밋 Diff"))
    }
    fn remote(&self, _root: &Path, _cancel: &CancelToken) -> Result<RemoteInfo> {
        Err(self.unsupported("원격 주소 편집"))
    }
    fn set_remote(
        &self,
        _root: &Path,
        _url: &str,
        _expected: &RemoteInfo,
        _cancel: &CancelToken,
    ) -> Result<()> {
        Err(self.unsupported("원격 주소 편집"))
    }
    fn conflict(
        &self,
        _root: &Path,
        _file: &FileChange,
        _cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        Err(self.unsupported("내장 충돌 해결"))
    }
    fn save_conflict(
        &self,
        _expected: &ConflictFile,
        _text: &str,
        _cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        Err(self.unsupported("충돌 결과 저장"))
    }
    fn resolve_conflict(&self, _expected: &ConflictFile, _cancel: &CancelToken) -> Result<()> {
        Err(self.unsupported("충돌 해결 표시"))
    }
    fn unsupported(&self, feature: &str) -> VcsError {
        VcsError(format!(
            "{}에서는 {feature} 기능을 지원하지 않습니다. 전용 도구를 사용하세요.",
            self.kind().name()
        ))
    }
}
pub fn create_provider(kind: VcsKind, executable: impl Into<String>) -> Arc<dyn VcsProvider> {
    create_provider_with_runner(kind, executable, Arc::new(ProcessRunner))
}
pub fn create_provider_with_runner(
    kind: VcsKind,
    executable: impl Into<String>,
    runner: Arc<dyn CommandRunner>,
) -> Arc<dyn VcsProvider> {
    let configured: String = executable.into();
    let exe = if configured.trim().is_empty() {
        kind.executable().to_owned()
    } else {
        configured.trim().to_owned()
    };
    match kind {
        VcsKind::Git => Arc::new(git::GitProvider::new(exe, runner)),
        VcsKind::Svn => Arc::new(svn::SvnProvider::new(exe, runner)),
        VcsKind::Perforce => Arc::new(perforce::PerforceProvider::new(exe, runner)),
        VcsKind::Lore => Arc::new(lore::LoreProvider::new(exe, runner)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn empty_preferences_resolve_to_each_default_cli() {
        for kind in VcsKind::ALL {
            assert_eq!(create_provider(kind, " ").executable(), kind.executable());
        }
    }
}
pub fn detect_repository(
    path: &Path,
    executables: &HashMap<VcsKind, String>,
    cancel: &CancelToken,
) -> Result<(Arc<dyn VcsProvider>, RepositoryInfo)> {
    let path = if path.is_file() {
        path.parent().unwrap_or(path)
    } else {
        path
    };
    let mut errors = Vec::new();
    for kind in VcsKind::ALL {
        cancel.check()?;
        let provider = create_provider(
            kind,
            executables
                .get(&kind)
                .filter(|p| !p.trim().is_empty())
                .cloned()
                .unwrap_or_else(|| kind.executable().into()),
        );
        match provider.repository(path, cancel) {
            Ok(repository) => return Ok((provider, repository)),
            Err(e) => errors.push(format!("{}: {e}", kind.name())),
        }
    }
    Err(VcsError(format!(
        "저장소를 찾을 수 없습니다. CLI 설치 경로와 저장소 폴더를 확인하세요.\n{}",
        errors.join("\n")
    )))
}
