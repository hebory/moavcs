use super::*;
use egui::{Align2, FontId, Rect, Sense, Stroke, Vec2};

const ROW_HEIGHT: f32 = 25.0;
const CODE_FONT: f32 = 13.0;
const GUTTER_WIDTH: f32 = 62.0;

impl MoaApp {
    pub(super) fn inspection_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        if let Some(inspection) = self.inspection.clone() {
            match inspection {
                Inspection::Diff(diff) => self.diff_window(ctx, &mut open, &diff),
                Inspection::Blame(blame) => self.blame_window(ctx, &mut open, &blame),
            }
            if !open {
                self.inspection = None;
            }
        }
    }

    fn diff_window(&mut self, ctx: &egui::Context, open: &mut bool, diff: &FileComparison) {
        let compared = moavcs_core::text::compare_lines(
            &diff.left_text,
            &diff.right_text,
            &CancelToken::new(),
        )
        .ok();
        let rows = compared
            .as_ref()
            .map(|comparison| comparison.rows.as_slice())
            .unwrap_or_default();
        let changed_rows = rows
            .iter()
            .enumerate()
            .filter(|(_, row)| row.kind != "unchanged")
            .map(|(index, _)| index)
            .collect::<Vec<_>>();
        let added = rows.iter().filter(|row| row.kind == "added").count();
        let removed = rows.iter().filter(|row| row.kind == "removed").count();
        let modified = rows.iter().filter(|row| row.kind == "modified").count();
        let title_path = Path::new(&diff.path)
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or(&diff.path);

        let shown = egui::Window::new(format!("차이 비교  ·  {title_path}"))
            .open(open)
            .resizable(true)
            .collapsible(false)
            .default_size(window_size(ctx, [1050.0, 680.0]))
            .min_size([640.0, 420.0])
            .max_size(window_size(ctx, [1050.0, 680.0]))
            .show(ctx, |ui| {
                ui.set_min_width(0.0);
                if self.busy.is_some() {
                    ui.disable();
                }

                ui.horizontal_wrapped(|ui| {
                    ui.add(
                        egui::Label::new(RichText::new(&diff.path).strong().color(INK)).truncate(),
                    )
                    .on_hover_text(&diff.path);
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        pill(ui, &format!("{}행 변경", changed_rows.len()), BLUE);
                        if removed > 0 {
                            pill(ui, &format!("−{removed}"), RED);
                        }
                        if added > 0 {
                            pill(ui, &format!("+{added}"), GREEN);
                        }
                        if modified > 0 {
                            pill(ui, &format!("±{modified}"), ACCENT);
                        }
                    });
                });
                ui.add_space(8.0);

                ui.columns(2, |panes| {
                    source_header(&mut panes[0], &diff.left_label, RED);
                    source_header(&mut panes[1], &diff.right_label, GREEN);
                });

                ui.horizontal_wrapped(|ui| {
                    ui.label(RichText::new("검색").small().color(MUTED));
                    ui.add_sized(
                        [ui.available_width().min(300.0), 30.0],
                        TextEdit::singleline(&mut self.inspection_search)
                            .id_salt("inspection_search_diff")
                            .hint_text("양쪽 내용 검색"),
                    );
                    if ui.button("이전 변경").clicked() && !changed_rows.is_empty() {
                        self.inspection_change_index =
                            self.inspection_change_index.saturating_sub(1);
                        let row =
                            changed_rows[self.inspection_change_index.min(changed_rows.len() - 1)];
                        self.inspection_scroll = row as f32 * ROW_HEIGHT;
                    }
                    if ui.button("다음 변경").clicked() && !changed_rows.is_empty() {
                        self.inspection_change_index =
                            (self.inspection_change_index + 1).min(changed_rows.len() - 1);
                        self.inspection_scroll =
                            changed_rows[self.inspection_change_index] as f32 * ROW_HEIGHT;
                    }
                    if ui.button("내보내기…").clicked() {
                        let text = format!(
                            "--- {} ---\n{}\n--- {} ---\n{}",
                            diff.left_label, diff.left_text, diff.right_label, diff.right_text
                        );
                        if let Some(path) = rfd::FileDialog::new()
                            .set_file_name("comparison.txt")
                            .save_file()
                        {
                            self.inspection_status = match std::fs::write(&path, text) {
                                Ok(()) => format!("비교 내용을 저장했습니다: {}", path.display()),
                                Err(error) => format!("저장하지 못했습니다: {error}"),
                            };
                        }
                    }
                });

                if !self.inspection_status.is_empty() {
                    ui.small(&self.inspection_status);
                }
                ui.add_space(6.0);

                if let Some(comparison) = &compared {
                    if comparison.rows.is_empty() {
                        surface().show(ui, |ui| {
                            ui.set_min_width(ui.available_width());
                            ui.vertical_centered(|ui| {
                                ui.add_space(32.0);
                                ui.label(RichText::new("✓").size(26.0).color(GREEN));
                                ui.label(RichText::new("두 내용이 같습니다").strong().color(INK));
                                ui.add_space(32.0);
                            });
                        });
                    } else {
                        let viewport_height = ui.available_height().max(160.0);
                        let pane_width = diff_content_width(rows, ui.available_width());
                        ScrollArea::both()
                            .id_salt("inspection_diff_rows")
                            .vertical_scroll_offset(self.inspection_scroll)
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                ui.set_min_width(pane_width * 2.0);
                                ui.set_min_height(
                                    viewport_height.max(rows.len() as f32 * ROW_HEIGHT),
                                );
                                let content_rect = ui.available_rect_before_wrap();
                                let painter = ui.painter();
                                painter.rect_filled(content_rect, 0.0, BG);
                                for gutter_left in
                                    [content_rect.left(), content_rect.left() + pane_width]
                                {
                                    let gutter = Rect::from_min_max(
                                        egui::pos2(gutter_left, content_rect.top()),
                                        egui::pos2(
                                            gutter_left + GUTTER_WIDTH,
                                            content_rect.bottom(),
                                        ),
                                    );
                                    painter.rect_filled(gutter, 0.0, SURFACE);
                                    let border_x = gutter.right() - 1.0;
                                    painter.line_segment(
                                        [
                                            egui::pos2(border_x, gutter.top()),
                                            egui::pos2(border_x, gutter.bottom()),
                                        ],
                                        Stroke::new(1.0_f32, BORDER),
                                    );
                                }
                                let divider_x = content_rect.left() + pane_width;
                                painter.line_segment(
                                    [
                                        egui::pos2(divider_x, content_rect.top()),
                                        egui::pos2(divider_x, content_rect.bottom()),
                                    ],
                                    Stroke::new(1.0_f32, BORDER),
                                );
                                for row in rows {
                                    let query = self.inspection_search.trim();
                                    let hit = !query.is_empty()
                                        && (row
                                            .left_text
                                            .to_lowercase()
                                            .contains(&query.to_lowercase())
                                            || row
                                                .right_text
                                                .to_lowercase()
                                                .contains(&query.to_lowercase()));
                                    let left_fill = if hit {
                                        Color32::from_rgb(255, 243, 196)
                                    } else {
                                        match row.kind.as_str() {
                                            "removed" | "modified" => RED.gamma_multiply(0.09),
                                            _ => SURFACE,
                                        }
                                    };
                                    let right_fill = if hit {
                                        Color32::from_rgb(255, 243, 196)
                                    } else {
                                        match row.kind.as_str() {
                                            "added" | "modified" => GREEN.gamma_multiply(0.09),
                                            _ => SURFACE,
                                        }
                                    };
                                    let (rect, _) = ui.allocate_exact_size(
                                        Vec2::new(pane_width * 2.0, ROW_HEIGHT),
                                        Sense::hover(),
                                    );
                                    let left_rect = Rect::from_min_size(
                                        rect.min,
                                        Vec2::new(pane_width, ROW_HEIGHT),
                                    );
                                    let right_rect = Rect::from_min_size(
                                        rect.min + Vec2::new(pane_width, 0.0),
                                        Vec2::new(pane_width, ROW_HEIGHT),
                                    );
                                    paint_diff_cell(
                                        ui,
                                        left_rect,
                                        row.left_number,
                                        &row.left_text,
                                        left_fill,
                                    );
                                    paint_diff_cell(
                                        ui,
                                        right_rect,
                                        row.right_number,
                                        &row.right_text,
                                        right_fill,
                                    );
                                    ui.painter().line_segment(
                                        [right_rect.left_top(), right_rect.left_bottom()],
                                        Stroke::new(1.0_f32, BORDER),
                                    );
                                }
                            });
                    }
                } else {
                    surface().show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.colored_label(RED, "비교 내용을 만들지 못했습니다.");
                    });
                }
            });
        if let Some(shown) = shown {
            observe_window(ctx, "diff", shown.response.rect);
        }
    }

    fn blame_window(&mut self, ctx: &egui::Context, open: &mut bool, blame: &BlameResult) {
        let title_path = Path::new(&blame.path)
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or(&blame.path);
        let shown = egui::Window::new(format!("행별 이력  ·  {title_path}"))
            .open(open)
            .resizable(true)
            .collapsible(false)
            .default_size(window_size(ctx, [1050.0, 680.0]))
            .min_size([640.0, 420.0])
            .max_size(window_size(ctx, [1050.0, 680.0]))
            .show(ctx, |ui| {
                if self.busy.is_some() {
                    ui.disable();
                }
                ui.horizontal_wrapped(|ui| {
                    ui.add(
                        egui::Label::new(RichText::new(&blame.path).strong().color(INK)).truncate(),
                    )
                    .on_hover_text(&blame.path);
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        pill(ui, &format!("{}행", blame.lines.len()), BLUE);
                        pill(ui, &blame.revision_label, MUTED);
                    });
                });
                ui.add_space(8.0);
                ui.horizontal_wrapped(|ui| {
                    ui.label(RichText::new("검색").small().color(MUTED));
                    ui.add_sized(
                        [ui.available_width().min(360.0), 30.0],
                        TextEdit::singleline(&mut self.inspection_search)
                            .id_salt("inspection_search_blame")
                            .hint_text("작성자 · 리비전 · 날짜 · 코드"),
                    );
                    if primary(ui, "다음 찾기").clicked()
                        && !self.inspection_search.trim().is_empty()
                    {
                        let query = self.inspection_search.to_lowercase();
                        let start = (self.inspection_change_index + 1).min(blame.lines.len());
                        let found = (start..blame.lines.len()).chain(0..start).find(|&index| {
                            let line = &blame.lines[index];
                            format!(
                                "{} {} {} {}",
                                line.revision, line.author, line.date, line.text
                            )
                            .to_lowercase()
                            .contains(&query)
                        });
                        if let Some(index) = found {
                            self.inspection_change_index = index;
                            self.inspection_scroll = index as f32 * ROW_HEIGHT;
                            self.inspection_status =
                                format!("{}행으로 이동했습니다.", blame.lines[index].line_number);
                        } else {
                            self.inspection_status = "검색 결과가 없습니다.".into();
                        }
                    }
                    if ui.button("TSV 내보내기…").clicked() {
                        let mut text = String::from("Line\tRevision\tAuthor\tDate\tText\n");
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
                            self.inspection_status = match std::fs::write(&path, text) {
                                Ok(()) => format!("Blame을 저장했습니다: {}", path.display()),
                                Err(error) => format!("저장하지 못했습니다: {error}"),
                            };
                        }
                    }
                });
                if !self.inspection_status.is_empty() {
                    ui.small(&self.inspection_status);
                }
                ui.add_space(6.0);

                let content_width = ui.available_width();
                let author_width = content_width.clamp(110.0, 190.0);
                let revision_width = content_width.clamp(100.0, 145.0);
                let date_width = content_width.clamp(112.0, 165.0);
                let line_width = 62.0;
                let code_width =
                    (content_width - line_width - revision_width - author_width - date_width)
                        .max(260.0);
                let table_width =
                    line_width + revision_width + author_width + date_width + code_width;
                ScrollArea::both()
                    .id_salt("inspection_blame_lines")
                    .vertical_scroll_offset(self.inspection_scroll)
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.set_min_width(table_width);
                        paint_blame_header(
                            ui,
                            [
                                line_width,
                                revision_width,
                                author_width,
                                date_width,
                                code_width,
                            ],
                        );
                        for (index, line) in blame.lines.iter().enumerate() {
                            let selected = index == self.inspection_change_index
                                && !self.inspection_search.trim().is_empty();
                            paint_blame_row(
                                ui,
                                line,
                                [
                                    line_width,
                                    revision_width,
                                    author_width,
                                    date_width,
                                    code_width,
                                ],
                                selected,
                            );
                        }
                    });
            });
        if let Some(shown) = shown {
            observe_window(ctx, "blame", shown.response.rect);
        }
    }

    pub(super) fn conflict_ui(&mut self, ctx: &egui::Context) {
        let mut open = true;
        if let Some(conflict) = self.conflict.clone() {
            let title_path = Path::new(&conflict.path)
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or(&conflict.path);
            let shown = egui::Window::new(format!("충돌 해결  ·  {title_path}"))
                .open(&mut open)
                .resizable(true)
                .collapsible(false)
                .default_size(window_size(ctx, [1080.0, 720.0]))
                .min_size([640.0, 420.0])
                .max_size(window_size(ctx, [1080.0, 720.0]))
                .show(ctx, |ui| {
                    if self.busy.is_some() {
                        ui.disable();
                    }
                    section(
                        ui,
                        "충돌 해결",
                        "양쪽 변경을 검토하고 결과를 편집한 다음 저장하고 해결 완료를 표시하세요.",
                    );

                    ui.horizontal_wrapped(|ui| {
                        if ui.button("내 변경 전체 적용").clicked() {
                            self.conflict_text = conflict.left_text.clone();
                            self.conflict_selection = None;
                        }
                        if ui.button("받은 변경 전체 적용").clicked() {
                            self.conflict_text = conflict.right_text.clone();
                            self.conflict_selection = None;
                        }
                        if let Some(base) = &conflict.base_text {
                            if ui.button("기준 내용 전체 적용").clicked() {
                                self.conflict_text = base.clone();
                                self.conflict_selection = None;
                            }
                        }
                        ui.separator();
                        if primary(ui, "결과 저장").clicked() {
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
                    if let Some((start, end)) = self.conflict_selection {
                        if start < end {
                            ui.horizontal_wrapped(|ui| {
                                ui.label(RichText::new("선택한 줄").small().color(MUTED));
                                if ui.button("내 변경으로 바꾸기").clicked() {
                                    replace_selected_lines(
                                        &mut self.conflict_text,
                                        conflict.left_text.as_str(),
                                        start..end,
                                    );
                                }
                                if ui.button("받은 변경으로 바꾸기").clicked() {
                                    replace_selected_lines(
                                        &mut self.conflict_text,
                                        conflict.right_text.as_str(),
                                        start..end,
                                    );
                                }
                                if let Some(base) = &conflict.base_text {
                                    if ui.button("기준 내용으로 바꾸기").clicked() {
                                        replace_selected_lines(
                                            &mut self.conflict_text,
                                            base,
                                            start..end,
                                        );
                                    }
                                }
                            });
                        }
                    }

                    ScrollArea::vertical()
                        .id_salt("conflict_editor_content")
                        .auto_shrink([false, false])
                        .show(ui, |ui| {
                            let has_markers =
                                moavcs_core::text::contains_markers(&self.conflict_text);
                            ui.horizontal_wrapped(|ui| {
                                if has_markers {
                                    pill(ui, "충돌 표식이 남아 있습니다", RED);
                                } else {
                                    pill(ui, "충돌 표식 없음", GREEN);
                                }
                                ui.label(
                                    RichText::new("원본 파일은 안전 백업을 남긴 뒤 교체됩니다.")
                                        .small()
                                        .color(MUTED),
                                );
                            });
                            if let Some(path) = self
                                .conflict
                                .as_ref()
                                .and_then(|saved| saved.backup_path.as_ref())
                            {
                                surface().show(ui, |ui| {
                                    ui.horizontal(|ui| {
                                        ui.label(RichText::new("원본 백업").strong().color(INK));
                                        ui.add(
                                            egui::Label::new(
                                                RichText::new(path.display().to_string())
                                                    .monospace()
                                                    .color(MUTED),
                                            )
                                            .truncate(),
                                        )
                                        .on_hover_text(path.display().to_string());
                                    });
                                });
                            }

                            ui.add_space(6.0);
                            ui.columns(2, |panes| {
                                source_pane(
                                    &mut panes[0],
                                    &conflict.left_label,
                                    &conflict.left_text,
                                    "conflict_local_source",
                                    RED,
                                    205.0,
                                );
                                source_pane(
                                    &mut panes[1],
                                    &conflict.right_label,
                                    &conflict.right_text,
                                    "conflict_incoming_source",
                                    GREEN,
                                    205.0,
                                );
                            });
                            if let Some(base) = &conflict.base_text {
                                egui::CollapsingHeader::new("기준 내용 보기")
                                    .default_open(false)
                                    .show(ui, |ui| {
                                        code_view(ui, "conflict_base_source", base, 150.0);
                                    });
                            }

                            ui.add_space(6.0);
                            ui.horizontal(|ui| {
                                ui.label(RichText::new("병합 결과").strong().color(INK));
                                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                    ui.small(
                                        RichText::new(
                                            "텍스트를 선택하면 해당 줄만 교체할 수 있습니다",
                                        )
                                        .color(MUTED),
                                    );
                                });
                            });
                            let editor_width = ui.available_width();
                            let response = TextEdit::multiline(&mut self.conflict_text)
                                .id_salt("conflict_result")
                                .font(egui::TextStyle::Monospace)
                                .desired_width(editor_width)
                                .desired_rows(12)
                                .frame(true)
                                .show(ui);
                            if let Some(range) = response.cursor_range {
                                let selected = range.as_sorted_char_range();
                                self.conflict_selection = (selected.start < selected.end)
                                    .then_some((selected.start, selected.end));
                            }
                        });
                });
            if let Some(shown) = shown {
                observe_window(ctx, "conflict", shown.response.rect);
            }
        }
        if !open {
            self.conflict = None;
        }
    }
}

