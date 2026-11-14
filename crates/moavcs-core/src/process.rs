use crate::models::*;
use std::{
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

#[derive(Clone, Default, Debug)]
pub struct CancelToken(Arc<AtomicBool>);
impl CancelToken {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }
    pub fn cancelled(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
    pub fn check(&self) -> Result<()> {
        if self.cancelled() {
            Err(VcsError("작업이 취소되었습니다.".into()))
        } else {
            Ok(())
        }
    }
}
#[derive(Debug, Clone)]
pub struct CommandSpec {
    pub executable: String,
    pub arguments: Vec<String>,
    pub working_directory: PathBuf,
    pub standard_input: Option<String>,
    pub timeout: Duration,
}
impl CommandSpec {
    pub fn new(
        executable: impl Into<String>,
        root: impl Into<PathBuf>,
        args: impl IntoIterator<Item = impl AsRef<str>>,
    ) -> Self {
        Self {
            executable: executable.into(),
            working_directory: root.into(),
            arguments: args.into_iter().map(|s| s.as_ref().to_owned()).collect(),
            standard_input: None,
            timeout: Duration::from_secs(300),
        }
    }
}
pub trait CommandRunner: Send + Sync {
    fn run(&self, command: &CommandSpec, cancel: &CancelToken) -> Result<CommandResult>;
}
pub struct ProcessRunner;

pub const MAX_OUTPUT_BYTES: usize = 4 * 1024 * 1024;
impl CommandRunner for ProcessRunner {
    fn run(&self, spec: &CommandSpec, cancel: &CancelToken) -> Result<CommandResult> {
        use std::{
            io::{Read, Write},
            process::{Command, Stdio},
            sync::mpsc,
            thread,
            time::Instant,
        };
        cancel.check()?;
        if !spec.working_directory.is_dir() {
            return Err(VcsError(format!(
                "작업 폴더를 찾을 수 없습니다: {}",
                spec.working_directory.display()
            )));
        }
        if spec.executable.trim().is_empty() {
            return Err("실행 파일을 지정하세요.".into());
        }
        let mut command = Command::new(&spec.executable);
        command
            .args(&spec.arguments)
            .current_dir(&spec.working_directory)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .env("GIT_TERMINAL_PROMPT", "0")
            .env("GCM_INTERACTIVE", "never");
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        let mut child = command.spawn().map_err(|e| {
            VcsError(format!(
                "'{}' 실행에 실패했습니다. CLI 설치 경로를 확인하세요.\n{e}",
                spec.executable
            ))
        })?;
        #[cfg(windows)]
        let mut job = ProcessJob::attach(&child);
        let overflow = Arc::new(AtomicBool::new(false));
        let (tx, rx) = mpsc::channel();
        for (index, pipe) in [
            (
                0,
                Box::new(child.stdout.take().unwrap()) as Box<dyn Read + Send>,
            ),
            (
                1,
                Box::new(child.stderr.take().unwrap()) as Box<dyn Read + Send>,
            ),
        ] {
            let tx = tx.clone();
            let overflow = overflow.clone();
            thread::spawn(move || {
                let mut pipe = pipe;
                let mut output = Vec::new();
                let mut buffer = [0u8; 8192];
                let result: std::io::Result<()> = (|| {
                    loop {
                        let count = pipe.read(&mut buffer)?;
                        if count == 0 {
                            break;
                        }
                        let remaining = MAX_OUTPUT_BYTES.saturating_sub(output.len());
                        output.extend_from_slice(&buffer[..count.min(remaining)]);
                        if count > remaining {
                            overflow.store(true, Ordering::Release);
                            break;
                        }
                    }
                    Ok(())
                })();
                let _ = tx.send((
                    index,
                    result.map(|_| String::from_utf8_lossy(&output).into_owned()),
                ));
            });
        }
        drop(tx);
        let input = spec.standard_input.clone().unwrap_or_default();
        let mut stdin = child.stdin.take().unwrap();
        let input_thread = thread::spawn(move || stdin.write_all(input.as_bytes()));
        let started = Instant::now();
        let mut failure = None;
        let exit = loop {
            if overflow.load(Ordering::Acquire) {
                failure = Some("명령 출력이 4 MiB 제한을 초과했습니다.".into());
                break None;
            }
            if cancel.cancelled() {
                failure = Some("작업이 취소되었습니다.".into());
                break None;
            }
            if started.elapsed() >= spec.timeout {
                failure = Some(format!(
                    "명령이 제한 시간 {}초를 초과했습니다.",
                    spec.timeout.as_secs()
                ));
                break None;
            }
            match child.try_wait() {
                Ok(Some(status)) => break Some(status),
                Ok(None) => thread::sleep(Duration::from_millis(20)),
                Err(e) => {
                    failure = Some(e.to_string());
                    break None;
                }
            }
        };
        if failure.is_some() {
            #[cfg(windows)]
            kill_tree(child.id());
            let _ = child.kill();
            let _ = child.wait();
            #[cfg(windows)]
            drop(job.take());
        }
        let mut outputs = [String::new(), String::new()];
        for _ in 0..2 {
            match rx.recv_timeout(Duration::from_secs(3)) {
                Ok((index, result)) => match result {
                    Ok(text) => outputs[index] = text,
                    Err(e) if failure.is_none() => failure = Some(e.to_string()),
                    _ => {}
                },
                Err(_) => {
                    if failure.is_none() {
                        failure = Some("명령 출력 스트림을 닫지 못했습니다.".into());
                    }
                    break;
                }
            }
        }
        if overflow.load(Ordering::Acquire) {
            failure = Some("명령 출력이 4 MiB 제한을 초과했습니다.".into());
        }
        if input_thread.is_finished() {
            let _ = input_thread.join();
        }
        #[cfg(windows)]
        if failure.is_none() {
            if let Some(job) = &mut job {
                job.disarm()?;
            }
        }
        if let Some(message) = failure {
            return Err(VcsError(message));
        }
        Ok(CommandResult {
            exit_code: exit.and_then(|s| s.code()).unwrap_or(-1),
            stdout: std::mem::take(&mut outputs[0]),
            stderr: std::mem::take(&mut outputs[1]),
        })
    }
}

#[cfg(windows)]
fn kill_tree(pid: u32) {
    use std::os::windows::process::CommandExt;
    let executable = std::env::var_os("WINDIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("C:\\Windows"))
        .join("System32/taskkill.exe");
    let _ = std::process::Command::new(executable)
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .creation_flags(0x08000000)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();
}
#[cfg(windows)]
struct ProcessJob(windows::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl ProcessJob {
    fn disarm(&mut self) -> Result<()> {
        use windows::Win32::System::JobObjects::*;
        let info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        unsafe {
            SetInformationJobObject(
                self.0,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const _,
                std::mem::size_of_val(&info) as u32,
            )
        }
        .map_err(|e| {
            VcsError(format!(
                "완료된 명령의 프로세스 그룹을 해제하지 못했습니다: {e}"
            ))
        })
    }
    fn attach(child: &std::process::Child) -> Option<Self> {
        use std::os::windows::io::AsRawHandle;
        use windows::Win32::{
            Foundation::{CloseHandle, HANDLE},
            System::JobObjects::*,
        };
        unsafe {
            let handle = CreateJobObjectW(None, None).ok()?;
            let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const _,
                std::mem::size_of_val(&info) as u32,
            )
            .is_err()
                || AssignProcessToJobObject(handle, HANDLE(child.as_raw_handle())).is_err()
            {
                let _ = CloseHandle(handle);
                None
            } else {
                Some(Self(handle))
            }
        }
    }
}
#[cfg(windows)]
impl Drop for ProcessJob {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::Foundation::CloseHandle(self.0);
        }
    }
}
