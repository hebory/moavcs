use moavcs_core::{models::*, process::*, providers::lore::LoreProvider, VcsProvider};
use std::{
    collections::VecDeque,
    path::PathBuf,
    sync::{Arc, Mutex},
};
static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

struct Runner(Mutex<VecDeque<CommandResult>>);
impl CommandRunner for Runner {
    fn run(&self, _: &CommandSpec, c: &CancelToken) -> Result<CommandResult> {
        c.check()?;
        self.0
            .lock()
            .unwrap()
            .pop_front()
            .ok_or_else(|| VcsError("fixture exhausted".into()))
    }
}
fn ok(s: &str) -> CommandResult {
    CommandResult {
        exit_code: 0,
        stdout: s.into(),
        stderr: String::new(),
    }
}
fn temp() -> PathBuf {
    let p = std::env::temp_dir().join(format!("moavcs-lore-test-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(p.join(".lore")).unwrap();
    std::fs::write(
        p.join(".lore/config.toml"),
        "remote_url = \"https://example.test/team/repo\"\n",
    )
    .unwrap();
    p
}

#[test]
fn offline_status_fixture_reports_branch_changes_and_remote() {
    let root = temp();
    let status="Repository local\nOn branch main revision 4 -> abcdef\nChanges staged for commit:\n A staged.txt\nChanges not staged for commit:\n M edited.txt\nUntracked files:\n A new.txt\n";
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(status), ok(status)])))),
    );
    let repo = provider.repository(&root, &CancelToken::new()).unwrap();
    assert_eq!(repo.branch, "main");
    assert!(repo.summary.contains("example.test"));
    let rows = provider.status(&root, &CancelToken::new()).unwrap();
    assert_eq!(rows.len(), 3);
    assert_eq!(rows[0].path, "edited.txt");
    assert_eq!(rows[2].index_status, "A");
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn lore_history_fixture_accepts_only_documented_author_lookup_followup() {
    fn repository() -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "moavcs-lore-history-fixture-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        std::fs::create_dir_all(p.join(".lore")).unwrap();
        std::fs::write(p.join(".lore/config.toml"), "").unwrap();
        p
    }
    let entry=serde_json::json!({"tagName":"revisionHistoryEntry","data":{"revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","revisionNumber":4}}).to_string();
    let primary=serde_json::json!({"tagName":"complete","data":{"status":0,"error":{"errorCode":0,"message":"","traceLocations":[]}}}).to_string();
    let author=serde_json::json!({"tagName":"authUserInfo","data":{"id":"author@example.invalid","name":"Author"}}).to_string();
    let author_complete=serde_json::json!({"tagName":"complete","data":{"status":0,"error":{"errorCode":0,"message":"","traceLocations":[]}}}).to_string();
    let remote_error=serde_json::json!({"tagName":"log","data":{"level":"error","category":0,"timestamp":0,"location":"lore_revision::relay","message":"No remote configured\n  at lore\\src\\auth.rs:794:59"}}).to_string();
    let followup=serde_json::json!({"tagName":"complete","data":{"status":111,"error":{"errorCode":111,"message":"No remote configured","traceLocations":[{"file":"lore\\src\\auth.rs","line":794,"column":59,"context":""}]}}}).to_string();
    // Some inspection commands (including Lore 0.13 `file audit`) end after their primary completion.
    let root = repository();
    let stdout = [entry.clone(), primary.clone()].join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    let history = provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .unwrap();
    assert_eq!(history.entries[0].id, "4");
    let _ = std::fs::remove_dir_all(root);
    // Other commands can emit an author lookup with its own successful completion.
    let root = repository();
    let stdout = [
        entry.clone(),
        primary.clone(),
        author.clone(),
        author_complete,
    ]
    .join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    assert_eq!(
        provider
            .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
            .unwrap()
            .entries[0]
            .id,
        "4"
    );
    let _ = std::fs::remove_dir_all(root);
    // An offline auth lookup is valid only as the exact error log followed by status/error code 111.
    let root = repository();
    let stdout = [
        entry.clone(),
        primary.clone(),
        remote_error.clone(),
        followup.clone(),
    ]
    .join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    let history = provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .unwrap();
    assert_eq!(history.entries[0].id, "4");
    let _ = std::fs::remove_dir_all(root);
    // An incomplete lookup and a duplicate completion without a preceding lookup remain format errors.
    let root = repository();
    let stdout=[entry.clone(),primary.clone(),serde_json::json!({"tagName":"authUserInfo","data":{"id":"author@example.invalid","name":"Author"}}).to_string()].join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    assert!(provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .is_err());
    let _ = std::fs::remove_dir_all(root);
    let root = repository();
    let stdout = [
        entry.clone(),
        primary.clone(),
        remote_error.clone(),
        author,
        followup.clone(),
    ]
    .join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    assert!(provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .is_err());
    let _ = std::fs::remove_dir_all(root);
    let root = repository();
    let unrelated = remote_error.replace("lore_revision::relay", "another_operation");
    let stdout = [entry, primary, unrelated, followup].join("\n");
    let provider = LoreProvider::new(
        "lore".into(),
        Arc::new(Runner(Mutex::new(VecDeque::from([ok(&stdout)])))),
    );
    assert!(provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .is_err());
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn real_lore_offline_workflow_when_executable_is_configured() {
    let Some(exe) = std::env::var_os("MOAVCS_LORE_EXE") else {
        return;
    };
    let root = std::env::temp_dir().join(format!("moavcs-lore-real-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let provider = LoreProvider::new(exe.to_string_lossy().into_owned(), Arc::new(ProcessRunner));
    let init = VcsRequest::new(Operation::Initialize, &root);
    provider
        .execute(&init, &CancelToken::new())
        .expect("Lore offline repository creation must succeed");
    std::fs::write(root.join("offline.txt"), "Lore line one\nLore line two\n").unwrap();
    let mut stage = VcsRequest::new(Operation::Stage, &root);
    stage.paths = vec!["offline.txt".into()];
    provider
        .execute(&stage, &CancelToken::new())
        .expect("Lore stage must succeed");
    let mut commit = VcsRequest::new(Operation::Commit, &root);
    commit.message = "첫 Lore 커밋".into();
    provider
        .execute(&commit, &CancelToken::new())
        .expect("Lore commit must succeed");
    let history = provider
        .history(&root, HistoryScope::CurrentBranch, &CancelToken::new())
        .expect("Lore JSON history must parse");
    assert!(!history.entries.is_empty());
    let id = history.entries[0].id.clone();
    let detail = provider
        .history_details(&root, &id, &CancelToken::new())
        .expect("Lore revision details must parse");
    assert_eq!(detail.files[0].path, "offline.txt");
    let patch = provider
        .history_diff(&root, &id, Some(&detail.files[0]), &CancelToken::new())
        .expect("Lore initial revision file diff must render");
    assert!(patch.contains("+Lore line one"));
    let blame = provider
        .blame(&root, &detail.files[0], &CancelToken::new())
        .expect("Lore audit should provide file blame");
    assert_eq!(blame.lines.len(), 2);
    std::fs::write(root.join("offline.txt"), "Lore line one\nchanged line\n").unwrap();
    let changed = FileChange {
        path: "offline.txt".into(),
        status: "M".into(),
        worktree_status: "M".into(),
        ..Default::default()
    };
    let compare = provider
        .comparison(&root, &changed, false, &CancelToken::new())
        .expect("Lore built-in left/right compare must read the pinned revision");
    assert!(compare.left_text.contains("Lore line two"));
    assert!(compare.right_text.contains("changed line"));
    let _ = std::fs::remove_dir_all(root);
}