fn source_header(ui: &mut egui::Ui, title: &str, color: Color32) {
    ui.horizontal(|ui| {
        ui.label(RichText::new("●").color(color));
        ui.label(RichText::new(title).strong().color(INK));
    });
    ui.add_space(4.0);
}

fn diff_content_width(rows: &[moavcs_core::text::ComparisonRow], available: f32) -> f32 {
    let left = rows
        .iter()
        .map(|row| row.left_text.chars().count())
        .max()
        .unwrap_or(0);
    let right = rows
        .iter()
        .map(|row| row.right_text.chars().count())
        .max()
        .unwrap_or(0);
    let longest = left.max(right) as f32;
    (available / 2.0)
        .max(GUTTER_WIDTH + 220.0)
        .max(GUTTER_WIDTH + longest * (CODE_FONT * 0.62) + 20.0)
}

fn paint_diff_cell(
    ui: &egui::Ui,
    rect: Rect,
    line_number: Option<usize>,
    text: &str,
    fill: Color32,
) {
    let painter = ui.painter().with_clip_rect(rect);
    painter.rect_filled(rect, 0.0, fill);
    let gutter = Rect::from_min_max(
        rect.left_top(),
        egui::pos2(rect.left() + GUTTER_WIDTH, rect.bottom()),
    );
    painter.rect_filled(gutter, 0.0, SURFACE);
    let number = line_number
        .map(|number| number.to_string())
        .unwrap_or_default();
    painter.text(
        rect.left_top() + Vec2::new(6.0, rect.height() / 2.0),
        Align2::LEFT_CENTER,
        number,
        FontId::monospace(12.0),
        MUTED,
    );
    painter.line_segment(
        [
            egui::pos2(rect.left() + GUTTER_WIDTH - 1.0, rect.top()),
            egui::pos2(rect.left() + GUTTER_WIDTH - 1.0, rect.bottom()),
        ],
        Stroke::new(1.0_f32, BORDER),
    );
    painter.text(
        rect.left_top() + Vec2::new(GUTTER_WIDTH, rect.height() / 2.0),
        Align2::LEFT_CENTER,
        text,
        FontId::monospace(CODE_FONT),
        INK,
    );
}

