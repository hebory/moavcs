//! Git provider. Commands are always passed as an argument vector; paths are literal
//! pathspecs and every mutating operation is constrained to the repository root.
use crate::{
    models::*,
    process::{CancelToken, CommandRunner, CommandSpec},
    providers::VcsProvider,
    text,
};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

mod conflicts;
mod history;
mod history_context;
mod inspect;
mod remote;

pub struct GitProvider {
    executable: String,
    runner: Arc<dyn CommandRunner>,
}
impl GitProvider {
    pub fn new(executable: String, runner: Arc<dyn CommandRunner>) -> Self {
        Self { executable, runner }
    }
    pub(super) fn run(
        &self,
        root: &Path,
        args: impl IntoIterator<Item = String>,
        cancel: &CancelToken,
    ) -> Result<CommandResult> {
        cancel.check()?;
        let command = CommandSpec::new(self.executable.clone(), root, args);
        self.runner.run(&command, cancel)
    }
    pub(super) fn run_input(
        &self,
        root: &Path,
        args: impl IntoIterator<Item = String>,
        input: Option<String>,
        cancel: &CancelToken,
    ) -> Result<CommandResult> {
        cancel.check()?;
        let mut command = CommandSpec::new(self.executable.clone(), root, args);
        command.standard_input = input;
        self.runner.run(&command, cancel)
    }
    pub(super) fn required(
        &self,
        root: &Path,
        args: impl IntoIterator<Item = String>,
        cancel: &CancelToken,
    ) -> Result<CommandResult> {
        self.run(root, args, cancel)?.ensure()
    }
    pub(super) fn root(&self, path: &Path, cancel: &CancelToken) -> Result<PathBuf> {
        let full = absolute(path)?;
        let output = self.required(
            &full,
            vec!["rev-parse".into(), "--show-toplevel".into()],
            cancel,
        )?;
        absolute(Path::new(output.stdout.trim()))
    }
    pub(super) fn path(&self, root: &Path, relative: &str) -> Result<String> {
        text::validate_relative(root, relative)
    }
    pub(super) fn read_commit_path(
        &self,
        root: &Path,
        revision: &str,
        path: &str,
        cancel: &CancelToken,
    ) -> Result<Option<String>> {
        let exists = self.run(
            root,
            vec!["cat-file".into(), "-e".into(), format!("{revision}:{path}")],
            cancel,
        )?;
        if !exists.success() {
            return Ok(None);
        }
        let result = self.required(
            root,
            vec!["show".into(), format!("{revision}:{path}")],
            cancel,
        )?;
        Ok(Some(text::decode_snapshot(result.stdout.as_bytes(), path)?))
    }
    fn validate_request_paths(&self, root: &Path, paths: &[String]) -> Result<Vec<String>> {
        paths.iter().map(|path| self.path(root, path)).collect()
    }
}
impl VcsProvider for GitProvider {
    fn kind(&self) -> VcsKind {
        VcsKind::Git
    }
    fn executable(&self) -> &str {
        &self.executable
    }
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            stage: true,
            unstage: true,
            commit: true,
            update: true,
            push: true,
            initialize: true,
            clone: true,
            fetch: true,
            remote_url: true,
            conflict_resolution: true,
            blame: true,
            comparison: true,
            history: true,
        }
    }
    fn repository(&self, path: &Path, cancel: &CancelToken) -> Result<RepositoryInfo> {
        let root = self.root(path, cancel)?;
        let branch = self.run(
            &root,
            vec!["symbolic-ref".into(), "-q".into(), "HEAD".into()],
            cancel,
        )?;
        let branch = if branch.success() {
            branch
                .stdout
                .trim()
                .strip_prefix("refs/heads/")
                .unwrap_or(branch.stdout.trim())
                .to_owned()
        } else {
            self.run(
                &root,
                vec!["rev-parse".into(), "--short".into(), "HEAD".into()],
                cancel,
            )
            .ok()
            .filter(|x| x.success())
            .map(|x| format!("({})", x.stdout.trim()))
            .unwrap_or_else(|| "(커밋 전)".into())
        };
        Ok(RepositoryInfo {
            kind: VcsKind::Git,
            root,
            branch,
            summary: String::new(),
        })
    }
    fn status(&self, root: &Path, cancel: &CancelToken) -> Result<Vec<FileChange>> {
        let root = self.root(root, cancel)?;
        let output = self
            .required(
                &root,
                vec![
                    "-c".into(),
                    "core.quotepath=false".into(),
                    "status".into(),
                    "--porcelain=v1".into(),
                    "-z".into(),
                    "--untracked-files=all".into(),
                ],
                cancel,
            )?
            .stdout;
        parse_status(&output, &root)
    }
    fn diff(
        &self,
        root: &Path,
        file: Option<&FileChange>,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root, cancel)?;
        let mut args = vec![
            "--literal-pathspecs".into(),
            "--no-pager".into(),
            "diff".into(),
        ];
        if staged {
            args.push("--cached".into())
        }
        args.extend([
            "--no-ext-diff".into(),
            "--no-textconv".into(),
            "--no-color".into(),
        ]);
        if let Some(file) = file {
            let path = self.path(&root, &file.path)?;
            if !staged && file.index_status == "?" && file.worktree_status == "?" {
                return self.untracked_diff(&root, &path, cancel);
            }
            args.extend(["--".into(), path]);
        }
        let result = self.required(&root, args, cancel)?;
        if file.is_some() {
            return Ok(result.stdout);
        }
        let mut out = result.stdout;
        if !staged {
            for change in self
                .status(&root, cancel)?
                .into_iter()
                .filter(|f| f.index_status == "?" && f.worktree_status == "?")
                .take(30)
            {
                let d = self.untracked_diff(&root, &change.path, cancel)?;
                if out.len() + d.len() > 4 * 1024 * 1024 {
                    out.push_str("\n[출력 크기 제한에 도달했습니다.]\n");
                    break;
                }
                out.push_str(&d);
            }
        }
        Ok(out)
    }
    fn history(
        &self,
        root: &Path,
        scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        self.history_snapshot(root, scope, cancel)
    }
    fn execute(&self, request: &VcsRequest, cancel: &CancelToken) -> Result<CommandResult> {
        let working = absolute(&request.working_directory)?;
        match request.operation {
            Operation::Initialize => {
                fs::create_dir_all(&working)?;
                self.required(&working, vec!["init".into()], cancel)
            }
            Operation::Clone => {
                let remote = text::validate_remote_url(&request.remote)?;
                if request.destination.as_os_str().is_empty() {
                    return Err(VcsError(
                        "복제할 저장소 주소와 대상 폴더를 입력하세요.".into(),
                    ));
                }
                let dest = absolute(&request.destination)?;
                if dest.is_dir() && fs::read_dir(&dest)?.next().is_some() {
                    return Err(VcsError("복제 대상 폴더가 비어 있어야 합니다.".into()));
                }
                let parent = if working.is_dir() {
                    working
                } else {
                    dest.parent()
                        .ok_or_else(|| VcsError("복제 대상 폴더의 상위 경로가 없습니다.".into()))?
                        .to_path_buf()
                };
                fs::create_dir_all(&parent)?;
                self.required(
                    &parent,
                    vec![
                        "clone".into(),
                        "--".into(),
                        remote,
                        dest.to_string_lossy().into_owned(),
                    ],
                    cancel,
                )
            }
            Operation::Stage | Operation::Unstage => {
                let root = self.root(&working, cancel)?;
                let mut paths = self.validate_request_paths(&root, &request.paths)?;
                if paths.is_empty() {
                    return Err(VcsError("먼저 파일을 하나 이상 선택하세요.".into()));
                }
                if request.operation == Operation::Unstage {
                    // A staged rename is one UI row but two index paths. Restore both
                    // halves while preserving unrelated staged changes.
                    for change in self.status(&root, cancel)? {
                        if let Some(original) = change.original_path {
                            if paths.contains(&change.path) || paths.contains(&original) {
                                for related in [original, change.path] {
                                    if !paths.contains(&related) {
                                        paths.push(related);
                                    }
                                }
                            }
                        }
                    }
                }
                let mut args = vec!["--literal-pathspecs".into()];
                if request.operation == Operation::Stage {
                    args.push("add".into())
                } else {
                    args.extend(["restore".into(), "--staged".into()])
                };
                args.push("--".into());
                args.extend(paths.clone());
                let result = self.run(&root, args, cancel)?;
                if request.operation == Operation::Unstage
                    && !result.success()
                    && !self
                        .run(
                            &root,
                            vec!["rev-parse".into(), "--verify".into(), "HEAD".into()],
                            cancel,
                        )?
                        .success()
                {
                    let mut args = vec![
                        "--literal-pathspecs".into(),
                        "rm".into(),
                        "--cached".into(),
                        "-f".into(),
                        "-r".into(),
                        "--ignore-unmatch".into(),
                        "--".into(),
                    ];
                    args.extend(paths);
                    return self.required(&root, args, cancel);
                }
                result.ensure()
            }
            Operation::Commit => {
                let root = self.root(&working, cancel)?;
                if request.message.trim().is_empty() {
                    return Err(VcsError("커밋 메시지를 입력하세요.".into()));
                }
                if !self.status(&root, cancel)?.iter().any(FileChange::staged) {
                    return Err(VcsError(
                        "커밋할 스테이징된 파일이 없습니다. 먼저 스테이지하세요.".into(),
                    ));
                }
                self.run_input(
                    &root,
                    vec!["commit".into(), "--file=-".into()],
                    Some(format!("{}\n", request.message)),
                    cancel,
                )?
                .ensure()
            }
            Operation::Update => {
                let root = self.root(&working, cancel)?;
                self.required(&root, vec!["pull".into(), "--ff-only".into()], cancel)
            }
            Operation::Push => {
                let root = self.root(&working, cancel)?;
                self.required(
                    &root,
                    vec![
                        "-c".into(),
                        "push.autoSetupRemote=true".into(),
                        "push".into(),
                    ],
                    cancel,
                )
            }
            Operation::Fetch => {
                let root = self.root(&working, cancel)?;
                self.required(&root, vec!["fetch".into(), "--all".into()], cancel)
            }
        }
    }
    fn comparison(
        &self,
        root: &Path,
        file: &FileChange,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        self.comparison_impl(root, file, staged, cancel)
    }
    fn blame(&self, root: &Path, file: &FileChange, cancel: &CancelToken) -> Result<BlameResult> {
        self.blame_impl(root, file, cancel)
    }
    fn history_details(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        self.history_details_impl(root, revision, cancel)
    }
    fn history_diff(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        self.history_diff_impl(root, revision, file, cancel)
    }
    fn remote(&self, root: &Path, cancel: &CancelToken) -> Result<RemoteInfo> {
        self.remote_info(root, cancel)
    }
    fn set_remote(
        &self,
        root: &Path,
        url: &str,
        expected: &RemoteInfo,
        cancel: &CancelToken,
    ) -> Result<()> {
        self.set_remote_info(root, url, expected, cancel)
    }
    fn conflict(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        self.conflict_file(root, file, cancel)
    }
    fn save_conflict(
        &self,
        expected: &ConflictFile,
        content: &str,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        self.save_conflict_file(expected, content, cancel)
    }
    fn resolve_conflict(&self, expected: &ConflictFile, cancel: &CancelToken) -> Result<()> {
        self.resolve_conflict_file(expected, cancel)
    }
}

