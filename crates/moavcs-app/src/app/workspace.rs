use super::*;

const RAIL_TEXT: Color32 = Color32::from_rgb(233, 244, 242);
const RAIL_MUTED: Color32 = Color32::from_rgb(179, 201, 201);
const RAIL_ACTIVE: Color32 = Color32::from_rgb(36, 76, 79);
const RAIL_HOVER: Color32 = Color32::from_rgb(36, 61, 68);
const RAIL_MARK: Color32 = Color32::from_rgb(117, 221, 190);

impl MoaApp {
    pub(super) fn top(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::top("app_bar")
            .exact_height(56.)
            .frame(
                egui::Frame::none()
                    .fill(SURFACE)
                    .inner_margin(egui::vec2(18., 8.)),
            )
            .show(ctx, |ui| {
                ui.add_enabled_ui(!self.controls_locked(), |ui| {
                    ui.horizontal_centered(|ui| {
                        ui.menu_button("저장소", |ui| {
                            if ui.button("저장소 열기…     Ctrl+O").clicked() {
                                ui.close_menu();
                                self.pick_folder();
                            }
                            if ui.button("새 저장소 / 복제…").clicked() {
                                ui.close_menu();
                                self.reset_checkout();
                                self.dialog = Some(Dialog::Checkout);
                            }
                            ui.separator();
                            if self
                                .provider
                                .as_ref()
                                .is_some_and(|p| p.capabilities().remote_url)
                                && ui.button("원격 주소 설정…").clicked()
                            {
                                ui.close_menu();
                                self.request_remote();
                            }
                            if self
                                .provider
                                .as_ref()
                                .is_some_and(|p| p.capabilities().fetch)
                                && ui.button("원격 추적 정보 갱신 (Fetch)").clicked()
                            {
                                ui.close_menu();
                                self.execute(Operation::Fetch, String::new(), vec![]);
                            }
                            ui.menu_button("버전 관리 도구", |ui| {
                                for k in VcsKind::ALL {
                                    if ui.button(k.name()).clicked() {
                                        ui.close_menu();
                                        if let Some(path) =
                                            self.repo.as_ref().map(|r| r.root.clone())
                                        {
                                            self.open(path, Some(k));
                                        }
                                    }
                                }
                            });
                        });
                        ui.menu_button("파일 도구", |ui| {
                            if let Some(path) = self.file.clone() {
                                ui.add(
                                    egui::Label::new(RichText::new(&path).small().color(MUTED))
                                        .truncate(),
                                );
                                if ui.button("내장 좌우 Diff").clicked() {
                                    ui.close_menu();
                                    self.inspect_internal(false);
                                }
                                if ui.button("내장 Blame").clicked() {
                                    ui.close_menu();
                                    self.inspect_internal(true);
                                }
                                if ui.button("설정한 Diff 도구").clicked() {
                                    ui.close_menu();
                                    self.inspect(false);
                                }
                                if ui.button("설정한 Blame 도구").clicked() {
                                    ui.close_menu();
                                    self.inspect(true);
                                }
                                if self.selected_conflicted && ui.button("충돌 해결…").clicked()
                                {
                                    ui.close_menu();
                                    self.open_conflict();
                                }
                            } else {
                                ui.label("먼저 파일을 선택하세요.");
                            }
                            ui.separator();
                            if ui.button("다른 파일 선택…").clicked() {
                                ui.close_menu();
                                self.pick_file_for_inspection();
                            }
                            if ui.button("비교·이력 도구 설정…").clicked() {
                                ui.close_menu();
                                self.settings_tab = SettingsTab::FileTools;
                                self.dialog = Some(Dialog::Settings);
                            }
                        });
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            if icon_button(ui, Icon::Settings, "설정").clicked() {
                                self.dialog = Some(Dialog::Settings);
                            }
                            if icon_button(ui, Icon::Refresh, "새로 고침 · F5").clicked() {
                                self.refresh();
                            }
                            ui.separator();
                            if self
                                .provider
                                .as_ref()
                                .is_some_and(|p| p.capabilities().push)
                                && ui
                                    .button("보내기")
                                    .on_hover_text("현재 브랜치의 커밋을 원격에 공유합니다")
                                    .clicked()
                            {
                                self.request_sync(Operation::Push);
                            }
                            if self
                                .provider
                                .as_ref()
                                .is_some_and(|p| p.capabilities().update)
                                && ui
                                    .button("가져오기")
                                    .on_hover_text("원격 변경을 가져와 작업 공간에 적용합니다")
                                    .clicked()
                            {
                                self.request_sync(Operation::Update);
                            }
                        });
                    });
                });
            });
    }

    pub(super) fn sidebar(&mut self, ctx: &egui::Context) {
        let width = if ctx.screen_rect().width() < 1050. {
            184.
        } else {
            234.
        };
        egui::SidePanel::left("repositories")
            .exact_width(width)
            .resizable(false)
            .frame(egui::Frame::none().fill(NAVY).inner_margin(14.))
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    let (mark, _) =
                        ui.allocate_exact_size(egui::vec2(34., 34.), egui::Sense::hover());
                    ui.painter()
                        .rect_filled(mark, 9., Color32::from_rgb(196, 72, 43));
                    let font =
                        egui::FontId::new(30., egui::FontFamily::Name("Moa Semibold".into()));
                    ui.painter().text(
                        mark.center() - egui::vec2(0., 2.),
                        egui::Align2::CENTER_CENTER,
                        "m",
                        font,
                        SURFACE,
                    );
                    ui.label(heading("MoaVCS", 23.).color(RAIL_TEXT));
                });
                ui.add_enabled_ui(!self.controls_locked(), |ui| {
                    ui.add_space(20.);
                    ui.label(heading("작업 공간", 13.).color(RAIL_MUTED));
                    ui.add_enabled_ui(self.repo.is_some(), |ui| {
                        ui.spacing_mut().item_spacing.y = 3.;
                        for (tab, name, icon) in [
                            (Tab::Changes, "변경 파일", Icon::Changes),
                            (Tab::History, "커밋 이력", Icon::History),
                            (Tab::Output, "실행 기록", Icon::Output),
                        ] {
                            if nav_row(ui, name, icon, self.repo.is_some() && self.tab == tab) {
                                self.tab = tab;
                                if tab == Tab::History {
                                    self.load_history();
                                }
                            }
                        }
                    });
                    ui.add_space(14.);
                    ui.horizontal(|ui| {
                        ui.label(heading("저장소", 13.).color(RAIL_MUTED));
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            ui.visuals_mut().widgets.inactive.weak_bg_fill = Color32::TRANSPARENT;
                            ui.visuals_mut().widgets.inactive.bg_stroke = egui::Stroke::NONE;
                            ui.visuals_mut().widgets.hovered.weak_bg_fill = RAIL_HOVER;
                            ui.visuals_mut().widgets.open.weak_bg_fill = RAIL_ACTIVE;
                            ui.menu_button(RichText::new("…").color(RAIL_TEXT), |ui| {
                                if ui.button("고정 항목을 남기고 최근 기록 비우기").clicked()
                                {
                                    match self.settings_store.clear_recent() {
                                        Ok(s) => self.settings = s,
                                        Err(e) => self.notice = Some(e.to_string()),
                                    };
                                    ui.close_menu();
                                }
                            });
                        });
                    });
                    ui.scope(|ui| {
                        ui.visuals_mut().override_text_color = Some(RAIL_TEXT);
                        ui.visuals_mut().extreme_bg_color = RAIL_HOVER;
                        ui.visuals_mut().widgets.inactive.bg_stroke =
                            egui::Stroke::new(1.0_f32, Color32::from_rgb(79, 107, 112));
                        ui.visuals_mut().selection.bg_fill = RAIL_ACTIVE;
                        ui.visuals_mut().selection.stroke = egui::Stroke::new(1.0_f32, RAIL_MARK);
                        ui.visuals_mut().text_cursor.stroke = egui::Stroke::new(1.5_f32, RAIL_MARK);
                        ui.add_sized(
                            [ui.available_width(), 36.],
                            TextEdit::singleline(&mut self.query)
                                .id_salt("sidebar_search")
                                .hint_text(RichText::new("저장소 검색").color(RAIL_MUTED))
                                .margin(egui::vec2(10., 7.)),
                        );
                    });
                    let footer = ui.max_rect().bottom() - 48.;
                    ScrollArea::vertical()
                        .id_salt("sidebar_repositories")
                        .max_height((footer - ui.cursor().top()).max(50.))
                        .auto_shrink([false, false])
                        .show(ui, |ui| {
                            let query = self.query.to_lowercase();
                            let mut found = false;
                            for pin in [true, false] {
                                let items: Vec<_> = self
                                    .settings
                                    .recent_repositories
                                    .iter()
                                    .filter(|r| {
                                        r.is_pinned == pin
                                            && (r.path.to_lowercase().contains(&query)
                                                || r.vcs_kind
                                                    .name()
                                                    .to_lowercase()
                                                    .contains(&query))
                                    })
                                    .cloned()
                                    .collect();
                                if items.is_empty() {
                                    continue;
                                }
                                found = true;
                                ui.add_space(6.);
                                ui.label(
                                    RichText::new(if pin { "고정됨" } else { "최근 열기" })
                                        .size(13.)
                                        .color(RAIL_MUTED),
                                );
                                for r in items {
                                    let path = Path::new(&r.path);
                                    let name = path
                                        .file_name()
                                        .and_then(|s| s.to_str())
                                        .unwrap_or(&r.path);
                                    let parent = path
                                        .parent()
                                        .unwrap_or(Path::new(""))
                                        .display()
                                        .to_string();
                                    let selected = self
                                        .repo
                                        .as_ref()
                                        .is_some_and(|repo| repo.root == PathBuf::from(&r.path));
                                    let (rect, response) = ui.allocate_exact_size(
                                        egui::vec2(ui.available_width(), 62.),
                                        egui::Sense::click(),
                                    );
                                    response.widget_info(|| {
                                        egui::WidgetInfo::selected(
                                            egui::WidgetType::SelectableLabel,
                                            ui.is_enabled(),
                                            selected,
                                            &r.path,
                                        )
                                    });
                                    if selected || response.hovered() || response.has_focus() {
                                        ui.painter().rect_filled(
                                            rect,
                                            7.,
                                            if selected { RAIL_ACTIVE } else { RAIL_HOVER },
                                        );
                                    }
                                    if selected {
                                        ui.painter().rect_filled(
                                            egui::Rect::from_min_size(
                                                rect.min + egui::vec2(0., 15.),
                                                egui::vec2(3., 28.),
                                            ),
                                            2.,
                                            RAIL_MARK,
                                        );
                                    }
                                    draw_icon(
                                        ui.painter(),
                                        egui::Rect::from_min_size(
                                            rect.min + egui::vec2(9., 12.),
                                            egui::vec2(19., 19.),
                                        ),
                                        if pin { Icon::Pin } else { Icon::Folder },
                                        RAIL_MUTED,
                                    );
                                    line_text(
                                        ui,
                                        egui::Rect::from_min_size(
                                            rect.min + egui::vec2(35., 9.),
                                            egui::vec2(rect.width() - 69., 22.),
                                        ),
                                        name,
                                        15.,
                                        RAIL_TEXT,
                                    );
                                    line_text(
                                        ui,
                                        egui::Rect::from_min_size(
                                            rect.min + egui::vec2(35., 31.),
                                            egui::vec2(rect.width() - 44., 22.),
                                        ),
                                        &format!("{} · {}", r.vcs_kind.name(), parent),
                                        13.,
                                        RAIL_MUTED,
                                    );
                                    response.clone().on_hover_text(&r.path);
                                    let menu_rect = egui::Rect::from_min_size(
                                        egui::pos2(rect.right() - 32., rect.top() + 4.),
                                        egui::vec2(28., 26.),
                                    );
                                    let mut menu_clicked = false;
                                    ui.allocate_new_ui(
                                        egui::UiBuilder::new().max_rect(menu_rect),
                                        |ui| {
                                            ui.spacing_mut().button_padding = egui::vec2(4., 3.);
                                            ui.spacing_mut().interact_size = egui::vec2(24., 24.);
                                            ui.visuals_mut().widgets.inactive.weak_bg_fill =
                                                Color32::TRANSPARENT;
                                            ui.visuals_mut().widgets.inactive.bg_stroke =
                                                egui::Stroke::NONE;
                                            ui.visuals_mut().widgets.hovered.weak_bg_fill =
                                                RAIL_HOVER;
                                            ui.visuals_mut().widgets.open.weak_bg_fill =
                                                RAIL_ACTIVE;
                                            let menu = ui.menu_button(
                                                RichText::new("…").color(RAIL_TEXT),
                                                |ui| {
                                                    if ui
                                                        .button(if r.is_pinned {
                                                            "고정 해제"
                                                        } else {
                                                            "상단에 고정"
                                                        })
                                                        .clicked()
                                                    {
                                                        self.pin_recent(&r.path, !r.is_pinned);
                                                        ui.close_menu();
                                                    }
                                                    if ui.button("목록에서 제거").clicked() {
                                                        self.remove_recent(&r.path);
                                                        ui.close_menu();
                                                    }
                                                },
                                            );
                                            menu_clicked =
                                                menu.response.clicked() || menu.response.hovered();
                                        },
                                    );
                                    response.context_menu(|ui| {
                                        if ui
                                            .button(if r.is_pinned {
                                                "고정 해제"
                                            } else {
                                                "상단에 고정"
                                            })
                                            .clicked()
                                        {
                                            self.pin_recent(&r.path, !r.is_pinned);
                                            ui.close_menu();
                                        }
                                        if ui.button("목록에서 제거").clicked() {
                                            self.remove_recent(&r.path);
                                            ui.close_menu();
                                        }
                                    });
                                    if response.clicked() && !menu_clicked {
                                        self.open(PathBuf::from(&r.path), Some(r.vcs_kind));
                                    }
                                    ui.advance_cursor_after_rect(rect);
                                }
                            }
                            if !found {
                                ui.add_space(18.);
                                ui.label(
                                    RichText::new(if self.query.is_empty() {
                                        "열었던 저장소가 여기에 표시됩니다."
                                    } else {
                                        "검색 결과가 없습니다."
                                    })
                                    .size(13.)
                                    .color(RAIL_MUTED),
                                );
                            }
                        });
                    let bottom = egui::Rect::from_min_max(
                        egui::pos2(ui.min_rect().left(), footer + 6.),
                        ui.max_rect().max,
                    );
                    ui.allocate_new_ui(egui::UiBuilder::new().max_rect(bottom), |ui| {
                        if nav_row(ui, "저장소 열기", Icon::Folder, false) {
                            self.pick_folder();
                        }
                    });
                });
            });
    }

    pub(super) fn body(&mut self, ctx: &egui::Context) {
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(BG).inner_margin(20.))
            .show(ctx, |ui| {
                if self.controls_locked() {
                    ui.disable();
                }
                if let Some(repo) = self.repo.clone() {
                    let title = repo
                        .root
                        .file_name()
                        .and_then(|s| s.to_str())
                        .unwrap_or("저장소");
                    ui.horizontal(|ui| {
                        let measured = ui.fonts(|f| {
                            f.layout_no_wrap(title.to_owned(), egui::FontId::proportional(25.), INK)
                                .size()
                                .x
                        });
                        let budget = (ui.available_width() - 205.).max(100.);
                        ui.add_sized(
                            [measured.min(budget) + 4., 34.],
                            egui::Label::new(RichText::new(title).size(25.).strong()).truncate(),
                        )
                        .on_hover_text(title);
                        pill(ui, repo.kind.name(), BLUE);
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            if self.is_loading() {
                                pill(ui, "정보를 불러오는 중", BLUE);
                            }
                            pill(
                                ui,
                                &if self.changes.is_empty() {
                                    "작업 공간이 최신입니다".into()
                                } else {
                                    format!("{}개 변경", self.changes.len())
                                },
                                if self.changes.is_empty() {
                                    GREEN
                                } else {
                                    ACCENT
                                },
                            );
                        });
                    });
                    let path = repo.root.display().to_string();
                    ui.horizontal(|ui| {
                        if !repo.branch.is_empty() {
                            draw_small_icon(ui, Icon::Branch, MUTED);
                            ui.add(
                                egui::Label::new(
                                    RichText::new(compact(&repo.branch, 26))
                                        .size(11.5)
                                        .color(INK),
                                )
                                .truncate(),
                            )
                            .on_hover_text(&repo.branch);
                            ui.separator();
                        }
                        ui.add(
                            egui::Label::new(RichText::new(&path).color(MUTED).size(11.5))
                                .truncate(),
                        )
                        .on_hover_text(&path)
                        .context_menu(|ui| {
                            if ui.button("경로 복사").clicked() {
                                ui.output_mut(|o| o.copied_text = path.clone());
                                ui.close_menu();
                            }
                        });
                    });
                    ui.add_space(8.);
                    if let Some(notice) = self.notice.clone() {
                        let (r, response) = ui.allocate_exact_size(
                            egui::vec2(ui.available_width(), 38.),
                            egui::Sense::hover(),
                        );
                        ui.painter()
                            .rect_filled(r, 7., Color32::from_rgb(255, 244, 237));
                        let close = egui::Rect::from_min_size(
                            egui::pos2(r.right() - 30., r.top() + 5.),
                            egui::vec2(26., 28.),
                        );
                        line_text(
                            ui,
                            egui::Rect::from_min_size(
                                r.min + egui::vec2(12., 11.),
                                egui::vec2(r.width() - 54., 19.),
                            ),
                            &notice.replace('\n', "  "),
                            12.,
                            ACCENT,
                        );
                        response.on_hover_text(&notice).context_menu(|ui| {
                            ui.set_max_width(560.);
                            ScrollArea::vertical()
                                .id_salt("notice_details")
                                .max_height(260.)
                                .show(ui, |ui| {
                                    ui.add(egui::Label::new(&notice).wrap());
                                });
                            if ui.button("내용 복사").clicked() {
                                ui.output_mut(|o| o.copied_text = notice.clone());
                                ui.close_menu();
                            }
                        });
                        let close_response =
                            ui.interact(close, ui.id().with("notice_close"), egui::Sense::click());
                        draw_icon(ui.painter(), close.shrink(5.), Icon::Close, MUTED);
                        if close_response.clicked() {
                            self.notice = None;
                        }
                        ui.add_space(10.);
                    }
                    match self.tab {
                        Tab::Changes => self.changes_ui(ui),
                        Tab::History => self.history_ui(ui),
                        Tab::Output => {
                            surface().show(ui, |ui| {
                                section(ui, "실행 기록", "저장소 작업의 결과와 오류를 확인합니다.");
                                code_view_prepared(
                                    ui,
                                    "workspace_log",
                                    &self.logs_text,
                                    &self.logs_view,
                                    ui.available_height() - 10.,
                                );
                            });
                        }
                    }
                } else {
                    let rect = ui.available_rect_before_wrap();
                    let w = rect.width().min(660.);
                    let card = egui::Rect::from_center_size(rect.center(), egui::vec2(w, 330.));
                    panel(ui, card, 28., |ui| {
                        ui.add_space(8.);
                        pill(ui, "MoaVCS", ACCENT);
                        ui.add_space(12.);
                        ui.label(
                            RichText::new("변경을 살피고,\n자신 있게 기록하세요.")
                                .size(29.)
                                .strong(),
                        );
                        ui.add_space(8.);
                        ui.label(
                            RichText::new(
                                "Git · SVN · Perforce · Lore 저장소를 한곳에서 관리합니다.",
                            )
                            .color(MUTED),
                        );
                        ui.add_space(24.);
                        ui.horizontal(|ui| {
                            if primary(ui, "저장소 폴더 열기").clicked() {
                                self.pick_folder();
                            }
                            if ui.button("새 저장소 / 복제…").clicked() {
                                self.reset_checkout();
                                self.dialog = Some(Dialog::Checkout);
                            }
                        });
                    });
                }
            });
    }

    pub(super) fn changes_ui(&mut self, ui: &mut egui::Ui) {
        let area = ui.available_rect_before_wrap();
        ui.allocate_rect(area, egui::Sense::hover());
        let (left, right) = split(
            ui,
            "changes_split",
            area,
            &mut self.worktree_split,
            220.,
            250.,
        );
        let composer_h = if area.height() < 390. {
            138.
        } else if area.height() < 500. {
            150.
        } else {
            162.
        };
        let files_rect = egui::Rect::from_min_max(
            left.min,
            egui::pos2(
                left.right(),
                (left.bottom() - composer_h - 12.).max(left.top() + 90.),
            ),
        );
        let composer =
            egui::Rect::from_min_max(egui::pos2(left.left(), files_rect.bottom() + 12.), left.max);
        let cap = self
            .provider
            .as_ref()
            .map(|p| p.capabilities())
            .unwrap_or_default();
        let vcs_kind = self.repo.as_ref().map(|repo| repo.kind).unwrap_or_default();
        panel(ui, files_rect, 12., |ui| {
            ui.horizontal(|ui| {
                ui.label(heading("변경 파일", 16.));
                pill(ui, &self.changes.len().to_string(), MUTED);
            });
            ui.horizontal_wrapped(|ui| {
                if cap.stage
                    && ui
                        .add_enabled(
                            !self.picked.is_empty(),
                            egui::Button::new("포함").min_size(egui::vec2(58., 32.)),
                        )
                        .on_hover_text("선택한 파일을 커밋에 포함합니다")
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
                        .add_enabled(
                            !self.picked.is_empty(),
                            egui::Button::new("제외").min_size(egui::vec2(58., 32.)),
                        )
                        .on_hover_text("선택한 파일의 포함을 해제합니다")
                        .clicked()
                {
                    self.execute(
                        Operation::Unstage,
                        String::new(),
                        self.picked.iter().cloned().collect(),
                    );
                }
                ui.label(
                    RichText::new(format!("{}개 선택", self.picked.len()))
                        .size(13.)
                        .color(MUTED),
                );
            });
            ui.add_space(3.);
            if self.changes.is_empty() {
                empty(
                    ui,
                    "변경 사항이 없습니다",
                    "파일을 수정하면 이곳에 표시됩니다.",
                    Icon::Check,
                );
            } else {
                let changes = &self.changes;
                let picked = &mut self.picked;
                let selected_file = self.file.as_deref();
                let mut file_to_select = None;
                ui.spacing_mut().item_spacing.y = 0.;
                ScrollArea::vertical()
                    .id_salt("changes_file_list")
                    .auto_shrink([false, false])
                    .max_height(ui.available_height())
                    .show_rows(ui, 44., changes.len(), |ui, rows| {
                        for index in rows {
                            let f = &changes[index];
                            let (r, response) = ui.allocate_exact_size(
                                egui::vec2(ui.available_width(), 44.),
                                egui::Sense::click(),
                            );
                            let selected = selected_file == Some(&f.path);
                            response.widget_info(|| {
                                egui::WidgetInfo::selected(
                                    egui::WidgetType::SelectableLabel,
                                    ui.is_enabled(),
                                    selected,
                                    &f.path,
                                )
                            });
                            if selected || response.hovered() || response.has_focus() {
                                ui.painter().rect_filled(
                                    r,
                                    6.,
                                    if selected { SELECTED } else { BG },
                                );
                            }
                            let mut included = picked.contains(&f.path);
                            let check = ui.put(
                                egui::Rect::from_min_size(
                                    r.min + egui::vec2(5., 8.),
                                    egui::vec2(28., 28.),
                                ),
                                egui::Checkbox::without_text(&mut included),
                            );
                            if check.changed() {
                                if included {
                                    picked.insert(f.path.clone());
                                } else {
                                    picked.remove(&f.path);
                                }
                            }
                            let color = change_color(&f);
                            let tag = egui::Rect::from_min_size(
                                r.min + egui::vec2(38., 11.),
                                egui::vec2(22., 22.),
                            );
                            ui.painter()
                                .rect_filled(tag, 4., color.gamma_multiply(0.12));
                            ui.painter().text(
                                tag.center(),
                                egui::Align2::CENTER_CENTER,
                                change_letter(&f, vcs_kind),
                                egui::FontId::monospace(13.),
                                color,
                            );
                            let path = Path::new(&f.path);
                            let name = path.file_name().and_then(|s| s.to_str()).unwrap_or(&f.path);
                            let x = r.left() + 68.;
                            let width = (r.right() - x - 4.).max(20.);
                            line_text(
                                ui,
                                egui::Rect::from_min_size(
                                    egui::pos2(x, r.top() + 1.),
                                    egui::vec2(width, 22.),
                                ),
                                name,
                                15.,
                                INK,
                            );
                            let parent =
                                path.parent().unwrap_or(Path::new("")).display().to_string();
                            line_text(
                                ui,
                                egui::Rect::from_min_size(
                                    egui::pos2(x, r.top() + 23.),
                                    egui::vec2(width, 18.),
                                ),
                                &format!(
                                    "{}{}",
                                    workspace_status(&f, vcs_kind),
                                    if parent.is_empty() {
                                        String::new()
                                    } else {
                                        format!(" · {parent}")
                                    }
                                ),
                                13.,
                                MUTED,
                            );
                            response.clone().on_hover_text(&f.path);
                            if response.clicked() && !check.clicked() {
                                file_to_select = Some(f.path.clone());
                            }
                            ui.advance_cursor_after_rect(r);
                        }
                    });
                if let Some(path) = file_to_select {
                    self.select_file(path);
                }
            }
        });
        panel(ui, composer, 12., |ui| {
            if !cap.commit {
                section(
                    ui,
                    "변경 제출",
                    "Perforce의 변경 제출은 전용 CLI에서 진행하세요.",
                );
                ui.label(
                    RichText::new("이 화면에서는 변경 내용과 파일 이력을 검토할 수 있습니다.")
                        .size(13.)
                        .color(MUTED),
                );
                return;
            }
            ui.label(heading("변경 기록", 16.));
            if composer.height() > 176. {
                ui.label(
                    RichText::new(
                        if self.repo.as_ref().is_some_and(|r| r.kind == VcsKind::Svn) {
                            "선택한 파일을 서버에 커밋합니다."
                        } else {
                            "포함된 모든 변경을 커밋합니다."
                        },
                    )
                    .size(13.)
                    .color(MUTED),
                );
            }
            let bounds = ui.max_rect();
            let footer = egui::Rect::from_min_max(
                egui::pos2(bounds.left(), bounds.bottom() - 34.),
                bounds.max,
            );
            let editor = egui::Rect::from_min_max(
                ui.cursor().min,
                egui::pos2(bounds.right(), footer.top() - 8.),
            );
            ui.allocate_new_ui(egui::UiBuilder::new().max_rect(editor), |ui| {
                ScrollArea::vertical()
                    .id_salt("commit_editor_scroll")
                    .max_height(editor.height().max(24.))
                    .min_scrolled_height(editor.height().max(24.))
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.add(
                            TextEdit::multiline(&mut self.message)
                                .id_salt("commit_message")
                                .hint_text("변경 내용을 설명하세요")
                                .desired_rows(2)
                                .desired_width(editor.width())
                                .font(egui::FontId::proportional(15.))
                                .margin(egui::vec2(10., 7.)),
                        );
                    });
            });
            ui.allocate_new_ui(egui::UiBuilder::new().max_rect(footer), |ui| {
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new(if self.has_conflicts {
                            "충돌 해결 필요".to_owned()
                        } else if self
                            .repo
                            .as_ref()
                            .is_some_and(|repo| repo.kind == VcsKind::Svn)
                        {
                            format!("제출 대상 {}개", self.picked.len())
                        } else {
                            format!("커밋 포함 {}개", self.staged_file_count)
                        })
                        .size(13.)
                        .color(MUTED),
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        let enabled =
                            cap.commit && !self.message.trim().is_empty() && !self.has_conflicts;
                        if ui
                            .add_enabled_ui(enabled, |ui| primary(ui, "커밋"))
                            .inner
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
                });
            });
        });
        panel(ui, right, 14., |ui| {
            ui.horizontal(|ui| {
                draw_small_icon(ui, Icon::Diff, BLUE);
                ui.add(
                    egui::Label::new(heading(
                        self.file.as_deref().unwrap_or("변경 미리 보기"),
                        16.,
                    ))
                    .truncate(),
                )
                .on_hover_text(self.file.as_deref().unwrap_or_default());
            });
            ui.horizontal_wrapped(|ui| {
                if ui
                    .add_enabled(
                        self.file.is_some(),
                        egui::Button::new("좌우 비교").min_size(egui::vec2(88., 32.)),
                    )
                    .clicked()
                {
                    self.inspect(false);
                }
                if ui
                    .add_enabled(
                        self.file.is_some(),
                        egui::Button::new("Blame").min_size(egui::vec2(72., 32.)),
                    )
                    .clicked()
                {
                    self.inspect(true);
                }
                if self
                    .provider
                    .as_ref()
                    .is_some_and(|p| p.kind() == VcsKind::Git)
                    && ui.checkbox(&mut self.staged, "커밋할 내용").changed()
                {
                    self.load_diff();
                }
            });
            ui.add_space(6.);
            ui.separator();
            ui.add_space(4.);
            if self.diff.is_empty() {
                if self.read_slots.contains_key(&ReadSlot::Diff) {
                    empty(
                        ui,
                        "차이를 불러오는 중",
                        "비교가 끝나면 이곳에 표시됩니다. 다른 파일과 작업 공간은 계속 사용할 수 있습니다.",
                        Icon::Refresh,
                    );
                } else {
                    empty(
                        ui,
                        "표시할 차이가 없습니다",
                        if self.file.is_some() {
                            "커밋 포함 여부를 바꾸거나 다른 파일을 선택하세요."
                        } else {
                            "왼쪽에서 파일을 선택해 변경 내용을 확인하세요."
                        },
                        Icon::Diff,
                    );
                }
            } else {
                code_view_prepared(
                    ui,
                    "changes_diff_preview",
                    &self.diff,
                    &self.diff_view,
                    ui.available_height(),
                );
            }
        });
    }

    pub(super) fn history_ui(&mut self, ui: &mut egui::Ui) {
        let mut reload_history = false;
        if let Some(g) = self.history.git.as_ref() {
            egui::Frame::none()
                .fill(SURFACE)
                .stroke(egui::Stroke::new(1.0_f32, BORDER))
                .rounding(8.)
                .inner_margin(10.)
                .show(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal_wrapped(|ui| {
                        pill(ui, &format!("HEAD  {}", short(&g.head_id)), BLUE);
                        ui.label(
                            RichText::new(format!(
                                "추적  {}",
                                if g.upstream.is_empty() {
                                    "없음"
                                } else {
                                    &g.upstream
                                }
                            ))
                            .size(13.),
                        );
                        if let (Some(a), Some(b)) = (g.ahead, g.behind) {
                            pill(
                                ui,
                                &format!("로컬만 {a} · 추적만 {b}"),
                                if a > 0 || b > 0 { ACCENT } else { GREEN },
                            );
                        }
                        ui.label(
                            RichText::new(format!(
                                "푸시  {}",
                                if g.push_target.is_empty() {
                                    "미설정"
                                } else {
                                    &g.push_target
                                }
                            ))
                            .size(13.)
                            .color(MUTED),
                        );
                    });
                    ui.horizontal_wrapped(|ui| {
                        ui.spacing_mut().interact_size.y = 32.;
                        ui.label(RichText::new("마지막 Fetch 기준").size(13.).color(MUTED));
                        let old = self.scope;
                        ui.selectable_value(
                            &mut self.scope,
                            HistoryScope::CurrentBranch,
                            "현재 + 추적 브랜치",
                        );
                        ui.selectable_value(
                            &mut self.scope,
                            HistoryScope::AllBranches,
                            "모든 브랜치",
                        );
                        if self.scope != old {
                            reload_history = true;
                        }
                    });
                });
            ui.add_space(10.);
        }
        if reload_history {
            self.load_history();
        }
        let area = ui.available_rect_before_wrap();
        ui.allocate_rect(area, egui::Sense::hover());
        let (left, right) = split(
            ui,
            "history_split",
            area,
            &mut self.history_split,
            230.,
            250.,
        );
        let mut requested_commit = None;
        let graph_width = self.history_graph.column_width();
        panel(ui, left, 10., |ui| {
            ui.horizontal(|ui| {
                ui.label(heading("커밋", 16.));
                pill(ui, &self.history.entries.len().to_string(), MUTED);
            });
            ui.add_space(6.);
            if self.history.entries.is_empty() {
                if self.read_slots.contains_key(&ReadSlot::History) {
                    ui.horizontal(|ui| {
                        ui.spinner();
                        ui.label("커밋 이력을 불러오는 중…");
                    });
                } else {
                    ui.label("표시할 커밋이 없습니다.");
                }
                return;
            }
            ui.spacing_mut().item_spacing.y = 0.;
            ScrollArea::vertical()
                .id_salt("history_commit_list")
                .auto_shrink([false, false])
                .max_height(ui.available_height())
                .show_rows(ui, 76., self.history.entries.len(), |ui, rows| {
                    ui.spacing_mut().item_spacing.y = 0.;
                    for index in rows {
                        let entry = &self.history.entries[index];
                        let (r, response) = ui.allocate_exact_size(
                            egui::vec2(ui.available_width(), 76.),
                            egui::Sense::click(),
                        );
                        let selected = self.revision.as_deref() == Some(&entry.id);
                        response.widget_info(|| {
                            egui::WidgetInfo::selected(
                                egui::WidgetType::SelectableLabel,
                                ui.is_enabled(),
                                selected,
                                entry.summary(),
                            )
                        });
                        if selected || response.hovered() || response.has_focus() {
                            ui.painter()
                                .rect_filled(r, 6., if selected { SELECTED } else { BG });
                        }
                        let graph = egui::Rect::from_min_size(r.min, egui::vec2(graph_width, 76.));
                        ui.allocate_new_ui(egui::UiBuilder::new().max_rect(graph), |ui| {
                            ui.set_clip_rect(graph.intersect(ui.clip_rect()));
                            self.history_graph.paint_row(ui, &entry.id, 76.);
                        });
                        let x = r.left() + graph_width + 3.;
                        let width = (r.right() - x - 8.).max(40.);
                        line_text(
                            ui,
                            egui::Rect::from_min_size(
                                egui::pos2(x, r.top() + 8.),
                                egui::vec2(width, 23.),
                            ),
                            entry.summary(),
                            15.,
                            INK,
                        );
                        line_text(
                            ui,
                            egui::Rect::from_min_size(
                                egui::pos2(x, r.top() + 31.),
                                egui::vec2(width, 19.),
                            ),
                            &format!("{} · {}", short(&entry.id), entry.author),
                            13.,
                            MUTED,
                        );
                        let refs = entry
                            .references
                            .iter()
                            .map(|r| r.name.as_str())
                            .collect::<Vec<_>>()
                            .join(" · ");
                        line_text(
                            ui,
                            egui::Rect::from_min_size(
                                egui::pos2(x, r.top() + 53.),
                                egui::vec2(width, 19.),
                            ),
                            &format!(
                                "{}{}",
                                relation(entry.relation),
                                if refs.is_empty() {
                                    String::new()
                                } else {
                                    format!("   {refs}")
                                }
                            ),
                            13.,
                            BLUE,
                        );
                        response.clone().on_hover_text(format!(
                            "{}\n{}\n{}",
                            entry.message, entry.author, entry.date
                        ));
                        if response.clicked() {
                            requested_commit = Some(entry.id.clone());
                        }
                    }
                });
        });
        if let Some(id) = requested_commit {
            if let Some(entry) = self
                .history
                .entries
                .iter()
                .find(|entry| entry.id == id)
                .cloned()
            {
                self.load_details(&entry);
            }
        }
        let mut requested_history_file = None;
        panel(ui, right, 14., |ui| {
            if let Some(detail) = self.details.as_ref() {
                if right.height() < 380. {
                    ui.horizontal_wrapped(|ui| {
                        pill(ui, short(&detail.entry.id), BLUE);
                        ui.add(egui::Label::new(heading(detail.entry.summary(), 15.)).truncate())
                            .on_hover_text(&detail.entry.message);
                    });
                    ui.add(
                        egui::Label::new(
                            RichText::new(format!(
                                "{} · {}",
                                detail.entry.author, detail.entry.date
                            ))
                            .size(13.)
                            .color(MUTED),
                        )
                        .truncate(),
                    )
                    .on_hover_text(format!(
                        "{}\n{} <{}>\n부모: {}",
                        detail.entry.id,
                        detail.entry.author,
                        detail.entry.author_email,
                        detail.entry.parent_ids.join(", ")
                    ));
                    ui.separator();
                    let label = self.history_file.as_deref().unwrap_or("파일 선택");
                    egui::ComboBox::from_id_salt("compact_history_file")
                        .width(ui.available_width())
                        .selected_text(compact(
                            &format!("{}개 파일 · {}", detail.files.len(), label),
                            36,
                        ))
                        .show_ui(ui, |ui| {
                            ui.set_max_width(400.);
                            ScrollArea::vertical().max_height(320.).show_rows(
                                ui,
                                28.,
                                detail.files.len(),
                                |ui, rows| {
                                    for index in rows {
                                        let file = &detail.files[index];
                                        if ui
                                            .selectable_label(
                                                self.history_file.as_deref() == Some(&file.path),
                                                &file.path,
                                            )
                                            .clicked()
                                        {
                                            requested_history_file = Some(file.path.clone());
                                        }
                                    }
                                },
                            );
                        });
                    ui.add(
                        egui::Label::new(
                            RichText::new(&detail.comparison_label)
                                .size(13.)
                                .color(MUTED),
                        )
                        .truncate(),
                    );
                    if !self.hdiff.is_empty() {
                        code_view_prepared(
                            ui,
                            "history_compact_diff",
                            &self.hdiff,
                            &self.history_diff_view,
                            ui.available_height(),
                        );
                    }
                    return;
                }

                ui.horizontal_wrapped(|ui| {
                    pill(ui, short(&detail.entry.id), BLUE);
                    if ui
                        .add(egui::Button::new("복사").min_size(egui::vec2(64., 32.)))
                        .on_hover_text("전체 커밋 ID 복사")
                        .clicked()
                    {
                        ui.output_mut(|o| o.copied_text = detail.entry.id.clone());
                    }
                });
                ui.add(egui::Label::new(heading(detail.entry.summary(), 18.)).truncate())
                    .on_hover_text(&detail.entry.message);
                ui.add(
                    egui::Label::new(
                        RichText::new(format!("{}  ·  {}", detail.entry.author, detail.entry.date))
                            .size(13.)
                            .color(MUTED),
                    )
                    .truncate(),
                )
                .on_hover_text(format!(
                    "작성자: {} <{}>\n커미터: {} <{}>\n커밋 날짜: {}\n부모: {}",
                    detail.entry.author,
                    detail.entry.author_email,
                    detail.entry.committer,
                    detail.entry.committer_email,
                    detail.entry.commit_date,
                    detail.entry.parent_ids.join(", ")
                ));
                if detail.entry.message.lines().count() > 1 {
                    ScrollArea::vertical()
                        .id_salt("history_message")
                        .max_height(75.)
                        .show(ui, |ui| {
                            ui.add(
                                egui::Label::new(RichText::new(&detail.entry.message).size(15.))
                                    .wrap(),
                            );
                        });
                }
                ui.add_space(3.);
                ui.separator();
                ui.horizontal_wrapped(|ui| {
                    ui.label(heading(format!("{}개 파일 변경", detail.files.len()), 15.));
                    ui.label(
                        RichText::new("파일을 선택해 Diff 확인")
                            .size(13.)
                            .color(MUTED),
                    );
                });
                let max_list_h = if ui.available_height() < 320. {
                    66.
                } else {
                    102.
                };
                ui.spacing_mut().item_spacing.y = 0.;
                ScrollArea::vertical()
                    .id_salt("history_detail_files")
                    .max_height(max_list_h)
                    .auto_shrink([false, true])
                    .show_rows(ui, 36., detail.files.len(), |ui, rows| {
                        for index in rows {
                            let file = &detail.files[index];
                            let selected = self.history_file.as_deref() == Some(&file.path);
                            let (r, response) = ui.allocate_exact_size(
                                egui::vec2(ui.available_width(), 36.),
                                egui::Sense::click(),
                            );
                            if selected || response.hovered() || response.has_focus() {
                                ui.painter().rect_filled(
                                    r,
                                    4.,
                                    if selected { SELECTED } else { BG },
                                );
                            }
                            draw_icon(
                                ui.painter(),
                                egui::Rect::from_min_size(
                                    r.min + egui::vec2(7., 9.),
                                    egui::vec2(18., 18.),
                                ),
                                Icon::File,
                                change_color(&file),
                            );
                            line_text(
                                ui,
                                egui::Rect::from_min_size(
                                    r.min + egui::vec2(32., 7.),
                                    egui::vec2((r.width() - 40.).max(1.), 22.),
                                ),
                                &file.path,
                                13.,
                                INK,
                            );
                            response.clone().on_hover_text(&file.path);
                            if response.clicked() {
                                requested_history_file = Some(file.path.clone());
                            }
                        }
                    });
                ui.add_space(4.);
                ui.add(
                    egui::Label::new(
                        RichText::new(&detail.comparison_label)
                            .size(13.)
                            .color(MUTED),
                    )
                    .truncate(),
                )
                .on_hover_text(&detail.comparison_label);
                ui.separator();
                if self.hdiff.is_empty() {
                    if self.read_slots.contains_key(&ReadSlot::HistoryDiff) {
                        empty(
                            ui,
                            "파일 비교를 불러오는 중",
                            "커밋 파일 목록과 다른 화면은 계속 사용할 수 있습니다.",
                            Icon::Refresh,
                        );
                    } else {
                        empty(
                            ui,
                            "파일의 변경 내용을 확인하세요",
                            "위 목록에서 파일을 선택하세요.",
                            Icon::Diff,
                        );
                    }
                } else {
                    code_view_prepared(
                        ui,
                        "history_detail_diff",
                        &self.hdiff,
                        &self.history_diff_view,
                        ui.available_height(),
                    );
                }
            } else {
                if self.read_slots.contains_key(&ReadSlot::Details) {
                    empty(
                        ui,
                        "커밋 세부 정보를 불러오는 중",
                        "커밋 목록을 계속 탐색할 수 있습니다.",
                        Icon::Refresh,
                    );
                } else {
                    empty(
                        ui,
                        "커밋을 선택하세요",
                        "작성자, 전체 메시지와 파일별 변경 내용을 확인합니다.",
                        Icon::History,
                    );
                }
            }
        });
        if let (Some(path), Some(revision), Some(detail)) = (
            requested_history_file,
            self.revision.clone(),
            self.details.as_ref(),
        ) {
            if let Some(file) = detail.files.iter().find(|file| file.path == path).cloned() {
                self.load_hdiff(&revision, &file);
            }
        }
    }
}

