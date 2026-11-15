# Rust 로컬 개발

기본 앱은 Cargo workspace로 빌드합니다. GUI는 egui/eframe, VCS 처리는 `moavcs-core`, 탐색기 상태 배지는 Rust COM DLL `moavcs-shell`입니다. 기존 `src/MoaVcs.*`와 .NET 테스트는 이식 비교용이며 새 빌드·배포에서 실행하지 않습니다.

## 준비와 실행

- Windows 10/11 x64
- Rust stable MSVC 도구 체인(Rust 1.88 이상), rustfmt
- Visual Studio Build Tools의 x64 링커와 Windows SDK(리소스 컴파일러 포함)
- 검증할 VCS CLI, 설치 파일 생성 시 Inno Setup Compiler

```powershell
.\scripts\build.ps1
.\scripts\run.ps1 -AppArguments @('--path', 'D:\Work Area\repo')
```

`Cargo.lock`을 함께 커밋하고 검증·배포에는 `--locked`를 사용합니다. `.cargo/config.toml`은 Windows MSVC 런타임을 정적으로 연결하므로 별도의 .NET 또는 VC 런타임 설치 없이 배포합니다. Windows SDK와 링커는 빌드할 때 필요합니다.

## 검사

```powershell
$env:MOAVCS_LORE_EXE = .\scripts\get-test-lore.ps1
.\scripts\verify.ps1 -Configuration Release
```

검증 스크립트는 Rust 서식 검사, Cargo workspace 테스트, 앱·DLL 빌드와 `moavcs --ui-smoke`를 실행합니다. GUI smoke는 별도 설정·임시 저장소를 사용하고 `artifacts/screenshots/rust`에 화면과 검사 결과를 생성합니다. 사용자 저장소·설정을 변경하거나 로컬 PC에 탐색기 메뉴를 등록하지 않습니다.

좁은 코어 검사는 다음 명령을 사용합니다.

```powershell
cargo test -p moavcs-core --lib
cargo test -p moavcs-core --test process_contract
cargo test -p moavcs-core --test git_integration
cargo test -p moavcs-core --test provider_fixtures
cargo test -p moavcs-core --test lore_integration
cargo test -p moavcs-shell
```

Git 검사는 임시 로컬 저장소와 bare 원격을 사용합니다. SVN·Perforce는 서버 없는 명령·출력 fixture 검사입니다. Lore는 공식 0.13.0 CLI를 지정하면 오프라인 임시 저장소를 검사하며 원격 인증을 검증하지 않습니다. CLI 환경 변수가 없으면 실제 Lore 검사만 SKIP으로 표시합니다. 잘못된 실행 파일 경로가 지정되면 실패합니다.

GUI는 `cargo run -p moavcs-app -- --ui-smoke artifacts/screenshots/rust`로 별도 확인할 수 있습니다. 실제 탐색기 레지스트리·COM 설치/제거 검사는 GitHub Actions의 격리된 Windows runner에서만 수행하며, GUI smoke가 그 설치 검사를 대체하지 않습니다.

GUI smoke는 작업 공간·이력·Diff·Blame·충돌·복제·원격 주소·설정의 화면을 캡처합니다. 860×620 최소 창, 긴 한글 경로와 브랜치명, 여러 변경 파일도 검사하고, 실제 대화상자 외곽이 viewport를 벗어나면 실패합니다. 사용자 포인터 위치가 hover·tooltip 캡처에 영향을 주지 않도록 자동 검사에서는 포인터 입력을 비웁니다.

Windows GUI 빌드는 wgpu의 `dx12` 기능을 명시적으로 활성화합니다. Vulkan 드라이버가 없는 환경도 DX12 어댑터를 탐색하며, GitHub runner에서는 Windows 소프트웨어 렌더러를 사용할 수 있습니다. `$env:WGPU_BACKEND='dx12'`로 해당 경로를 지정해 진단할 수 있습니다. GUI smoke는 사용한 backend와 장치 종류를 로그 및 `renderer.json`에 남깁니다.

## 모듈과 호환성

- `crates/moavcs-core`: 제공자 인터페이스·프로세스 취소/출력 제한·Git/SVN/Perforce/Lore·Diff/Blame/충돌·설정·경로·외부 도구·오버레이 바이너리 규약
- `crates/moavcs-app`: 비동기 작업 채널과 Rust GUI, 시작 인수, 탐색기 메뉴·상태 조회 작업자
- `crates/moavcs-app/src/app`: 공통 디자인 토큰·위젯(`theme`), 작업 공간·이력(`workspace`), 설정·저장소 창(`dialogs`), 비교·충돌 창(`inspection`)
- `crates/moavcs-shell`: in-process `IShellIconOverlayIdentifier`/`IClassFactory` COM ABI와 캐시 조회
- `scripts`: Cargo 검증·게시·설치 파일 생성과 격리된 설치 검사

설정 위치는 `%LOCALAPPDATA%\MoaVcs\settings.json`입니다. 기존 PascalCase JSON과 VCS enum 문자열, 고정 저장소·최근 목록·CLI 경로·외부 도구 설정을 읽습니다. 갱신은 파일별 named mutex와 임시 파일의 원자적 교체를 사용합니다. 읽을 수 없는 설정은 원본을 보존하며, 오래 열린 설정창이 제거된 최근 저장소를 되살리지 않도록 최근 목록은 별도로 갱신합니다.

상태 캐시는 v1 규약을 유지합니다. magic `0x4D4F4156`, UTC FILETIME, UTF-16LE 절대 경로를 저장하며 16 MiB/100,000개 항목으로 제한합니다. DLL은 캐시만 읽고 VCS CLI를 호출하지 않습니다. worker는 현재 사용자 SID의 named object로 제어하며, 사용자가 활성화한 최근 고정 드라이브 저장소를 제한된 시간 안에 조회합니다.

아이콘 원본은 `assets/explorer-icons/icons.xaml`과 `assets/explorer-overlays/icons.xaml`입니다. 기존 PowerShell 아이콘 생성기는 디자인 원본의 재생성에만 사용하며 일반 Rust 빌드에서는 커밋된 ICO/PNG를 리소스로 포함합니다.

## 시작 인수와 배포

기존 `--path`, `--shell-path <경로|>`, `--vcs`, `--action open|status|diff|blame|log|commit|update|push|clone|init|remote|settings`를 유지합니다. `--install-context-menu`, `--uninstall-context-menu`, `--overlay-worker`, `--pause-overlays`, `--resume-overlays`, `--disable-overlays`와 `--headless`도 설치 프로그램과 호환됩니다. headless 실패는 0이 아닌 종료 코드와 `setup-error.log`를 남깁니다.

각 단계의 관련 검사를 통과한 뒤 한글 메시지로 커밋하고 현재 원격 브랜치로 푸시합니다. `master`와 `codex/**` 브랜치의 CI 및 안정 버전 태그의 릴리스는 같은 Rust 검증·설치 검사를 수행합니다. [릴리스 안내](releases.md)를 참고하세요.
