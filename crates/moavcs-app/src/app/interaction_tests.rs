use super::*;

fn isolated_app() -> MoaApp {
    let (tx, rx) = std::sync::mpsc::channel();
    MoaApp::new(
        LaunchOptions {
            ui_smoke: Some(std::env::temp_dir().join("moavcs-sidebar-test")),
            ..Default::default()
        },
        rx,
        tx,
    )
}

fn app_without_smoke() -> MoaApp {
    let (tx, rx) = std::sync::mpsc::channel();
    MoaApp::new(LaunchOptions::default(), rx, tx)
}

fn rendered_text(frame: &egui::FullOutput, label: &str) -> Option<egui::Pos2> {
    frame.shapes.iter().find_map(|shape| match &shape.shape {
        egui::Shape::Text(text) if text.galley.text() == label => {
            let rect = text.visual_bounding_rect().intersect(shape.clip_rect);
            rect.is_positive().then(|| rect.center())
        }
        _ => None,
    })
}

fn click(pos: egui::Pos2, pressed: bool) -> Vec<egui::Event> {
    vec![
        egui::Event::PointerMoved(pos),
        egui::Event::PointerButton {
            pos,
            button: egui::PointerButton::Primary,
            pressed,
            modifiers: egui::Modifiers::NONE,
        },
    ]
}

#[test]
fn clean_worktree_can_show_unpushed_commits_and_filter_publication_state() {
    let mut app = isolated_app();
    app.repo = Some(RepositoryInfo {
        kind: VcsKind::Git,
        ..Default::default()
    });
    let git = GitHistoryContext {
        current_branch: "main".into(),
        head_id: "aaaaaaa".into(),
        push_target: "origin/main".into(),
        push_target_id: "bbbbbbb".into(),
        push_ahead: Some(1),
        push_behind: Some(0),
        ..Default::default()
    };
    app.git_sync = Some(git.clone());
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    let frame = ctx.run(
        egui::RawInput {
            screen_rect: Some(egui::Rect::from_min_size(
                egui::Pos2::ZERO,
                egui::vec2(1370., 900.),
            )),
            ..Default::default()
        },
        |ctx| app.body(ctx),
    );
    assert!(rendered_text(&frame, "미커밋 파일 없음").is_some());
    assert!(rendered_text(&frame, "미푸시 1개").is_some());

    app.history.git = Some(git);
    app.history.entries = [HistoryRelation::Shared, HistoryRelation::LocalOnly]
        .into_iter()
        .enumerate()
        .map(|(i, relation)| HistoryEntry {
            id: i.to_string(),
            push_relation: Some(relation),
            ..Default::default()
        })
        .collect();
    app.history_filter = Some(HistoryRelation::LocalOnly);
    app.apply_history_filter();
    assert_eq!(app.visible_history, [1]);
    app.history_filter = Some(HistoryRelation::UpstreamOnly);
    app.apply_history_filter();
    assert!(app.visible_history.is_empty() && app.details.is_none());
    app.history.git.as_mut().unwrap().push_ahead = None;
    app.apply_history_filter();
    assert_eq!(app.history_filter, None);
    assert_eq!(app.visible_history, [0, 1]);
}

#[test]
fn selecting_git_file_previews_the_side_with_changes() {
    let mut app = isolated_app();
    app.repo = Some(RepositoryInfo {
        kind: VcsKind::Git,
        ..Default::default()
    });
    for (path, index, working) in [("index", "M", ""), ("mixed", "M", "M"), ("work", "", "M")] {
        app.changes.push(FileChange {
            path: path.into(),
            status: "modified".into(),
            index_status: index.into(),
            worktree_status: working.into(),
            ..Default::default()
        });
    }
    app.select_file("index");
    assert!(app.staged);
    app.select_file("mixed");
    assert!(!app.staged);
    app.staged = true;
    app.select_file("mixed");
    assert!(app.staged);
    app.select_file("work");
    assert!(!app.staged);
}

#[test]
fn minimum_window_keeps_file_list_visible_with_sync_summary_and_notice() {
    let mut app = isolated_app();
    app.repo = Some(RepositoryInfo {
        kind: VcsKind::Git,
        ..Default::default()
    });
    app.provider = Some(create_provider(VcsKind::Git, "git"));
    app.git_sync = Some(GitHistoryContext::default());
    app.notice = Some("원격 상태 안내".into());
    app.set_changes(vec![FileChange {
        path: "compact-file.txt".into(),
        status: "untracked".into(),
        ..Default::default()
    }]);
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    let frame = ctx.run(
        egui::RawInput {
            screen_rect: Some(egui::Rect::from_min_size(
                egui::Pos2::ZERO,
                egui::vec2(860., 620.),
            )),
            ..Default::default()
        },
        |ctx| {
            app.top(ctx);
            app.sidebar(ctx);
            app.body(ctx);
        },
    );
    for label in ["compact-file.txt", "해제", "커밋"] {
        assert!(rendered_text(&frame, label).is_some(), "{label}");
    }
}