fn paint_blame_header(ui: &mut egui::Ui, widths: [f32; 5]) {
    let labels = ["행", "리비전", "작성자", "날짜", "내용"];
    let total = widths.iter().sum();
    let (rect, _) = ui.allocate_exact_size(Vec2::new(total, 30.0), Sense::hover());
    ui.painter().rect_filled(rect, 4.0, NAVY);
    let mut x = rect.left();
    for (label, width) in labels.into_iter().zip(widths) {
        ui.painter().text(
            egui::pos2(x + 8.0, rect.center().y),
            Align2::LEFT_CENTER,
            label,
            FontId::proportional(12.0),
            Color32::WHITE,
        );
        x += width;
    }
}

fn paint_blame_row(ui: &mut egui::Ui, line: &BlameLine, widths: [f32; 5], selected: bool) {
    let total = widths.iter().sum();
    let (rect, _) = ui.allocate_exact_size(Vec2::new(total, ROW_HEIGHT), Sense::hover());
    let fill = if selected {
        BLUE.gamma_multiply(0.14)
    } else {
        SURFACE
    };
    ui.painter().rect_filled(rect, 0.0, fill);
    let values = [
        line.line_number.to_string(),
        short(&line.revision).to_owned(),
        line.author.clone(),
        line.date.clone(),
        line.text.replace('\n', " "),
    ];
    let mut x = rect.left();
    for (index, (value, width)) in values.into_iter().zip(widths).enumerate() {
        let cell = Rect::from_min_size(egui::pos2(x, rect.top()), Vec2::new(width, rect.height()));
        let painter = ui.painter().with_clip_rect(cell);
        painter.text(
            egui::pos2(x + 8.0, rect.center().y),
            Align2::LEFT_CENTER,
            value,
            if index == 4 {
                FontId::monospace(CODE_FONT)
            } else {
                FontId::proportional(12.0)
            },
            if index == 0 { MUTED } else { INK },
        );
        x += width;
        if index < 4 {
            painter.line_segment(
                [
                    egui::pos2(x, rect.top() + 3.0),
                    egui::pos2(x, rect.bottom() - 3.0),
                ],
                Stroke::new(1.0_f32, BORDER),
            );
        }
    }
    ui.painter().line_segment(
        [rect.left_bottom(), rect.right_bottom()],
        Stroke::new(1.0_f32, BORDER.gamma_multiply(0.5)),
    );
}

