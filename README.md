# moavcs

Git, Subversion, Perforce, Epic Games Lore를 한 화면에서 다루는 Windows 데스크톱 버전 관리 클라이언트입니다. .NET 8과 WPF를 사용합니다.

MoaVCS는 로컬 저장소 열기, 변경 확인, 파일 비교·행별 이력, VCS별 커밋과 원격 작업을 제공합니다. 제공자별 기능과 제한은 [지원 범위](docs/supported-vcs.md)에 정리했습니다. VCS CLI는 별도로 설치해야 합니다. 앱 왼쪽 아래 **설정 → 버전 관리 도구**에서 실행 파일 경로를 지정할 수 있습니다.

## 처음 사용하는 순서

1. 시작 화면에서 **저장소 폴더 열기**를 누르거나 위쪽 **저장소 → 새 저장소 만들기 / 복제…**를 선택합니다. 기존 저장소를 열거나 새 저장소를 만들고 원격 저장소를 복제할 수 있습니다.
2. **변경된 파일** 목록에서 파일을 고르면 오른쪽 미리 보기에서 내용을 확인할 수 있습니다. 전체 비교와 행별 이력은 위쪽 **파일 도구** 메뉴에서 엽니다.
3. Git과 Lore는 파일을 선택해 **커밋에 포함**을 누르고, 설명을 적은 뒤 **커밋**합니다. 이 두 도구의 커밋은 선택한 행만이 아니라 **커밋에 포함된 모든 변경**을 기록합니다. Git에서 오른쪽 미리 보기의 **커밋할 내용**을 선택하면 포함된 내용만 확인할 수 있습니다.
4. Git과 Lore는 커밋 후 **보내기**로 원격에 공유합니다. Git의 원격 주소는 **저장소 → 원격 주소 설정…**에서 관리합니다.

SVN은 새 파일을 **새 파일 등록**한 뒤, 파일 목록에서 서버에 기록할 항목을 선택하고 설명을 적어 **커밋**합니다. SVN 커밋은 선택한 파일만 서버에 제출합니다. Perforce는 **변경 등록**과 **등록 해제**로 파일을 등록한 다음 P4V 또는 p4 CLI에서 changelist를 제출합니다. MoaVCS에서는 Perforce 커밋 제출을 지원하지 않습니다.

## 실행

