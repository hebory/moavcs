#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod history_graph;
mod launch;
mod platform;
mod smoke;

use app::MoaApp;
use launch::LaunchOptions;
use std::sync::mpsc;

fn main() -> eframe::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--version") {
        println!("MoaVCS {}", env!("CARGO_PKG_VERSION"));
        return Ok(());
    }
    let startup = LaunchOptions::parse(args).unwrap_or_else(|error| {
        eprintln!("MoaVCS: {error}");
        write_setup_error(&error.to_string());
        std::process::exit(2);
    });
    if let Some(output) = startup.ui_smoke.clone() {
        return smoke::run(output);
    }
    match platform::handle_startup(&startup) {
        Ok(true) => return Ok(()),
        Ok(false) => {}
        Err(error) => {
            eprintln!("MoaVCS shell integration: {error}");
            write_setup_error(&error.to_string());
            std::process::exit(3);
        }
    }
    if let Ok(settings) = moavcs_core::settings::SettingsStore::default().load() {
        let _ = platform::maybe_start_worker(&settings);
    }
    let (sender, receiver) = mpsc::channel();
    let app = MoaApp::new(startup, receiver, sender);
    let mut options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_inner_size([1370.0, 900.0])
            .with_min_inner_size([860.0, 620.0])
            .with_title("MoaVCS")
            .with_icon(app_icon()),
        ..Default::default()
    };
    configure_graphics(&mut options);
    eframe::run_native(
        "MoaVCS",
        options,
        Box::new(|creation| {
            app::configure_visuals(&creation.egui_ctx);
            Ok(Box::new(app))
        }),
    )
}

pub(crate) fn configure_graphics(_options: &mut eframe::NativeOptions) {
    #[cfg(windows)]
    {
        // Windows machines without a Vulkan driver can use DX12/WARP. Keep
        // the standard WGPU_BACKEND override available for diagnostics.
        _options.wgpu_options.supported_backends = wgpu::util::backend_bits_from_env()
            .unwrap_or(wgpu::Backends::DX12 | wgpu::Backends::VULKAN);
    }
}

fn app_icon() -> std::sync::Arc<eframe::egui::IconData> {
    let image = image::load_from_memory(include_bytes!("../../../icon.png"))
        .expect("embedded MoaVCS icon is a valid image")
        .to_rgba8();
    let (width, height) = image.dimensions();
    std::sync::Arc::new(eframe::egui::IconData {
        rgba: image.into_raw(),
        width,
        height,
    })
}

fn write_setup_error(message: &str) {
    let Some(local) = std::env::var_os("LOCALAPPDATA") else {
        return;
    };
    let directory = std::path::PathBuf::from(local).join("MoaVcs");
    if std::fs::create_dir_all(&directory).is_ok() {
        let path = directory.join("setup-error.log");
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
        {
            use std::io::Write;
            let _ = writeln!(file, "{message}");
        }
    }
}
