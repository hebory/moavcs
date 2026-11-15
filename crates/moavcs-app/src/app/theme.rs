use eframe::egui::{self, Color32, FontId, Response, RichText, Stroke, Ui};

pub(super) const BG: Color32 = Color32::from_rgb(243, 245, 249);
pub(super) const SURFACE: Color32 = Color32::WHITE;
pub(super) const BORDER: Color32 = Color32::from_rgb(223, 229, 238);
pub(super) const INK: Color32 = Color32::from_rgb(27, 40, 59);
pub(super) const MUTED: Color32 = Color32::from_rgb(112, 126, 146);
pub(super) const ACCENT: Color32 = Color32::from_rgb(215, 79, 47);
pub(super) const BLUE: Color32 = Color32::from_rgb(54, 104, 185);
pub(super) const GREEN: Color32 = Color32::from_rgb(38, 133, 100);
pub(super) const RED: Color32 = Color32::from_rgb(190, 65, 73);
pub(super) const NAVY: Color32 = Color32::from_rgb(24, 35, 52);

pub(super) fn observe_window(ctx: &egui::Context, name: &str, rect: egui::Rect) {
    let key = egui::Id::new("smoke_window_bounds");
    ctx.data_mut(|data| {
        if let Some(mut bounds) =
            data.get_temp::<std::collections::BTreeMap<String, egui::Rect>>(key)
        {
            bounds.insert(name.to_owned(), rect);
            data.insert_temp(key, bounds);
        }
    });
}
pub(super) fn observed_windows(
    ctx: &egui::Context,
) -> std::collections::BTreeMap<String, egui::Rect> {
    ctx.data(|data| data.get_temp(egui::Id::new("smoke_window_bounds")))
        .unwrap_or_default()
}
pub(super) fn reset_observed_windows(ctx: &egui::Context) {
    ctx.data_mut(|data| {
        data.insert_temp(
            egui::Id::new("smoke_window_bounds"),
            std::collections::BTreeMap::<String, egui::Rect>::new(),
        )
    });
}

pub(super) fn surface() -> egui::Frame {
    egui::Frame::none()
        .fill(SURFACE)
        .stroke(Stroke::new(1.0_f32, BORDER))
        .rounding(10.0)
        .inner_margin(14.0)
}

pub(super) fn primary(ui: &mut Ui, label: &str) -> Response {
    ui.add(
        egui::Button::new(RichText::new(label).color(Color32::WHITE).strong())
            .fill(ACCENT)
            .stroke(Stroke::NONE)
            .min_size(egui::vec2(88.0, 34.0)),
    )
}

pub(super) fn pill(ui: &mut Ui, text: &str, color: Color32) -> Response {
    egui::Frame::none()
        .fill(color.gamma_multiply(0.10))
        .rounding(5.0)
        .inner_margin(egui::vec2(8.0, 4.0))
        .show(ui, |ui| {
            ui.label(RichText::new(text).color(color).size(11.5).strong())
        })
        .response
}

pub(super) fn section(ui: &mut Ui, title: &str, subtitle: &str) {
    ui.label(RichText::new(title).size(16.0).strong().color(INK));
    if !subtitle.is_empty() {
        ui.label(RichText::new(subtitle).size(12.0).color(MUTED));
    }
    ui.add_space(8.0);
}

pub(super) fn window_size(ctx: &egui::Context, size: [f32; 2]) -> egui::Vec2 {
    let available = ctx.screen_rect().size() - egui::vec2(40.0, 76.0);
    egui::vec2(
        size[0].min(available.x).max(320.0),
        size[1].min(available.y).max(280.0),
    )
}

#[derive(Clone, Copy)]
pub(super) enum Icon {
    Folder,
    Branch,
    Changes,
    History,
    Settings,
    Refresh,
    File,
    Diff,
    Check,
    Pin,
    Close,
}