[GitHub Releases](https://github.com/hebory/moavcs/releases/latest)에서 **moavcs-버전-win-x64-setup.exe**를 내려받아 설치하세요. 설치 파일과 portable ZIP에는 .NET 런타임이 포함되어 있습니다. 현재 사용자 계정에 설치하며, 탐색기 메뉴 등록은 설치 과정에서 선택할 수 있습니다. VCS CLI는 앱의 다운로드 버튼을 통해 별도로 설치합니다.

Windows 10/11 x64용입니다. portable ZIP은 모두 압축 해제한 뒤 **moavcs.exe**를 실행하세요. exe와 같은 폴더의 DLL·JSON 파일을 함께 보관해야 합니다.

소스에서 실행하려면 .NET 8 SDK를 설치하고, 저장소 루트에서 PowerShell을 열고 실행합니다.

    .\scripts\run.ps1

폴더 경로를 지정하려면 PowerShell에서 아래와 같이 실행합니다.

    .\scripts\run.ps1 -AppArguments @("--path", "C:\Work Area\repo")

앱만 게시하려면 .\scripts\publish.ps1을 사용합니다. 기본 출력은 artifacts\publish\win-x64이며 .NET 8 Desktop Runtime 설치가 필요합니다. -SelfContained를 추가하면 런타임을 포함합니다. 설치 파일과 ZIP·체크섬은 .\scripts\package.ps1로 만들며 Inno Setup 컴파일러가 필요합니다. 자동 생성 절차는 [릴리스 안내](docs/releases.md)를 참고하세요.

검증은 .\scripts\verify.ps1로 실행합니다. 실제 Git 통합 테스트, VCS 어댑터 fixture 테스트, 탐색기 명령 인용과 설정 저장, WPF 화면 렌더링 검사를 포함합니다. Lore 0.13.0의 로컬 통합·화면 검사는 `MOAVCS_LORE_EXE`를 지정하면 실행되며 CI와 릴리스에서는 항상 실행합니다. 준비 방법은 [로컬 개발](docs/development.md)을 참고하세요. SVN·Perforce·Lore의 실제 서버 연결은 아직 검증하지 않았으며, Perforce changelist 제출은 현재 GUI에서 지원하지 않습니다.

## VCS 도구 다운로드

**설정 → 버전 관리 도구** 탭의 **다운로드 ↗** 버튼이 기본 브라우저에서 공식 배포 페이지를 엽니다. Git for Windows, Windows SVN CLI 배포판인 SlikSVN, Perforce P4 CLI, Epic Games Lore를 선택할 수 있습니다. 설치 후 **설치된 도구 확인 → 설정 저장**을 누르세요. 앱을 재시작하지 않아도 갱신된 사용자·시스템 PATH를 다시 읽습니다. 설치 경로가 PATH에 없으면 **찾기**로 실행 파일을 선택하세요.

연결 주소: [Git](https://git-scm.com/install/windows), [SlikSVN](https://sliksvn.com/download/), [Perforce P4 CLI](https://www.perforce.com/downloads/helix-command-line-client-p4), [Epic Games Lore](https://github.com/EpicGames/lore/releases/latest). MoaVCS는 이 도구들의 설치 파일이나 계정 정보를 재배포·저장하지 않습니다.

## 내장 Diff · Blame과 외부 도구

파일을 선택한 뒤 위쪽 **파일 도구 → 선택 파일 비교 (Diff)** 또는 **선택 파일의 행별 이력 (Blame)**을 누르면 설정한 도구로 엽니다. 내장 화면을 바로 사용하려면 **파일 도구 → 내장 도구로 열기 → 선택 파일 비교 / 선택 파일의 행별 이력**을 선택하세요. 변경 목록에 없는 파일은 **다른 파일을 골라 비교…** 또는 **다른 파일의 행별 이력…**에서 고릅니다. 오른쪽 미리 보기의 **커밋할 내용** 체크는 Git에서만 사용할 수 있으며, 선택하면 HEAD와 인덱스를 비교합니다.

Lore 0.13.0 이상에서도 내장 좌우 diff와 Blame을 사용할 수 있습니다. 현재 커밋과 작업 파일을 비교하고, 커밋된 줄의 작성자 identity·리비전·날짜를 표시합니다. 추가·삭제·스테이지된 이름 변경을 지원합니다.

**설정 → 비교·이력 도구**에서 외부 프로그램을 켜고 실행 파일을 지정할 수 있습니다. 인수 템플릿은 **고급 인수 설정**을 펼쳐 입력합니다. 기본값은 내장 도구입니다. VCS별 비교 기준과 제한은 [파일 도구 안내](docs/file-tools.md)를 참고하세요.

## 원격 주소 설정

Git 저장소를 연 다음 위쪽 **저장소 → 원격 주소 설정…**에서 `origin`의 현재 주소를 확인하고 변경할 수 있습니다. origin이 없으면 추가합니다. 기본 fetch 주소만 편집하며, 추가 fetch 주소와 별도로 지정한 push 주소는 보존합니다. 저장된 주소와 Git의 URL 치환 규칙을 적용한 실제 주소를 구분해 표시합니다. 주소 저장은 네트워크에 연결하거나 자동으로 Pull·Push하지 않습니다.

탐색기에서도 **MoaVCS → 원격 주소…**로 같은 창을 열 수 있습니다. SVN·Perforce의 접속 설정은 Git origin과 의미가 달라 이 편집기에 포함하지 않습니다. Lore는 기존 저장소의 원격 주소 변경을 지원하지 않습니다.

원격 주소를 추가한 뒤 처음 **보내기**를 누르면, Git의 기본 푸시가 성공할 때 현재 브랜치의 원격 추적 브랜치(upstream)도 설정합니다. Git 2.37 이상에서 지원하며 기존 푸시 대상과 추적 설정을 따릅니다. 원격에 다른 이력이 있으면 Git이 전송을 거부하고 오류를 표시합니다.

## 탐색기 메뉴

앱 왼쪽 아래 **설정 → 탐색기 메뉴 → 메뉴 등록 / 갱신**을 선택하거나 아래 명령을 실행하면 현재 사용자 계정에 메뉴를 등록합니다. 관리자 권한은 필요하지 않습니다.

    .\artifacts\publish\win-x64\moavcs.exe --install-context-menu
    .\artifacts\publish\win-x64\moavcs.exe --uninstall-context-menu

TortoiseGit처럼 **커밋**과 폴더의 **업데이트 / Pull**은 탐색기 첫 메뉴에서 바로 실행하고, 나머지는 **MoaVCS** 하위 메뉴에서 선택합니다. 하위 메뉴는 동기화·비교, 로그·Blame·변경 확인, 저장소 관리, 설정을 구분선으로 나눕니다. 복제·생성도 추가 하위 메뉴 없이 바로 열립니다. 설정의 두 체크박스를 끄면 커밋·업데이트를 MoaVCS 하위 메뉴에 모을 수 있습니다. 업데이트 후 **메뉴 등록 / 갱신**으로 기존 구성을 교체하세요. Windows 11에서는 **더 많은 옵션 표시**에서 사용합니다. 자세한 내용은 [탐색기 메뉴 안내](docs/explorer-integration.md)를 참고하세요.

## Moa Launcher 이미지

런처 목록용 [icon.png](icon.png)와 상세 화면용 [cover.png](cover.png)를 제공합니다. Moa Launcher의 **설정 → 카탈로그 가져오기**에서 [moalauncher.json](moalauncher.json)을 가져오면 이미지와 GitHub Release 설치 파일이 연결된 MoaVCS 항목을 추가할 수 있습니다. [등록 방법과 이미지 원본](docs/branding.md)을 참고하세요.

## 구성

- src/MoaVcs.Core: 공통 VCS 모델, 프로세스 실행, 저장소 제공자
- src/MoaVcs.App: WPF 화면과 Windows 통합
- tests/MoaVcs.Tests: 외부 테스트 프레임워크 없이 실행하는 통합 테스트
- docs: 구현 범위와 Windows 셸 통합

사용자 설정은 %LOCALAPPDATA%\MoaVcs\settings.json에 저장합니다. [기여 및 로컬 빌드](docs/development.md)를 참고하세요.