fn line_text(ui: &egui::Ui, rect: egui::Rect, text: &str, size: f32, color: Color32) {
    let mut job = egui::text::LayoutJob::simple(
        text.to_owned(),
        egui::FontId::proportional(size),
        color,
        rect.width(),
    );
    job.wrap.max_rows = 1;
    job.wrap.break_anywhere = true;
    job.wrap.overflow_character = Some('…');
    let galley = ui.fonts(|f| f.layout_job(job));
    ui.painter()
        .with_clip_rect(rect)
        .galley(rect.min, galley, color);
}
fn draw_small_icon(ui: &mut egui::Ui, icon: Icon, color: Color32) {
    let (r, _) = ui.allocate_exact_size(egui::vec2(18., 18.), egui::Sense::hover());
    draw_icon(ui.painter(), r, icon, color);
}
fn nav_row(ui: &mut egui::Ui, text: &str, icon: Icon, active: bool) -> bool {
    let (r, response) =
        ui.allocate_exact_size(egui::vec2(ui.available_width(), 38.), egui::Sense::click());
    response.widget_info(|| {
        egui::WidgetInfo::selected(
            egui::WidgetType::SelectableLabel,
            ui.is_enabled(),
            active,
            text,
        )
    });
    if active || response.hovered() || response.has_focus() {
        ui.painter()
            .rect_filled(r, 7., Color32::from_rgb(37, 54, 76));
    }
    let color = if active {
        Color32::from_rgb(225, 234, 247)
    } else {
        Color32::from_rgb(151, 170, 195)
    };
    draw_icon(
        ui.painter(),
        egui::Rect::from_min_size(r.min + egui::vec2(11., 10.), egui::vec2(18., 18.)),
        icon,
        color,
    );
    ui.painter().text(
        r.min + egui::vec2(40., 19.),
        egui::Align2::LEFT_CENTER,
        text,
        egui::FontId::proportional(13.),
        color,
    );
    response.clicked()
}
fn change_color(f: &FileChange) -> Color32 {
    if f.conflicted() {
        RED
    } else if f.status.contains("delet") || f.status == "D" {
        RED
    } else if f.status == "?" || f.status.contains("untracked") {
        MUTED
    } else if f.staged() || f.status.contains("add") {
        GREEN
    } else {
        BLUE
    }
}
fn change_letter(f: &FileChange, vcs_kind: VcsKind) -> &'static str {
    if vcs_kind == VcsKind::Git {
        return git_change_letter(f);
    }
    if f.conflicted() {
        "!"
    } else if f.status.to_lowercase().contains("renam") || f.status == "R" {
        "R"
    } else if f.status.to_lowercase().contains("cop") || f.status == "C" {
        "C"
    } else if f.status.contains("delet") || f.status == "D" {
        "D"
    } else if f.status == "?" || f.status.contains("untracked") {
        "?"
    } else if f.status.contains("add") {
        "A"
    } else {
        "M"
    }
}