fn source_pane(ui: &mut egui::Ui, label: &str, text: &str, id: &str, color: Color32, height: f32) {
    surface().show(ui, |ui| {
        ui.horizontal(|ui| {
            ui.label(RichText::new("●").color(color));
            ui.add(egui::Label::new(RichText::new(label).strong().color(INK)).truncate())
                .on_hover_text(label);
        });
        ui.add_space(5.0);
        code_view(ui, id, text, height);
    });
}

fn replace_selected_lines(result: &mut String, source: &str, selection: std::ops::Range<usize>) {
    let selected_char_range = selection.start..selection.end;
    let selected_byte_range = char_range_to_byte_range(result, selected_char_range);
    if selected_byte_range.start >= selected_byte_range.end {
        return;
    }
    let line_start = result[..selected_byte_range.start]
        .rfind('\n')
        .map(|index| index + 1)
        .unwrap_or(0);
    let last_selected_byte = selected_byte_range.end - 1;
    let line_end = result.as_bytes()[last_selected_byte..]
        .iter()
        .position(|byte| *byte == b'\n')
        .map(|index| last_selected_byte + index + 1)
        .unwrap_or(result.len());
    let preserve_trailing_newline = result[line_start..line_end].ends_with('\n');
    let start_line = result[..line_start].matches('\n').count();
    let line_count = result[line_start..line_end]
        .split_inclusive('\n')
        .count()
        .max(1);
    let source_lines = source.split_inclusive('\n').collect::<Vec<_>>();
    let source_start = source_lines
        .iter()
        .take(start_line)
        .map(|line| line.len())
        .sum::<usize>();
    let source_end = source_lines
        .iter()
        .skip(start_line)
        .take(line_count)
        .map(|line| line.len())
        .sum::<usize>();
    let mut replacement = if source_start <= source.len() {
        source[source_start..(source_start + source_end).min(source.len())].to_owned()
    } else {
        String::new()
    };
    let newline = if result.contains("\r\n") {
        "\r\n"
    } else {
        "\n"
    };
    replacement = replacement.replace("\r\n", "\n").replace('\r', "\n");
    if newline != "\n" {
        replacement = replacement.replace('\n', newline);
    }
    if preserve_trailing_newline {
        if !replacement.is_empty() && !replacement.ends_with(newline) {
            replacement.push_str(newline);
        }
    } else if replacement.ends_with(newline) {
        replacement.truncate(replacement.len() - newline.len());
    }
    result.replace_range(line_start..line_end, &replacement);
}

