# moavcs

Git, Subversion, Perforce, Epic Games Lore를 한 화면에서 다루는 Windows 데스크톱 버전 관리 클라이언트입니다. .NET 8과 WPF를 사용합니다.

현재 코드는 초기 MVP입니다. 로컬 저장소 열기, 변경 파일·diff·이력 보기와 각 VCS의 지원 범위 안에서 파일 등록과 커밋을 제공합니다. 제공자별 기능 범위는 [지원 범위](docs/supported-vcs.md)에 적었습니다. VCS CLI는 별도로 설치해야 합니다. **도구 및 탐색기 설정**에서 실행 파일 경로를 지정할 수 있습니다.

## 실행

Windows 10 또는 11과 .NET 8 Desktop Runtime이 필요합니다. 빌드된 배포 폴더에서는 **moavcs.exe**를 실행하세요. 이 작업 공간에서는 **artifacts\win-x64\moavcs.exe**에 실행 파일이 있습니다. exe와 같은 폴더의 DLL·JSON 파일을 함께 보관해야 합니다.

소스에서 실행하려면 .NET 8 SDK를 설치하고, 저장소 루트에서 PowerShell을 열고 실행합니다.

    .\scripts\run.ps1

폴더 경로를 지정하려면 PowerShell에서 아래와 같이 실행합니다.

    .\scripts\run.ps1 -AppArguments @("--path", "C:\Work Area\repo")

배포 디렉터리는 .\scripts\publish.ps1로 만듭니다. 기본 출력은 artifacts\win-x64이며 .NET 8 Desktop Runtime 설치가 필요합니다. 런타임을 포함하려면 -SelfContained를 추가합니다. 런타임을 포함하는 빌드는 관련 패키지 다운로드가 필요할 수 있습니다.

검증은 .\scripts\verify.ps1로 실행합니다. 실제 Git 통합 테스트, 다른 VCS의 어댑터 fixture 테스트, 탐색기 명령 인용과 설정 저장, WPF 화면 렌더링 검사를 포함합니다. SVN·Perforce·Lore의 실제 서버 연결은 아직 검증하지 않았으며, Perforce changelist 제출은 현재 GUI에서 지원하지 않습니다.

## 탐색기 메뉴

앱에서 **도구 및 탐색기 설정 → 메뉴 등록 / 갱신**을 선택하거나 아래 명령을 실행하면 현재 사용자 계정에 메뉴를 등록합니다. 관리자 권한은 필요하지 않습니다.

    .\artifacts\win-x64\moavcs.exe --install-context-menu
    .\artifacts\win-x64\moavcs.exe --uninstall-context-menu

폴더, 폴더 배경과 파일 메뉴에 moavcs 하위 메뉴가 추가됩니다. Windows 11은 고전 Win32 탐색기 메뉴를 **더 많은 옵션 표시** 아래에 보여줄 수 있습니다. 자세한 내용은 [탐색기 메뉴 안내](docs/explorer-integration.md)를 참고하세요.

## 구성

- src/MoaVcs.Core: 공통 VCS 모델, 프로세스 실행, 저장소 제공자
- src/MoaVcs.App: WPF 화면과 Windows 통합
- tests/MoaVcs.Tests: 외부 테스트 프레임워크 없이 실행하는 통합 테스트
- docs: 구현 범위와 Windows 셸 통합

사용자 설정은 %LOCALAPPDATA%\MoaVcs\settings.json에 저장합니다. [기여 및 로컬 빌드](docs/development.md)를 참고하세요.