#[cfg(test)]
mod workspace_status_tests {
    use super::*;

    fn file(status: &str, index_status: &str, worktree_status: &str) -> FileChange {
        FileChange {
            status: status.into(),
            index_status: index_status.into(),
            worktree_status: worktree_status.into(),
            ..FileChange::default()
        }
    }

    #[test]
    fn git_staged_only_and_mixed_changes_have_clear_labels() {
        let staged = file("staged", "M", "");
        assert_eq!(change_letter(&staged, VcsKind::Git), "M");
        assert_eq!(workspace_status(&staged, VcsKind::Git), "커밋 포함");

        let mixed = file("modified", "A", "M");
        assert_eq!(change_letter(&mixed, VcsKind::Git), "A");
        assert_eq!(workspace_status(&mixed, VcsKind::Git), "일부 포함");
    }

    #[test]
    fn git_status_badges_preserve_rename_copy_delete_and_add() {
        assert_eq!(change_letter(&file("renamed", "R", ""), VcsKind::Git), "R");
        assert_eq!(change_letter(&file("copied", "C", ""), VcsKind::Git), "C");
        assert_eq!(change_letter(&file("deleted", "D", ""), VcsKind::Git), "D");
        assert_eq!(change_letter(&file("added", "A", ""), VcsKind::Git), "A");
    }

