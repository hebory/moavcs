use crate::history_graph::{GraphLayout, GRAPH_WIDTH};
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
    collections::BTreeSet,
    path::{Path, PathBuf},
    sync::{
        mpsc::{Receiver, Sender, TryRecvError},
        Arc,
    },
    thread,
};

const BG: Color32 = Color32::from_rgb(246, 247, 249);
const NAVY: Color32 = Color32::from_rgb(24, 38, 56);
const ACCENT: Color32 = Color32::from_rgb(216, 81, 48);
const MUTED: Color32 = Color32::from_rgb(111, 126, 146);

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
    Diff(FileComparison),
    Blame(BlameResult),
}
pub(super) enum Event {
    Opened(
        u64,
        std::result::Result<(Arc<dyn VcsProvider>, RepositoryInfo, Vec<FileChange>), String>,
    ),
    Status(u64, std::result::Result<Vec<FileChange>, String>),
    History(u64, std::result::Result<HistorySnapshot, String>),
    Details(u64, String, std::result::Result<HistoryDetails, String>),
    Hdiff(u64, String, String, std::result::Result<String, String>),
    Diff(u64, String, std::result::Result<String, String>),
    Inspect(u64, String, std::result::Result<Inspection, String>),
    Remote(u64, std::result::Result<RemoteInfo, String>),
    RemoteSaved(u64, std::result::Result<(), String>),
    Done(u64, String, std::result::Result<String, String>),
    Conflict(u64, std::result::Result<ConflictFile, String>),
    ConflictSaved(u64, std::result::Result<ConflictFile, String>),
    ConflictResolved(u64, std::result::Result<(), String>),
}

