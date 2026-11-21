use super::*;

impl MoaApp {
    pub(super) fn load_git_sync(&mut self) {
        self.git_sync = None;
        self.git_sync_error = None;
        let (Some(provider), Some(repo)) = (self.provider.clone(), self.repo.as_ref()) else {
            return;
        };
        if repo.kind != VcsKind::Git {
            return;
        }
        let root = repo.root.clone();
        let (id, cancel) = self.begin_read(ReadSlot::GitSync, "브랜치의 푸시 상태를 확인하는 중…");
        let tx = self.tx.clone();
        thread::spawn(move || {
            let result = provider
                .git_sync_status(&root, &cancel)
                .map_err(|e| e.to_string());
            let _ = tx.send(Event::GitSync(id, result));
        });
    }

    pub(super) fn publication_summary(&self, ui: &mut egui::Ui) {
        let Some(git) = &self.git_sync else {
            let text = if self.read_slots.contains_key(&ReadSlot::GitSync) {
                "로컬 커밋과 원격 반영 위치를 확인하는 중…"
            } else if self.git_sync_error.is_some() {
                "원격 반영 상태 조회 실패 · F5로 다시 확인"
            } else {
                "원격 반영 상태 조회 전 · F5로 확인"
            };
            let response = ui.label(RichText::new(text).small().color(MUTED));
            if let Some(error) = &self.git_sync_error {
                response.on_hover_text(error);
            }
            return;
        };
        publication_card(ui, git);
    }

    pub(super) fn apply_history_filter(&mut self) {
        if self
            .history
            .git
            .as_ref()
            .is_none_or(|git| git.push_ahead.is_none())
        {
            self.history_filter = None;
        }
        self.visible_history = self
            .history
            .entries
            .iter()
            .enumerate()
            .filter(|(_, entry)| {
                self.history_filter.is_none() || entry.push_relation == self.history_filter
            })
            .map(|(index, _)| index)
            .collect();
        let selected = self
            .visible_history
            .iter()
            .copied()
            .find(|i| Some(self.history.entries[*i].id.as_str()) == self.revision.as_deref())
            .or_else(|| self.visible_history.first().copied());
        if let Some(index) = selected {
            self.load_details(&self.history.entries[index].clone());
        } else {
            self.cancel_read_slot(ReadSlot::Details);
            self.cancel_read_slot(ReadSlot::HistoryDiff);
            self.revision = None;
            self.details = None;
            self.history_file = None;
            self.hdiff.clear();
            self.history_diff_view = PreparedText::default();
        }
    }
}

pub(super) fn publication_card(ui: &mut egui::Ui, git: &GitHistoryContext) {
    egui::Frame::none()
            .fill(SURFACE)
            .stroke(egui::Stroke::new(1.0_f32, BORDER))
            .rounding(7.)
            .inner_margin(egui::vec2(12., 6.))
            .show(ui, |ui| {
                ui.columns(3, |cols| {
                    let local = if git.is_detached { "분리된 HEAD" } else { &git.current_branch };
                    let head = if git.is_unborn { "첫 커밋 전" } else { short(&git.head_id) };
                    summary_column(&mut cols[0], "로컬 최신 커밋", &format!("{local}  ·  {head}"), INK, &git.head_id);
                    let remote = if git.push_target.is_empty() {
                        "푸시 대상 미확인".into()
                    } else if git.push_target_id.is_empty() {
                        format!("{} · 위치 미확인", git.push_target)
                    } else {
                        format!("{}  ·  {}", git.push_target, short(&git.push_target_id))
                    };
                    let hint = if git.push_target.is_empty() {
                        "저장소 → 원격 주소 설정에서 origin을 확인하세요. 첫 푸시가 성공하면 Git 설정에 따라 현재 브랜치의 추적 대상이 연결됩니다.".to_owned()
                    } else {
                        format!("{}\n저장소 → 원격 추적 정보 갱신 (Fetch)으로 원격 정보를 확인할 수 있습니다.", git.push_target_id)
                    };
                    summary_column(&mut cols[1], "푸시 대상 · 마지막 확인", &remote, BLUE, hint.trim());
                    let (state, color) = publication_state(git);
                    summary_column(&mut cols[2], "푸시 대상과의 차이", &state, color,
                        "미푸시는 현재 브랜치에만 있는 커밋, 원격에만 있는 커밋은 푸시 대상에만 있는 커밋입니다. 개별 커밋은 커밋 이력에서 확인하세요.");
                });
            });
}

fn summary_column(ui: &mut egui::Ui, title: &str, value: &str, color: Color32, hint: &str) {
    ui.spacing_mut().item_spacing.y = 1.;
    ui.add(egui::Label::new(RichText::new(title).size(11.5).color(MUTED)).truncate());
    ui.add(egui::Label::new(heading(value, 14.).color(color)).truncate())
        .on_hover_text(format!("{value}\n{hint}"));
}

pub(super) fn publication_state(git: &GitHistoryContext) -> (String, Color32) {
    match (git.push_ahead, git.push_behind) {
        (Some(0), Some(0)) => ("로컬 커밋 모두 반영됨".into(), GREEN),
        (Some(ahead), Some(0)) => (format!("미푸시 {ahead}개"), BLUE),
        (Some(0), Some(behind)) => (format!("원격에만 {behind}개"), ACCENT),
        (Some(ahead), Some(behind)) => (format!("미푸시 {ahead}개 · 원격에만 {behind}개"), RED),
        _ if git.is_unborn => ("첫 커밋 전".into(), MUTED),
        _ if git.is_detached => ("브랜치 선택 필요".into(), MUTED),
        _ if git.is_shallow => ("얕은 복제 · 개수 미확인".into(), MUTED),
        _ if git.push_target.is_empty() => ("원격 반영 여부 미확인".into(), MUTED),
        _ => ("원격 추적 정보 확인 필요".into(), MUTED),
    }
}