pub(super) fn draw_icon(painter: &egui::Painter, rect: egui::Rect, icon: Icon, color: Color32) {
    let r = rect.shrink(2.5);
    let p = |x: f32, y: f32| egui::pos2(r.left() + r.width() * x, r.top() + r.height() * y);
    let stroke = Stroke::new(1.45_f32, color);
    let line = |a: (f32, f32), b: (f32, f32)| {
        painter.line_segment([p(a.0, a.1), p(b.0, b.1)], stroke);
    };
    match icon {
        Icon::Folder => {
            painter.add(egui::Shape::closed_line(
                vec![
                    p(0., 0.22),
                    p(0.4, 0.22),
                    p(0.52, 0.38),
                    p(1., 0.38),
                    p(1., 0.9),
                    p(0., 0.9),
                ],
                stroke,
            ));
        }
        Icon::Branch => {
            line((0.25, 0.18), (0.25, 0.85));
            line((0.25, 0.6), (0.77, 0.4));
            line((0.77, 0.4), (0.77, 0.17));
            for (x, y) in [(0.25, 0.15), (0.25, 0.85), (0.77, 0.15)] {
                painter.circle(p(x, y), 2.2, SURFACE, stroke);
            }
        }
        Icon::Changes | Icon::File => {
            painter.rect_stroke(
                egui::Rect::from_min_max(p(0.16, 0.04), p(0.84, 0.96)),
                2.,
                stroke,
            );
            for y in [0.34, 0.55, 0.75] {
                line((0.31, y), (0.69, y));
            }
        }
        Icon::History | Icon::Refresh => {
            painter.circle_stroke(r.center(), r.width() * 0.42, stroke);
            if matches!(icon, Icon::History) {
                line((0.5, 0.23), (0.5, 0.52));
                line((0.5, 0.52), (0.72, 0.65));
            } else {
                line((0., 0.1), (0., 0.42));
                line((0., 0.42), (0.28, 0.42));
            }
        }
        Icon::Settings => {
            painter.circle_stroke(r.center(), r.width() * 0.22, stroke);
            for i in 0..8 {
                let a = i as f32 * std::f32::consts::TAU / 8.;
                let d = egui::vec2(a.cos(), a.sin());
                painter.line_segment(
                    [
                        r.center() + d * r.width() * 0.34,
                        r.center() + d * r.width() * 0.48,
                    ],
                    stroke,
                );
            }
        }
        Icon::Diff => {
            for (a, b) in [(0.05, 0.43), (0.57, 0.95)] {
                painter.rect_stroke(egui::Rect::from_min_max(p(a, 0.1), p(b, 0.9)), 1., stroke);
            }
        }
        Icon::Check => {
            line((0.12, 0.52), (0.4, 0.8));
            line((0.4, 0.8), (0.93, 0.19));
        }
        Icon::Pin => {
            line((0.3, 0.1), (0.75, 0.1));
            line((0.4, 0.1), (0.4, 0.49));
            line((0.65, 0.1), (0.65, 0.49));
            line((0.2, 0.6), (0.85, 0.6));
            line((0.52, 0.6), (0.52, 0.97));
        }
        Icon::Close => {
            line((0.2, 0.2), (0.8, 0.8));
            line((0.2, 0.8), (0.8, 0.2));
        }
    }
}

pub(super) fn icon_button(ui: &mut Ui, icon: Icon, tooltip: &str) -> Response {
    let (rect, response) = ui.allocate_exact_size(egui::vec2(30., 30.), egui::Sense::click());
    if response.hovered() {
        ui.painter()
            .rect_filled(rect, 6., BORDER.gamma_multiply(0.55));
    }
    draw_icon(
        ui.painter(),
        rect.shrink(5.),
        icon,
        if ui.is_enabled() {
            MUTED
        } else {
            MUTED.gamma_multiply(0.4)
        },
    );
    response.on_hover_text(tooltip)
}

pub(super) fn code_view(ui: &mut Ui, id: &str, text: &str, height: f32) {
    let all_lines: Vec<_> = text.lines().collect();
    // File identity and revision metadata already sit above the preview. Start
    // a patch at its first hunk so short viewports show the changed code.
    let hunk = if text.starts_with("diff --git ") {
        all_lines
            .iter()
            .position(|line| line.starts_with("@@"))
            .unwrap_or(0)
    } else {
        0
    };
    let lines = &all_lines[hunk..];
    let height = height.max(40.0);
    let width = ui.available_width();
    let longest = lines
        .iter()
        .map(|s| s.chars().count())
        .max()
        .unwrap_or(0)
        .min(4096) as f32;
    let row_width = (longest * 8.2 + 76.).max(width);
    ScrollAreaExt::render(ui, id, lines, height, row_width);
}
struct ScrollAreaExt;
impl ScrollAreaExt {
    fn render(ui: &mut Ui, id: &str, lines: &[&str], height: f32, width: f32) {
        ui.scope(|ui| {
            ui.spacing_mut().item_spacing.y = 0.;
            egui::ScrollArea::both()
                .id_salt(id)
                .auto_shrink([false, false])
                .max_height(height)
                .min_scrolled_height(height)
                .show_rows(ui, 21.0, lines.len(), |ui, range| {
                    for i in range {
                        let line = lines[i];
                        let (fg, bg) = if line.starts_with('+') && !line.starts_with("+++") {
                            (GREEN, Color32::from_rgb(234, 246, 238))
                        } else if line.starts_with('-') && !line.starts_with("---") {
                            (RED, Color32::from_rgb(253, 237, 236))
                        } else if line.starts_with("@@") {
                            (BLUE, Color32::from_rgb(235, 241, 251))
                        } else {
                            (INK, Color32::TRANSPARENT)
                        };
                        let (r, _) =
                            ui.allocate_exact_size(egui::vec2(width, 21.), egui::Sense::hover());
                        ui.painter().rect_filled(r, 0., bg);
                        ui.painter().rect_filled(
                            egui::Rect::from_min_max(r.min, egui::pos2(r.left() + 45., r.bottom())),
                            0.,
                            Color32::from_rgb(247, 249, 252),
                        );
                        ui.painter().text(
                            egui::pos2(r.left() + 35., r.center().y),
                            egui::Align2::RIGHT_CENTER,
                            (i + 1).to_string(),
                            FontId::monospace(11.5),
                            MUTED,
                        );
                        ui.painter().text(
                            egui::pos2(r.left() + 57., r.center().y),
                            egui::Align2::LEFT_CENTER,
                            line,
                            FontId::monospace(12.5),
                            fg,
                        );
                    }
                });
        });
    }
}