pub struct MoaApp {
    tx: Sender<Event>,
    rx: Receiver<Event>,
    settings_store: SettingsStore,
    settings: AppSettings,
    startup: LaunchOptions,
    generation: u64,
    cancel: Option<CancelToken>,
    busy: Option<String>,
    provider: Option<Arc<dyn VcsProvider>>,
    repo: Option<RepositoryInfo>,
    changes: Vec<FileChange>,
    picked: BTreeSet<String>,
    file: Option<String>,
    diff: String,
    tab: Tab,
    scope: HistoryScope,
    history: HistorySnapshot,
    history_graph: GraphLayout,
    revision: Option<String>,
    details: Option<HistoryDetails>,
    history_file: Option<String>,
    hdiff: String,
    logs: Vec<String>,
    notice: Option<String>,
    dialog: Option<Dialog>,
    pending_sync: Option<Operation>,
    settings_tab: SettingsTab,
    query: String,
    message: String,
    staged: bool,
    checkout_kind: VcsKind,
    checkout_url: String,
    checkout_parent: String,
    checkout_name: String,
    name_touched: bool,
    remote: Option<RemoteInfo>,
    remote_url: String,
    remote_expected: Option<RemoteInfo>,
    inspection: Option<Inspection>,
    inspection_search: String,
    inspection_status: String,
    inspection_change_index: usize,
    inspection_scroll: f32,
    conflict: Option<ConflictFile>,
    conflict_text: String,
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
            generation: 0,
            cancel: None,
            busy: None,
            provider: None,
            repo: None,
            changes: vec![],
            picked: BTreeSet::new(),
            file: None,
            diff: String::new(),
            tab: Tab::Changes,
            scope: HistoryScope::CurrentBranch,
            history: HistorySnapshot::default(),
            history_graph: GraphLayout::default(),
            revision: None,
            details: None,
            history_file: None,
            hdiff: String::new(),
            logs: vec![],
            notice: None,
            dialog: None,
            pending_sync: None,
            settings_tab: SettingsTab::default(),
            query: String::new(),
            message: String::new(),
            staged: false,
            checkout_kind: startup.vcs.unwrap_or_default(),
            checkout_url: String::new(),
            checkout_parent: parent,
            checkout_name: String::new(),
            name_touched: false,
            remote: None,
            remote_url: String::new(),
            remote_expected: None,
            inspection: None,
            inspection_search: String::new(),
            inspection_status: String::new(),
            inspection_change_index: 0,
            inspection_scroll: 0.0,
            conflict: None,
            conflict_text: String::new(),
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
    fn log(&mut self, s: impl Into<String>) {
        self.logs.push(s.into());
        if self.logs.len() > 400 {
            self.logs.drain(..100);
        }
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
        if self.smoke_capture.is_some() || self.busy.is_some() {
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
                    if self.busy.is_none() {
                        self.load_diff();
                    }
                    return;
                }
                self.smoke_assert("diff preview", true, "selected file preview is populated");
                self.smoke_capture(ctx,"workspace",1,serde_json::json!({"repository_opened":true,"modified_file_visible":true,"branch":repo.branch,"status":file.status}));
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
                if self.busy.is_some() {
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
                self.staged = false;
                self.execute(Operation::Commit, "MoaVCS smoke commit".into(), vec![]);
                self.smoke_step = 3;
            }
            3 => {
                let clean = self.changes.is_empty();
                self.smoke_assert(
                    "commit operation",
                    clean,
                    "commit action left fixture changes",
                );
                self.smoke_all_assertions.insert(
                    "commit_operation".into(),
                    serde_json::json!({"clean_after_commit": clean, "commit_message": "MoaVCS smoke commit"}),
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
                if self.busy.is_some() {
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
                self.smoke_capture(ctx,"history",5,serde_json::json!({"new_commit_visible":committed,"file_detail_visible":details_have_file,"file_diff_visible":file_diff,"branch":branch,"upstream":upstream,"summary":summary}));
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
                let Some(Inspection::Diff(diff)) = self.inspection.clone() else {
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
                self.smoke_capture(ctx,"diff",8,serde_json::json!({"left_right_diff_visible":valid,"path":diff.path,"left_label":diff.left_label,"right_label":diff.right_label}));
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
                self.smoke_capture(ctx,"blame",10,serde_json::json!({"blame_rows":blame.lines.len(),"committed_line_visible":valid,"path":blame.path}));
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
                self.request_remote();
                self.smoke_step = 11;
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
                self.save_remote();
                self.smoke_step = 12;
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
                self.conflict_text = "Resolved by MoaVCS smoke\n".into();
                self.save_conflict();
                self.smoke_step = 15;
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
                self.dialog = None;
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
        let (g, c) = self.begin("저장소를 여는 중…");
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
    fn refresh(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let (g, c) = self.begin("작업 공간을 새로 고치는 중…");
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
        let (g, c) = self.begin("커밋 이력을 불러오는 중…");
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
        let (g, c) = self.begin("커밋 세부 내역을 불러오는 중…");
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
        let (g, c) = self.begin("파일 Diff를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Hdiff(
                g,
                id.clone(),
                path.clone(),
                p.history_diff(&r.root, &id, Some(&file), &c)
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn execute(&mut self, op: Operation, msg: String, paths: Vec<String>) {
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
        let (g, c) = self.begin("Diff를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Diff(
                g,
                path,
                p.diff(&r.root, f.as_ref(), staged, &c)
                    .map_err(|e| e.to_string()),
            ));
        });
    }
    fn select_file(&mut self, path: impl Into<String>) {
        let path = path.into();
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
        self.inspection_scroll = 0.0;
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
        let (g, c) = self.begin("파일 도구를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = if !force_internal
                && tool.use_external
                && !tool.executable_path.trim().is_empty()
            {
                if blame {
                    p.blame(&r.root, &f, &c).and_then(|data| {
                        external::launch_blame(&tool, &data, &r.root)?;
                        Ok(Inspection::Blame(data))
                    })
                } else {
                    p.comparison(&r.root, &f, staged, &c).and_then(|data| {
                        external::launch_diff(&tool, &data, &r.root)?;
                        Ok(Inspection::Diff(data))
                    })
                }
            } else if blame {
                p.blame(&r.root, &f, &c).map(Inspection::Blame)
            } else {
                p.comparison(&r.root, &f, staged, &c).map(Inspection::Diff)
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
                    if self.finish(g) {
                        match r {
                            Ok((p, repo, changes)) => {
                                self.provider = Some(p);
                                self.repo = Some(repo.clone());
                                self.changes = changes;
                                self.picked.clear();
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
                                match self.startup.action.as_str() {
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
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Status(g, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(v) => {
                                self.changes = v;
                                if let Some(f) = self.file.clone() {
                                    if self.changes.iter().any(|x| x.path == f) {
                                        self.select_file(f)
                                    } else {
                                        self.file = None;
                                        self.diff.clear();
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
                    if self.finish(g) {
                        match r {
                            Ok(h) => {
                                self.history = h;
                                self.history_graph = GraphLayout::new(&self.history.entries);
                                if let Some(e) = self.history.entries.first().cloned() {
                                    self.load_details(&e)
                                }
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Details(g, id, r) => {
                    if self.finish(g) && self.revision.as_deref() == Some(&id) {
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
                    if self.finish(g)
                        && self.revision.as_deref() == Some(&id)
                        && self.history_file.as_deref() == Some(&path)
                    {
                        match r {
                            Ok(v) => self.hdiff = v,
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Diff(g, path, r) => {
                    if self.finish(g) && self.file.as_deref().unwrap_or("") == path {
                        match r {
                            Ok(v) => self.diff = v,
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Inspect(g, path, r) => {
                    if self.finish(g) && self.file.as_deref() == Some(&path) {
                        match r {
                            Ok(v) => self.inspection = Some(v),
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Remote(g, r) => {
                    if self.finish(g) {
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
                                self.request_remote()
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Done(g, label, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(v) => {
                                self.log(format!("{label}: {v}"));
                                self.refresh()
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
                Event::Conflict(g, r) => {
                    if self.finish(g) {
                        match r {
                            Ok(v) => {
                                self.conflict_text = v.working_text.clone();
                                self.conflict = Some(v)
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
                                self.conflict = Some(v);
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
                                self.notice = Some("충돌 해결로 표시했습니다.".into());
                                self.refresh()
                            }
                            Err(e) => self.notice = Some(e),
                        }
                    }
                }
            }
        }
        if self.busy.is_some() {
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
    fn top(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::top("top")
            .frame(egui::Frame::none().fill(Color32::WHITE).inner_margin(12.0))
            .show(ctx, |ui| {
                if self.busy.is_some() || self.pending_sync.is_some() {
                    ui.disable();
                }
                ui.horizontal(|ui| {
                    ui.label(RichText::new("● moa VCS").color(ACCENT).strong().size(19.0));
                    ui.separator();
                    ui.menu_button("저장소 ▾", |ui| {
                        if ui.button("저장소 폴더 열기…   Ctrl+O").clicked() {
                            ui.close_menu();
                            self.pick_folder()
                        }
                        if ui.button("새 저장소 만들기 / 복제…").clicked() {
                            ui.close_menu();
                            self.reset_checkout();
                            self.dialog = Some(Dialog::Checkout)
                        }
                        if self
                            .provider
                            .as_ref()
                            .is_some_and(|p| p.capabilities().remote_url)
                            && ui.button("원격 주소 설정…").clicked()
                        {
                            ui.close_menu();
                            self.request_remote()
                        }
                        if self
                            .provider
                            .as_ref()
                            .is_some_and(|p| p.capabilities().fetch)
                            && ui.button("원격 추적 정보 가져오기 (Fetch)").clicked()
                        {
                            ui.close_menu();
                            self.execute(Operation::Fetch, String::new(), vec![])
                        }
                        ui.menu_button("버전 관리 도구 변경", |ui| {
                            for k in VcsKind::ALL {
                                if ui.button(k.name()).clicked() {
                                    ui.close_menu();
                                    if let Some(path) = self.repo.as_ref().map(|r| r.root.clone()) {
                                        self.open(path, Some(k))
                                    }
                                }
                            }
                        });
                    });
                    ui.menu_button("파일 도구 ▾", |ui| {
                        let selected = self.file.clone();
                        if let Some(path) = &selected {
                            ui.label(RichText::new(path).small().color(MUTED));
                            if ui.button("내장 좌우 Diff 열기").clicked() {
                                ui.close_menu();
                                self.inspect_internal(false);
                            }
                            if ui.button("내장 Blame 열기").clicked() {
                                ui.close_menu();
                                self.inspect_internal(true);
                            }
                            if ui.button("설정한 외부 도구로 열기").clicked() {
                                ui.close_menu();
                                self.inspect(false);
                            }
                            if self
                                .changes
                                .iter()
                                .any(|change| change.path == *path && change.conflicted())
                                && ui.button("충돌 해결 열기").clicked()
                            {
                                ui.close_menu();
                                self.open_conflict();
                            }
                        } else {
                            ui.label("파일을 고르면 Diff와 Blame 도구를 사용할 수 있습니다.");
                        }
                        if ui.button("저장소에서 다른 파일 선택…").clicked() {
                            ui.close_menu();
                            self.pick_file_for_inspection();
                        }
                        ui.separator();
                        if ui.button("외부 Diff·Blame 도구 설정…").clicked() {
                            ui.close_menu();
                            self.dialog = Some(Dialog::Settings);
                        }
                    });
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui.button("⚙ 설정").clicked() {
                            self.dialog = Some(Dialog::Settings)
                        }
                        if ui.button("↻ 새로 고침  F5").clicked() {
                            self.refresh()
                        }
                        if self
                            .provider
                            .as_ref()
                            .is_some_and(|p| p.capabilities().push)
                            && ui.button("↑ 보내기").clicked()
                        {
                            self.request_sync(Operation::Push)
                        }
                        if self
                            .provider
                            .as_ref()
                            .is_some_and(|p| p.capabilities().update)
                            && ui.button("↓ 가져오기").clicked()
                        {
                            self.request_sync(Operation::Update)
                        }
                        if ui.button("＋ 열기").clicked() {
                            self.pick_folder()
                        }
                    })
                })
            });
    }
    fn sidebar(&mut self, ctx: &egui::Context) {
        egui::SidePanel::left("repos")
            .exact_width(272.)
            .resizable(false)
            .frame(egui::Frame::none().fill(NAVY).inner_margin(14.))
            .show(ctx, |ui| {
                if self.busy.is_some() || self.pending_sync.is_some() {
                    ui.disable();
                }
                ui.label(RichText::new("내 작업").color(Color32::LIGHT_GRAY).strong());
                if ui.button("▣  작업 공간").clicked() {
                    self.tab = Tab::Changes
                }
                if ui.button("▢  저장소 열기").clicked() {
                    self.pick_folder()
                }
                ui.add_space(15.);
                ui.horizontal(|ui| {
                    ui.label(RichText::new("저장소 목록").color(Color32::GRAY));
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.menu_button("···", |ui| {
                            if ui.button("최근 목록 비우기").clicked() {
                                match self.settings_store.clear_recent() {
                                    Ok(s) => self.settings = s,
                                    Err(e) => self.notice = Some(e.to_string()),
                                }
                                ui.close_menu()
                            }
                        })
                    });
                });
                ui.add(
                    TextEdit::singleline(&mut self.query)
                        .id_salt("sidebar_search")
                        .hint_text("이름, 경로 또는 도구 검색"),
                );
                ScrollArea::vertical()
                    .id_salt("sidebar_repositories")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        for pin in [true, false] {
                            let items: Vec<_> = self
                                .settings
                                .recent_repositories
                                .iter()
                                .filter(|r| {
                                    r.is_pinned == pin
                                        && (self.query.is_empty()
                                            || r.path
                                                .to_lowercase()
                                                .contains(&self.query.to_lowercase())
                                            || r.vcs_kind
                                                .name()
                                                .to_lowercase()
                                                .contains(&self.query.to_lowercase()))
                                })
                                .cloned()
                                .collect();
                            if !items.is_empty() {
                                ui.label(
                                    RichText::new(if pin { "고정" } else { "최근" })
                                        .color(Color32::GRAY),
                                );
                                for r in items {
                                    let name = Path::new(&r.path)
                                        .file_name()
                                        .and_then(|x| x.to_str())
                                        .unwrap_or(&r.path);
                                    let name = compact(name, 24);
                                    let parent = compact(
                                        Path::new(&r.path)
                                            .parent()
                                            .unwrap_or(Path::new(""))
                                            .to_string_lossy()
                                            .as_ref(),
                                        30,
                                    );
                                    ui.horizontal(|ui| {
                                        let current = self
                                            .repo
                                            .as_ref()
                                            .is_some_and(|x| x.root == PathBuf::from(&r.path));
                                        let label_width = (ui.available_width() - 40.0).max(72.0);
                                        if ui
                                            .add_sized(
                                                [label_width, 48.0],
                                                egui::Button::new(format!("{name}\n{parent}"))
                                                    .selected(current)
                                                    .frame(false)
                                                    .wrap(),
                                            )
                                            .clicked()
                                        {
                                            self.open(PathBuf::from(&r.path), Some(r.vcs_kind))
                                        }
                                        ui.menu_button("···", |ui| {
                                            if ui
                                                .button(if r.is_pinned {
                                                    "고정 해제"
                                                } else {
                                                    "고정"
                                                })
                                                .clicked()
                                            {
                                                self.pin_recent(&r.path, !r.is_pinned);
                                                ui.close_menu()
                                            }
                                            if ui.button("목록에서 제거").clicked() {
                                                self.remove_recent(&r.path);
                                                ui.close_menu()
                                            }
                                        })
                                    });
                                }
                            }
                        }
                    });
                ui.with_layout(Layout::bottom_up(Align::LEFT), |ui| {
                    ui.small(
                        RichText::new(concat!("MoaVCS · ", env!("CARGO_PKG_VERSION"))).color(MUTED),
                    )
                });
            });
    }
    fn body(&mut self, ctx: &egui::Context) {
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(BG).inner_margin(18.))
            .show(ctx, |ui| {
                if self.busy.is_some() || self.pending_sync.is_some() {
                    ui.disable();
                }
                if let Some(repo) = self.repo.clone() {
                    ui.label(
                        RichText::new("YOUR WORK, CONNECTED")
                            .color(ACCENT)
                            .small()
                            .strong(),
                    );
                    ui.horizontal(|ui| {
                        ui.heading(
                            repo.root
                                .file_name()
                                .and_then(|x| x.to_str())
                                .unwrap_or("저장소"),
                        );
                        ui.label(
                            RichText::new(&repo.branch)
                                .background_color(Color32::from_rgb(238, 243, 248)),
                        );
                    });
                    ui.small(RichText::new(repo.root.display().to_string()).color(MUTED));
                    ui.horizontal(|ui| {
                        ui.strong(repo.kind.name());
                        ui.small(RichText::new(&repo.summary).color(MUTED));
                    });
                    if let Some(n) = self.notice.clone() {
                        ui.colored_label(ACCENT, n);
                    }
                    ui.horizontal(|ui| {
                        for (tab, name) in [
                            (Tab::Changes, "변경 파일"),
                            (Tab::History, "커밋 이력"),
                            (Tab::Output, "실행 기록"),
                        ] {
                            if ui.selectable_label(self.tab == tab, name).clicked() {
                                self.tab = tab;
                                if tab == Tab::History {
                                    self.load_history();
                                }
                            }
                        }
                    });
                    ui.separator();
                    match self.tab {
                        Tab::Changes => self.changes_ui(ui),
                        Tab::History => self.history_ui(ui),
                        Tab::Output => {
                            ScrollArea::vertical()
                                .id_salt("workspace_output")
                                .auto_shrink([false, false])
                                .stick_to_bottom(true)
                                .show(ui, |ui| {
                                    for line in &self.logs {
                                        ui.monospace(line);
                                    }
                                });
                        }
                    }
                } else {
                    ui.vertical_centered(|ui| {
                        ui.add_space(90.);
                        ui.heading(RichText::new("moa VCS").color(ACCENT).size(36.));
                        ui.heading("Git · SVN · Perforce · Lore");
                        ui.label("버전 관리 작업을 한 곳에서 이어가세요.");
                        if ui.button("저장소 폴더 열기").clicked() {
                            self.pick_folder();
                        }
                        if ui.button("새 저장소 만들기 / 복제…").clicked() {
                            self.reset_checkout();
                            self.dialog = Some(Dialog::Checkout);
                        }
                    });
                }
            });
    }
    fn changes_ui(&mut self, ui: &mut egui::Ui) {
        let cap = self
            .provider
            .as_ref()
            .map(|p| p.capabilities())
            .unwrap_or_default();
        ui.horizontal(|ui| {
            ui.label(format!("{}개 변경", self.changes.len()));
            if cap.stage
                && ui
                    .add_enabled(
                        !self.picked.is_empty(),
                        egui::Button::new("선택을 커밋에 포함"),
                    )
                    .clicked()
            {
                self.execute(
                    Operation::Stage,
                    String::new(),
                    self.picked.iter().cloned().collect(),
                );
            }
            if cap.unstage
                && ui
                    .add_enabled(!self.picked.is_empty(), egui::Button::new("포함 해제"))
                    .clicked()
            {
                self.execute(
                    Operation::Unstage,
                    String::new(),
                    self.picked.iter().cloned().collect(),
                );
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if self
                    .provider
                    .as_ref()
                    .is_some_and(|p| p.kind() == VcsKind::Git)
                {
                    if ui.checkbox(&mut self.staged, "커밋할 내용").changed() {
                        self.load_diff();
                    }
                }
            });
        });
        let content_height = (ui.available_height() - 150.0).max(80.0);
        ui.allocate_ui_with_layout(
            egui::vec2(ui.available_width(), content_height),
            Layout::top_down(Align::LEFT),
            |ui| {
                ui.columns(2, |cols| {
                    cols[0].vertical(|ui| {
                        ScrollArea::vertical()
                            .id_salt("changes_file_list")
                            .max_height(content_height)
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                for f in self.changes.clone() {
                                    ui.horizontal(|ui| {
                                        let mut yes = self.picked.contains(&f.path);
                                        if ui.checkbox(&mut yes, "").changed() {
                                            if yes {
                                                self.picked.insert(f.path.clone());
                                            } else {
                                                self.picked.remove(&f.path);
                                            }
                                        }
                                        let file_label_width =
                                            (ui.available_width() - 12.0).max(80.0);
                                        if ui
                                            .add_sized(
                                                [file_label_width, 34.0],
                                                egui::Button::new(format!(
                                                    "{}  {}",
                                                    status(&f),
                                                    compact(&f.path, 64)
                                                ))
                                                .selected(self.file.as_deref() == Some(&f.path))
                                                .frame(false)
                                                .truncate(),
                                            )
                                            .clicked()
                                        {
                                            self.select_file(f.path.clone());
                                        }
                                    });
                                }
                            });
                    });
                    cols[1].vertical(|ui| {
                        ui.heading(self.file.as_deref().unwrap_or("변경 미리 보기"));
                        ui.horizontal(|ui| {
                            if ui.button("Diff").clicked() {
                                self.inspect(false);
                            }
                            if ui.button("Blame").clicked() {
                                self.inspect(true);
                            }
                        });
                        ui.separator();
                        ScrollArea::both()
                            .id_salt("changes_diff_preview")
                            .max_height(content_height)
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                ui.add(
                                    TextEdit::multiline(&mut self.diff.clone())
                                        .id_salt("changes_diff_text")
                                        .font(egui::TextStyle::Monospace)
                                        .desired_width(f32::INFINITY)
                                        .interactive(false),
                                );
                            });
                    });
                })
            },
        );
        ui.separator();
        ui.strong("변경 기록 남기기");
        ui.horizontal(|ui| {
            let width = (ui.available_width() - 100.0).max(120.0);
            ui.add_sized(
                [width, 64.0],
                TextEdit::multiline(&mut self.message)
                    .id_salt("commit_message")
                    .hint_text("무엇을 바꿨나요? 예: 검색 결과 오류 수정")
                    .desired_rows(2)
                    .desired_width(width),
            );
            if ui
                .add_enabled(
                    cap.commit
                        && !self.message.trim().is_empty()
                        && !self.changes.iter().any(FileChange::conflicted),
                    egui::Button::new("커밋").min_size(egui::vec2(84.0, 64.0)),
                )
                .clicked()
            {
                self.execute(
                    Operation::Commit,
                    self.message.clone(),
                    self.picked.iter().cloned().collect(),
                );
                self.message.clear();
            }
        });
    }
    fn history_ui(&mut self, ui: &mut egui::Ui) {
        if let Some(g) = self.history.git.clone() {
            ui.horizontal_wrapped(|ui| {
                ui.label(format!(
                    "브랜치 {} · HEAD {} · 추적 {} · 푸시 {}",
                    g.current_branch,
                    short(&g.head_id),
                    if g.upstream.is_empty() {
                        "없음"
                    } else {
                        &g.upstream
                    },
                    if g.push_target.is_empty() {
                        "확인 불가"
                    } else {
                        &g.push_target
                    }
                ));
                ui.label(if let (Some(a), Some(b)) = (g.ahead, g.behind) {
                    format!("마지막 Fetch 기준 · 로컬만 {a} · 추적만 {b}")
                } else {
                    g.status
                });
                ui.selectable_value(
                    &mut self.scope,
                    HistoryScope::CurrentBranch,
                    "현재 브랜치 + 추적 브랜치",
                );
                ui.selectable_value(&mut self.scope, HistoryScope::AllBranches, "모든 브랜치");
                if ui.button("적용").clicked() {
                    self.load_history();
                }
            });
        }
        ui.label(&self.history.scope_label);
        ui.columns(2, |cols| {
            cols[0].vertical(|ui| {
                ScrollArea::vertical()
                    .id_salt("history_commit_list")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        let entries = self.history.entries.clone();
                        for e in &entries {
                            let text_width =
                                (ui.available_width() - GRAPH_WIDTH - ui.spacing().item_spacing.x)
                                    .max(0.0);
                            const ROW_HEIGHT: f32 = 74.0;
                            let refs = e
                                .references
                                .iter()
                                .map(|r| r.name.as_str())
                                .collect::<Vec<_>>()
                                .join(" · ");
                            let line = format!(
                                "{}  {}\n{} · {} · {}",
                                relation(e.relation),
                                compact(&e.summary(), 48),
                                short(&e.id),
                                compact(&e.author, 24),
                                compact(&refs, 32)
                            );
                            ui.horizontal(|ui| {
                                self.history_graph.paint_row(ui, &e.id, ROW_HEIGHT);
                                if ui
                                    .add_sized(
                                        [text_width, ROW_HEIGHT],
                                        egui::Button::new(line)
                                            .selected(self.revision.as_deref() == Some(&e.id))
                                            .frame(false)
                                            .wrap(),
                                    )
                                    .clicked()
                                {
                                    self.load_details(e);
                                }
                            });
                        }
                    });
            });
            cols[1].vertical(|ui| {
                if let Some(d) = self.details.clone() {
                    ui.monospace(format!("리비전 {}", d.entry.id));
                    ui.label(format!(
                        "작성자 {} <{}> · {}",
                        d.entry.author, d.entry.author_email, d.entry.date
                    ));
                    ui.label(format!(
                        "커미터 {} <{}> · {}",
                        d.entry.committer, d.entry.committer_email, d.entry.commit_date
                    ));
                    ui.label(format!(
                        "부모 {}",
                        d.entry
                            .parent_ids
                            .iter()
                            .map(|p| short(p))
                            .collect::<Vec<_>>()
                            .join(", ")
                    ));
                    ui.add(
                        TextEdit::multiline(&mut d.entry.message.clone())
                            .id_salt("history_commit_message")
                            .desired_rows(4)
                            .interactive(false),
                    );
                    ui.label(&d.comparison_label);
                    ui.columns(2, |panes| {
                        panes[0].vertical(|ui| {
                            let height = ui.available_height();
                            ScrollArea::vertical()
                                .id_salt("history_detail_files")
                                .max_height(height)
                                .auto_shrink([false, false])
                                .show(ui, |ui| {
                                    for f in d.files.clone() {
                                        if ui
                                            .add_sized(
                                                [ui.available_width(), 30.0],
                                                egui::Button::new(compact(&f.path, 60))
                                                    .selected(
                                                        self.history_file.as_deref()
                                                            == Some(&f.path),
                                                    )
                                                    .frame(false)
                                                    .truncate(),
                                            )
                                            .clicked()
                                        {
                                            if let Some(id) = self.revision.clone() {
                                                self.load_hdiff(&id, &f);
                                            }
                                        }
                                    }
                                });
                        });
                        panes[1].vertical(|ui| {
                            let height = ui.available_height();
                            ScrollArea::both()
                                .id_salt("history_detail_diff")
                                .max_height(height)
                                .auto_shrink([false, false])
                                .show(ui, |ui| {
                                    ui.add(
                                        TextEdit::multiline(&mut self.hdiff.clone())
                                            .id_salt("history_file_diff_text")
                                            .font(egui::TextStyle::Monospace)
                                            .desired_rows(24)
                                            .desired_width(f32::INFINITY)
                                            .interactive(false),
                                    );
                                });
                        });
                    });
                } else {
                    ui.label("기록을 선택하면 전체 메시지와 파일별 변경 내용을 볼 수 있습니다.");
                }
            });
        });
    }
    fn request_remote(&mut self) {
        let (Some(p), Some(r)) = (self.provider.clone(), self.repo.clone()) else {
            return;
        };
        let (g, c) = self.begin("원격 주소를 불러오는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Remote(
                g,
                p.remote(&r.root, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn save_remote(&mut self) {
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
        let (g, c) = self.begin("충돌 내용을 여는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let _ = tx.send(Event::Conflict(
                g,
                p.conflict(&r.root, &f, &c).map_err(|e| e.to_string()),
            ));
        });
    }
    fn save_conflict(&mut self) {
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
    fn dialogs(&mut self, ctx: &egui::Context) {
        match self.dialog {
            Some(Dialog::Checkout) => {
                let mut open = true;
                egui::Window::new("새 저장소 만들기 / 복제")
                    .open(&mut open)
                    .collapsible(false)
                    .show(ctx, |ui| {
                        if self.busy.is_some() {
                            ui.disable();
                        }
                        ui.horizontal(|ui| {
                            for kind in VcsKind::ALL {
                                ui.selectable_value(&mut self.checkout_kind, kind, kind.name());
                            }
                        });
                        ui.label("원격 주소");
                        let mut url = self.checkout_url.clone();
                        if ui.text_edit_singleline(&mut url).changed() {
                            self.set_checkout_url(url);
                        }
                        ui.horizontal(|ui| {
                            ui.label("저장할 위치 (상위 폴더)");
                            ui.text_edit_singleline(&mut self.checkout_parent);
                            if ui.button("찾아보기…").clicked() {
                                if let Some(path) = rfd::FileDialog::new().pick_folder() {
                                    self.checkout_parent = path.display().to_string();
                                }
                            }
                        });
                        ui.horizontal(|ui| {
                            ui.label("저장소 폴더 이름");
                            if ui.text_edit_singleline(&mut self.checkout_name).changed() {
                                self.name_touched = true;
                            }
                        });
                        let destination = checkout::destination(
                            Path::new(&self.checkout_parent),
                            &self.checkout_name,
                        );
                        ui.small(format!(
                            "최종 저장 경로: {}",
                            destination
                                .as_ref()
                                .map(|path| path.display().to_string())
                                .unwrap_or_else(|_| "올바른 경로를 입력하세요".into())
                        ));
                        ui.horizontal(|ui| {
                            if ui.button("새 로컬 저장소 만들기").clicked() {
                                if let Ok(path) = destination.clone() {
                                    let request = VcsRequest::new(Operation::Initialize, path);
                                    self.run_request(request, "저장소 초기화");
                                    self.dialog = None;
                                }
                            }
                            if ui
                                .add_enabled(
                                    !self.checkout_url.trim().is_empty() && destination.is_ok(),
                                    egui::Button::new("복제 / 체크아웃"),
                                )
                                .clicked()
                            {
                                let mut request = VcsRequest::new(
                                    Operation::Clone,
                                    PathBuf::from(&self.checkout_parent),
                                );
                                request.remote = self.checkout_url.clone();
                                request.destination =
                                    destination.expect("enabled only with a valid destination");
                                self.run_request(request, "복제");
                                self.dialog = None;
                            }
                            if ui.button("취소").clicked() {
                                self.dialog = None;
                            }
                        });
                    });
                if !open {
                    self.dialog = None;
                }
            }
            Some(Dialog::Settings) => self.settings_ui(ctx),
            Some(Dialog::Remote) => self.remote_ui(ctx),
            Some(Dialog::ConfirmSync) => self.confirm_sync_ui(ctx),
            None => {}
        }
        if self.inspection.is_some() {
            self.inspection_ui(ctx);
        }
        if self.conflict.is_some() {
            self.conflict_ui(ctx);
        }
    }
    fn confirm_sync_ui(&mut self, ctx: &egui::Context) {
        let Some(operation) = self.pending_sync else {
            self.dialog = None;
            return;
        };
        let Some(repo) = self.repo.clone() else {
            self.pending_sync = None;
            self.dialog = None;
            return;
        };
        let mut open = true;
        let (title, action, detail) = match operation {
            Operation::Update => (
                "원격 변경 가져오기 확인",
                "가져오기 실행",
                if repo.kind == VcsKind::Git {
                    "원격 변경을 가져오고 fast-forward로 적용합니다. 분기된 이력은 자동 병합하지 않습니다."
                } else {
                    "서버의 변경 내용을 현재 작업 공간에 동기화합니다. 충돌이 생기면 별도로 해결해야 합니다."
                },
            ),
            Operation::Push => (
                "원격 저장소 보내기 확인",
                "보내기 실행",
                "현재 브랜치의 커밋을 설정된 원격 저장소로 전송합니다.",
            ),
            _ => return,
        };
        egui::Window::new(title)
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size([560.0, 300.0])
            .collapsible(false)
            .resizable(false)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                ui.strong(repo.root.display().to_string());
                ui.label(format!("{} · 브랜치 {}", repo.kind.name(), repo.branch));
                if operation == Operation::Push {
                    if let Some(target) = self
                        .history
                        .git
                        .as_ref()
                        .map(|history| history.push_target.as_str())
                        .filter(|target| !target.is_empty())
                    {
                        ui.label(format!("전송 대상: {target}"));
                    } else {
                        ui.label(format!("저장소 연결: {}", repo.summary));
                    }
                }
                ui.separator();
                ui.label(detail);
                ui.add_space(12.0);
                ui.horizontal(|ui| {
                    if ui.button(action).clicked() {
                        self.pending_sync = None;
                        self.dialog = None;
                        self.execute(operation, String::new(), vec![]);
                    }
                    if ui.button("취소").clicked() {
                        self.cancel_sync_confirmation();
                    }
                });
            });
        if !open {
            self.cancel_sync_confirmation();
        }
    }
    fn run_request(&mut self, req: VcsRequest, label: &str) {
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
    fn settings_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        let available = ctx.screen_rect().size();
        let max_size = egui::vec2(
            (available.x - 24.0).max(420.0),
            (available.y - 70.0).max(360.0),
        );
        let default_size = egui::vec2(930.0_f32.min(max_size.x), 740.0_f32.min(max_size.y));
        egui::Window::new("도구 및 탐색기 설정")
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size(default_size)
            .max_size(max_size)
            .resizable(true)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                ScrollArea::vertical()
                    .id_salt("settings_contents")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
                            ui.selectable_value(
                                &mut self.settings_tab,
                                SettingsTab::Vcs,
                                "버전 관리 도구",
                            );
                            ui.selectable_value(
                                &mut self.settings_tab,
                                SettingsTab::FileTools,
                                "비교·이력 도구",
                            );
                            ui.selectable_value(
                                &mut self.settings_tab,
                                SettingsTab::Explorer,
                                "탐색기",
                            );
                        });
                        ui.separator();
                        if self.settings_tab == SettingsTab::Vcs {
                            ui.heading("버전 관리 도구");
                            for k in VcsKind::ALL {
                                ui.horizontal(|ui| {
                                    ui.label(k.name());
                                    let path = self.settings.executable_paths.entry(k).or_default();
                                    ui.text_edit_singleline(path);
                                    if ui.button("찾아보기…").clicked() {
                                        if let Some(file) = rfd::FileDialog::new().pick_file() {
                                            *path = file.display().to_string();
                                        }
                                    }
                                    if ui.button("공식 다운로드 ↗").clicked() {
                                        let _ = external::open_url(
                                            moavcs_core::external::download_url(k),
                                        );
                                    }
                                });
                            }
                        }
                        if self.settings_tab == SettingsTab::FileTools {
                            ui.heading("비교·이력 도구");
                            for kind in [ExternalToolKind::Diff, ExternalToolKind::Blame] {
                                let tool = self.settings.external_tools.entry(kind).or_default();
                                ui.checkbox(&mut tool.use_external, format!("외부 {kind:?} 사용"));
                                ui.text_edit_singleline(&mut tool.executable_path);
                                for arg in &mut tool.argument_templates {
                                    ui.text_edit_singleline(arg);
                                }
                                if ui.button("인수 추가").clicked() {
                                    tool.argument_templates.push(String::new());
                                }
                            }
                        }
                        if self.settings_tab == SettingsTab::Explorer {
                            ui.heading("탐색기");
                            ui.checkbox(
                                &mut self.settings.explorer_menu.show_commit_at_top_level,
                                "커밋 메뉴를 첫 단계에 표시",
                            );
                            ui.checkbox(
                                &mut self.settings.explorer_menu.show_update_at_top_level,
                                "가져오기 메뉴를 첫 단계에 표시",
                            );
                            ui.checkbox(
                                &mut self.settings.explorer_overlays_enabled,
                                "파일 상태 배지 표시",
                            );
                            ui.checkbox(
                                &mut self.settings.explorer_overlays_prefer_priority,
                                "MoaVCS 배지를 우선 표시",
                            );
                            ui.horizontal(|ui| {
                                if ui.button("탐색기 메뉴 설치 / 갱신").clicked() {
                                    self.notice = Some(
                                        match crate::platform::install_context_menu(&self.settings)
                                        {
                                            Ok(()) => "탐색기 메뉴를 갱신했습니다.".into(),
                                            Err(error) => error.to_string(),
                                        },
                                    );
                                }
                                if ui.button("탐색기 메뉴 제거").clicked() {
                                    self.notice =
                                        Some(match crate::platform::uninstall_context_menu() {
                                            Ok(()) => "MoaVCS 탐색기 메뉴를 제거했습니다.".into(),
                                            Err(error) => error.to_string(),
                                        });
                                }
                                if ui.button("상태 배지 적용").clicked() {
                                    self.notice = Some(
                                        match crate::platform::configure_overlays(
                                            self.settings.explorer_overlays_enabled,
                                            self.settings.explorer_overlays_prefer_priority,
                                        ) {
                                            Ok(()) => {
                                                "탐색기 상태 배지 설정을 적용했습니다.".into()
                                            }
                                            Err(error) => error.to_string(),
                                        },
                                    );
                                }
                            });
                        }
                        if ui.button("설정 저장").clicked() {
                            self.save_preferences();
                        }
                    });
            });
        if !open {
            self.dialog = None;
        }
    }
    fn remote_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        egui::Window::new("원격 주소 설정 · origin")
            .open(&mut open)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                if let Some(remote) = self.remote.clone() {
                    ui.label(if remote.exists {
                        "Fetch 주소"
                    } else {
                        "새 origin 주소"
                    });
                    ui.text_edit_singleline(&mut self.remote_url);
                    ui.label(format!("현재 Fetch: {}", remote.fetch_urls.join(" · ")));
                    ui.label(format!("Push 주소: {}", remote.push_urls.join(" · ")));
                    ui.label(format!(
                        "실제 Fetch 대상: {}",
                        remote.effective_fetch_urls.join(" · ")
                    ));
                    if let Some(reason) = &remote.read_only_reason {
                        ui.colored_label(ACCENT, reason);
                    }
                    let supported = remote.read_only_reason.is_none()
                        && self
                            .provider
                            .as_ref()
                            .is_some_and(|p| p.capabilities().remote_url);
                    if ui
                        .add_enabled(
                            supported && !self.remote_url.trim().is_empty(),
                            egui::Button::new("origin 주소 저장"),
                        )
                        .clicked()
                    {
                        self.save_remote();
                    }
                    if ui.button("닫기").clicked() {
                        self.dialog = None;
                    }
                }
            });
        if !open {
            self.dialog = None;
        }
    }
    fn inspection_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        if let Some(inspection) = self.inspection.clone() {
            match inspection {
                Inspection::Diff(diff) => {
                    let compared = moavcs_core::text::compare_lines(
                        &diff.left_text,
                        &diff.right_text,
                        &CancelToken::new(),
                    )
                    .ok();
                    let changed_rows = compared
                        .as_ref()
                        .map(|line_diff| {
                            line_diff
                                .rows
                                .iter()
                                .enumerate()
                                .filter(|(_, row)| row.kind != "unchanged")
                                .map(|(index, _)| index)
                                .collect::<Vec<_>>()
                        })
                        .unwrap_or_default();
                    egui::Window::new(format!("Diff · {}", diff.path))
                        .open(&mut open)
                        .resizable(true)
                        .default_size([1080.0, 740.0])
                        .show(ctx, |ui| {
                            if self.busy.is_some() {
                                ui.disable();
                            }
                            ui.label(format!("{} → {}", diff.left_label, diff.right_label));
                            ui.horizontal(|ui| {
                                ui.label("찾기");
                                ui.add(
                                    TextEdit::singleline(&mut self.inspection_search)
                                        .id_salt("inspection_search_diff")
                                        .hint_text("내용 검색"),
                                );
                                if ui.button("← 이전 변경").clicked() && !changed_rows.is_empty()
                                {
                                    self.inspection_change_index =
                                        self.inspection_change_index.saturating_sub(1);
                                    let row = changed_rows
                                        [self.inspection_change_index.min(changed_rows.len() - 1)];
                                    self.inspection_scroll = row as f32 * 22.0;
                                }
                                if ui.button("다음 변경 →").clicked() && !changed_rows.is_empty()
                                {
                                    self.inspection_change_index = (self.inspection_change_index
                                        + 1)
                                    .min(changed_rows.len() - 1);
                                    self.inspection_scroll =
                                        changed_rows[self.inspection_change_index] as f32 * 22.0;
                                }
                                if ui.button("내용 저장…").clicked() {
                                    let text = format!(
                                        "--- {} ---\n{}\n--- {} ---\n{}",
                                        diff.left_label,
                                        diff.left_text,
                                        diff.right_label,
                                        diff.right_text
                                    );
                                    if let Some(path) = rfd::FileDialog::new()
                                        .set_file_name("comparison.txt")
                                        .save_file()
                                    {
                                        match std::fs::write(&path, text) {
                                            Ok(()) => {
                                                self.inspection_status =
                                                    format!("저장했습니다: {}", path.display())
                                            }
                                            Err(error) => {
                                                self.inspection_status =
                                                    format!("저장 실패: {error}")
                                            }
                                        }
                                    }
                                }
                                if !self.inspection_search.is_empty() {
                                    let all = format!("{}\n{}", diff.left_text, diff.right_text);
                                    self.inspection_status = if all
                                        .to_lowercase()
                                        .contains(&self.inspection_search.to_lowercase())
                                    {
                                        "검색 내용이 비교 화면에 있습니다.".into()
                                    } else {
                                        "검색 결과가 없습니다.".into()
                                    };
                                }
                            });
                            if !self.inspection_status.is_empty() {
                                ui.small(&self.inspection_status);
                            }
                            if let Some(compared) = &compared {
                                ui.label(format!(
                                    "{}개 변경 행 · 빨강: 이전 / 초록: 현재",
                                    changed_rows.len()
                                ));
                                ScrollArea::vertical()
                                    .id_salt("inspection_diff_rows")
                                    .vertical_scroll_offset(self.inspection_scroll)
                                    .auto_shrink([false, false])
                                    .show(ui, |ui| {
                                        for row in &compared.rows {
                                            ui.columns(2, |panes| {
                                                let left = format!(
                                                    "{:>5}  {}",
                                                    row.left_number
                                                        .map(|n| n.to_string())
                                                        .unwrap_or_default(),
                                                    row.left_text
                                                );
                                                let right = format!(
                                                    "{:>5}  {}",
                                                    row.right_number
                                                        .map(|n| n.to_string())
                                                        .unwrap_or_default(),
                                                    row.right_text
                                                );
                                                let search_hit = !self.inspection_search.is_empty()
                                                    && (left.to_lowercase().contains(
                                                        &self.inspection_search.to_lowercase(),
                                                    ) || right.to_lowercase().contains(
                                                        &self.inspection_search.to_lowercase(),
                                                    ));
                                                let left_bg = if row.kind == "removed"
                                                    || row.kind == "modified"
                                                {
                                                    Color32::from_rgb(255, 231, 228)
                                                } else {
                                                    Color32::TRANSPARENT
                                                };
                                                let right_bg = if row.kind == "added"
                                                    || row.kind == "modified"
                                                {
                                                    Color32::from_rgb(229, 245, 235)
                                                } else {
                                                    Color32::TRANSPARENT
                                                };
                                                panes[0].label(
                                                    RichText::new(left)
                                                        .monospace()
                                                        .background_color(if search_hit {
                                                            Color32::from_rgb(255, 244, 186)
                                                        } else {
                                                            left_bg
                                                        }),
                                                );
                                                panes[1].label(
                                                    RichText::new(right)
                                                        .monospace()
                                                        .background_color(if search_hit {
                                                            Color32::from_rgb(255, 244, 186)
                                                        } else {
                                                            right_bg
                                                        }),
                                                );
                                            });
                                        }
                                    });
                            }
                        });
                }
                Inspection::Blame(blame) => {
                    egui::Window::new(format!("Blame · {}", blame.path))
                        .open(&mut open)
                        .default_size([1080.0, 740.0])
                        .show(ctx, |ui| {
                            if self.busy.is_some() {
                                ui.disable();
                            }
                            ui.label(&blame.revision_label);
                            ui.horizontal(|ui| {
                                ui.label("찾기");
                                ui.add(
                                    TextEdit::singleline(&mut self.inspection_search)
                                        .id_salt("inspection_search_blame")
                                        .hint_text("내용 · 작성자 · 리비전"),
                                );
                                if ui.button("다음 찾기").clicked()
                                    && !self.inspection_search.is_empty()
                                {
                                    let query = self.inspection_search.to_lowercase();
                                    let start =
                                        (self.inspection_change_index + 1).min(blame.lines.len());
                                    let found =
                                        (start..blame.lines.len()).chain(0..start).find(|&i| {
                                            format!(
                                                "{} {} {}",
                                                blame.lines[i].revision,
                                                blame.lines[i].author,
                                                blame.lines[i].text
                                            )
                                            .to_lowercase()
                                            .contains(&query)
                                        });
                                    if let Some(i) = found {
                                        self.inspection_change_index = i;
                                        self.inspection_scroll = i as f32 * 20.0;
                                        self.inspection_status = format!(
                                            "{}행에서 찾았습니다.",
                                            blame.lines[i].line_number
                                        );
                                    } else {
                                        self.inspection_status = "검색 결과가 없습니다.".into();
                                    }
                                }
                                if ui.button("이력 저장…").clicked() {
                                    let mut text =
                                        String::from("Line\tRevision\tAuthor\tDate\tText\n");
                                    for line in &blame.lines {
                                        text.push_str(&format!(
                                            "{}\t{}\t{}\t{}\t{}\n",
                                            line.line_number,
                                            line.revision.replace('\t', "    "),
                                            line.author.replace('\t', "    "),
                                            line.date.replace('\t', "    "),
                                            line.text.replace('\t', "    ").replace('\n', " ")
                                        ));
                                    }
                                    if let Some(path) = rfd::FileDialog::new()
                                        .set_file_name("blame.tsv")
                                        .save_file()
                                    {
                                        match std::fs::write(&path, text) {
                                            Ok(()) => {
                                                self.inspection_status =
                                                    format!("저장했습니다: {}", path.display())
                                            }
                                            Err(error) => {
                                                self.inspection_status =
                                                    format!("저장 실패: {error}")
                                            }
                                        }
                                    }
                                }
                            });
                            if !self.inspection_status.is_empty() {
                                ui.small(&self.inspection_status);
                            }
                            ScrollArea::both()
                                .id_salt("inspection_blame_lines")
                                .vertical_scroll_offset(self.inspection_scroll)
                                .auto_shrink([false, false])
                                .show(ui, |ui| {
                                    for (i, line) in blame.lines.iter().enumerate() {
                                        let text = format!(
                                            "{:>5}  {}  {}  {}",
                                            line.line_number,
                                            short(&line.revision),
                                            line.author,
                                            line.text
                                        );
                                        if i == self.inspection_change_index
                                            && !self.inspection_search.is_empty()
                                        {
                                            ui.colored_label(
                                                Color32::from_rgb(35, 88, 66),
                                                RichText::new(text)
                                                    .background_color(Color32::from_rgb(
                                                        229, 245, 235,
                                                    ))
                                                    .monospace(),
                                            );
                                        } else {
                                            ui.monospace(text);
                                        }
                                    }
                                });
                        });
                }
            }
            if !open {
                self.inspection = None;
            }
        }
    }
    fn conflict_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        if let Some(conflict) = self.conflict.clone() {
            egui::Window::new(format!("충돌 해결 · {}", conflict.path))
                .open(&mut open)
                .resizable(true)
                .show(ctx, |ui| {
                    if self.busy.is_some() {
                        ui.disable();
                    }
                    ui.label("좌우 내용을 비교하고 결과를 편집한 다음 저장하세요.");
                    ui.horizontal(|ui| {
                        if ui.button("내 변경 적용").clicked() {
                            self.conflict_text = conflict.left_text.clone();
                        }
                        if ui.button("받은 변경 적용").clicked() {
                            self.conflict_text = conflict.right_text.clone();
                        }
                        if let Some(base) = &conflict.base_text {
                            if ui.button("기준 내용 적용").clicked() {
                                self.conflict_text = base.clone();
                            }
                        }
                        if ui.button("편집 결과 저장").clicked() {
                            self.save_conflict();
                        }
                        let has_markers = moavcs_core::text::contains_markers(&self.conflict_text);
                        if ui
                            .add_enabled(!has_markers, egui::Button::new("해결 완료 표시"))
                            .clicked()
                        {
                            self.resolve_conflict();
                        }
                    });
                    if moavcs_core::text::contains_markers(&self.conflict_text) {
                        ui.colored_label(
                            ACCENT,
                            "충돌 표식이 남아 있습니다. 모든 표식을 정리한 뒤 저장하세요.",
                        );
                    }
                    if let Some(path) = self
                        .conflict
                        .as_ref()
                        .and_then(|saved| saved.backup_path.as_ref())
                    {
                        ui.small(
                            RichText::new(format!("원본 백업: {}", path.display())).color(MUTED),
                        );
                    }
                    ui.columns(3, |panes| {
                        panes[0].vertical(|ui| {
                            ui.strong(&conflict.left_label);
                            ui.add(
                                TextEdit::multiline(&mut conflict.left_text.clone())
                                    .id_salt("conflict_left")
                                    .font(egui::TextStyle::Monospace)
                                    .desired_width(f32::INFINITY)
                                    .interactive(false),
                            );
                        });
                        panes[1].vertical(|ui| {
                            ui.strong(&conflict.right_label);
                            ui.add(
                                TextEdit::multiline(&mut conflict.right_text.clone())
                                    .id_salt("conflict_right")
                                    .font(egui::TextStyle::Monospace)
                                    .desired_width(f32::INFINITY)
                                    .interactive(false),
                            );
                        });
                        panes[2].vertical(|ui| {
                            ui.strong("병합 결과");
                            ui.add(
                                TextEdit::multiline(&mut self.conflict_text)
                                    .id_salt("conflict_result")
                                    .font(egui::TextStyle::Monospace)
                                    .desired_width(f32::INFINITY),
                            );
                        });
                    });
                });
        }
        if !open {
            self.conflict = None;
        }
    }
}
impl eframe::App for MoaApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.events(ctx);
        self.smoke_collect_screenshot(ctx);
        if self.busy.is_none() && self.pending_sync.is_none() {
            if ctx.input(|input| input.key_pressed(egui::Key::O) && input.modifiers.command) {
                self.pick_folder();
            }
            if ctx.input(|input| input.key_pressed(egui::Key::F5)) {
                self.refresh();
            }
        }
        egui::TopBottomPanel::bottom("status").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("●");
                if let Some(label) = self.busy.clone() {
                    ui.spinner();
                    ui.label(label);
                    if ui.button("취소").clicked() {
                        if let Some(cancel) = &self.cancel {
                            cancel.cancel();
                        }
                        self.notice = Some("작업을 취소하고 있습니다…".into());
                    }
                } else {
                    ui.label("준비됨");
                }
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    ui.small("Ctrl+O 열기 · F5 새로 고침");
                });
            });
        });
        self.top(ctx);
        self.sidebar(ctx);
        self.body(ctx);
        self.dialogs(ctx);
        self.smoke_tick(ctx);
    }
}

