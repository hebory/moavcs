# 로컬 개발

## 준비

- Windows 10/11
- .NET 8 SDK (WPF 구성 요소 포함, 빌드 시) 또는 .NET 8 Desktop Runtime (실행 시)
- 사용하려는 버전 관리 시스템의 CLI
- 셸 확장 빌드·검증·패키징: Visual Studio 2022의 C++ x64 빌드 도구(v143)와 Windows SDK

프로젝트는 외부 NuGet 패키지를 사용하지 않습니다.

## 빌드와 실행

    .\scripts\build.ps1
    .\scripts\run.ps1 -AppArguments @("--path", "C:\Work Area\repo")

tests\MoaVcs.Tests는 실제 Git 임시 저장소 통합 테스트, 프로세스 취소·시간 제한·출력 제한, 다른 CLI의 명령과 출력 파싱, 설정 저장 및 탐색기 경로 인용을 확인합니다. tests\MoaVcs.UiSmoke는 별도의 Git 저장소로 WPF 화면과 비동기 로딩을 검사하고 화면을 artifacts\screenshots에 렌더링합니다. 두 테스트 모두 외부 테스트 패키지 없이 실행합니다. Windows와 Git CLI가 필요합니다.

    .\scripts\verify.ps1

검증은 사용자 저장소를 변경하거나 탐색기 메뉴를 등록하지 않습니다. 임시 저장소는 .local 안에서 생성하고 정리합니다. WPF 렌더링 검사는 실제 탐색기에서 클릭하는 수동 검증을 대신하지 않습니다.

저장소 목록은 `dotnet run --project tests/MoaVcs.Tests -- --repositories`로 고정·제거·최근 기록 제한·기존 설정 호환과 다른 설정 보존을 검사합니다. WPF 검사를 좁히려면 `$env:MOAVCS_UI_SMOKE_ONLY = 'repositories'`를 설정하고 `dotnet run --project tests/MoaVcs.UiSmoke`를 실행합니다. 검색·현재 저장소 강조·항목 메뉴·고정/해제·최근 목록 비우기와 오래 열린 설정창이 제거한 항목을 복원하지 않는지를 확인합니다. 전체 검증 전에는 해당 환경 변수를 제거하세요.

복제·체크아웃 경로 제안은 `dotnet run --project tests/MoaVcs.Tests -- --checkout`으로 URL 형식과 안전한 폴더 이름을 검사합니다. WPF 검사를 좁히려면 PowerShell에서 `$env:MOAVCS_UI_SMOKE_ONLY = 'checkout'`을 설정하고 `dotnet run --project tests/MoaVcs.UiSmoke`를 실행하세요. 탐색기 시작 폴더, 주소·수동 이름·상위 폴더 변경, 찾아보기, 모드 전환, 실제 로컬 Git 복제를 확인합니다. 전체 검증 전에는 `Remove-Item Env:MOAVCS_UI_SMOKE_ONLY`로 제한을 해제하세요. SVN·Lore 주소의 폴더 이름 제안은 서버 연결 없이 검사합니다. 이름 제한은 [Windows 파일 이름 규칙](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file)을 따릅니다.

커밋 상세 변경을 좁게 확인하려면 `dotnet run --project tests/MoaVcs.Tests -- --history-git` 또는 `--history-providers`를 사용합니다. 전자는 실제 Git의 최초·병합 커밋과 추가·삭제·이름 변경·바이너리·작업 트리 격리, 후자는 서버에 연결하지 않는 SVN·Perforce fixture 검사입니다. Lore의 리비전 상세 검사는 `--lore`에 포함되며 실제 CLI를 지정해야 통합 검사가 실행됩니다. WPF smoke는 커밋 선택, 파일 선택, 빠른 선택 전환, 긴 메시지와 최소 창 크기도 검사합니다.

Git 브랜치·원격 이력 정보는 `dotnet run --project tests/MoaVcs.Tests -- --history-context`로 검사합니다. 실제 Git과 임시 로컬 bare 원격을 사용해 refs·태그·부모, 추적 브랜치와의 분기·동기화 수, 추적 미설정·분리된 HEAD·첫 커밋 전·얕은 저장소와 명시적 Fetch를 확인합니다. WPF 검사는 `$env:MOAVCS_UI_SMOKE_ONLY = 'history-info'`를 설정해 `dotnet run --project tests/MoaVcs.UiSmoke`로 좁힐 수 있습니다. Fetch 전후의 원격 커밋 표시, 범위 필터, 병합 그래프, 작성자·부모, 최소 창 크기와 작업 파일 보존을 검사합니다. 외부 Git 호스팅 서비스의 인증·서버 연결 검증과는 구분하며 전체 검증 전에는 환경 변수를 해제합니다.

