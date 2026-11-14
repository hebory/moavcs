//! Real, offline Git coverage. Everything is created below one uniquely named temp
//! directory; the test never changes the checked-out workspace or uses a network.
use moavcs_core::{
    models::*,
    process::{CancelToken, CommandRunner, CommandSpec, ProcessRunner},
    providers::git::GitProvider,
    VcsProvider,
};
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Arc,
    },
    time::{SystemTime, UNIX_EPOCH},
};

static NEXT: AtomicU64 = AtomicU64::new(0);
fn git_exe() -> String {
    std::env::var("MOAVCS_GIT_EXE").unwrap_or_else(|_| "git".into())
}
fn command(root: &Path, args: &[&str]) -> CommandResult {
    let spec = CommandSpec::new(git_exe(), root, args);
    ProcessRunner
        .run(&spec, &CancelToken::new())
        .expect("Git must run")
}
fn successful(root: &Path, args: &[&str]) {
    let result = command(root, args);
    assert!(
        result.success(),
        "git {:?} failed:\n{}",
        args,
        result.output()
    )
}
fn new_dir() -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let p = std::env::temp_dir().join(format!(
        "moavcs-git-{}-{stamp}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir_all(&p).unwrap();
    p
}
fn req(op: Operation, root: &Path) -> VcsRequest {
    VcsRequest::new(op, root)
}
fn commit(provider: &GitProvider, root: &Path, message: &str) {
    let mut r = req(Operation::Commit, root);
    r.message = message.into();
    provider.execute(&r, &CancelToken::new()).unwrap();
}
struct LocalAppDataGuard(Option<OsString>);
impl LocalAppDataGuard {
    fn isolate(path: &Path) -> Self {
        let previous = std::env::var_os("LOCALAPPDATA");
        std::env::set_var("LOCALAPPDATA", path);
        Self(previous)
    }
}
impl Drop for LocalAppDataGuard {
    fn drop(&mut self) {
        if let Some(previous) = self.0.take() {
            std::env::set_var("LOCALAPPDATA", previous)
        } else {
            std::env::remove_var("LOCALAPPDATA")
        }
    }
}

#[test]
fn real_git_workflow_preserves_literal_paths_history_remote_and_conflicts() {
    let exe = git_exe();
    let base = new_dir().join("한글 repo");
    std::fs::create_dir_all(&base).unwrap();
    let local_appdata = base.parent().unwrap().join("localappdata");
    let local_appdata_guard = LocalAppDataGuard::isolate(&local_appdata);
    let provider = GitProvider::new(exe, Arc::new(ProcessRunner));
    let init = req(Operation::Initialize, &base);
    provider.execute(&init, &CancelToken::new()).unwrap();
    let unborn = provider
        .history(&base, HistoryScope::CurrentBranch, &CancelToken::new())
        .unwrap();
    assert!(unborn.git.as_ref().unwrap().is_unborn);
    assert!(
        !unborn.git.as_ref().unwrap().current_branch.is_empty(),
        "unborn branch name must remain visible"
    );
    successful(&base, &["config", "user.name", "MoaVCS Integration"]);
    successful(&base, &["config", "user.email", "moavcs@example.invalid"]);

    // The literal metacharacters and Unicode in this filename exercise pathspec safety.
    let file = "한글 [literal].txt";
    std::fs::write(base.join(file), "first\nsecond\n").unwrap();
    let mut stage = req(Operation::Stage, &base);
    stage.paths = vec![file.into()];
    provider.execute(&stage, &CancelToken::new()).unwrap();
    commit(&provider, &base, "첫 커밋");
    assert!(provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .is_empty());
    let first = provider
        .history(&base, HistoryScope::CurrentBranch, &CancelToken::new())
        .unwrap();
    assert_eq!(first.entries.len(), 1);
    let first_id = first.entries[0].id.clone();
    let first_details = provider
        .history_details(&base, &first_id, &CancelToken::new())
        .unwrap();
    assert_eq!(first_details.files[0].path, file);
    assert!(provider
        .history_diff(
            &base,
            &first_id,
            Some(&first_details.files[0]),
            &CancelToken::new()
        )
        .unwrap()
        .contains("+first"));

    // Worktree vs index comparison, stage/unstage, then rename metadata.
    std::fs::write(base.join(file), "first\nworking change\n").unwrap();
    let change = provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .into_iter()
        .find(|x| x.path == file)
        .unwrap();
    let comparison = provider
        .comparison(&base, &change, false, &CancelToken::new())
        .unwrap();
    assert!(comparison.left_text.contains("second"));
    assert!(comparison.right_text.contains("working change"));
    let added = "new [file].txt";
    std::fs::write(base.join(added), "new file\n").unwrap();
    stage.paths = vec![added.into()];
    provider.execute(&stage, &CancelToken::new()).unwrap();
    assert!(provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .iter()
        .any(|x| x.path == added && x.staged()));
    let mut unstage = req(Operation::Unstage, &base);
    unstage.paths = vec![added.into()];
    provider.execute(&unstage, &CancelToken::new()).unwrap();
    assert!(provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .iter()
        .any(|x| x.path == added && x.status == "untracked"));
    // Restore the original committed bytes so Git's default rename similarity threshold
    // recognizes a pure rename instead of treating it as delete plus add.
    std::fs::write(base.join(file), "first\nsecond\n").unwrap();
    std::fs::rename(base.join(file), base.join("이름 변경.txt")).unwrap();
    stage.paths = vec![file.into(), "이름 변경.txt".into(), added.into()];
    provider.execute(&stage, &CancelToken::new()).unwrap();
    let mut unstage_rename = req(Operation::Unstage, &base);
    unstage_rename.paths = vec!["이름 변경.txt".into()];
    provider
        .execute(&unstage_rename, &CancelToken::new())
        .unwrap();
    let after_unstage = provider.status(&base, &CancelToken::new()).unwrap();
    assert!(
        after_unstage
            .iter()
            .filter(|row| row.path == file || row.path == "이름 변경.txt")
            .all(|row| !row.staged()),
        "both index paths of a rename must be unstaged"
    );
    assert!(
        after_unstage
            .iter()
            .any(|row| row.path == added && row.staged()),
        "unrelated staged files must stay staged"
    );
    provider.execute(&stage, &CancelToken::new()).unwrap();
    commit(&provider, &base, "경로 변경 및 두 번째 커밋");
    let history = provider
        .history(&base, HistoryScope::AllBranches, &CancelToken::new())
        .unwrap();
    assert!(history.entries.len() >= 2);
    assert!(history.git.is_some());
    let detail = provider
        .history_details(&base, &history.entries[0].id, &CancelToken::new())
        .unwrap();
    assert!(
        detail
            .files
            .iter()
            .any(|f| f.original_path.as_deref() == Some(file) && f.path == "이름 변경.txt"),
        "unexpected detail files: {:?}",
        detail.files
    );
    let file_change = FileChange {
        path: "이름 변경.txt".into(),
        status: "modified".into(),
        index_status: String::new(),
        worktree_status: String::new(),
        original_path: None,
    };
    let blame = provider
        .blame(&base, &file_change, &CancelToken::new())
        .unwrap();
    assert_eq!(blame.lines.len(), 2);
    assert_eq!(blame.lines[0].author, "MoaVCS Integration");

    // Local bare remote: push auto-establishes upstream, clone and fetch are offline.
    let bare = base.parent().unwrap().join("origin.git");
    successful(
        base.parent().unwrap(),
        &["init", "--bare", bare.to_str().unwrap()],
    );
    let before = provider.remote(&base, &CancelToken::new()).unwrap();
    assert!(!before.exists);
    provider
        .set_remote(&base, bare.to_str().unwrap(), &before, &CancelToken::new())
        .unwrap();
    let push = req(Operation::Push, &base);
    provider.execute(&push, &CancelToken::new()).unwrap();
    let context = provider
        .history(&base, HistoryScope::CurrentBranch, &CancelToken::new())
        .unwrap()
        .git
        .unwrap();
    assert!(!context.upstream.is_empty());
    assert_eq!(context.ahead, Some(0));
    assert_eq!(context.behind, Some(0));
    let mirror = base.parent().unwrap().join("second-fetch.git");
    let mirror_url = mirror.to_string_lossy().into_owned();
    successful(
        &base,
        &[
            "config",
            "--local",
            "--add",
            "remote.origin.url",
            &mirror_url,
        ],
    );
    let multiple = provider.remote(&base, &CancelToken::new()).unwrap();
    assert_eq!(multiple.fetch_urls.len(), 2);
    assert!(multiple.read_only_reason.is_none());
    let replacement = base.parent().unwrap().join("replacement.git");
    let replacement_url = replacement.to_string_lossy().into_owned();
    successful(
        base.parent().unwrap(),
        &["init", "--bare", &replacement_url],
    );
    provider
        .set_remote(&base, &replacement_url, &multiple, &CancelToken::new())
        .unwrap();
    let after = provider.remote(&base, &CancelToken::new()).unwrap();
    assert_eq!(after.fetch_urls, vec![replacement_url.clone(), mirror_url]);
    let clone = base.parent().unwrap().join("clone");
    let mut clone_req = req(Operation::Clone, base.parent().unwrap());
    clone_req.remote = bare.to_string_lossy().into_owned();
    clone_req.destination = clone.clone();
    provider.execute(&clone_req, &CancelToken::new()).unwrap();
    assert_eq!(
        provider
            .history(&clone, HistoryScope::CurrentBranch, &CancelToken::new())
            .unwrap()
            .entries
            .len(),
        history.entries.len()
    );
    let opened = provider.remote(&base, &CancelToken::new()).unwrap();
    successful(
        &base,
        &[
            "config",
            "--local",
            "--add",
            "remote.origin.url",
            &replacement_url,
        ],
    );
    let duplicated = provider.remote(&base, &CancelToken::new()).unwrap();
    assert!(duplicated.read_only_reason.is_some());
    let bare_url = bare.to_string_lossy().into_owned();
    assert!(provider
        .set_remote(&base, &bare_url, &duplicated, &CancelToken::new())
        .is_err());
    assert!(provider
        .set_remote(&base, &bare_url, &opened, &CancelToken::new())
        .is_err());

    // Build two conflicting branches and verify the stale-safe native resolution API.
    std::fs::write(base.join("conflict.txt"), "base\n").unwrap();
    stage.paths = vec!["conflict.txt".into()];
    provider.execute(&stage, &CancelToken::new()).unwrap();
    commit(&provider, &base, "충돌 기준");
    successful(&base, &["branch", "conflict-base"]);
    successful(&base, &["switch", "-c", "conflict-one"]);
    std::fs::write(base.join("conflict.txt"), "one\n").unwrap();
    stage.paths = vec!["conflict.txt".into()];
    provider.execute(&stage, &CancelToken::new()).unwrap();
    commit(&provider, &base, "첫 변경");
    successful(&base, &["switch", "conflict-base"]);
    successful(&base, &["switch", "-c", "conflict-two"]);
    std::fs::write(base.join("conflict.txt"), "two\n").unwrap();
    provider.execute(&stage, &CancelToken::new()).unwrap();
    commit(&provider, &base, "둘째 변경");
    let merge = command(&base, &["merge", "conflict-one"]);
    assert!(!merge.success());
    let conflict = provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .into_iter()
        .find(|x| x.path == "conflict.txt")
        .unwrap();
    assert!(conflict.conflicted());
    let opened = provider
        .conflict(&base, &conflict, &CancelToken::new())
        .unwrap();
    assert!(
        opened.left_text.contains("two"),
        "left side is the current branch's stage-2 content"
    );
    assert!(
        opened.right_text.contains("one"),
        "right side is the incoming branch's stage-3 content"
    );
    let saved = provider
        .save_conflict(&opened, "resolved\n", &CancelToken::new())
        .unwrap();
    assert!(saved
        .backup_path
        .as_ref()
        .is_some_and(|p| p.starts_with(&local_appdata)));
    provider
        .resolve_conflict(&saved, &CancelToken::new())
        .unwrap();
    assert!(!provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .iter()
        .any(FileChange::conflicted));
    assert!(provider
        .status(&base, &CancelToken::new())
        .unwrap()
        .iter()
        .any(|x| x.path == "conflict.txt" && x.staged()));
    commit(&provider, &base, "충돌 해결");
    drop(local_appdata_guard);
    std::fs::remove_dir_all(base.parent().unwrap()).unwrap();
}

#[test]
fn remote_url_from_included_config_is_reported_read_only() {
    let base = new_dir().join("included-config-repo");
    std::fs::create_dir_all(&base).unwrap();
    let provider = GitProvider::new(git_exe(), Arc::new(ProcessRunner));
    provider
        .execute(&req(Operation::Initialize, &base), &CancelToken::new())
        .unwrap();
    let included = base.parent().unwrap().join("remote-include.ini");
    std::fs::write(
        &included,
        "[remote \"origin\"]\n\turl = https://example.test/included.git\n",
    )
    .unwrap();
    let include_path = included.to_string_lossy().replace('\\', "/");
    successful(
        &base,
        &["config", "--local", "--add", "include.path", &include_path],
    );
    let remote = provider.remote(&base, &CancelToken::new()).unwrap();
    assert!(remote.exists);
    assert_eq!(remote.fetch_urls.len(), 1);
    assert_eq!(remote.fetch_urls[0], "https://example.test/included.git");
    assert!(remote
        .read_only_reason
        .as_deref()
        .is_some_and(|s| s.contains("전역, include 또는 worktree")));
    let _ = std::fs::remove_dir_all(base.parent().unwrap());
}
