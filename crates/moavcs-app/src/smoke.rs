use crate::{
    app::{self, MoaApp},
    launch::LaunchOptions,
};
use std::{
    fs,
    path::{Path, PathBuf},
    process::{Command, Output},
    sync::mpsc,
    time::{SystemTime, UNIX_EPOCH},
};

pub fn run(output: PathBuf) -> eframe::Result {
    if let Err(error) = fs::create_dir_all(&output) {
        fail(&output, &format!("UI smoke output directory: {error}"));
    }
    // Canonicalization adds the Windows `\\?\` verbatim prefix, which older
    // Git for Windows rejects for `git init --bare` paths.
    let output = std::path::absolute(&output)
        .unwrap_or_else(|error| fail(&output, &format!("resolve smoke output path: {error}")));
    let (watchdog_done, watchdog_rx) = mpsc::channel::<()>();
    let watchdog_output = output.clone();
    std::thread::spawn(move || {
        if matches!(
            watchdog_rx.recv_timeout(std::time::Duration::from_secs(180)),
            Err(mpsc::RecvTimeoutError::Timeout)
        ) {
            fail(
                &watchdog_output,
                "UI smoke exceeded its 180 second deadline",
            );
        }
    });
    let fixture = match create_fixture(&output) {
        Ok(path) => path,
        Err(error) => fail(&output, &format!("Git fixture setup failed: {error}")),
    };
    let (sender, receiver) = mpsc::channel();
    let app = MoaApp::new(
        LaunchOptions {
            path: Some(fixture.join("workspace")),
            ui_smoke: Some(output.clone()),
            ..LaunchOptions::default()
        },
        receiver,
        sender,
    );
    let mut options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_inner_size([1370.0, 900.0])
            .with_min_inner_size([860.0, 620.0])
            .with_title("MoaVCS UI Smoke"),
        ..Default::default()
    };
    crate::configure_graphics(&mut options);
    let global_git_config = output.join("empty-global-gitconfig");
    if let Err(error) = fs::write(&global_git_config, b"") {
        fail(&output, &format!("isolated git config: {error}"));
    }
    let isolated_local_app_data = output.join("localappdata");
    if let Err(error) = fs::create_dir_all(&isolated_local_app_data) {
        fail(
            &output,
            &format!("isolated LOCALAPPDATA directory: {error}"),
        );
    }
    let previous_global_git_config = std::env::var_os("GIT_CONFIG_GLOBAL");
    let previous_git_config_nosystem = std::env::var_os("GIT_CONFIG_NOSYSTEM");
    let previous_local_app_data = std::env::var_os("LOCALAPPDATA");
    std::env::set_var("GIT_CONFIG_GLOBAL", &global_git_config);
    std::env::set_var("GIT_CONFIG_NOSYSTEM", "1");
    std::env::set_var("LOCALAPPDATA", &isolated_local_app_data);
    let renderer_output = output.clone();
    let result = eframe::run_native(
        "MoaVCS UI Smoke",
        options,
        Box::new(|creation| {
            if let Some(state) = &creation.wgpu_render_state {
                let info = state.adapter.get_info();
                println!("UI renderer: {:?} ({:?})", info.backend, info.device_type);
                let renderer = serde_json::json!({
                    "backend": format!("{:?}", info.backend),
                    "device_type": format!("{:?}", info.device_type),
                });
                if let Err(error) = fs::write(
                    renderer_output.join("renderer.json"),
                    serde_json::to_vec_pretty(&renderer).unwrap_or_default(),
                ) {
                    fail(&renderer_output, &format!("renderer metadata: {error}"));
                }
            }
            app::configure_visuals(&creation.egui_ctx);
            creation
                .egui_ctx
                .style_mut(|style| style.animation_time = 0.0);
            Ok(Box::new(app))
        }),
    );
    if let Some(value) = previous_global_git_config {
        std::env::set_var("GIT_CONFIG_GLOBAL", value);
    } else {
        std::env::remove_var("GIT_CONFIG_GLOBAL");
    }
    if let Some(value) = previous_git_config_nosystem {
        std::env::set_var("GIT_CONFIG_NOSYSTEM", value);
    } else {
        std::env::remove_var("GIT_CONFIG_NOSYSTEM");
    }
    if let Some(value) = previous_local_app_data {
        std::env::set_var("LOCALAPPDATA", value);
    } else {
        std::env::remove_var("LOCALAPPDATA");
    }
    let _ = watchdog_done.send(());
    result?;
    for file in [
        "workspace.png",
        "history.png",
        "diff.png",
        "blame.png",
        "settings.png",
        "min-window.png",
        "history-min.png",
        "settings-min.png",
        "settings-vcs.png",
        "settings-tools.png",
        "diff-min.png",
        "checkout.png",
        "remote.png",
        "conflict.png",
        "long-labels-min.png",
        "workspace.assertions.json",
        "history.assertions.json",
        "diff.assertions.json",
        "blame.assertions.json",
        "settings.assertions.json",
        "min-window.assertions.json",
        "assertions.json",
        "complete",
    ] {
        let path = output.join(file);
        if !path.is_file() || fs::metadata(&path).map(|m| m.len() == 0).unwrap_or(true) {
            fail(&output, &format!("UI smoke did not create {file}"));
        }
    }
    Ok(())
}

