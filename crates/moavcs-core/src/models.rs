use serde::{Deserialize, Serialize};
use std::{fmt, path::PathBuf};

pub type Result<T> = std::result::Result<T, VcsError>;
#[derive(Debug, Clone)]
pub struct VcsError(pub String);
impl fmt::Display for VcsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}
impl std::error::Error for VcsError {}
impl From<std::io::Error> for VcsError {
    fn from(e: std::io::Error) -> Self {
        Self(e.to_string())
    }
}
impl From<serde_json::Error> for VcsError {
    fn from(e: serde_json::Error) -> Self {
        Self(e.to_string())
    }
}
impl From<&str> for VcsError {
    fn from(e: &str) -> Self {
        Self(e.to_owned())
    }
}
impl From<String> for VcsError {
    fn from(e: String) -> Self {
        Self(e)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
pub enum VcsKind {
    #[default]
    Git,
    Svn,
    Perforce,
    Lore,
}
impl VcsKind {
    pub const ALL: [Self; 4] = [Self::Git, Self::Svn, Self::Perforce, Self::Lore];
    pub fn name(self) -> &'static str {
        match self {
            Self::Git => "Git",
            Self::Svn => "Subversion",
            Self::Perforce => "Perforce",
            Self::Lore => "Lore",
        }
    }
    pub fn executable(self) -> &'static str {
        match self {
            Self::Git => "git",
            Self::Svn => "svn",
            Self::Perforce => "p4",
            Self::Lore => "lore",
        }
    }
    pub fn parse(s: &str) -> Result<Self> {
        Self::ALL
            .into_iter()
            .find(|k| format!("{k:?}").eq_ignore_ascii_case(s))
            .ok_or_else(|| VcsError(format!("지원하지 않는 VCS: {s}")))
    }
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Operation {
    Stage,
    Unstage,
    Commit,
    Update,
    Push,
    Initialize,
    Clone,
    Fetch,
}
#[derive(Debug, Clone, Default)]
pub struct Capabilities {
    pub stage: bool,
    pub unstage: bool,
    pub commit: bool,
    pub update: bool,
    pub push: bool,
    pub initialize: bool,
    pub clone: bool,
    pub fetch: bool,
    pub remote_url: bool,
    pub conflict_resolution: bool,
    pub blame: bool,
    pub comparison: bool,
    pub history: bool,
}
impl Capabilities {
    pub fn supports(&self, op: Operation) -> bool {
        match op {
            Operation::Stage => self.stage,
            Operation::Unstage => self.unstage,
            Operation::Commit => self.commit,
            Operation::Update => self.update,
            Operation::Push => self.push,
            Operation::Initialize => self.initialize,
            Operation::Clone => self.clone,
            Operation::Fetch => self.fetch,
        }
    }
}
#[derive(Debug, Clone, Default)]
pub struct RepositoryInfo {
    pub kind: VcsKind,
    pub root: PathBuf,
    pub branch: String,
    pub summary: String,
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct FileChange {
    pub path: String,
    pub status: String,
    pub index_status: String,
    pub worktree_status: String,
    pub original_path: Option<String>,
}
impl FileChange {
    pub fn conflicted(&self) -> bool {
        self.status.eq_ignore_ascii_case("conflict")
            || self.status.eq_ignore_ascii_case("conflicted")
    }
    pub fn staged(&self) -> bool {
        !self.conflicted() && !self.index_status.trim().is_empty() && self.index_status != "?"
    }
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct HistoryEntry {
    pub id: String,
    pub author: String,
    pub date: String,
    pub message: String,
    pub parent_ids: Vec<String>,
    pub references: Vec<HistoryReference>,
    pub relation: HistoryRelation,
    pub author_email: String,
    pub committer: String,
    pub committer_email: String,
    pub commit_date: String,
}
impl HistoryEntry {
    pub fn summary(&self) -> &str {
        self.message.lines().next().unwrap_or("")
    }
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum HistoryRelation {
    #[default]
    Unknown,
    LocalOnly,
    UpstreamOnly,
    Shared,
    Other,
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum HistoryReferenceKind {
    #[default]
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct HistoryReference {
    pub name: String,
    pub kind: HistoryReferenceKind,
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum HistoryScope {
    #[default]
    CurrentBranch,
    AllBranches,
}
#[derive(Debug, Clone, Default)]
pub struct GitHistoryContext {
    pub current_branch: String,
    pub head_id: String,
    pub is_detached: bool,
    pub is_unborn: bool,
    pub upstream: String,
    pub upstream_id: String,
    pub push_target: String,
    pub push_target_id: String,
    pub ahead: Option<u64>,
    pub behind: Option<u64>,
    pub is_shallow: bool,
    pub status: String,
}
#[derive(Debug, Clone, Default)]
pub struct HistorySnapshot {
    pub entries: Vec<HistoryEntry>,
    pub scope_label: String,
    pub git: Option<GitHistoryContext>,
    pub is_truncated: bool,
}
#[derive(Debug, Clone, Default)]
pub struct HistoryDetails {
    pub entry: HistoryEntry,
    pub files: Vec<FileChange>,
    pub comparison_label: String,
}
#[derive(Debug, Clone, Default)]
pub struct FileComparison {
    pub path: String,
    pub left_label: String,
    pub right_label: String,
    pub left_text: String,
    pub right_text: String,
}
#[derive(Debug, Clone, Default)]
pub struct BlameLine {
    pub line_number: usize,
    pub revision: String,
    pub author: String,
    pub date: String,
    pub text: String,
}
#[derive(Debug, Clone, Default)]
pub struct BlameResult {
    pub path: String,
    pub revision_label: String,
    pub lines: Vec<BlameLine>,
}
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RemoteInfo {
    pub name: String,
    pub exists: bool,
    pub fetch_urls: Vec<String>,
    pub push_urls: Vec<String>,
    pub effective_fetch_urls: Vec<String>,
    pub effective_push_urls: Vec<String>,
    pub read_only_reason: Option<String>,
}
#[derive(Debug, Clone, Default)]
pub struct ConflictFile {
    pub root: PathBuf,
    pub path: String,
    pub base_text: Option<String>,
    pub left_text: String,
    pub right_text: String,
    pub working_text: String,
    pub state_token: String,
    pub file_hash: String,
    pub has_utf8_bom: bool,
    pub newline: String,
    pub left_label: String,
    pub right_label: String,
    pub backup_path: Option<PathBuf>,
}
#[derive(Debug, Clone)]
pub struct VcsRequest {
    pub operation: Operation,
    pub working_directory: PathBuf,
    pub paths: Vec<String>,
    pub message: String,
    pub remote: String,
    pub destination: PathBuf,
}
impl VcsRequest {
    pub fn new(operation: Operation, root: impl Into<PathBuf>) -> Self {
        Self {
            operation,
            working_directory: root.into(),
            paths: vec![],
            message: String::new(),
            remote: String::new(),
            destination: PathBuf::new(),
        }
    }
}
#[derive(Debug, Clone, Default)]
pub struct CommandResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}
impl CommandResult {
    pub fn success(&self) -> bool {
        self.exit_code == 0
    }
    pub fn output(&self) -> String {
        [self.stdout.trim_end(), self.stderr.trim_end()]
            .into_iter()
            .filter(|s| !s.is_empty())
            .collect::<Vec<_>>()
            .join("\n")
    }
    pub fn ensure(self) -> Result<Self> {
        if self.success() {
            Ok(self)
        } else {
            Err(VcsError(if self.output().is_empty() {
                format!("명령 종료 코드 {}", self.exit_code)
            } else {
                self.output()
            }))
        }
    }
}