    #[test]
    fn non_git_status_labels_keep_provider_status_behavior() {
        let modified = file("modified", "", "M");
        assert_eq!(change_letter(&modified, VcsKind::Svn), "M");
        assert_eq!(workspace_status(&modified, VcsKind::Svn), "수정");
    }
}

fn git_change_letter(f: &FileChange) -> &'static str {
    if f.conflicted() {
        return "!";
    }
    if f.status.eq_ignore_ascii_case("untracked") || f.status == "?" {
        return "?";
    }
    let codes = [&f.index_status, &f.worktree_status];
    for (code, letter) in [('R', "R"), ('C', "C"), ('D', "D"), ('A', "A")] {
        if codes.iter().any(|value| value.starts_with(code)) {
            return letter;
        }
    }
    if codes.iter().any(|value| !value.is_empty()) {
        "M"
    } else if f.status.eq_ignore_ascii_case("renamed") {
        "R"
    } else if f.status.eq_ignore_ascii_case("copied") {
        "C"
    } else if f.status.eq_ignore_ascii_case("deleted") {
        "D"
    } else if f.status.eq_ignore_ascii_case("added") {
        "A"
    } else {
        "M"
    }
}

fn workspace_status(f: &FileChange, vcs_kind: VcsKind) -> &str {
    if vcs_kind != VcsKind::Git || f.conflicted() {
        return status(f);
    }
    let staged = !f.index_status.trim().is_empty() && f.index_status != "?";
    let unstaged = !f.worktree_status.trim().is_empty() && f.worktree_status != "?";
    match (staged, unstaged) {
        (true, true) => "일부 포함",
        (true, false) => "커밋 포함",
        (false, _) => status(f),
    }
}
fn panel(ui: &mut egui::Ui, rect: egui::Rect, margin: f32, contents: impl FnOnce(&mut egui::Ui)) {
    ui.painter().rect_filled(rect, 10., SURFACE);
    ui.painter()
        .rect_stroke(rect, 10., egui::Stroke::new(1.0_f32, BORDER));
    let inner = rect.shrink(margin);
    ui.allocate_new_ui(egui::UiBuilder::new().max_rect(inner), |ui| {
        ui.set_clip_rect(inner.intersect(ui.clip_rect()));
        ui.set_width(inner.width());
        ui.set_max_height(inner.height());
        contents(ui);
    });
}
fn split(
    ui: &mut egui::Ui,
    id: &str,
    area: egui::Rect,
    ratio: &mut f32,
    min_left: f32,
    min_right: f32,
) -> (egui::Rect, egui::Rect) {
    let gap = 12.;
    let available = (area.width() - gap).max(1.);
    let lower = min_left.min(available * 0.48);
    let upper = (available - min_right).max(lower);
    let mut width = (available * *ratio).clamp(lower, upper);
    let handle = egui::Rect::from_min_max(
        egui::pos2(area.left() + width, area.top()),
        egui::pos2(area.left() + width + gap, area.bottom()),
    );
    let response = ui.interact(handle, ui.id().with(id), egui::Sense::drag());
    if response.dragged() {
        width = (width + response.drag_delta().x).clamp(lower, upper);
        *ratio = width / available;
    }
    if response.hovered() || response.dragged() {
        ui.ctx().set_cursor_icon(egui::CursorIcon::ResizeHorizontal);
        ui.painter().line_segment(
            [
                egui::pos2(handle.center().x, handle.center().y - 20.),
                egui::pos2(handle.center().x, handle.center().y + 20.),
            ],
            egui::Stroke::new(2.0_f32, BORDER),
        );
    }
    (
        egui::Rect::from_min_max(area.min, egui::pos2(area.left() + width, area.bottom())),
        egui::Rect::from_min_max(egui::pos2(area.left() + width + gap, area.top()), area.max),
    )
}
fn empty(ui: &mut egui::Ui, title: &str, description: &str, icon: Icon) {
    let height = ui.available_height();
    ui.add_space((height * 0.28).min(90.).max(8.));
    ui.vertical_centered(|ui| {
        let (r, _) = ui.allocate_exact_size(egui::vec2(38., 38.), egui::Sense::hover());
        ui.painter().rect_filled(r, 9., BG);
        draw_icon(ui.painter(), r.shrink(7.), icon, MUTED);
        ui.add_space(9.);
        ui.label(RichText::new(title).color(MUTED).strong());
        ui.add(egui::Label::new(RichText::new(description).size(11.5).color(MUTED)).wrap());
    });
}
