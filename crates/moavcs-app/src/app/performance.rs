use super::*;
use std::{
    fs,
    hint::black_box,
    process::Command,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const LINES: usize = 10_000;
const FILES: usize = 2_000;
const SAMPLES: usize = 30;

fn fixture() -> PathBuf {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let root = std::env::temp_dir().join(format!("moavcs-render-{}-{nonce}", std::process::id()));
    fs::create_dir(&root).unwrap();
    let git = |args: &[&str]| {
        let output = Command::new("git")
            .current_dir(&root)
            .env("GIT_CONFIG_NOSYSTEM", "1")
            .env("GIT_CONFIG_GLOBAL", root.join("empty-config"))
            .args(args)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
    };
    git(&["init", "-q"]);
    let text = |modified: bool| {
        (0..LINES)
            .map(|i| {
                let tag = if modified && i % 100 == 0 {
                    "changed"
                } else {
                    "tracked"
                };
                format!("source line {i:05} {tag}\n")
            })
            .collect::<String>()
    };
    fs::write(root.join("source.txt"), text(false)).unwrap();
    git(&["add", "source.txt"]);
    git(&[
        "-c",
        "user.name=Benchmark",
        "-c",
        "user.email=bench@example.invalid",
        "commit",
        "-qm",
        "fixture",
    ]);
    fs::write(root.join("source.txt"), text(true)).unwrap();
    root
}

fn measure_panel(label: &str, ctx: &egui::Context, mut panel: impl FnMut(&egui::Context)) {
    let mut samples = Vec::with_capacity(SAMPLES);
    let mut visible_files = 0;
    for frame in 0..SAMPLES + 4 {
        let input = egui::RawInput {
            screen_rect: Some(egui::Rect::from_min_size(
                egui::Pos2::ZERO,
                egui::vec2(1370., 900.),
            )),
            ..Default::default()
        };
        let start = Instant::now();
        let output = ctx.run(input, |ctx| panel(ctx));
        if frame == 3 {
            visible_files = output.shapes.iter().filter(|s| {
                matches!(&s.shape, egui::Shape::Text(t) if t.galley.text().starts_with("file_") && s.clip_rect.intersects(t.visual_bounding_rect()))
            }).count();
        }
        black_box(ctx.tessellate(output.shapes, output.pixels_per_point));
        let elapsed = start.elapsed();
        if frame >= 4 {
            samples.push(elapsed);
        }
    }
    samples.sort_unstable();
    let ms = |d: Duration| d.as_secs_f64() * 1000.;
    println!("PERF {label} samples={SAMPLES} median_ms={:.3} p95_ms={:.3} visible_file_labels={visible_files}",
        (ms(samples[SAMPLES / 2 - 1]) + ms(samples[SAMPLES / 2])) / 2.,
        ms(samples[(SAMPLES * 95).div_ceil(100) - 1]));
}

/// Opt-in wall-clock benchmark. Run with --release --ignored --nocapture --test-threads=1.
#[test]
#[ignore = "local performance measurement using a temporary Git repository"]
fn benchmark_desktop_views() {
    let root = fixture();
    let (tx, rx) = std::sync::mpsc::channel();
    let mut app = MoaApp::new(
        LaunchOptions {
            ui_smoke: Some(root.clone()),
            ..Default::default()
        },
        rx,
        tx,
    );
    let provider = create_provider(VcsKind::Git, "git");
    app.repo = Some(provider.repository(&root, &CancelToken::new()).unwrap());
    app.provider = Some(provider);
    app.file = Some("source.txt".into());
    let ctx = egui::Context::default();
    configure_visuals(&ctx);
    ctx.style_mut(|style| style.animation_time = 0.);
    for (label, blame) in [("diff_10k", false), ("blame_10k", true)] {
        app.inspection = None;
        let start = Instant::now();
        app.inspect_internal(blame);
        while app.inspection.is_none() {
            app.events(&ctx);
            assert!(app.notice.is_none(), "{:?}", app.notice);
            assert!(
                start.elapsed() < Duration::from_secs(30),
                "inspection worker timeout"
            );
            std::thread::sleep(Duration::from_millis(1));
        }
        println!(
            "PERF {label} worker_ready_ms={:.3}",
            start.elapsed().as_secs_f64() * 1000.
        );
        measure_panel(label, &ctx, |ctx| app.inspection_ui(ctx));
    }
    app.inspection = None;
    app.file = None;
    app.changes = (0..FILES)
        .map(|i| FileChange {
            path: format!("src/module_{}/file_{i:04}.rs", i % 50),
            status: "modified".into(),
            worktree_status: "M".into(),
            ..Default::default()
        })
        .collect();
    measure_panel("changes_2k", &ctx, |ctx| {
        app.top(ctx);
        app.sidebar(ctx);
        app.body(ctx);
    });
    fs::remove_dir_all(root).unwrap();
}