pub fn configure_visuals(ctx: &egui::Context) {
    let mut style = (*ctx.style()).clone();
    style.visuals = egui::Visuals::light();
    let v = &mut style.visuals;
    v.panel_fill = BG;
    v.window_fill = SURFACE;
    v.extreme_bg_color = Color32::from_rgb(248, 250, 253);
    v.faint_bg_color = Color32::from_rgb(246, 248, 252);
    v.override_text_color = Some(INK);
    v.window_rounding = 10.0.into();
    v.window_stroke = Stroke::new(1.0_f32, BORDER);
    v.window_shadow.color = Color32::from_black_alpha(30);
    v.selection.bg_fill = Color32::from_rgb(230, 238, 251);
    v.selection.stroke = Stroke::new(1.0_f32, BLUE);
    v.widgets.noninteractive.bg_stroke = Stroke::new(1.0_f32, BORDER);
    v.widgets.noninteractive.fg_stroke = Stroke::new(1.0_f32, INK);
    v.widgets.noninteractive.bg_fill = SURFACE;
    v.widgets.noninteractive.weak_bg_fill = SURFACE;
    v.widgets.open.bg_fill = Color32::from_rgb(247, 249, 252);
    v.widgets.open.weak_bg_fill = Color32::from_rgb(247, 249, 252);
    v.widgets.open.fg_stroke = Stroke::new(1.0_f32, INK);
    v.widgets.inactive.bg_fill = SURFACE;
    v.widgets.inactive.weak_bg_fill = SURFACE;
    v.widgets.inactive.bg_stroke = Stroke::new(1.0_f32, BORDER);
    v.widgets.inactive.fg_stroke = Stroke::new(1.0_f32, INK);
    v.widgets.hovered.bg_fill = Color32::from_rgb(238, 243, 251);
    v.widgets.hovered.weak_bg_fill = Color32::from_rgb(238, 243, 251);
    v.widgets.hovered.bg_stroke = Stroke::new(1.0_f32, Color32::from_rgb(179, 196, 221));
    v.widgets.hovered.fg_stroke = Stroke::new(1.0_f32, INK);
    v.widgets.active.bg_fill = Color32::from_rgb(228, 236, 249);
    v.widgets.active.weak_bg_fill = v.widgets.active.bg_fill;
    v.widgets.active.bg_stroke = Stroke::new(1.0_f32, BLUE);
    v.widgets.active.fg_stroke = Stroke::new(1.0_f32, INK);
    for widgets in [
        &mut v.widgets.inactive,
        &mut v.widgets.hovered,
        &mut v.widgets.active,
        &mut v.widgets.noninteractive,
    ] {
        widgets.rounding = 4.0.into();
        widgets.expansion = 0.;
    }
    style.spacing.item_spacing = egui::vec2(8., 8.);
    style.spacing.button_padding = egui::vec2(11., 7.);
    style.spacing.interact_size = egui::vec2(32., 30.);
    style.spacing.text_edit_width = 240.;
    style.visuals.text_cursor.stroke = Stroke::new(1.5_f32, BLUE);
    style
        .text_styles
        .insert(egui::TextStyle::Body, FontId::proportional(13.5));
    style
        .text_styles
        .insert(egui::TextStyle::Button, FontId::proportional(13.0));
    style
        .text_styles
        .insert(egui::TextStyle::Small, FontId::proportional(11.5));
    style
        .text_styles
        .insert(egui::TextStyle::Heading, FontId::proportional(18.));
    style
        .text_styles
        .insert(egui::TextStyle::Monospace, FontId::monospace(12.5));
    ctx.set_style(style);
    #[cfg(windows)]
    {
        let fonts_dir = std::path::PathBuf::from(
            std::env::var_os("WINDIR").unwrap_or_else(|| "C:\\Windows".into()),
        )
        .join("Fonts");
        let mut fonts = egui::FontDefinitions::default();
        if let Ok(bytes) = std::fs::read(fonts_dir.join("segoeui.ttf")) {
            fonts
                .font_data
                .insert("moa_ui".into(), egui::FontData::from_owned(bytes));
            fonts
                .families
                .entry(egui::FontFamily::Proportional)
                .or_default()
                .insert(0, "moa_ui".into());
        }
        if let Ok(bytes) = std::fs::read(fonts_dir.join("malgun.ttf")) {
            fonts
                .font_data
                .insert("moa_korean".into(), egui::FontData::from_owned(bytes));
            for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
                fonts
                    .families
                    .entry(family)
                    .or_default()
                    .push("moa_korean".into());
            }
        }
        ctx.set_fonts(fonts);
    }
}