fn char_range_to_byte_range(text: &str, chars: std::ops::Range<usize>) -> std::ops::Range<usize> {
    let start = text
        .char_indices()
        .nth(chars.start)
        .map(|(index, _)| index)
        .unwrap_or(text.len());
    let end = text
        .char_indices()
        .nth(chars.end)
        .map(|(index, _)| index)
        .unwrap_or(text.len());
    start..end
}

#[cfg(test)]
mod tests {
    use super::{char_range_to_byte_range, replace_selected_lines};

    fn chars_for(text: &str, needle: &str) -> std::ops::Range<usize> {
        let byte_start = text.find(needle).expect("selection text is present");
        let char_start = text[..byte_start].chars().count();
        char_start..char_start + needle.chars().count()
    }

    #[test]
    fn selected_unicode_columns_replace_only_their_line() {
        let mut result = "앞 줄\nkeep me\n끝 줄\n".to_owned();
        let selection = chars_for(&result, "eep");
        let byte_range = char_range_to_byte_range(&result, selection.clone());
        assert_eq!(&result[byte_range], "eep");

        replace_selected_lines(&mut result, "내 줄\n바꾼 내용\n다른 줄\n", selection);

        assert_eq!(result, "앞 줄\n바꾼 내용\n끝 줄\n");
    }

    #[test]
    fn selection_ending_at_next_line_boundary_does_not_replace_that_line() {
        let mut result = "one\ntwo\nthree\n".to_owned();
        replace_selected_lines(&mut result, "ONE\nTWO\nTHREE\n", 4..8);

        assert_eq!(result, "one\nTWO\nthree\n");
    }

    #[test]
    fn last_line_without_final_newline_preserves_crlf_and_eof() {
        let mut result = "첫 줄\r\n기존 내용".to_owned();
        let selection = chars_for(&result, "기존");

        replace_selected_lines(&mut result, "로컬 첫 줄\n바뀐 내용\n", selection);

        assert_eq!(result, "첫 줄\r\n바뀐 내용");
        assert!(!result.ends_with('\n'));
    }
}
