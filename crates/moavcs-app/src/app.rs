mod dialogs;
mod git_sync;
mod inspection;
mod inspection_data;
#[cfg(test)]
mod interaction_tests;
#[cfg(test)]
mod performance;
mod theme;
mod workspace;
use theme::*;

use crate::history_graph::GraphLayout;
use crate::launch::LaunchOptions;
use eframe::egui::{self, Align, Color32, Layout, RichText, ScrollArea, TextEdit};
use moavcs_core::{
    checkout, create_provider, detect_repository, external,
    models::*,
    process::CancelToken,
    providers::VcsProvider,
    settings::{AppSettings, ExternalToolKind, SettingsStore},
};
use std::{
    collections::{BTreeSet, HashMap},
    path::{Path, PathBuf},
    sync::{
        mpsc::{Receiver, Sender, TryRecvError},
        Arc,
    },
    thread,
};

#[derive(Clone, Copy, PartialEq, Eq, Default)]
enum Tab {
    #[default]
    Changes,
    History,
    Output,
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum Dialog {
    Checkout,
    Settings,
    Remote,
    ConfirmSync,
}
#[derive(Clone, Copy, PartialEq, Eq, Default)]
enum SettingsTab {
    #[default]
    Vcs,
    FileTools,
    Explorer,
}
#[derive(Clone)]
pub(super) enum Inspection {
    Diff {
        data: Arc<FileComparison>,
        prepared: Arc<inspection_data::PreparedDiff>,
    },
    Blame(BlameResult),
}
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
enum ReadSlot {
    Open,
    Status,
    GitSync,
    History,
    Details,
    HistoryDiff,
    Diff,
    Inspect,
    Remote,
    Conflict,
}
struct PendingRead {
    id: u64,
    cancel: CancelToken,
    label: String,
}
pub(super) enum Event {
    Opened(
        u64,
        std::result::Result<(Arc<dyn VcsProvider>, RepositoryInfo, Vec<FileChange>), String>,
    ),
    Status(u64, std::result::Result<Vec<FileChange>, String>),
    GitSync(u64, std::result::Result<Option<GitHistoryContext>, String>),
    History(u64, std::result::Result<HistorySnapshot, String>),
    Details(u64, String, std::result::Result<HistoryDetails, String>),
    Hdiff(
        u64,
        String,
        String,
        std::result::Result<(String, PreparedText), String>,
    ),
    Diff(
        u64,
        String,
        std::result::Result<(String, PreparedText), String>,
    ),
    Inspect(u64, String, std::result::Result<Inspection, String>),
    Remote(u64, std::result::Result<RemoteInfo, String>),
    RemoteSaved(u64, std::result::Result<(), String>),
    Done(u64, String, std::result::Result<String, String>),
    Conflict(u64, String, std::result::Result<ConflictFile, String>),
    ConflictSaved(u64, std::result::Result<ConflictFile, String>),
    ConflictResolved(u64, std::result::Result<(), String>),
}

pub struct MoaApp {
    tx: Sender<Event>,
    rx: Receiver<Event>,
    settings_store: SettingsStore,
    settings: AppSettings,
    startup: LaunchOptions,
    startup_action_pending: bool,
    generation: u64,
    cancel: Option<CancelToken>,
    busy: Option<String>,
    next_read: u64,
    read_slots: HashMap<ReadSlot, PendingRead>,
    provider: Option<Arc<dyn VcsProvider>>,
    repo: Option<RepositoryInfo>,
    changes: Vec<FileChange>,
    git_file_counts: workspace::GitFileCounts,
    git_sync: Option<GitHistoryContext>,
    git_sync_error: Option<String>,
    picked: BTreeSet<String>,
    file: Option<String>,
    diff: String,
    tab: Tab,
    scope: HistoryScope,
    history: HistorySnapshot,
    history_filter: Option<HistoryRelation>,
    visible_history: Vec<usize>,
    history_graph: GraphLayout,
    revision: Option<String>,
    details: Option<HistoryDetails>,
    history_file: Option<String>,
    hdiff: String,
    logs: Vec<String>,
    logs_text: String,
    notice: Option<String>,
    dialog: Option<Dialog>,
    pending_sync: Option<Operation>,
    settings_tab: SettingsTab,
    query: String,
    message: String,
    staged: bool,
    worktree_split: f32,
    history_split: f32,
    checkout_kind: VcsKind,
    checkout_url: String,
    checkout_parent: String,
    checkout_name: String,
    name_touched: bool,
    remote: Option<RemoteInfo>,
    remote_url: String,
    remote_expected: Option<RemoteInfo>,
    inspection: Option<Inspection>,
    prepared_diff: Option<Arc<inspection_data::PreparedDiff>>,
    inspection_search: String,
    inspection_status: String,
    inspection_change_index: usize,
    diff_view: PreparedText,
    history_diff_view: PreparedText,
    logs_view: PreparedText,
    has_conflicts: bool,
    staged_file_count: usize,
    selected_conflicted: bool,
    conflict: Option<Arc<ConflictFile>>,
    conflict_view: Option<Arc<inspection_data::PreparedConflict>>,
    conflict_text: String,
    conflict_selection: Option<(usize, usize)>,
    smoke_output: Option<PathBuf>,
    smoke_step: u8,
    smoke_next: u8,
    smoke_capture: Option<String>,
    smoke_capture_requested: bool,
    smoke_capture_frames: u8,
    smoke_capture_assertions: serde_json::Value,
    smoke_all_assertions: serde_json::Map<String, serde_json::Value>,
}
impl MoaApp {
    pub fn new(startup: LaunchOptions, rx: Receiver<Event>, tx: Sender<Event>) -> Self {
        let store = if let Some(path) = startup.ui_smoke.as_ref() {
            SettingsStore::new(path.join(".smoke-settings.json"))
        } else {
            SettingsStore::default()
        };
        let settings = store.load().unwrap_or_default();
        let parent = startup
            .path
            .as_ref()
            .map(|p| {
                if p.is_file() {
                    p.parent().unwrap_or(p)
                } else {
                    p
                }
                .display()
                .to_string()
            })
            .unwrap_or_default();
        let smoke_output = startup.ui_smoke.clone();
        let mut a = Self {
            tx,
            rx,
            settings_store: store,
            settings,
            startup: startup.clone(),
            startup_action_pending: startup.path.is_some(),
            generation: 0,
            cancel: None,
            busy: None,
            next_read: 0,
            read_slots: HashMap::new(),
            provider: None,
            repo: None,
            changes: vec![],
            git_file_counts: Default::default(),
            git_sync: None,
            git_sync_error: None,
            picked: BTreeSet::new(),
            file: None,
            diff: String::new(),
            tab: Tab::Changes,
            scope: HistoryScope::CurrentBranch,
            history: HistorySnapshot::default(),
            history_filter: None,
            visible_history: vec![],
            history_graph: GraphLayout::default(),
            revision: None,
            details: None,
            history_file: None,
            hdiff: String::new(),
            logs: vec![],
            logs_text: String::new(),
            notice: None,
            dialog: None,
            pending_sync: None,
            settings_tab: SettingsTab::default(),
            query: String::new(),
            message: String::new(),
            staged: false,
            worktree_split: 0.32,
            history_split: 0.42,
            checkout_kind: startup.vcs.unwrap_or_default(),
            checkout_url: String::new(),
            checkout_parent: parent,
            checkout_name: String::new(),
            name_touched: false,
            remote: None,
            remote_url: String::new(),
            remote_expected: None,
            inspection: None,
            prepared_diff: None,
            inspection_search: String::new(),
            inspection_status: String::new(),
            inspection_change_index: 0,
            diff_view: PreparedText::default(),
            history_diff_view: PreparedText::default(),
            logs_view: PreparedText::default(),
            has_conflicts: false,
            staged_file_count: 0,
            selected_conflicted: false,
            conflict: None,
            conflict_view: None,
            conflict_text: String::new(),
            conflict_selection: None,
            smoke_output,
            smoke_step: 0,
            smoke_next: 0,
            smoke_capture: None,
            smoke_capture_requested: false,
            smoke_capture_frames: 0,
            smoke_capture_assertions: serde_json::json!({}),
            smoke_all_assertions: serde_json::Map::new(),
        };
        if let Some(path) = startup.path.clone() {
            if startup.action == "clone" || startup.action == "init" {
                a.dialog = Some(Dialog::Checkout)
            } else {
                a.open(path, startup.vcs)
            }
        } else if startup.action == "settings" {
            a.dialog = Some(Dialog::Settings)
        } else if startup.action == "clone" || startup.action == "init" {
            a.dialog = Some(Dialog::Checkout)
        }
        a
    }
    fn begin(&mut self, label: &str) -> (u64, CancelToken) {
        self.cancel_reads();
        if let Some(c) = self.cancel.take() {
            c.cancel()
        }
        self.generation += 1;
        let t = CancelToken::new();
        self.cancel = Some(t.clone());
        self.busy = Some(label.into());
        (self.generation, t)
    }
    fn finish(&mut self, g: u64) -> bool {
        if g != self.generation {
            return false;
        }
        self.busy = None;
        self.cancel = None;
        true
    }
    fn begin_read(&mut self, slot: ReadSlot, label: &str) -> (u64, CancelToken) {
        self.cancel_read_slot(slot);
        self.next_read = self.next_read.wrapping_add(1);
        let id = self.next_read;
        let cancel = CancelToken::new();
        self.read_slots.insert(
            slot,
            PendingRead {
                id,
                cancel: cancel.clone(),
                label: label.to_owned(),
            },
        );
        (id, cancel)
    }
    fn cancel_read_slot(&mut self, slot: ReadSlot) {
        if let Some(pending) = self.read_slots.remove(&slot) {
            pending.cancel.cancel();
        }
    }
    fn finish_read(&mut self, slot: ReadSlot, id: u64) -> bool {
        if self
            .read_slots
            .get(&slot)
            .is_some_and(|pending| pending.id == id)
        {
            self.read_slots.remove(&slot);
            return true;
        }
        false
    }
    fn cancel_reads(&mut self) {
        if self.read_slots.contains_key(&ReadSlot::Open) {
            self.startup_action_pending = false;
        }
        for (_, pending) in self.read_slots.drain() {
            pending.cancel.cancel();
        }
    }
    fn controls_locked(&self) -> bool {
        self.busy.is_some()
            || self.pending_sync.is_some()
            || self.read_slots.contains_key(&ReadSlot::Open)
    }
    fn take_startup_action(&mut self) -> Option<String> {
        if !self.startup_action_pending {
            return None;
        }
        self.startup_action_pending = false;
        Some(self.startup.action.clone())
    }
    fn is_loading(&self) -> bool {
        !self.read_slots.is_empty()
    }
    fn log(&mut self, s: impl Into<String>) {
        self.logs.push(s.into());
        if self.logs.len() > 400 {
            self.logs.drain(..100);
        }
        self.logs_text = self.logs.join("\n\n");
        self.logs_view = PreparedText::new(&self.logs_text);
    }
    fn pin_recent(&mut self, path: &str, pinned: bool) {
        match self.settings_store.pin(path, pinned) {
            Ok(settings) => self.settings = settings,
            Err(error) => self.notice = Some(error.to_string()),
        }
    }
    fn remove_recent(&mut self, path: &str) {
        match self.settings_store.remove(path) {
            Ok(settings) => self.settings = settings,
            Err(error) => self.notice = Some(error.to_string()),
        }
    }
    fn save_preferences(&mut self) {
        match self.settings_store.save_preferences(&self.settings) {
            Ok(()) => {
                self.dialog = None;
                self.notice = Some("설정을 저장했습니다.".into())
            }
            Err(error) => self.notice = Some(error.to_string()),
        }
    }
    fn set_checkout_url(&mut self, url: String) {
        self.checkout_url = url;
        if !self.name_touched {
            self.checkout_name = checkout::repository_name(&self.checkout_url).unwrap_or_default();
        }
    }
    fn smoke_assert(&mut self, name: &str, condition: bool, detail: &str) {
        if !condition {
            if let Some(out) = &self.smoke_output {
                let message = format!("{name}: {detail}");
                let _ = std::fs::write(out.join("failure.txt"), &message);
                eprintln!("UI smoke failed: {message}");
            }
            std::process::exit(5);
        }
    }
    fn smoke_capture(
        &mut self,
        ctx: &egui::Context,
        name: &str,
        next: u8,
        assertions: serde_json::Value,
    ) {
        self.smoke_capture = Some(name.into());
        self.smoke_capture_requested = false;
        self.smoke_capture_frames = 3;
        self.smoke_next = next;
        self.smoke_capture_assertions = assertions;
        ctx.request_repaint();
    }
    fn smoke_collect_screenshot(&mut self, ctx: &egui::Context) {
        let Some(output) = self.smoke_output.clone() else {
            return;
        };
        if self.smoke_capture.is_some() && !self.smoke_capture_requested {
            if self.smoke_capture_frames > 0 {
                self.smoke_capture_frames -= 1;
                ctx.request_repaint_after(std::time::Duration::from_millis(90));
                return;
            }
            self.smoke_capture_requested = true;
            ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot);
            ctx.request_repaint_after(std::time::Duration::from_millis(150));
            return;
        }
        let Some(name) = self.smoke_capture.clone() else {
            return;
        };
        let image = ctx.input(|input| {
            input.raw.events.iter().find_map(|event| {
                if let egui::Event::Screenshot { image, .. } = event {
                    Some(image.clone())
                } else {
                    None
                }
            })
        });
        let Some(image) = image else { return };
        for (window, rect) in observed_windows(ctx) {
            let viewport = ctx.screen_rect().expand(2.0);
            self.smoke_assert(
                "window fits viewport",
                viewport.contains_rect(rect),
                &format!("{window}: {rect:?} outside {viewport:?}"),
            );
        }
        let result = (|| -> std::result::Result<(), String> {
            let mut pixels = Vec::with_capacity(image.pixels.len() * 4);
            for pixel in &image.pixels {
                pixels.extend_from_slice(&pixel.to_array());
            }
            image::save_buffer(
                output.join(format!("{name}.png")),
                &pixels,
                image.size[0] as u32,
                image.size[1] as u32,
                image::ColorType::Rgba8,
            )
            .map_err(|error| error.to_string())?;
            let assertions = self.smoke_capture_assertions.clone();
            std::fs::write(
                output.join(format!("{name}.assertions.json")),
                serde_json::to_vec_pretty(&assertions).map_err(|error| error.to_string())?,
            )
            .map_err(|error| error.to_string())?;
            Ok(())
        })();
        if let Err(error) = result {
            self.smoke_assert("screenshot", false, &error);
        }
        self.smoke_all_assertions
            .insert(name, self.smoke_capture_assertions.clone());
        self.smoke_capture = None;
        self.smoke_capture_requested = false;
        self.smoke_capture_frames = 0;
        self.smoke_step = self.smoke_next;
    }
    fn smoke_tick(&mut self, ctx: &egui::Context) {
        let Some(output) = self.smoke_output.clone() else {
            return;
        };
        let phase_id = egui::Id::new("smoke_phase_log");
        let phase_changed = ctx.data_mut(|data| {
            let changed = data.get_temp::<u8>(phase_id) != Some(self.smoke_step);
            if changed {
                data.insert_temp(phase_id, self.smoke_step);
            }
            changed
        });
        if phase_changed {
            println!("UI smoke step {}", self.smoke_step);
        }
        if self.smoke_capture.is_some() || self.busy.is_some() || self.is_loading() {
            return;
        }
        let Some(repo) = self.repo.clone() else {
            return;
        };
        let changed = self
            .changes
            .iter()
            .find(|file| file.path == "tracked.txt")
            .cloned();
        match self.smoke_step {
            0 => {
                self.smoke_assert(
                    "workspace repository",
                    repo.kind == VcsKind::Git,
                    "Git fixture did not open",
                );
                self.smoke_assert(
                    "status row",
                    changed.is_some(),
                    "modified tracked.txt row missing",
                );
                let pending_worktree = changed.as_ref().is_some_and(|file| {
                    !file.staged()
                        && !file.worktree_status.trim().is_empty()
                        && file.worktree_status != "?"
                });
                self.smoke_assert(
                    "publication before staging",
                    pending_worktree,
                    "fixture edit was not shown as an unstaged worktree change",
                );
                let baseline_head = crate::smoke::head_id(&repo.root).unwrap_or_default();
                self.smoke_assert(
                    "publication baseline HEAD",
                    !baseline_head.is_empty(),
                    "could not read fixture HEAD before staging",
                );
                self.smoke_all_assertions.insert(
                    "publication_baseline".into(),
                    serde_json::json!({"head":baseline_head,"worktree_change":pending_worktree,"staged":false}),
                );
                self.smoke_assert(
                    "sidebar recent repository",
                    self.settings
                        .recent_repositories
                        .iter()
                        .any(|item| item.path == repo.root.to_string_lossy()),
                    "opened repository was not added to the sidebar",
                );
                let file = changed.expect("checked above");
                if self.file.as_deref() != Some(file.path.as_str()) {
                    self.select_file(file.path.clone());
                }
                self.tab = Tab::Changes;
                if self.diff.is_empty() {
                    if let Some(error) = self.notice.clone() {
                        self.smoke_assert("diff preview", false, &error);
                    }
                    if self.busy.is_none() && !self.is_loading() {
                        self.load_diff();
                    }
                    return;
                }
                self.smoke_assert("diff preview", true, "selected file preview is populated");
                self.smoke_capture(ctx,"workspace",1,serde_json::json!({"repository_opened":true,"modified_file_visible":true,"branch":repo.branch,"status":file.status,"unstaged_before_stage":pending_worktree}));
            }
            1 => {
                let path = changed
                    .as_ref()
                    .map(|file| file.path.clone())
                    .unwrap_or_else(|| "tracked.txt".into());
                self.picked.insert(path);
                self.execute(Operation::Stage, String::new(), vec!["tracked.txt".into()]);
                self.smoke_step = 2;
            }
            2 => {
                let staged = changed.as_ref().is_some_and(FileChange::staged);
                self.smoke_assert(
                    "stage operation",
                    staged,
                    "stage button action did not stage tracked.txt",
                );
                self.smoke_all_assertions.insert(
                    "stage_operation".into(),
                    serde_json::json!({"staged": staged, "path": "tracked.txt"}),
                );
                self.staged = true;
                self.load_diff();
                self.smoke_step = 22;
            }
            22 => {
                if self.busy.is_some() || self.is_loading() {
                    return;
                }
                let staged_preview = self.diff.contains("UI 변경 내용");
                self.smoke_assert(
                    "staged diff preview",
                    staged_preview,
                    "staged checkbox did not show the index diff",
                );
                self.smoke_all_assertions.insert(
                    "staged_diff_preview".into(),
                    serde_json::json!({"visible": staged_preview}),
                );
                self.smoke_capture(
                    ctx,
                    "staged",
                    24,
                    serde_json::json!({"staged_before_commit":true,"path":"tracked.txt"}),
                );
            }
            24 => {
                self.staged = false;
                self.execute(Operation::Commit, "MoaVCS smoke commit".into(), vec![]);
                self.smoke_step = 3;
            }
            3 => {
                let clean = self.changes.is_empty();
                let baseline = self
                    .smoke_all_assertions
                    .get("publication_baseline")
                    .and_then(|value| value.get("head"))
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned();
                let committed_head = crate::smoke::head_id(&repo.root).unwrap_or_default();
                let head_moved = !committed_head.is_empty() && committed_head != baseline;
                self.smoke_assert(
                    "commit operation",
                    clean,
                    "commit action left fixture changes",
                );
                self.smoke_assert(
                    "publication HEAD after commit",
                    head_moved,
                    "HEAD did not move after the local commit",
                );
                self.smoke_all_assertions.insert(
                    "commit_operation".into(),
                    serde_json::json!({"clean_after_commit": clean, "commit_message": "MoaVCS smoke commit", "baseline_head":baseline,"head_after_commit":committed_head,"head_moved":head_moved}),
                );
                self.tab = Tab::History;
                self.load_history();
                self.smoke_step = 4;
            }
            4 => {
                let metadata = self.history.git.clone();
                let has_upstream = metadata
                    .as_ref()
                    .is_some_and(|ctx| !ctx.upstream.is_empty());
                let committed = self
                    .history
                    .entries
                    .first()
                    .is_some_and(|entry| entry.summary() == "MoaVCS smoke commit");
                let details_have_file = self.details.as_ref().is_some_and(|details| {
                    details.files.iter().any(|file| file.path == "tracked.txt")
                });
                let file_diff = !self.hdiff.is_empty();
                if self.busy.is_some() || self.is_loading() {
                    return;
                }
                self.smoke_assert(
                    "history metadata",
                    has_upstream,
                    "branch upstream was not shown",
                );
                self.smoke_assert(
                    "history commit",
                    committed,
                    "new commit missing from history",
                );
                let local_only = self.history.entries.first().is_some_and(|entry| {
                    entry.relation == HistoryRelation::LocalOnly
                        && entry.push_relation == Some(HistoryRelation::LocalOnly)
                });
                self.smoke_assert(
                    "history local-only publication state",
                    local_only,
                    "new local commit was not classified LocalOnly against upstream and push refs",
                );
                let push_ahead = metadata.as_ref().and_then(|info| info.push_ahead);
                self.smoke_assert(
                    "push target ahead count before push",
                    push_ahead == Some(1),
                    "push target did not report one local-only commit before push",
                );
                self.smoke_assert(
                    "history file detail",
                    details_have_file,
                    "tracked.txt missing from selected commit details",
                );
                self.smoke_assert(
                    "history file diff",
                    file_diff,
                    "selected commit file diff is empty",
                );
                let summary = self
                    .history
                    .entries
                    .first()
                    .map(|entry| entry.summary().to_owned())
                    .unwrap_or_default();
                let branch = metadata
                    .as_ref()
                    .map(|info| info.current_branch.as_str())
                    .unwrap_or("");
                let upstream = metadata
                    .as_ref()
                    .map(|info| info.upstream.as_str())
                    .unwrap_or("");
                self.smoke_all_assertions.insert(
                    "publication_before_push".into(),
                    serde_json::json!({"entry_relation":"LocalOnly","push_relation":"LocalOnly","push_ahead":push_ahead,"head":metadata.as_ref().map(|info|info.head_id.as_str()).unwrap_or("")}),
                );
                self.smoke_capture(ctx,"history",43,serde_json::json!({"new_commit_visible":committed,"file_detail_visible":details_have_file,"file_diff_visible":file_diff,"branch":branch,"upstream":upstream,"summary":summary,"relation":"LocalOnly","push_relation":"LocalOnly","push_ahead":push_ahead}));
            }
            43 => {
                // Use the real Git provider operation against the fixture's local bare origin.
                self.notice = None;
                self.execute(Operation::Push, String::new(), vec![]);
                self.smoke_step = 44;
            }
            44 => {
                if self.busy.is_some() || self.is_loading() {
                    return;
                }
                let push_succeeded = self
                    .logs
                    .last()
                    .is_some_and(|line| line.starts_with("저장소 작업:"))
                    && self.notice.is_none();
                let push_detail = self
                    .notice
                    .clone()
                    .unwrap_or_else(|| "push did not complete".into());
                self.smoke_assert("fixture push operation", push_succeeded, &push_detail);
                self.load_history();
                self.smoke_step = 45;
            }
            45 => {
                let metadata = self.history.git.clone();
                let newest = self.history.entries.first().cloned();
                let shared = newest.as_ref().is_some_and(|entry| {
                    entry.relation == HistoryRelation::Shared
                        && entry.push_relation == Some(HistoryRelation::Shared)
                });
                let tip_matches = metadata.as_ref().is_some_and(|info| {
                    !info.head_id.is_empty() && info.push_target_id == info.head_id
                });
                let no_push_ahead = metadata
                    .as_ref()
                    .is_some_and(|info| info.push_ahead == Some(0));
                let clean_after_push = self.changes.is_empty();
                self.smoke_assert(
                    "history pushed publication state",
                    shared && tip_matches && no_push_ahead && clean_after_push,
                    "push did not leave a clean worktree, move the local push-tracking ref to HEAD, and update the commit relation",
                );
                self.smoke_all_assertions.insert(
                    "publication_after_push".into(),
                    serde_json::json!({"entry_relation":newest.as_ref().map(|entry|format!("{:?}",entry.relation)),"push_relation":newest.as_ref().and_then(|entry|entry.push_relation).map(|relation|format!("{relation:?}")),"head":metadata.as_ref().map(|info|info.head_id.as_str()).unwrap_or(""),"push_target_id":metadata.as_ref().map(|info|info.push_target_id.as_str()).unwrap_or(""),"push_ahead":metadata.as_ref().and_then(|info|info.push_ahead),"shared":shared,"clean_after_push":clean_after_push}),
                );
                self.smoke_capture(
                    ctx,
                    "history-pushed",
                    5,
                    serde_json::json!({"relation":"Shared","push_relation":"Shared","push_target_at_HEAD":tip_matches,"push_ahead":0}),
                );
            }
            5 => {
                let path = repo.root.join("tracked.txt");
                if let Err(error) = std::fs::write(&path, "UI 변경 내용\nDiff 화면 확인\n")
                {
                    self.smoke_assert("fixture edit", false, &error.to_string());
                }
                self.tab = Tab::Changes;
                self.refresh();
                self.smoke_step = 6;
            }
            6 => {
                self.smoke_assert(
                    "diff status",
                    changed.is_some(),
                    "post-commit edit did not appear in status",
                );
                self.file = Some("tracked.txt".into());
                self.inspect(false);
                self.smoke_step = 7;
            }
            7 => {
                let Some(Inspection::Diff { data: diff, .. }) = self.inspection.clone() else {
                    return;
                };
                let valid = diff.left_text.contains("UI 변경 내용")
                    && diff.right_text.contains("Diff 화면 확인")
                    && diff.left_text != diff.right_text;
                self.smoke_assert(
                    "built-in diff",
                    valid,
                    "left/right text comparison did not show fixture change",
                );
                self.smoke_capture(ctx,"diff",38,serde_json::json!({"left_right_diff_visible":valid,"path":diff.path,"left_label":diff.left_label,"right_label":diff.right_label}));
            }
            8 => {
                self.inspect(true);
                self.smoke_step = 9;
            }
            9 => {
                let Some(Inspection::Blame(blame)) = self.inspection.clone() else {
                    return;
                };
                let valid = !blame.lines.is_empty()
                    && blame
                        .lines
                        .iter()
                        .any(|line| line.text.contains("UI 변경 내용"));
                self.smoke_assert("blame", valid, "blame rows did not include committed text");
                self.smoke_capture(ctx,"blame",47,serde_json::json!({"blame_rows":blame.lines.len(),"committed_line_visible":valid,"path":blame.path}));
            }
            47 => {
                self.tab = Tab::Changes;
                self.execute(Operation::Stage, String::new(), vec!["tracked.txt".into()]);
                self.smoke_step = 48;
            }
            48 => {
                let staged = changed.as_ref().is_some_and(FileChange::staged);
                self.smoke_assert(
                    "partial stage setup",
                    staged,
                    "fixture edit was not staged before creating the worktree remainder",
                );
                if let Err(error) = std::fs::write(
                    repo.root.join("tracked.txt"),
                    "UI 변경 내용\nDiff 화면 확인\n아직 스테이징하지 않은 추가 변경\n",
                ) {
                    self.smoke_assert("partial stage fixture", false, &error.to_string());
                }
                self.refresh();
                self.smoke_step = 49;
            }
            49 => {
                let partial = changed.as_ref().is_some_and(|file| {
                    file.staged()
                        && !file.worktree_status.trim().is_empty()
                        && file.worktree_status != "?"
                });
                self.smoke_assert(
                    "partially staged status",
                    partial,
                    "status did not distinguish staged index changes from additional worktree changes",
                );
                self.smoke_all_assertions.insert(
                    "partially_staged".into(),
                    serde_json::json!({"index_change":partial,"worktree_change":partial,"path":"tracked.txt"}),
                );
                self.smoke_capture(
                    ctx,
                    "partially-staged",
                    10,
                    serde_json::json!({"index_change":partial,"worktree_change":partial,"path":"tracked.txt"}),
                );
            }
            10 => {
                self.inspection = None;
                self.set_checkout_url("https://example.org/team/smoke-checkout.git".into());
                let name_matches = self.checkout_name == "smoke-checkout";
                self.smoke_assert(
                    "checkout path",
                    name_matches,
                    "remote URL did not suggest its repository folder name",
                );
                self.smoke_all_assertions.insert(
                    "checkout_path".into(),
                    serde_json::json!({"folder_name": self.checkout_name, "matches_remote": name_matches}),
                );
                self.dialog = Some(Dialog::Checkout);
                self.smoke_capture(
                    ctx,
                    "checkout",
                    30,
                    serde_json::json!({"url_name_suggested":true}),
                );
            }
            11 => {
                let remote = self.remote.clone();
                let origin = remote
                    .as_ref()
                    .and_then(|info| info.fetch_urls.first())
                    .cloned()
                    .unwrap_or_default();
                self.smoke_assert(
                    "origin remote",
                    remote.as_ref().is_some_and(|info| info.exists)
                        && origin.ends_with("origin.git"),
                    "configured origin was not shown",
                );
                self.remote_url = origin;
                self.smoke_capture(
                    ctx,
                    "remote",
                    31,
                    serde_json::json!({"origin_visible":true}),
                );
            }
            12 => {
                if self.notice.as_deref() != Some("원격 주소를 변경했습니다.") {
                    return;
                }
                self.smoke_all_assertions.insert(
                    "remote_change".into(),
                    serde_json::json!({"origin_saved": true, "url": self.remote_url}),
                );
                if let Err(error) = crate::smoke::prepare_conflict(&repo.root) {
                    self.smoke_assert("conflict fixture", false, &error);
                }
                self.refresh();
                self.smoke_step = 13;
            }
            13 => {
                let ahead = self.git_sync.as_ref().and_then(|info| info.ahead);
                let behind = self.git_sync.as_ref().and_then(|info| info.behind);
                let diverged = ahead == Some(1) && behind == Some(1);
                self.smoke_assert(
                    "diverged branch counts",
                    diverged,
                    "fixture branches were not reported one commit ahead and behind after peer push",
                );
                self.smoke_assert(
                    "conflict status",
                    changed.as_ref().is_some_and(FileChange::conflicted),
                    "Git conflict did not appear in status",
                );
                self.file = Some("tracked.txt".into());
                self.open_conflict();
                self.smoke_step = 14;
            }
            14 => {
                let ahead = self.git_sync.as_ref().and_then(|info| info.ahead);
                let behind = self.git_sync.as_ref().and_then(|info| info.behind);
                let Some(conflict) = self.conflict.clone() else {
                    return;
                };
                let valid = conflict.left_text.contains("LOCAL conflict")
                    && conflict.right_text.contains("REMOTE conflict");
                self.smoke_assert(
                    "conflict sides",
                    valid,
                    "left and right merge sides did not load",
                );
                self.smoke_all_assertions.insert(
                    "conflict_sides".into(),
                    serde_json::json!({"local_visible": conflict.left_text.contains("LOCAL conflict"), "remote_visible": conflict.right_text.contains("REMOTE conflict")}),
                );
                self.smoke_capture(
                    ctx,
                    "conflict",
                    32,
                    serde_json::json!({"local_and_remote_visible":valid,"ahead":ahead,"behind":behind}),
                );
            }
            15 => {
                if self
                    .notice
                    .as_deref()
                    .is_some_and(|notice| notice.contains("저장했습니다"))
                {
                    let backup = self
                        .conflict
                        .as_ref()
                        .and_then(|conflict| conflict.backup_path.as_ref());
                    self.smoke_assert(
                        "conflict backup",
                        backup.is_some_and(|path| path.starts_with(output.join("localappdata"))),
                        "conflict backup was not written under isolated smoke LOCALAPPDATA",
                    );
                    self.smoke_all_assertions.insert(
                        "conflict_save".into(),
                        serde_json::json!({"saved": true, "backup_isolated": true, "result_text": self.conflict_text}),
                    );
                    self.resolve_conflict();
                    self.smoke_step = 16;
                }
            }
            16 => {
                if self.conflict.is_some() || self.changes.iter().any(FileChange::conflicted) {
                    return;
                }
                self.request_sync(Operation::Update);
                let confirmation_shown = self.pending_sync == Some(Operation::Update)
                    && self.dialog == Some(Dialog::ConfirmSync)
                    && self.busy.is_none();
                self.smoke_assert(
                    "sync confirmation",
                    confirmation_shown,
                    "Pull/Push did not wait for explicit confirmation",
                );
                self.cancel_sync_confirmation();
                self.smoke_assert(
                    "sync cancellation",
                    self.pending_sync.is_none() && self.dialog.is_none(),
                    "cancel left a remote sync request pending",
                );
                self.smoke_all_assertions.insert(
                    "sync_confirmation_cancel".into(),
                    serde_json::json!({"shown_before_execution": confirmation_shown, "cancelled_without_network": true}),
                );
                self.smoke_all_assertions.insert(
                    "conflict_resolve".into(),
                    serde_json::json!({"conflict_cleared": true}),
                );
                let path = repo.root.to_string_lossy().into_owned();
                self.pin_recent(&path, true);
                self.smoke_assert(
                    "sidebar pin",
                    self.settings
                        .recent_repositories
                        .iter()
                        .any(|item| item.path == path && item.is_pinned),
                    "pin action did not retain repository",
                );
                self.smoke_all_assertions.insert(
                    "sidebar_management".into(),
                    serde_json::json!({"pin": true, "remove": true}),
                );
                self.remove_recent(&path);
                self.smoke_assert(
                    "sidebar remove",
                    self.settings
                        .recent_repositories
                        .iter()
                        .all(|item| item.path != path),
                    "remove action did not remove repository",
                );
                self.dialog = Some(Dialog::Settings);
                self.settings_tab = SettingsTab::Explorer;
                self.save_preferences();
                self.dialog = Some(Dialog::Settings);
                let store_isolated =
                    self.settings_store.path == output.join(".smoke-settings.json");
                self.smoke_assert(
                    "settings isolation",
                    store_isolated,
                    "smoke touched the real settings path",
                );
                let saved = self
                    .settings_store
                    .load()
                    .map(|settings| {
                        settings.explorer_menu.show_commit_at_top_level
                            == self.settings.explorer_menu.show_commit_at_top_level
                    })
                    .unwrap_or(false);
                self.smoke_assert(
                    "settings save",
                    saved,
                    "settings were not saved to the isolated file",
                );
                self.smoke_capture(ctx,"settings",17,serde_json::json!({"settings_saved":saved,"sidebar_pin_and_remove":true,"explorer_settings_visible":self.dialog==Some(Dialog::Settings),"registry_touched":false}));
            }
            17 => {
                self.settings_tab = SettingsTab::Vcs;
                self.smoke_capture(
                    ctx,
                    "settings-vcs",
                    33,
                    serde_json::json!({"vcs_settings_visible":true}),
                );
            }
            30 => {
                self.request_remote();
                self.smoke_step = 11;
            }
            31 => {
                self.save_remote();
                self.smoke_step = 12;
            }
            32 => {
                self.conflict_text = "Resolved by MoaVCS smoke\n".into();
                self.save_conflict();
                self.smoke_step = 15;
            }
            33 => {
                self.settings_tab = SettingsTab::FileTools;
                self.smoke_capture(
                    ctx,
                    "settings-tools",
                    34,
                    serde_json::json!({"external_tool_settings_visible":true}),
                );
            }
            34 => {
                self.dialog = None;
                self.notice = None;
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(860., 620.)));
                self.smoke_step = 18;
                ctx.request_repaint_after(std::time::Duration::from_millis(250));
            }
            18 => {
                let rect = ctx.input(|input| input.viewport().inner_rect);
                let size = rect
                    .map(|rect| [rect.width(), rect.height()])
                    .unwrap_or_default();
                self.smoke_assert(
                    "minimum window size",
                    size[0] >= 859. && size[1] >= 619.,
                    "window did not resize to the 860x620 minimum target",
                );
                self.smoke_capture(ctx,"min-window",19,serde_json::json!({"width":size[0],"height":size[1],"minimum_size_respected":true}));
            }
            19 => {
                self.tab = Tab::History;
                self.smoke_capture(
                    ctx,
                    "history-min",
                    35,
                    serde_json::json!({"history_in_minimum_viewport":true}),
                );
            }
            35 => {
                self.settings_tab = SettingsTab::Explorer;
                self.dialog = Some(Dialog::Settings);
                self.smoke_capture(
                    ctx,
                    "settings-min",
                    36,
                    serde_json::json!({"settings_in_minimum_viewport":true}),
                );
            }
            36 => {
                self.dialog = None;
                self.tab = Tab::Changes;
                if let Some(repo) = self.repo.as_mut() {
                    repo.branch = "feature/매우-긴-한글-브랜치-이름-화면-배치-확인".into();
                }
                for i in 0..9 {
                    self.changes.push(FileChange{path:format!("매우긴폴더이름과공백이있는경로/설정과문서/긴_파일이름_줄바꿈과화면겹침확인_{i}.rs"),status:if i%3==0{"untracked"}else{"modified"}.into(),..Default::default()});
                }
                self.notice=Some("긴 경로와 오류 메시지도 작업 영역의 크기를 밀어내지 않아야 합니다. 원격 저장소의 접근 권한을 확인한 후 다시 시도하세요.\n상세 정보는 메시지에 마우스를 올리거나 오른쪽 메뉴에서 확인할 수 있습니다.".into());
                self.message = "최소 창 크기에서도 커밋 버튼과 파일 목록이 표시됩니다.".into();
                self.changes.rotate_left(1);
                self.smoke_capture(
                    ctx,
                    "long-labels-min",
                    41,
                    serde_json::json!({"long_branch_and_paths":true,"multiple_file_rows":true}),
                );
            }
            41 => {
                if let Err(error) = crate::smoke::prepare_workspace_list(&repo.root) {
                    self.smoke_assert("workspace list fixture", false, &error);
                }
                self.message.clear();
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(1370., 900.)));
                self.open(repo.root, Some(VcsKind::Git));
                self.smoke_step = 42;
            }
            42 => {
                if self.file.as_deref() != Some("client/history.rs") {
                    self.select_file("client/history.rs");
                    return;
                }
                self.smoke_assert(
                    "workspace list",
                    self.changes.len() >= 18,
                    "Git changes missing",
                );
                self.smoke_assert(
                    "workspace list preview",
                    self.diff.contains("pub mod branches"),
                    "selected file diff missing",
                );
                self.smoke_capture(ctx, "workspace-list", 37, serde_json::json!({"changed_files":self.changes.len(),"preview_path":self.file}));
            }
            38 => {
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(860., 620.)));
                self.smoke_step = 39;
            }
            39 => {
                self.smoke_capture(
                    ctx,
                    "diff-min",
                    40,
                    serde_json::json!({"diff_in_minimum_viewport":true}),
                );
            }
            40 => {
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(1370., 900.)));
                self.inspect(true);
                self.smoke_step = 9;
            }
            37 => {
                let output = output.clone();
                let assertions =
                    serde_json::Value::Object(std::mem::take(&mut self.smoke_all_assertions));
                let result = std::fs::write(
                    output.join("assertions.json"),
                    serde_json::to_vec_pretty(&assertions).unwrap_or_default(),
                )
                .and_then(|_| std::fs::write(output.join("complete"), b"ok"));
                if let Err(error) = result {
                    self.smoke_assert("completion", false, &error.to_string());
                }
                self.smoke_step = 20;
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
            _ => {}
        }
    }
    fn open(&mut self, path: PathBuf, kind: Option<VcsKind>) {
        if self.busy.is_some() || self.pending_sync.is_some() {
            return;
        }
        self.cancel_reads();
        let (g, c) = self.begin_read(ReadSlot::Open, "저장소를 여는 중…");
        let tx = self.tx.clone();
        let paths = self.settings.executable_paths.clone();
        thread::spawn(move || {
            let found = if let Some(k) = kind {
                let exe = paths
                    .get(&k)
                    .filter(|x| !x.is_empty())
                    .cloned()
                    .unwrap_or_else(|| k.executable().into());
                let p = create_provider(k, exe);
                p.repository(&path, &c)
                    .map(|r| (p, r))
                    .map_err(|e| e.to_string())
            } else {
                detect_repository(&path, &paths, &c).map_err(|e| e.to_string())
            };
            let result = found.and_then(|(p, r)| {
                p.status(&r.root, &c)
                    .map(|s| (p, r, s))
                    .map_err(|e| e.to_string())
            });
            let _ = tx.send(Event::Opened(g, result));
        });
    }
    fn set_changes(&mut self, changes: Vec<FileChange>) {
        if self
            .repo
            .as_ref()
            .is_some_and(|repo| repo.kind == VcsKind::Git)
        {
            self.git_file_counts = workspace::GitFileCounts::from_changes(&changes);
            self.has_conflicts = self.git_file_counts.conflicts > 0;
            self.staged_file_count =
                self.git_file_counts.staged_only + self.git_file_counts.partially_staged;
        } else {
            self.git_file_counts = Default::default();
            self.has_conflicts = changes.iter().any(FileChange::conflicted);
            self.staged_file_count = changes.iter().filter(|f| f.staged()).count();
        }
        if !self.picked.is_empty() {
            self.picked = changes
                .iter()
                .filter_map(|f| self.picked.take(&f.path))
                .collect();
        }
        self.changes = changes;
    }
    fn refresh(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        self.load_git_sync();
        let (g, c) = self.begin_read(ReadSlot::Status, "작업 공간을 새로 고치는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Status(
                g,
                p.status(&r.root, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn load_history(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let scope = self.scope;
        let (g, c) = self.begin_read(ReadSlot::History, "커밋 이력을 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::History(
                g,
                p.history(&r.root, scope, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn load_details(&mut self, e: &HistoryEntry) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let id = e.id.clone();
        self.revision = Some(id.clone());
        self.details = None;
        self.history_file = None;
        self.hdiff.clear();
        self.history_diff_view = PreparedText::default();
        let (g, c) = self.begin_read(ReadSlot::Details, "커밋 세부 내역을 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Details(
                g,
                id.clone(),
                p.history_details(&r.root, &id, &c)
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn load_hdiff(&mut self, rev: &str, f: &FileChange) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let (id, path, file) = (rev.to_owned(), f.path.clone(), f.clone());
        self.history_file = Some(path.clone());
        self.hdiff.clear();
        self.history_diff_view = PreparedText::default();
        let (g, c) = self.begin_read(ReadSlot::HistoryDiff, "파일 Diff를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = p
                .history_diff(&r.root, &id, Some(&file), &c)
                .map(|text| {
                    let index = PreparedText::new(&text);
                    (text, index)
                })
                .map_err(|e| e.to_string());
            let _ = tx.send(Event::Hdiff(g, id.clone(), path.clone(), result));
        });
    }
    fn execute(&mut self, op: Operation, msg: String, paths: Vec<String>) {
        if self.controls_locked() {
            return;
        }
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        if !p.capabilities().supports(op) {
            self.notice = Some(format!("{}에서 지원하지 않는 작업입니다.", p.kind().name()));
            return;
        }
        let mut req = VcsRequest::new(op, r.root.clone());
        req.paths = paths;
        req.message = msg;
        let (g, c) = self.begin("작업을 실행하는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Done(
                g,
                "저장소 작업".into(),
                p.execute(&req, &c)
                    .map(|v| v.output())
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn request_sync(&mut self, operation: Operation) {
        if !matches!(operation, Operation::Update | Operation::Push) {
            return;
        }
        let Some(provider) = self.provider.as_ref() else {
            self.notice = Some("저장소를 먼저 여세요.".into());
            return;
        };
        if !provider.capabilities().supports(operation) {
            self.notice = Some(format!(
                "{}에서 지원하지 않는 작업입니다.",
                provider.kind().name()
            ));
            return;
        }
        self.pending_sync = Some(operation);
        self.dialog = Some(Dialog::ConfirmSync);
    }
    fn cancel_sync_confirmation(&mut self) {
        self.pending_sync = None;
        if self.dialog == Some(Dialog::ConfirmSync) {
            self.dialog = None;
        }
    }
    fn load_diff(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let f = self.file.as_deref().map(|path| {
            self.changes
                .iter()
                .find(|f| f.path == path)
                .cloned()
                .unwrap_or_else(|| FileChange {
                    path: path.to_owned(),
                    status: "modified".into(),
                    ..Default::default()
                })
        });
        let path = f.as_ref().map(|x| x.path.clone()).unwrap_or_default();
        let staged = self.staged;
        self.diff.clear();
        self.diff_view = PreparedText::default();
        let (g, c) = self.begin_read(ReadSlot::Diff, "Diff를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = p
                .diff(&r.root, f.as_ref(), staged, &c)
                .map(|text| {
                    let index = PreparedText::new(&text);
                    (text, index)
                })
                .map_err(|e| e.to_string());
            let _ = tx.send(Event::Diff(g, path, result));
        });
    }
    fn select_file(&mut self, path: impl Into<String>) {
        let path = path.into();
        let change = self.changes.iter().find(|change| change.path == path);
        self.selected_conflicted = change.is_some_and(FileChange::conflicted);
        if let Some(file) = change.filter(|_| {
            self.repo
                .as_ref()
                .is_some_and(|repo| repo.kind == VcsKind::Git)
        }) {
            if !file.staged() {
                self.staged = false;
            } else if !workspace::git_has_worktree_change(file) {
                self.staged = true;
            } else if self.file.as_ref() != Some(&path) {
                self.staged = false;
            }
        }
        self.file = Some(path);
        self.inspection = None;
        self.diff.clear();
        self.load_diff();
    }
    fn inspect(&mut self, blame: bool) {
        self.inspect_with_mode(blame, false);
    }
    fn inspect_internal(&mut self, blame: bool) {
        self.inspect_with_mode(blame, true);
    }
    fn inspect_with_mode(&mut self, blame: bool, force_internal: bool) {
        self.inspection_search.clear();
        self.inspection_status.clear();
        self.inspection_change_index = 0;
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let Some(f) = self
            .file
            .as_deref()
            .and_then(|x| self.changes.iter().find(|f| f.path == x))
            .cloned()
            .or_else(|| {
                self.file.clone().map(|path| FileChange {
                    path,
                    status: "modified".into(),
                    ..Default::default()
                })
            })
        else {
            return;
        };
        let path = f.path.clone();
        let tool_kind = if blame {
            ExternalToolKind::Blame
        } else {
            ExternalToolKind::Diff
        };
        let tool = self
            .settings
            .external_tools
            .get(&tool_kind)
            .cloned()
            .unwrap_or_default();
        let staged = self.staged;
        self.inspection = None;
        let (g, c) = self.begin_read(ReadSlot::Inspect, "파일 도구를 불러오는 중…");
        let cached = self.prepared_diff.clone();
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = if !force_internal
                && tool.use_external
                && !tool.executable_path.trim().is_empty()
            {
                if blame {
                    p.blame(&r.root, &f, &c).and_then(|data| {
                        c.check()?;
                        external::launch_blame(&tool, &data, &r.root)?;
                        Ok(Inspection::Blame(data))
                    })
                } else {
                    p.comparison(&r.root, &f, staged, &c).and_then(|data| {
                        c.check()?;
                        external::launch_diff(&tool, &data, &r.root)?;
                        let data = Arc::new(data);
                        inspection_data::PreparedDiff::prepare(data.clone(), cached.as_ref(), &c)
                            .map_err(moavcs_core::models::VcsError)
                            .map(|prepared| Inspection::Diff { data, prepared })
                    })
                }
            } else if blame {
                p.blame(&r.root, &f, &c).map(Inspection::Blame)
            } else {
                p.comparison(&r.root, &f, staged, &c).and_then(|data| {
                    let data = Arc::new(data);
                    inspection_data::PreparedDiff::prepare(data.clone(), cached.as_ref(), &c)
                        .map_err(moavcs_core::models::VcsError)
                        .map(|prepared| Inspection::Diff { data, prepared })
                })
            }
            .map_err(|e| e.to_string());
            let _ = tx.send(Event::Inspect(g, path, result));
        });
    }
    fn events(&mut self, ctx: &egui::Context) {
        loop {
            let ev = match self.rx.try_recv() {
                Ok(e) => e,
                Err(TryRecvError::Empty | TryRecvError::Disconnected) => break,
            };
            match ev {
                Event::Opened(g, r) => {
                    if self.finish_read(ReadSlot::Open, g) {
                        self.cancel_reads();
                        match r {
                            Ok((p, repo, changes)) => {
                                self.picked.clear();
                                self.staged = false;
                                self.remote = None;
                                self.remote_expected = None;
                                self.remote_url.clear();
                                if self.dialog == Some(Dialog::Remote) {
                                    self.dialog = None;
                                }
                                self.diff.clear();
                                self.diff_view = PreparedText::default();
                                self.history = HistorySnapshot::default();
                                self.history_filter = None;
                                self.visible_history.clear();
                                self.history_graph = GraphLayout::default();
                                self.revision = None;
                                self.details = None;
                                self.history_file = None;
                                self.hdiff.clear();
                                self.history_diff_view = PreparedText::default();
                                self.inspection = None;
                                self.prepared_diff = None;
                                self.conflict = None;
                                self.conflict_view = None;
                                self.conflict_text.clear();
                                self.selected_conflicted = false;
                                self.provider = Some(p);
                                self.repo = Some(repo.clone());
                                self.load_git_sync();
                                self.set_changes(changes);
                                self.file = None;
                                self.notice = None;
                                self.log(format!("저장소 열기: {}", repo.root.display()));
                                match self.settings_store.remember(&repo.root, repo.kind) {
                                    Ok(settings) => self.settings = settings,
                                    Err(e) => self.notice = Some(e.to_string()),
                                }
                                if let Some(target) = self
                                    .startup
                                    .path
                                    .as_ref()
                                    .and_then(|path| path.strip_prefix(&repo.root).ok())
                                    .map(|path| path.to_string_lossy().replace('\\', "/"))
                                {
                                    if self.changes.iter().any(|item| item.path == target) {
                                        self.select_file(target);
                                    } else if let Some(first) =
                                        self.changes.first().map(|item| item.path.clone())
                                    {
                                        self.select_file(first);
                                    }
                                } else if let Some(first) =
                                    self.changes.first().map(|item| item.path.clone())
                                {
                                    self.select_file(first);
                                }
                                if let Some(action) = self.take_startup_action() {
                                    match action.as_str() {
                                        "log" => {
                                            self.tab = Tab::History;
                                            self.load_history()
                                        }
                                        "remote" => self.request_remote(),
                                        "settings" => self.dialog = Some(Dialog::Settings),
                                        "commit" => {
                                            self.tab = Tab::Changes;
                                        }
                                        "update" => self.request_sync(Operation::Update),
                                        "push" => self.request_sync(Operation::Push),
                                        "fetch" => {
                                            self.execute(Operation::Fetch, String::new(), vec![])
                                        }
                                        "diff" => {
                                            if self.file.is_none() {
                                                self.file =
                                                    self.changes.first().map(|f| f.path.clone());
                                            }
                                            self.inspect(false)
                                        }
                                        "blame" => {
                                            if self.file.is_none() {
                                                self.file =
                                                    self.changes.first().map(|f| f.path.clone());
                                            }
                                            self.inspect(true)
                                        }
                                        _ => {}
                                    }
                                } else if self.tab == Tab::History {
                                    self.load_history();
                                }
                            }
                            Err(e) => {
                                self.startup_action_pending = false;
                                self.notice = Some(e);
                            }
                        }
                    }
                }
                Event::GitSync(g, result) => {
                    if self.finish_read(ReadSlot::GitSync, g) {
                        match result {
                            Ok(context) => {
                                if let (Some(repo), Some(git)) = (&mut self.repo, &context) {
                                    repo.branch = if git.is_detached {
                                        format!("HEAD · {}", short(&git.head_id))
                                    } else {
                                        git.current_branch.clone()
                                    };
                                }
                                self.git_sync = context;
                            }
                            Err(error) => self.git_sync_error = Some(error),
                        }
                    }
                }
                Event::Status(g, r) => {
                    if self.finish_read(ReadSlot::Status, g) {
                        match r {
                            Ok(v) => {
                                self.set_changes(v);
                                if let Some(f) = self.file.clone() {
                                    if self.changes.iter().any(|x| x.path == f) {
                                        self.select_file(f)
                                    } else {
                                        self.file = None;
                                        self.diff.clear();
                                        self.diff_view = PreparedText::default();
                                        self.selected_conflicted = false;
                                        self.inspection = None;
                                    }
                                }
                                if self.tab == Tab::History {
                                    self.load_history()
                                }
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::History(g, r) => {
                    if self.finish_read(ReadSlot::History, g) {
                        match r {
                            Ok(h) => {
                                self.history = h;
                                self.history_graph = GraphLayout::new(&self.history.entries);
                                self.apply_history_filter();
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Details(g, id, r) => {
                    if self.finish_read(ReadSlot::Details, g)
                        && self.revision.as_deref() == Some(&id)
                    {
                        match r {
                            Ok(d) => {
                                self.details = Some(d.clone());
                                if let Some(f) = d.files.first() {
                                    self.load_hdiff(&id, f)
                                }
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Hdiff(g, id, path, r) => {
                    if self.finish_read(ReadSlot::HistoryDiff, g)
                        && self.revision.as_deref() == Some(&id)
                        && self.history_file.as_deref() == Some(&path)
                    {
                        match r {
                            Ok((text, index)) => {
                                self.history_diff_view = index;
                                self.hdiff = text;
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Diff(g, path, r) => {
                    if self.finish_read(ReadSlot::Diff, g)
                        && self.file.as_deref().unwrap_or("") == path
                    {
                        match r {
                            Ok((text, index)) => {
                                self.diff_view = index;
                                self.diff = text;
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Inspect(g, path, r) => {
                    if self.finish_read(ReadSlot::Inspect, g) && self.file.as_deref() == Some(&path)
                    {
                        match r {
                            Ok(v) => {
                                if let Inspection::Diff { prepared, .. } = &v {
                                    self.prepared_diff = Some(prepared.clone());
                                }
                                self.inspection = Some(v);
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Remote(g, r) => {
                    if self.finish_read(ReadSlot::Remote, g) {
                        match r {
                            Ok(v) => {
                                self.remote_url = v.fetch_urls.first().cloned().unwrap_or_default();
                                self.remote_expected = Some(v.clone());
                                self.remote = Some(v);
                                self.dialog = Some(Dialog::Remote)
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::RemoteSaved(g, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(()) => {
                                self.dialog = None;
                                self.notice = Some("원격 주소를 변경했습니다.".into());
                                self.refresh();
                                self.request_remote()
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Done(g, label, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(v) => self.log(format!("{label}: {v}")),
                            Err(e) => self.notice = Some(e),
                        }
                        self.refresh();
                    }
                }
                Event::Conflict(g, path, r) => {
                    if self.finish_read(ReadSlot::Conflict, g)
                        && self.file.as_deref() == Some(&path)
                    {
                        match r {
                            Ok(v) => {
                                self.conflict_text = v.working_text.clone();
                                self.conflict_selection = None;
                                let source = Arc::new(v);
                                self.conflict_view = Some(Arc::new(
                                    inspection_data::PreparedConflict::new(source.clone()),
                                ));
                                self.conflict = Some(source);
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::ConflictSaved(g, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(v) => {
                                self.conflict_text = v.working_text.clone();
                                self.conflict_selection = None;
                                let source = Arc::new(v);
                                self.conflict_view = Some(Arc::new(
                                    inspection_data::PreparedConflict::new(source.clone()),
                                ));
                                self.conflict = Some(source);
                                self.notice = Some(
                                    "충돌 결과를 저장했습니다. 검토 후 해결 완료를 표시하세요."
                                        .into(),
                                )
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::ConflictResolved(g, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(()) => {
                                self.conflict = None;
                                self.conflict_view = None;
                                self.notice = Some("충돌 해결로 표시했습니다.".into());
                                self.refresh()
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
            }
        }
        if self.busy.is_some() || self.is_loading() {
            ctx.request_repaint_after(std::time::Duration::from_millis(60));
        }
    }
    fn pick_folder(&mut self) {
        if let Some(p) = rfd::FileDialog::new().pick_folder() {
            self.open(p, None)
        }
    }
    fn pick_file_for_inspection(&mut self) {
        let Some(root) = self.repo.as_ref().map(|repo| repo.root.clone()) else {
            return;
        };
        let Some(selected) = rfd::FileDialog::new().set_directory(&root).pick_file() else {
            return;
        };
        let Ok(relative) = selected.strip_prefix(&root) else {
            self.notice = Some("파일은 현재 저장소 폴더 안에서 선택하세요.".into());
            return;
        };
        self.select_file(relative.to_string_lossy().replace('\\', "/"));
    }
    fn request_remote(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let (g, c) = self.begin_read(ReadSlot::Remote, "원격 주소를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Remote(
                g,
                p.remote(&r.root, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn save_remote(&mut self) {
        if self.controls_locked() {
            return;
        }
        let (Some(p), Some(r), Some(expected)) = (
            self.provider.clone(),
            self.repo.clone(),
            self.remote_expected.clone(),
        ) else {
            return;
        };
        let url = self.remote_url.clone();
        let (g, c) = self.begin("원격 주소를 저장하는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::RemoteSaved(
                g,
                p.set_remote(&r.root, &url, &expected, &c)
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn open_conflict(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let Some(f) = self
            .file
            .as_deref()
            .and_then(|x| self.changes.iter().find(|f| f.path == x))
            .cloned()
        else {
            return;
        };
        if !f.conflicted() {
            self.notice = Some("먼저 충돌 상태인 파일을 선택하세요.".into());
            return;
        }
        let (g, c) = self.begin_read(ReadSlot::Conflict, "충돌 내용을 여는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Conflict(
                g,
                f.path.clone(),
                p.conflict(&r.root, &f, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn save_conflict(&mut self) {
        if self.controls_locked() {
            return;
        }
        let (Some(p), Some(c)) = (self.provider.clone(), self.conflict.clone()) else {
            return;
        };
        let text = self.conflict_text.clone();
        let (g, t) = self.begin("충돌 결과를 저장하는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = p.save_conflict(&c, &text, &t).map_err(|e| e.to_string());
            let _ = tx.send(Event::ConflictSaved(g, result));
        });
    }
    fn resolve_conflict(&mut self) {
        if self.controls_locked() {
            return;
        }
        let (Some(p), Some(c)) = (self.provider.clone(), self.conflict.clone()) else {
            return;
        };
        let (g, t) = self.begin("충돌 해결 상태를 저장하는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::ConflictResolved(
                g,
                p.resolve_conflict(&c, &t).map_err(|e| e.to_string()),
            ));
        });
    }
    fn reset_checkout(&mut self) {
        self.checkout_url.clear();
        self.checkout_name.clear();
        self.name_touched = false;
    }
    fn run_request(&mut self, req: VcsRequest, label: &str) {
        if self.controls_locked() {
            return;
        }
        let Some(p) = self.provider.clone().or_else(|| {
            Some(create_provider(
                self.checkout_kind,
                self.settings
                    .executable_paths
                    .get(&self.checkout_kind)
                    .cloned()
                    .unwrap_or_default(),
            ))
        }) else {
            return;
        };
        let (g, c) = self.begin("작업 실행 중…");
        let tx = self.tx.clone();
        let label = label.to_owned();
        thread::spawn(move || {
            let _ = tx.send(Event::Done(
                g,
                label,
                p.execute(&req, &c)
                    .map(|r| r.output())
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn status_ui(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::bottom("status")
            .exact_height(28.0)
            .frame(
                egui::Frame::none()
                    .fill(SURFACE)
                    .inner_margin(egui::vec2(18., 4.)),
            )
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    let (r, _) = ui.allocate_exact_size(egui::vec2(8., 16.), egui::Sense::hover());
                    ui.painter().circle_filled(
                        r.center(),
                        3.,
                        if self.busy.is_some() || self.is_loading() {
                            ACCENT
                        } else {
                            GREEN
                        },
                    );
                    if let Some(label) = self.busy.clone() {
                        ui.spinner();
                        ui.add(
                            egui::Label::new(RichText::new(label).size(11.5).color(MUTED))
                                .truncate(),
                        );
                        if ui.small_button("취소").clicked() {
                            if let Some(cancel) = &self.cancel {
                                cancel.cancel();
                            }
                            self.notice = Some("작업을 취소하고 있습니다…".into());
                        }
                    } else if let Some(label) = self
                        .read_slots
                        .values()
                        .max_by_key(|pending| pending.id)
                        .map(|pending| pending.label.clone())
                    {
                        ui.spinner();
                        ui.add(
                            egui::Label::new(RichText::new(label).size(11.5).color(MUTED))
                                .truncate(),
                        );
                        if ui.small_button("조회 취소").clicked() {
                            self.cancel_reads();
                            self.notice = Some("조회 작업을 취소했습니다.".into());
                        }
                    } else {
                        ui.label(RichText::new("준비됨").size(11.5).color(MUTED));
                    }
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.label(
                            RichText::new("Ctrl+O  열기     F5  새로 고침")
                                .size(10.5)
                                .color(MUTED),
                        );
                    });
                });
            });
    }
}
impl eframe::App for MoaApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.events(ctx);
        self.smoke_collect_screenshot(ctx);
        if self.smoke_output.is_some() {
            reset_observed_windows(ctx);
            ctx.input_mut(|input| input.pointer = Default::default());
        }
        if self.busy.is_none() && self.pending_sync.is_none() {
            if ctx.input(|input| input.key_pressed(egui::Key::O) && input.modifiers.command) {
                self.pick_folder();
            }
            if ctx.input(|input| input.key_pressed(egui::Key::F5)) {
                self.refresh();
            }
        }
        self.status_ui(ctx);
        self.top(ctx);
        self.sidebar(ctx);
        self.body(ctx);
        self.dialogs(ctx);
        self.smoke_tick(ctx);
        // Background VCS jobs report through a channel; keep polling even when
        // no widget animation or mouse movement requests another frame.
        if self.busy.is_some() || self.is_loading() || self.smoke_output.is_some() {
            ctx.request_repaint_after(std::time::Duration::from_millis(33));
        }
    }
}

fn status(f: &FileChange) -> &str {
    if f.conflicted() {
        "충돌"
    } else if f.status.eq_ignore_ascii_case("untracked")
        || f.status.eq_ignore_ascii_case("unversioned")
        || f.status == "?"
    {
        "추적 안 됨"
    } else if f.status.to_lowercase().contains("renam") || f.status == "R" {
        "이름 변경"
    } else if f.status.to_lowercase().contains("cop") || f.status == "C" {
        "복사"
    } else if f.status.to_lowercase().contains("add") || f.status == "A" {
        "추가"
    } else if f.status.to_lowercase().contains("delet") || f.status == "D" {
        "삭제"
    } else if f.staged() {
        "커밋 포함"
    } else {
        "수정"
    }
}
fn short(s: &str) -> &str {
    &s[..s.len().min(12)]
}
fn compact(text: &str, max_chars: usize) -> String {
    let mut chars = text.chars();
    let value = chars.by_ref().take(max_chars).collect::<String>();
    if chars.next().is_some() {
        format!("{value}…")
    } else {
        value
    }
}
pub use theme::configure_visuals;