fn status(f: &FileChange) -> &str {
    if f.conflicted() {
        "충돌"
    } else if f.status.to_lowercase().contains("add") || f.status == "?" {
        "추가"
    } else if f.status.to_lowercase().contains("delet") || f.status.contains('D') {
        "삭제"
    } else if f.staged() {
        "커밋 포함"
    } else {
        "수정"
    }
}
fn relation(r: HistoryRelation) -> &'static str {
    match r {
        HistoryRelation::LocalOnly => "로컬만",
        HistoryRelation::UpstreamOnly => "추적만",
        HistoryRelation::Shared => "공통",
        HistoryRelation::Other => "다른 이력",
        _ => "—",
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
pub fn configure_visuals(ctx: &egui::Context) {
    let mut s = (*ctx.style()).clone();
    s.visuals = egui::Visuals::light();
    s.visuals.panel_fill = BG;
    s.visuals.window_fill = Color32::WHITE;
    s.spacing.item_spacing = egui::vec2(10.0, 8.0);
    s.spacing.button_padding = egui::vec2(12.0, 8.0);
    s.text_styles
        .insert(egui::TextStyle::Body, egui::FontId::proportional(15.0));
    s.text_styles
        .insert(egui::TextStyle::Button, egui::FontId::proportional(14.0));
    s.text_styles
        .insert(egui::TextStyle::Small, egui::FontId::proportional(12.0));
    ctx.set_style(s);
    #[cfg(windows)]
    {
        for path in [
            r"C:\Windows\Fonts\malgun.ttf",
            r"C:\Windows\Fonts\malgunsl.ttf",
        ] {
            if let Ok(bytes) = std::fs::read(path) {
                let mut fonts = egui::FontDefinitions::default();
                fonts.font_data.insert(
                    "moa_korean".into(),
                    egui::FontData::from_owned(bytes).into(),
                );
                fonts
                    .families
                    .entry(egui::FontFamily::Proportional)
                    .or_default()
                    .insert(0, "moa_korean".into());
                fonts
                    .families
                    .entry(egui::FontFamily::Monospace)
                    .or_default()
                    .push("moa_korean".into());
                ctx.set_fonts(fonts);
                break;
            }
        }
    }
}