#[test]
fn sidebar_click_switches_view_while_history_is_in_flight() {
    let mut app = isolated_app();
    app.repo = Some(RepositoryInfo::default());
    let (request, token) = app.begin_read(ReadSlot::History, "이력 조회");
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    let render = |app: &mut MoaApp, events| {
        ctx.run(
            egui::RawInput {
                events,
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    egui::vec2(1370., 900.),
                )),
                ..Default::default()
            },
            |ctx| app.sidebar(ctx),
        )
    };
    let frame = render(&mut app, vec![]);
    let target = rendered_text(&frame, "실행 기록").expect("sidebar renders the output tab");
    for pressed in [true, false] {
        render(&mut app, click(target, pressed));
    }
    assert!(app.tab == Tab::Output);
    assert!(!token.cancelled());
    app.tx
        .send(Event::History(
            request,
            Ok(HistorySnapshot {
                entries: vec![HistoryEntry {
                    id: "abc123".into(),
                    ..Default::default()
                }],
                ..Default::default()
            }),
        ))
        .unwrap();
    app.events(&ctx);
    assert_eq!(app.history.entries.len(), 1);
    assert!(app.tab == Tab::Output);
}

#[test]
fn last_file_can_be_selected_after_scrolling_a_large_change_list() {
    let mut app = isolated_app();
    app.repo = Some(RepositoryInfo::default());
    app.changes = (0..2000)
        .map(|i| FileChange {
            path: format!("file_{i:04}.rs"),
            status: "modified".into(),
            ..Default::default()
        })
        .collect();
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    let draw = |app: &mut MoaApp, events| {
        ctx.run(
            egui::RawInput {
                events,
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    egui::vec2(1370., 900.),
                )),
                ..Default::default()
            },
            |ctx| {
                app.top(ctx);
                app.sidebar(ctx);
                app.body(ctx);
            },
        )
    };
    let first = draw(&mut app, vec![]);
    let pos = rendered_text(&first, "file_0000.rs").expect("first file visible");
    assert!(rendered_text(&first, "file_1999.rs").is_none());
    draw(
        &mut app,
        vec![
            egui::Event::PointerMoved(pos),
            egui::Event::MouseWheel {
                unit: egui::MouseWheelUnit::Point,
                delta: egui::vec2(0., -1_000_000.),
                modifiers: egui::Modifiers::NONE,
            },
        ],
    );
    let target = (0..20)
        .find_map(|_| rendered_text(&draw(&mut app, vec![]), "file_1999.rs"))
        .expect("the final row is reachable by scrolling");
    for pressed in [true, false] {
        draw(&mut app, click(target, pressed));
    }
    assert_eq!(app.file.as_deref(), Some("file_1999.rs"));
}

