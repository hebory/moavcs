use moavcs_core::{
    models::*,
    process::*,
    providers::{git::GitProvider, perforce::PerforceProvider, svn::SvnProvider},
    VcsProvider,
};
use std::{
    collections::VecDeque,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

struct FixtureRunner(Mutex<VecDeque<CommandResult>>);
impl FixtureRunner {
    fn new(values: impl IntoIterator<Item = CommandResult>) -> Self {
        Self(Mutex::new(values.into_iter().collect()))
    }
}
impl CommandRunner for FixtureRunner {
    fn run(&self, _: &CommandSpec, cancel: &CancelToken) -> Result<CommandResult> {
        cancel.check()?;
        self.0
            .lock()
            .unwrap()
            .pop_front()
            .ok_or_else(|| VcsError("fixture response exhausted".into()))
    }
}
fn result(stdout: &str) -> CommandResult {
    CommandResult {
        exit_code: 0,
        stdout: stdout.into(),
        stderr: String::new(),
    }
}
fn root() -> PathBuf {
    std::env::current_dir().unwrap()
}

#[test]
fn git_porcelain_fixture_preserves_untracked_and_conflict_states() {
    let stdout = " M tracked.txt\0?? new file.txt\0UU conflict.txt\0";
    let provider = GitProvider::new(
        "git".into(),
        Arc::new(FixtureRunner::new([result("C:/repo\n"), result(stdout)])),
    );
    let rows = provider
        .status(Path::new("."), &CancelToken::new())
        .unwrap();
    assert_eq!(rows.len(), 3);
    assert_eq!(rows[0].path, "tracked.txt");
    assert_eq!(rows[1].status, "untracked");
    assert!(rows[2].conflicted());
}

#[test]
fn svn_xml_fixture_maps_properties_and_text_conflicts() {
    let info="<?xml version=\"1.0\"?><info><entry path=\".\" revision=\"7\"><url>https://example.test/repo/trunk</url><relative-url>^/trunk</relative-url><repository><root>https://example.test/repo</root></repository><wc-info><wcroot-abspath>C:/repo</wcroot-abspath></wc-info></entry></info>";
    let status="<?xml version=\"1.0\"?><status><target path=\"C:/repo\"><entry path=\"C:/repo/a.txt\"><wc-status item=\"modified\" props=\"none\"/></entry><entry path=\"C:/repo/b.txt\"><wc-status item=\"conflicted\" props=\"none\" tree-conflicted=\"false\"/></entry></target></status>";
    let provider = SvnProvider::new(
        "svn".into(),
        Arc::new(FixtureRunner::new([result(info), result(status)])),
    );
    let rows = provider
        .status(Path::new("."), &CancelToken::new())
        .unwrap();
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].path, "a.txt");
    assert!(rows[1].conflicted());
}

#[test]
fn p4_tagged_fixture_keeps_status_instead_of_reporting_clean() {
    let workspace = root().to_string_lossy().replace('\\', "/");
    let info=format!("... user tester\n... client ws\n... clientRoot {workspace}\n... serverAddress ssl:example.test:1666\n");
    let status = format!("... clientFile {workspace}/src/a.cpp\n... action edit\n");
    let provider = PerforceProvider::new(
        "p4".into(),
        Arc::new(FixtureRunner::new([result(&info), result(&status)])),
    );
    let rows = provider.status(&root(), &CancelToken::new()).unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].status, "edit");
    assert_eq!(rows[0].path, "src/a.cpp");
}

#[test]
fn p4_json_describe_fixture_exposes_file_level_history() {
    let workspace = root().to_string_lossy().replace('\\', "/");
    let info=format!("... user tester\n... client ws\n... clientRoot {workspace}\n... serverAddress ssl:example.test:1666\n");
    let describe = r#"{"change":42,"status":"submitted","user":"alice","time":1700000000,"desc":"change message","depotFile0":"//depot/main/a.cpp","rev0":7,"action0":"edit","type0":"text"}"#;
    let provider = PerforceProvider::new(
        "p4".into(),
        Arc::new(FixtureRunner::new([result(&info), result(describe)])),
    );
    let details = provider
        .history_details(&root(), "42", &CancelToken::new())
        .unwrap();
    assert_eq!(details.entry.id, "42");
    assert_eq!(details.entry.author, "alice");
    assert_eq!(details.files.len(), 1);
    assert_eq!(details.files[0].path, "//depot/main/a.cpp");
    assert_eq!(details.files[0].status, "modified");
}

#[test]
fn p4_history_diff_fixture_reads_fixed_revisions_and_preserves_line_markers() {
    let workspace = root().to_string_lossy().replace('\\', "/");
    let info=format!("... user tester\n... client ws\n... clientRoot {workspace}\n... serverAddress ssl:example.test:1666\n");
    let describe = r#"{"change":"42","status":"submitted","user":"alice","time":"1700000000","desc":"change","depotFile0":"//depot/old%40name.txt","action0":"delete","rev0":"3","type0":"text","depotFile1":"//depot/new.txt","action1":"add","rev1":"1","type1":"text","depotFile2":"//depot/edit.txt","action2":"edit","rev2":"5","type2":"text","depotFile3":"//depot/data.bin","action3":"edit","rev3":"2","type3":"binary"}"#;
    let mut responses = vec![
        result(&info),
        result(describe),
        result(&info),
        result(describe),
        result("deleted content\n"),
        result(&info),
        result(describe),
        result("new content"),
        result(&info),
        result(describe),
        result("--- previous\n+++ committed\n"),
        result(&info),
        result(describe),
    ];
    let provider = PerforceProvider::new(
        "p4".into(),
        Arc::new(FixtureRunner::new(responses.drain(..))),
    );
    let details = provider
        .history_details(&root(), "42", &CancelToken::new())
        .unwrap();
    assert_eq!(details.files.len(), 4);
    let deleted = provider
        .history_diff(&root(), "42", Some(&details.files[0]), &CancelToken::new())
        .unwrap();
    assert!(deleted.contains("-deleted content"));
    let added = provider
        .history_diff(&root(), "42", Some(&details.files[1]), &CancelToken::new())
        .unwrap();
    assert!(added.contains("+new content"));
    assert!(added.contains("No newline at end of file"));
    let modified = provider
        .history_diff(&root(), "42", Some(&details.files[2]), &CancelToken::new())
        .unwrap();
    assert!(modified.contains("--- previous"));
    assert!(provider
        .history_diff(&root(), "42", Some(&details.files[3]), &CancelToken::new())
        .is_err());
}