fn create_fixture(output: &Path) -> Result<PathBuf, String> {
    fs::create_dir_all(output).map_err(|error| error.to_string())?;
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| error.to_string())?
        .as_nanos();
    let fixture = output.join(format!("fixture-{}-{nonce}", std::process::id()));
    let workspace = fixture.join("workspace");
    let origin = fixture.join("origin.git");
    fs::create_dir_all(&workspace).map_err(|error| error.to_string())?;
    git(
        &fixture,
        &fixture,
        [
            "init",
            "--bare",
            "--initial-branch=main",
            origin.to_string_lossy().as_ref(),
        ],
    )?;
    git(&fixture, &workspace, ["init", "--initial-branch=main"])?;
    git(
        &fixture,
        &workspace,
        ["config", "user.name", "MoaVCS Smoke"],
    )?;
    git(
        &fixture,
        &workspace,
        ["config", "user.email", "moavcs-smoke@example.invalid"],
    )?;
    fs::write(workspace.join("tracked.txt"), "base line\nsecond line\n")
        .map_err(|error| error.to_string())?;
    git(&fixture, &workspace, ["add", "tracked.txt"])?;
    git(&fixture, &workspace, ["commit", "-m", "기준 파일 추가"])?;
    git(
        &fixture,
        &workspace,
        ["remote", "add", "origin", origin.to_string_lossy().as_ref()],
    )?;
    git(
        &fixture,
        &workspace,
        ["push", "--set-upstream", "origin", "main"],
    )?;
    fs::write(workspace.join("tracked.txt"), "UI 변경 내용\nsecond line\n")
        .map_err(|error| error.to_string())?;
    Ok(fixture)
}

pub fn prepare_conflict(workspace: &Path) -> Result<(), String> {
    let fixture = workspace
        .parent()
        .ok_or_else(|| "workspace parent is missing".to_owned())?;
    let origin = git(fixture, workspace, ["remote", "get-url", "origin"])?;
    let origin = String::from_utf8_lossy(&origin.stdout).trim().to_owned();
    if origin.is_empty() {
        return Err("origin URL is empty".into());
    }
    let peer = fixture.join("peer");
    git(
        fixture,
        fixture,
        [
            "clone",
            "--branch",
            "main",
            origin.as_str(),
            peer.to_string_lossy().as_ref(),
        ],
    )?;
    git(fixture, &peer, ["config", "user.name", "MoaVCS Smoke Peer"])?;
    git(
        fixture,
        &peer,
        ["config", "user.email", "moavcs-smoke-peer@example.invalid"],
    )?;
    fs::write(peer.join("tracked.txt"), "REMOTE conflict\nsecond line\n")
        .map_err(|error| error.to_string())?;
    git(fixture, &peer, ["add", "tracked.txt"])?;
    git(fixture, &peer, ["commit", "-m", "Remote conflict side"])?;
    git(fixture, &peer, ["push", "origin", "main"])?;

    fs::write(
        workspace.join("tracked.txt"),
        "LOCAL conflict\nsecond line\n",
    )
    .map_err(|error| error.to_string())?;
    git(fixture, workspace, ["add", "tracked.txt"])?;
    git(fixture, workspace, ["commit", "-m", "Local conflict side"])?;
    let merge = git_raw(
        fixture,
        workspace,
        ["pull", "--no-rebase", "--no-edit", "origin", "main"],
    )?;
    if merge.status.success() {
        return Err("merge completed without producing the expected content conflict".into());
    }
    let status = git(fixture, workspace, ["status", "--porcelain=v1"])?;
    let status = String::from_utf8_lossy(&status.stdout);
    if !status
        .lines()
        .any(|line| line.ends_with("tracked.txt") && line.starts_with("UU"))
    {
        return Err(format!(
            "Git did not report an unresolved text conflict: {status}"
        ));
    }
    Ok(())
}

fn git<I, S>(fixture: &Path, cwd: &Path, args: I) -> Result<Output, String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<std::ffi::OsStr>,
{
    let output = git_raw(fixture, cwd, args)?;
    if !output.status.success() {
        return Err(format!(
            "git command failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    Ok(output)
}

fn git_raw<I, S>(fixture: &Path, cwd: &Path, args: I) -> Result<Output, String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<std::ffi::OsStr>,
{
    let args = args
        .into_iter()
        .map(|arg| arg.as_ref().to_owned())
        .collect::<Vec<std::ffi::OsString>>();
    let mut command = Command::new("git");
    command
        .current_dir(cwd)
        .args(&args)
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GIT_CONFIG_NOSYSTEM", "1")
        .env("GIT_CONFIG_GLOBAL", fixture.join("empty-global-gitconfig"));
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000);
    }
    command.output().map_err(|error| format!("git: {error}"))
}

fn fail(output: &Path, message: &str) -> ! {
    let _ = fs::create_dir_all(output);
    let _ = fs::write(output.join("failure.txt"), message);
    eprintln!("MoaVCS UI smoke failed: {message}");
    std::process::exit(5)
}
