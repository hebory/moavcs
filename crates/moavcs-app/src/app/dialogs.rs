use super::*;

impl MoaApp {
    pub(super) fn dialogs(&mut self, ctx: &egui::Context) {
        match self.dialog {
            Some(Dialog::Checkout) => self.checkout_ui(ctx),
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

    fn checkout_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        let size = window_size(ctx, [720.0, 540.0]);
        let shown = egui::Window::new("새 저장소")
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size(size)
            .min_size(window_size(ctx, [520.0, 400.0]))
            .max_size(window_size(ctx, [900.0, 680.0]))
            .collapsible(false)
            .resizable(true)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                section(
                    ui,
                    "저장소 만들기 또는 복제",
                    "VCS를 고르고 원격 주소와 저장 위치를 확인하세요.",
                );
                ui.add_space(12.0);
                ui.horizontal_wrapped(|ui| {
                    for kind in VcsKind::ALL {
                        ui.selectable_value(&mut self.checkout_kind, kind, kind.name());
                    }
                });
                ui.add_space(14.0);

                surface().show(ui, |ui| {
                    ui.set_width(ui.available_width());
                    ui.label(RichText::new("원격 저장소 주소").strong().color(INK));
                    ui.add_space(4.0);
                    let mut url = self.checkout_url.clone();
                    let response = ui.add_sized(
                        [ui.available_width(), 34.0],
                        TextEdit::singleline(&mut url)
                            .hint_text("예: https://github.com/team/project.git"),
                    );
                    if response.changed() {
                        self.set_checkout_url(url);
                    }
                    ui.add_space(12.0);

                    ui.label(RichText::new("저장할 위치").strong().color(INK));
                    ui.add_space(4.0);
                    ui.horizontal(|ui| {
                        let browse_width = 106.0;
                        let field_width = (ui.available_width() - browse_width - 8.0).max(80.0);
                        ui.add_sized(
                            [field_width, 34.0],
                            TextEdit::singleline(&mut self.checkout_parent)
                                .hint_text("상위 폴더 경로"),
                        );
                        if ui.button("찾아보기…").clicked() {
                            if let Some(path) = rfd::FileDialog::new().pick_folder() {
                                self.checkout_parent = path.display().to_string();
                            }
                        }
                    });
                    ui.add_space(12.0);

                    ui.label(RichText::new("저장소 폴더 이름").strong().color(INK));
                    ui.add_space(4.0);
                    if ui
                        .add_sized(
                            [ui.available_width(), 34.0],
                            TextEdit::singleline(&mut self.checkout_name).hint_text("저장소 이름"),
                        )
                        .changed()
                    {
                        self.name_touched = true;
                    }
                    ui.add_space(12.0);

                    let destination = checkout::destination(
                        Path::new(&self.checkout_parent),
                        &self.checkout_name,
                    );
                    ui.label(RichText::new("최종 저장 경로").small().color(MUTED));
                    let destination_text = destination
                        .as_ref()
                        .map(|path| path.display().to_string())
                        .unwrap_or_else(|_| "폴더 경로와 이름을 확인하세요".into());
                    let mut destination_display = destination_text.clone();
                    ui.add_sized(
                        [ui.available_width(), 30.0],
                        TextEdit::singleline(&mut destination_display)
                            .font(egui::TextStyle::Monospace)
                            .interactive(false),
                    );
                });

                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("새 저장소는 지정한 폴더에 생성됩니다.")
                            .small()
                            .color(MUTED),
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui.button("취소").clicked() {
                            self.dialog = None;
                        }
                        let valid_destination = checkout::destination(
                            Path::new(&self.checkout_parent),
                            &self.checkout_name,
                        );
                        if !self.checkout_url.trim().is_empty()
                            && valid_destination.is_ok()
                            && primary(ui, "복제 / 체크아웃").clicked()
                        {
                            let mut request = VcsRequest::new(
                                Operation::Clone,
                                PathBuf::from(&self.checkout_parent),
                            );
                            request.remote = self.checkout_url.clone();
                            request.destination = valid_destination.expect("checked above");
                            self.run_request(request, "복제");
                            self.dialog = None;
                        }
                        if let Ok(path) = checkout::destination(
                            Path::new(&self.checkout_parent),
                            &self.checkout_name,
                        ) {
                            if ui.button("새 로컬 저장소 만들기").clicked() {
                                let request = VcsRequest::new(Operation::Initialize, path);
                                self.run_request(request, "저장소 초기화");
                                self.dialog = None;
                            }
                        }
                    });
                });
            });
        if let Some(shown) = shown {
            observe_window(ctx, "checkout", shown.response.rect);
        }
        if !open {
            self.dialog = None;
        }
    }

    pub(super) fn confirm_sync_ui(&mut self, ctx: &egui::Context) {
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
        let (title, action, detail, accent) = match operation {
            Operation::Update => (
                "원격 변경 가져오기 확인",
                "가져오기 실행",
                if repo.kind == VcsKind::Git {
                    "원격 변경을 가져오고 fast-forward로 적용합니다. 분기된 이력은 자동 병합하지 않습니다."
                } else {
                    "서버의 변경 내용을 현재 작업 공간에 동기화합니다. 충돌이 생기면 별도로 해결해야 합니다."
                },
                BLUE,
            ),
            Operation::Push => (
                "원격 저장소 보내기 확인",
                "보내기 실행",
                "현재 브랜치의 커밋을 설정된 원격 저장소로 전송합니다.",
                ACCENT,
            ),
            _ => return,
        };
        let shown = egui::Window::new(title)
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size(window_size(ctx, [560.0, 350.0]))
            .min_size(window_size(ctx, [460.0, 300.0]))
            .max_size(window_size(ctx, [680.0, 520.0]))
            .collapsible(false)
            .resizable(false)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                section(ui, title, "실행 전에 저장소와 대상 정보를 확인하세요.");
                ui.add_space(10.0);
                surface().show(ui, |ui| {
                    ui.set_width(ui.available_width());
                    pill(ui, repo.kind.name(), accent);
                    ui.add_space(6.0);
                    ui.label(RichText::new(&repo.branch).strong().color(INK));
                    let mut repository_root = repo.root.display().to_string();
                    ui.add_sized(
                        [ui.available_width(), 34.0],
                        TextEdit::singleline(&mut repository_root)
                            .font(egui::TextStyle::Monospace)
                            .interactive(false),
                    );
                    if operation == Operation::Push {
                        let target = self
                            .history
                            .git
                            .as_ref()
                            .map(|history| history.push_target.as_str())
                            .filter(|target| !target.is_empty())
                            .unwrap_or(&repo.summary);
                        ui.label(RichText::new("전송 대상").small().color(MUTED));
                        ui.label(RichText::new(target).color(INK));
                    }
                });
                ui.add_space(10.0);
                ui.label(detail);
                ui.add_space(14.0);
                ui.horizontal(|ui| {
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui.button("취소").clicked() {
                            self.cancel_sync_confirmation();
                        }
                        if primary(ui, action).clicked() {
                            self.pending_sync = None;
                            self.dialog = None;
                            self.execute(operation, String::new(), vec![]);
                        }
                    });
                });
            });
        if let Some(shown) = shown {
            observe_window(ctx, "confirm_sync", shown.response.rect);
        }
        if !open {
            self.cancel_sync_confirmation();
        }
    }

    pub(super) fn settings_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        let shown = egui::Window::new("도구 및 탐색기 설정")
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size(window_size(ctx, [780.0, 560.0]))
            .min_size(window_size(ctx, [620.0, 440.0]))
            .max_size(window_size(ctx, [980.0, 760.0]))
            .collapsible(false)
            .resizable(true)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                let footer_height = 68.0;
                let content_height = (ui.available_height() - footer_height).max(240.0);
                ui.allocate_ui_with_layout(
                    egui::vec2(ui.available_width(), content_height),
                    Layout::top_down(Align::Min),
                    |ui| {
                        ui.horizontal(|ui| {
                            let nav_width = 184.0_f32.min((ui.available_width() * 0.30).max(138.0));
                            ui.allocate_ui_with_layout(
                                egui::vec2(nav_width, ui.available_height()),
                                Layout::top_down(Align::Min),
                                |ui| {
                                    section(ui, "설정", "도구와 탐색기 동작을 관리합니다.");
                                    ui.add_space(8.0);
                                    settings_nav(
                                        ui,
                                        &mut self.settings_tab,
                                        SettingsTab::Vcs,
                                        "버전 관리 도구",
                                        "Git · SVN · Perforce · Lore",
                                    );
                                    settings_nav(
                                        ui,
                                        &mut self.settings_tab,
                                        SettingsTab::FileTools,
                                        "비교·이력 도구",
                                        "Diff · Blame",
                                    );
                                    settings_nav(
                                        ui,
                                        &mut self.settings_tab,
                                        SettingsTab::Explorer,
                                        "탐색기",
                                        "메뉴 · 상태 배지",
                                    );
                                },
                            );
                            ui.separator();
                            let page_size = ui.available_size();
                            ui.allocate_ui_with_layout(
                                page_size,
                                Layout::top_down(Align::Min),
                                |page_ui| {
                                    let page_clip =
                                        page_ui.clip_rect().intersect(page_ui.max_rect());
                                    page_ui.set_clip_rect(page_clip);
                                    let scroll_id = match self.settings_tab {
                                        SettingsTab::Vcs => "settings_vcs_scroll",
                                        SettingsTab::FileTools => "settings_file_tools_scroll",
                                        SettingsTab::Explorer => "settings_explorer_scroll",
                                    };
                                    ScrollArea::vertical()
                                        .id_salt(scroll_id)
                                        .auto_shrink([false, false])
                                        .show(page_ui, |scroll_ui| {
                                            scroll_ui.with_layout(
                                                Layout::top_down(Align::Min),
                                                |content_ui| {
                                                    content_ui.set_width(page_size.x.max(1.0));
                                                    match self.settings_tab {
                                                        SettingsTab::Vcs => {
                                                            self.vcs_settings(content_ui)
                                                        }
                                                        SettingsTab::FileTools => {
                                                            self.file_tool_settings(content_ui)
                                                        }
                                                        SettingsTab::Explorer => {
                                                            self.explorer_settings(content_ui)
                                                        }
                                                    }
                                                },
                                            );
                                        });
                                },
                            );
                        });
                    },
                );
                ui.separator();
                ui.horizontal_wrapped(|ui| {
                    if let Some(notice) = self.notice.as_deref() {
                        let status_width = (ui.available_width() - 148.0).max(80.0);
                        ui.add_sized(
                            [status_width, 42.0],
                            egui::Label::new(RichText::new(notice).color(INK)).wrap(),
                        );
                    }
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if primary(ui, "설정 저장").clicked() {
                            self.save_preferences();
                        }
                    });
                });
            });
        if let Some(shown) = shown {
            observe_window(ctx, "settings", shown.response.rect);
        }
        if !open {
            self.dialog = None;
        }
    }

    fn vcs_settings(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "버전 관리 도구",
            "사용할 저장소 CLI의 실행 파일을 지정합니다. 비워 두면 PATH에서 찾습니다.",
        );
        ui.add_space(10.0);
        for kind in VcsKind::ALL {
            surface().show(ui, |ui| {
                ui.set_width(ui.available_width());
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        ui.label(RichText::new(kind.name()).strong().color(INK));
                        ui.label(
                            RichText::new(format!("실행 파일: {}", kind.executable()))
                                .small()
                                .color(MUTED),
                        );
                    });
                    if ui.button("공식 다운로드 ↗").clicked() {
                        let _ = external::open_url(moavcs_core::external::download_url(kind));
                    }
                });
                ui.add_space(6.0);
                let path = self.settings.executable_paths.entry(kind).or_default();
                ui.horizontal(|ui| {
                    let buttons_width = 104.0;
                    let field_width = (ui.available_width() - buttons_width - 8.0).max(90.0);
                    ui.add_sized(
                        [field_width, 34.0],
                        TextEdit::singleline(path).hint_text("PATH 기본값 사용"),
                    );
                    if ui.button("찾아보기…").clicked() {
                        if let Some(file) = rfd::FileDialog::new().pick_file() {
                            *path = file.display().to_string();
                        }
                    }
                });
            });
            ui.add_space(8.0);
        }
    }

    fn file_tool_settings(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "비교·이력 도구",
            "기본은 MoaVCS 내장 화면입니다. 외부 프로그램은 도구별로 선택할 수 있습니다.",
        );
        ui.add_space(10.0);
        for kind in [ExternalToolKind::Diff, ExternalToolKind::Blame] {
            let title = match kind {
                ExternalToolKind::Diff => "Diff 비교 도구",
                ExternalToolKind::Blame => "Blame 이력 도구",
            };
            surface().show(ui, |ui| {
                ui.set_width(ui.available_width());
                let tool = self.settings.external_tools.entry(kind).or_default();
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        ui.label(RichText::new(title).strong().color(INK));
                        ui.label(
                            RichText::new("MoaVCS 내장 도구 또는 외부 프로그램")
                                .small()
                                .color(MUTED),
                        );
                    });
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.label(if tool.use_external {
                            "외부 도구"
                        } else {
                            "내장 도구"
                        });
                        ui.checkbox(&mut tool.use_external, "외부 도구 사용");
                    });
                });
                ui.add_space(8.0);
                ui.label(RichText::new("실행 파일").small().color(MUTED));
                ui.add_sized(
                    [ui.available_width(), 34.0],
                    TextEdit::singleline(&mut tool.executable_path)
                        .hint_text("외부 실행 파일 경로"),
                );
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    ui.label(RichText::new("인수 템플릿").strong().color(INK));
                    ui.label(
                        RichText::new(match kind {
                            ExternalToolKind::Diff => "{left} · {right} 또는 {patch}",
                            ExternalToolKind::Blame => "{blame} 또는 {file}",
                        })
                        .small()
                        .color(MUTED),
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui.button("인수 추가").clicked() {
                            tool.argument_templates.push(String::new());
                        }
                    });
                });
                for (index, argument) in tool.argument_templates.iter_mut().enumerate() {
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new(format!("{}.", index + 1))
                                .small()
                                .color(MUTED),
                        );
                        ui.add_sized(
                            [ui.available_width(), 32.0],
                            TextEdit::singleline(argument).hint_text("프로그램에 전달할 인수 하나"),
                        );
                    });
                }
                if tool.argument_templates.is_empty() {
                    ui.label(
                        RichText::new("인수가 비어 있으면 도구 유형별 기본 인수를 사용합니다.")
                            .small()
                            .color(MUTED),
                    );
                }
            });
            ui.add_space(8.0);
        }
    }

    fn explorer_settings(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "탐색기 연동",
            "메뉴 구성 적용과 앱 설정 저장은 각각 실행해야 합니다.",
        );
        ui.add_space(10.0);
        surface().show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(RichText::new("우클릭 메뉴").strong().color(INK));
            ui.add_space(6.0);
            ui.checkbox(
                &mut self.settings.explorer_menu.show_commit_at_top_level,
                "커밋을 첫 단계 메뉴에 표시",
            );
            ui.checkbox(
                &mut self.settings.explorer_menu.show_update_at_top_level,
                "가져오기를 첫 단계 메뉴에 표시",
            );
            ui.add_space(4.0);
            ui.horizontal_wrapped(|ui| {
                if primary(ui, "탐색기 메뉴 설치 / 갱신").clicked() {
                    self.notice = Some(
                        match crate::platform::install_context_menu(&self.settings) {
                            Ok(()) => "탐색기 메뉴를 갱신했습니다.".into(),
                            Err(error) => error.to_string(),
                        },
                    );
                }
                if ui.button("탐색기 메뉴 제거").clicked() {
                    self.notice = Some(match crate::platform::uninstall_context_menu() {
                        Ok(()) => "MoaVCS 탐색기 메뉴를 제거했습니다.".into(),
                        Err(error) => error.to_string(),
                    });
                }
            });
        });
        ui.add_space(8.0);
        surface().show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(RichText::new("파일 상태 배지").strong().color(INK));
            ui.add_space(6.0);
            ui.checkbox(
                &mut self.settings.explorer_overlays_enabled,
                "수정됨 · 충돌 · 추적 안 됨 표시",
            );
            ui.checkbox(
                &mut self.settings.explorer_overlays_prefer_priority,
                "Windows 배지 슬롯에서 MoaVCS 우선 표시",
            );
            ui.label(
                RichText::new(
                    "상태 배지 설치와 우선순위 변경에는 관리자 승인이 필요할 수 있습니다.",
                )
                .small()
                .color(MUTED),
            );
            if primary(ui, "상태 배지 설정 적용").clicked() {
                self.notice = Some(
                    match crate::platform::configure_overlays(
                        self.settings.explorer_overlays_enabled,
                        self.settings.explorer_overlays_prefer_priority,
                    ) {
                        Ok(()) => "탐색기 상태 배지 설정을 적용했습니다.".into(),
                        Err(error) => error.to_string(),
                    },
                );
            }
        });
    }

    pub(super) fn remote_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        let shown = egui::Window::new("원격 주소 · origin")
            .open(&mut open)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .default_size(window_size(ctx, [700.0, 500.0]))
            .min_size(window_size(ctx, [520.0, 390.0]))
            .max_size(window_size(ctx, [900.0, 680.0]))
            .collapsible(false)
            .resizable(true)
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                if let Some(remote) = self.remote.clone() {
                    section(
                        ui,
                        "원격 저장소 주소",
                        "origin의 기본 Fetch 주소를 편집합니다. 추가 Fetch 및 별도 Push 주소는 보존됩니다.",
                    );
                    ui.add_space(10.0);
                    surface().show(ui, |ui| {
                        ui.set_width(ui.available_width());
                        ui.label(RichText::new(if remote.exists {
                            "저장할 Fetch 주소"
                        } else {
                            "새 origin 주소"
                        }).strong().color(INK));
                        ui.add_sized(
                            [ui.available_width(), 36.0],
                            TextEdit::singleline(&mut self.remote_url)
                                .hint_text("https://example.com/team/repository.git"),
                        );
                        if !remote.fetch_urls.is_empty() {
                            url_group(ui, "현재 Fetch 주소", &remote.fetch_urls);
                        }
                        if !remote.push_urls.is_empty() {
                            url_group(ui, "Push 전용 주소", &remote.push_urls);
                        }
                        if !remote.effective_fetch_urls.is_empty() {
                            url_group(ui, "Git이 실제 사용하는 Fetch 주소", &remote.effective_fetch_urls);
                        }
                        if let Some(reason) = &remote.read_only_reason {
                            ui.add_space(6.0);
                            ui.label(RichText::new(reason).color(RED));
                        }
                    });
                    ui.add_space(10.0);
                    ui.label(
                        RichText::new("주소를 저장해도 Pull 또는 Push는 자동 실행되지 않습니다.")
                            .small()
                            .color(MUTED),
                    );
                    ui.horizontal(|ui| {
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            if ui.button("닫기").clicked() {
                                self.dialog = None;
                            }
                            let supported = remote.read_only_reason.is_none()
                                && self.provider.as_ref().is_some_and(|p| p.capabilities().remote_url);
                            if supported && !self.remote_url.trim().is_empty()
                                && primary(ui, "origin 주소 저장").clicked()
                            {
                                self.save_remote();
                            }
                        });
                    });
                }
            });
        if let Some(shown) = shown {
            observe_window(ctx, "remote", shown.response.rect);
        }
        if !open {
            self.dialog = None;
        }
    }
}

fn settings_nav(
    ui: &mut egui::Ui,
    selected: &mut SettingsTab,
    tab: SettingsTab,
    title: &str,
    subtitle: &str,
) {
    let is_selected = *selected == tab;
    egui::Frame::none()
        .fill(if is_selected {
            BLUE.gamma_multiply(0.08)
        } else {
            Color32::TRANSPARENT
        })
        .rounding(7.0)
        .inner_margin(egui::Margin::symmetric(8.0, 6.0))
        .show(ui, |ui| {
            let response = ui.selectable_label(is_selected, RichText::new(title).strong());
            if response.clicked() {
                *selected = tab;
            }
            ui.label(RichText::new(subtitle).size(13.0).color(MUTED));
        });
    ui.add_space(8.0);
}

fn url_group(ui: &mut egui::Ui, label: &str, urls: &[String]) {
    ui.add_space(8.0);
    ui.label(RichText::new(label).small().color(MUTED));
    for url in urls {
        let mut displayed_url = url.clone();
        ui.add_sized(
            [ui.available_width(), 30.0],
            TextEdit::singleline(&mut displayed_url)
                .font(egui::TextStyle::Monospace)
                .interactive(false),
        );
    }
}