fn absolute(path: &Path) -> Result<PathBuf> {
    if path.is_absolute() {
        Ok(path.to_path_buf())
    } else {
        Ok(std::env::current_dir()?.join(path))
    }
}
fn parse_status(output: &str, root: &Path) -> Result<Vec<FileChange>> {
    let mut fields = output.split('\0');
    let mut rows = Vec::new();
    while let Some(field) = fields.next() {
        if field.is_empty() {
            continue;
        }
        if field.len() < 4 {
            return Err(VcsError(
                "Git status 출력 형식을 인식할 수 없습니다.".into(),
            ));
        }
        let xy = &field[..2];
        let path = &field[3..];
        if path.is_empty() {
            return Err(VcsError("Git status에 경로가 없습니다.".into()));
        }
        let index = xy.as_bytes()[0] as char;
        let work = xy.as_bytes()[1] as char;
        let rename = matches!(index, 'R' | 'C') || matches!(work, 'R' | 'C');
        let original = if rename {
            fields.next().map(str::to_owned)
        } else {
            None
        };
        let conflicted = ["DD", "AU", "UD", "UA", "DU", "AA", "UU"].contains(&xy);
        let status = if conflicted {
            "conflicted".to_string()
        } else if rename {
            "renamed".into()
        } else if xy == "??" {
            "untracked".into()
        } else if xy == "!!" {
            "ignored".into()
        } else if work != ' ' && work != '?' {
            String::from("modified")
        } else if index != ' ' && index != '?' {
            String::from("staged")
        } else {
            String::from("modified")
        };
        let path = text::validate_relative(root, path)?;
        let original = original
            .map(|p| text::validate_relative(root, &p))
            .transpose()?;
        rows.push(FileChange {
            path,
            status,
            index_status: if index == ' ' || index == '?' {
                String::new()
            } else {
                index.to_string()
            },
            worktree_status: if work == ' ' || work == '?' {
                String::new()
            } else {
                work.to_string()
            },
            original_path: original,
        });
    }
    Ok(rows)
}