#[test]
fn superseded_history_result_is_ignored() {
    let mut app = app_without_smoke();
    let (old_id, old_token) = app.begin_read(ReadSlot::History, "이전 이력");
    let (current_id, current_token) = app.begin_read(ReadSlot::History, "현재 이력");
    assert!(old_token.cancelled());
    assert!(!current_token.cancelled());

    app.tx
        .send(Event::History(
            old_id,
            Ok(HistorySnapshot {
                entries: vec![HistoryEntry {
                    id: "stale".into(),
                    ..Default::default()
                }],
                ..Default::default()
            }),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(app.history.entries.is_empty());
    assert!(app.read_slots.contains_key(&ReadSlot::History));

    app.tx
        .send(Event::History(
            current_id,
            Ok(HistorySnapshot {
                entries: vec![HistoryEntry {
                    id: "current".into(),
                    ..Default::default()
                }],
                ..Default::default()
            }),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert_eq!(app.history.entries[0].id, "current");
}

#[test]
fn starting_a_mutation_cancels_reads_and_rejects_their_results() {
    let mut app = app_without_smoke();
    let (read_id, read_token) = app.begin_read(ReadSlot::Status, "상태 조회");
    let (mutation_id, _) = app.begin("저장소 변경");
    assert!(read_token.cancelled());

    app.tx
        .send(Event::Status(
            read_id,
            Ok(vec![FileChange {
                path: "stale.txt".into(),
                status: "modified".into(),
                ..Default::default()
            }]),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(app.changes.is_empty());
    assert_eq!(app.generation, mutation_id);
    assert!(app.busy.is_some());
}

#[test]
fn failed_repository_open_keeps_the_existing_repository() {
    let mut app = app_without_smoke();
    app.repo = Some(RepositoryInfo {
        root: "D:/repos/current".into(),
        ..Default::default()
    });
    let (open_id, _) = app.begin_read(ReadSlot::Open, "저장소 열기");
    app.tx
        .send(Event::Opened(open_id, Err("경로를 찾지 못했습니다".into())))
        .unwrap();
    app.events(&egui::Context::default());

    assert_eq!(
        app.repo.as_ref().unwrap().root,
        PathBuf::from("D:/repos/current")
    );
    assert_eq!(app.notice.as_deref(), Some("경로를 찾지 못했습니다"));
    assert!(!app.controls_locked());
}

#[test]
fn stale_repository_open_result_is_ignored_after_a_newer_open() {
    let mut app = app_without_smoke();
    app.repo = Some(RepositoryInfo {
        root: "D:/repos/current".into(),
        ..Default::default()
    });
    let (stale_id, stale_token) = app.begin_read(ReadSlot::Open, "이전 저장소");
    let (current_id, _) = app.begin_read(ReadSlot::Open, "새 저장소");
    assert!(stale_token.cancelled());

    app.tx
        .send(Event::Opened(stale_id, Err("이전 조회 결과".into())))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(app.controls_locked());
    assert!(app.notice.is_none());
    assert_eq!(
        app.repo.as_ref().unwrap().root,
        PathBuf::from("D:/repos/current")
    );

    app.tx
        .send(Event::Opened(current_id, Err("새 경로 오류".into())))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(!app.controls_locked());
    assert_eq!(app.notice.as_deref(), Some("새 경로 오류"));
}

#[test]
fn stale_remote_and_conflict_reads_cannot_open_old_repository_dialogs() {
    let mut app = app_without_smoke();
    let (remote_id, _) = app.begin_read(ReadSlot::Remote, "원격 조회");
    let (conflict_id, _) = app.begin_read(ReadSlot::Conflict, "충돌 조회");
    app.cancel_reads();
    app.tx
        .send(Event::Remote(remote_id, Ok(RemoteInfo::default())))
        .unwrap();
    app.tx
        .send(Event::Conflict(
            conflict_id,
            "old.txt".into(),
            Ok(ConflictFile::default()),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(app.remote.is_none());
    assert!(app.dialog.is_none());
    assert!(app.conflict.is_none());
}

#[test]
fn history_empty_result_cancels_old_details_and_preserves_valid_selection() {
    let mut app = app_without_smoke();
    app.revision = Some("kept".into());
    app.details = Some(HistoryDetails::default());
    app.hdiff = "old diff".into();
    let (details_id, details_token) = app.begin_read(ReadSlot::Details, "세부 내역");
    let (diff_id, diff_token) = app.begin_read(ReadSlot::HistoryDiff, "이력 Diff");
    let (history_id, _) = app.begin_read(ReadSlot::History, "이력");
    app.tx
        .send(Event::History(history_id, Ok(HistorySnapshot::default())))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(details_token.cancelled());
    assert!(diff_token.cancelled());
    assert!(app.revision.is_none());
    assert!(app.details.is_none());
    assert!(app.hdiff.is_empty());
    app.tx
        .send(Event::Details(
            details_id,
            "kept".into(),
            Ok(HistoryDetails::default()),
        ))
        .unwrap();
    app.tx
        .send(Event::Hdiff(
            diff_id,
            "kept".into(),
            "old.txt".into(),
            Ok(("old diff".into(), PreparedText::default())),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert!(app.details.is_none());
    assert!(app.hdiff.is_empty());

    app.revision = Some("kept".into());
    let (history_id, _) = app.begin_read(ReadSlot::History, "이력");
    app.tx
        .send(Event::History(
            history_id,
            Ok(HistorySnapshot {
                entries: vec![HistoryEntry {
                    id: "kept".into(),
                    ..Default::default()
                }],
                ..Default::default()
            }),
        ))
        .unwrap();
    app.events(&egui::Context::default());
    assert_eq!(app.revision.as_deref(), Some("kept"));
}

#[test]
fn startup_action_is_consumed_only_once() {
    let mut app = app_without_smoke();
    app.startup_action_pending = true;
    app.startup.action = "fetch".into();
    assert_eq!(app.take_startup_action().as_deref(), Some("fetch"));
    assert!(app.take_startup_action().is_none());
}

#[test]
fn status_bar_cancel_button_cancels_background_read() {
    let mut app = app_without_smoke();
    let (_, token) = app.begin_read(ReadSlot::History, "커밋 이력을 불러오는 중…");
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    let render = |app: &mut MoaApp, events| {
        ctx.run(
            egui::RawInput {
                events,
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    egui::vec2(1370., 900.),
                )),
                ..Default::default()
            },
            |ctx| app.status_ui(ctx),
        )
    };
    let frame = render(&mut app, vec![]);
    let target = rendered_text(&frame, "조회 취소").expect("read cancel button is visible");
    for pressed in [true, false] {
        render(&mut app, click(target, pressed));
    }
    assert!(token.cancelled());
    assert!(!app.is_loading());
}