탐색기 아이콘의 벡터 원본은 `assets/explorer-icons/icons.xaml`이며 브랜드 로고는 `assets/branding/icon.xaml`을 재사용합니다. Windows에서 `powershell.exe -NoProfile -STA -File scripts/New-ExplorerIcons.ps1 -Preview`로 ICO를 다시 만들 수 있습니다. 밝은 배경·어두운 배경의 16·24·32 픽셀 미리 보기는 `artifacts/screenshots/explorer-icons.png`에 생성됩니다. 생성된 ICO를 원본과 함께 커밋하며, 일반 빌드에서는 생성 도구를 실행하지 않고 ICO를 복사합니다.

Lore의 실제 CLI 통합·WPF 검사는 아래처럼 실행합니다. 준비 스크립트는 공식 Windows x64 Lore 0.13.0 ZIP을 `.local/test-tools`에 내려받고 고정된 SHA-256을 검증합니다. 시스템 설치나 PATH 변경 없이 테스트에서만 사용합니다. CI와 릴리스에서도 같은 절차를 실행합니다.

```powershell
$env:MOAVCS_LORE_EXE = .\scripts\get-test-lore.ps1
.\scripts\verify.ps1
```

환경 변수가 없으면 실제 Lore 검사는 `SKIP`으로 표시하고 fixture 검사는 실행합니다. 잘못된 경로가 지정되면 실패합니다. 코어의 Lore 검사만 실행하려면 `dotnet run --project tests/MoaVcs.Tests -- --lore`를 사용하세요. 실제 검사는 오프라인 임시 저장소와 명시적인 테스트 identity를 사용하며 원격 서버 인증을 검증하지 않습니다.

## 네이티브 상태 배지

`scripts/build-shell.ps1 -Test`는 x64 C++ 셸 확장과 격리된 캐시·COM 테스트를 빌드합니다. `verify.ps1`에도 포함되며 로컬 검사에서는 레지스트리를 등록하지 않습니다. `dotnet run --project tests/MoaVcs.Tests -- --overlay`는 상태 집계, 경계 경로, 바이너리 형식, 실제 Git 상태 전환과 워커 실패 처리를 검사합니다.

`assets/explorer-overlays/icons.xaml`의 원본에서 `powershell.exe -NoProfile -STA -File scripts/New-ExplorerIcons.ps1 -Overlays -Preview`로 배지 ICO와 미리 보기를 만듭니다. 네이티브 DLL에는 세 아이콘을 리소스로 내장합니다. DLL은 `/MT`로 빌드하며 별도 VC 런타임 설치가 필요하지 않습니다.

`package.ps1`은 일반 앱 안에 별도의 관리자 설치 도구 `shell/MoaVcsShellSetup.exe`를 포함합니다. 단순 `publish.ps1` 결과에는 이 설치 도구가 없으므로 상태 배지 활성화에는 완성된 패키지를 사용하세요. CI의 `test-overlay-installer.ps1`은 격리된 관리자 Windows runner에서만 셸 확장 등록·COM 호출·일반/우선 표시 전환·제거를 검사합니다.

상태 캐시 형식은 Core의 `ExplorerOverlaySnapshot`과 C++의 `OverlayCache`에서 공유합니다. 16 MiB / 100,000개 항목으로 제한하고 UTF-16LE 절대 경로와 UTC FILETIME을 저장합니다. 셸 프로세스에는 CLR을 로드하거나 VCS 명령을 실행하지 않습니다.

## CLI 인자

- --path <폴더 또는 파일>: 열 경로
- --vcs Git|Svn|Perforce|Lore: 감지된 유형 대신 명시적인 유형 선택
- --action open|status|diff|blame|log|commit|update|push|clone|init|settings: 탐색기 메뉴 작업. update/push는 저장소를 연 뒤 확인창을 표시하며, clone/init/settings는 해당 대화상자를 바로 엽니다.
- --install-context-menu 및 --uninstall-context-menu: 현재 사용자 셸 메뉴 등록 관리
- --overlay-worker: 사용자별 백그라운드 상태 조회. 시작 항목 소유권과 활성화 설정이 있어야 실행됩니다.
- --pause-overlays / --resume-overlays / --disable-overlays: 설치 프로그램의 작업자 종료·재시작·제거. `--headless`와 함께 사용합니다.
- --headless: 위 메뉴 등록·제거 명령을 설치 프로그램에서 창 없이 실행하고 종료. 실패 시 종료 코드 1과 %LOCALAPPDATA%\MoaVcs\setup-error.log를 남깁니다.

작업 단계마다 관련 검증 후 한글 메시지로 커밋하고 현재 원격 브랜치에 푸시합니다. `master`와 `codex/**` 브랜치의 푸시 및 pull request는 Windows CI 검증을 실행합니다. 설치 파일 배포는 [릴리스 안내](releases.md)를 따릅니다.

공통 어댑터는 UI가 특정 CLI의 출력 형식과 저장소별 동작을 직접 알지 않도록 나눕니다. 추가 제공자를 구현할 때에는 각 기능이 정확히 가능한 경우에만 해당 기능을 표시하고, 선택하지 않은 파일을 커밋하거나 되돌리는 동작을 추가하지 마세요.
