# 로컬 개발

## 준비

- Windows 10/11
- .NET 8 SDK (WPF 구성 요소 포함, 빌드 시) 또는 .NET 8 Desktop Runtime (실행 시)
- 사용하려는 버전 관리 시스템의 CLI

프로젝트는 외부 NuGet 패키지를 사용하지 않습니다.

## 빌드와 실행

    .\scripts\build.ps1
    .\scripts\run.ps1 -AppArguments @("--path", "C:\Work Area\repo")

tests\MoaVcs.Tests는 실제 Git 임시 저장소 통합 테스트, 프로세스 취소·시간 제한·출력 제한, 다른 CLI의 명령과 출력 파싱, 설정 저장 및 탐색기 경로 인용을 확인합니다. tests\MoaVcs.UiSmoke는 별도의 Git 저장소로 WPF 화면과 비동기 로딩을 검사하고 화면을 artifacts\screenshots에 렌더링합니다. 두 테스트 모두 외부 테스트 패키지 없이 실행합니다. Windows와 Git CLI가 필요합니다.

    .\scripts\verify.ps1

검증은 사용자 저장소를 변경하거나 탐색기 메뉴를 등록하지 않습니다. 임시 저장소는 .local 안에서 생성하고 정리합니다. WPF 렌더링 검사는 실제 탐색기에서 클릭하는 수동 검증을 대신하지 않습니다.

## CLI 인자

- --path <폴더 또는 파일>: 열 경로
- --vcs Git|Svn|Perforce|Lore: 감지된 유형 대신 명시적인 유형 선택
- --action open|status|diff|log|commit: 탐색기 메뉴 작업
- --install-context-menu 및 --uninstall-context-menu: 현재 사용자 셸 메뉴 등록 관리

공통 어댑터는 UI가 특정 CLI의 출력 형식과 저장소별 동작을 직접 알지 않도록 나눕니다. 추가 제공자를 구현할 때에는 각 기능이 정확히 가능한 경우에만 해당 기능을 표시하고, 선택하지 않은 파일을 커밋하거나 되돌리는 동작을 추가하지 마세요.
