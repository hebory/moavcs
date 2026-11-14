use moavcs_core::{process::CommandSpec, CancelToken, CommandRunner, ProcessRunner};
use std::{
    env, thread,
    time::{Duration, Instant},
};
#[test]
#[ignore]
fn child_waits() {
    thread::sleep(Duration::from_secs(30));
}
#[test]
#[ignore]
fn child_floods() {
    use std::io::Write;
    let mut output = std::io::stdout().lock();
    let buffer = [b'x'; 8192];
    for _ in 0..1024 {
        if output.write_all(&buffer).is_err() {
            break;
        }
    }
}
#[test]
#[ignore]
fn child_background() {
    #[cfg(windows)]
    {
        use windows::{
            core::{PCWSTR, PWSTR},
            Win32::{Foundation::CloseHandle, System::Threading::*},
        };
        // A persistent helper must detach inherited command pipes as SSH daemons do.
        // Stdio::null alone can leave other inherited Windows pipe handles open.
        let exe = env::current_exe().unwrap();
        let application = exe
            .to_string_lossy()
            .encode_utf16()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let mut command = format!(
            "\"{}\" --exact child_waits --ignored --nocapture",
            exe.display()
        )
        .encode_utf16()
        .chain(Some(0))
        .collect::<Vec<_>>();
        let startup = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            ..Default::default()
        };
        let mut info = PROCESS_INFORMATION::default();
        unsafe {
            CreateProcessW(
                PCWSTR(application.as_ptr()),
                PWSTR(command.as_mut_ptr()),
                None,
                None,
                false,
                CREATE_NO_WINDOW,
                None,
                None,
                &startup,
                &mut info,
            )
            .unwrap();
            let _ = CloseHandle(info.hThread);
            let _ = CloseHandle(info.hProcess);
        }
        println!("helper-pid={}", info.dwProcessId);
    }
    #[cfg(not(windows))]
    {
        use std::process::{Command, Stdio};
        let child = Command::new(env::current_exe().unwrap())
            .args(["--exact", "child_waits", "--ignored", "--nocapture"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        println!("helper-pid={}", child.id());
    }
}
#[cfg(windows)]
#[test]
fn normal_exit_keeps_background_helper_alive() {
    use windows::Win32::{Foundation::CloseHandle, System::Threading::*};
    let output = ProcessRunner
        .run(&fixture("child_background"), &CancelToken::new())
        .unwrap()
        .ensure()
        .unwrap()
        .stdout;
    let pid: u32 = output
        .split_once("helper-pid=")
        .unwrap()
        .1
        .split_whitespace()
        .next()
        .unwrap()
        .parse()
        .unwrap();
    unsafe {
        let handle = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE,
            false,
            pid,
        )
        .unwrap();
        let mut code = 0;
        GetExitCodeProcess(handle, &mut code).unwrap();
        let _ = TerminateProcess(handle, 0);
        let _ = CloseHandle(handle);
        assert_eq!(
            code, 259,
            "successful VCS completion must not kill a persistent SSH/helper process"
        );
    }
}
fn fixture(name: &str) -> CommandSpec {
    CommandSpec::new(
        env::current_exe().unwrap().to_string_lossy(),
        env::current_dir().unwrap(),
        ["--exact", name, "--ignored", "--nocapture"],
    )
}
#[test]
fn cancellation_timeout_and_output_bound() {
    let runner = ProcessRunner;
    let mut command = fixture("child_waits");
    command.timeout = Duration::from_millis(300);
    let started = Instant::now();
    assert!(runner
        .run(&command, &CancelToken::new())
        .unwrap_err()
        .to_string()
        .contains("제한 시간"));
    assert!(started.elapsed() < Duration::from_secs(6));
    let token = CancelToken::new();
    let stop = token.clone();
    thread::spawn(move || {
        thread::sleep(Duration::from_millis(150));
        stop.cancel();
    });
    assert!(runner
        .run(&fixture("child_waits"), &token)
        .unwrap_err()
        .to_string()
        .contains("취소"));
    assert!(runner
        .run(&fixture("child_floods"), &CancelToken::new())
        .unwrap_err()
        .to_string()
        .contains("4 MiB"));
}
#[test]
fn literal_unicode_arguments() {
    let command = CommandSpec::new(
        "git",
        env::current_dir().unwrap(),
        [
            "-c",
            "moavcs.test=한글 경로; $literal `text`",
            "config",
            "--get",
            "moavcs.test",
        ],
    );
    let result = ProcessRunner
        .run(&command, &CancelToken::new())
        .unwrap()
        .ensure()
        .unwrap();
    assert_eq!(result.stdout.trim(), "한글 경로; $literal `text`");
}
