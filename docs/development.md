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

탐색기 아이콘의 벡터 원본은 `assets/explorer-icons/icons.xaml`이며 브랜드 로고는 `assets/branding/icon.xaml`을 재사용합니다. Windows에서 `powershell.exe -NoProfile -STA -File scripts/New-ExplorerIcons.ps1 -Preview`로 ICO를 다시 만들 수 있습니다. 밝은 배경·어두운 배경의 16·24·32 픽셀 미리 보기는 `artifacts/screenshots/explorer-icons.png`에 생성됩니다. 생성된 ICO를 원본과 함께 커밋하며, 일반 빌드에서는 생성 도구를 실행하지 않고 ICO를 복사합니다.

Lore의 실제 CLI 통합·WPF 검사는 아래처럼 실행합니다. 준비 스크립트는 공식 Windows x64 Lore 0.13.0 ZIP을 `.local/test-tools`에 내려받고 고정된 SHA-256을 검증합니다. 시스템 설치나 PATH 변경 없이 테스트에서만 사용합니다. CI와 릴리스에서도 같은 절차를 실행합니다.

```powershell
$env:MOAVCS_LORE_EXE = .\scripts\get-test-lore.ps1
.\scripts\verify.ps1
```

환경 변수가 없으면 실제 Lore 검사는 `SKIP`으로 표시하고 fixture 검사는 실행합니다. 잘못된 경로가 지정되면 실패합니다. 코어의 Lore 검사만 실행하려면 `dotnet run --project tests/MoaVcs.Tests -- --lore`를 사용하세요. 실제 검사는 오프라인 임시 저장소와 명시적인 테스트 identity를 사용하며 원격 서버 인증을 검증하지 않습니다.

## CLI 인자

- --path <폴더 또는 파일>: 열 경로
- --vcs Git|Svn|Perforce|Lore: 감지된 유형 대신 명시적인 유형 선택
- --action open|status|diff|blame|log|commit|update|push|clone|init|settings: 탐색기 메뉴 작업. update/push는 저장소를 연 뒤 확인창을 표시하며, clone/init/settings는 해당 대화상자를 바로 엽니다.
- --install-context-menu 및 --uninstall-context-menu: 현재 사용자 셸 메뉴 등록 관리
- --headless: 위 메뉴 등록·제거 명령을 설치 프로그램에서 창 없이 실행하고 종료. 실패 시 종료 코드 1과 %LOCALAPPDATA%\MoaVcs\setup-error.log를 남깁니다.

작업 단계마다 관련 검증 후 한글 메시지로 커밋하고 현재 원격 브랜치에 푸시합니다. `master`와 `codex/**` 브랜치의 푸시 및 pull request는 Windows CI 검증을 실행합니다. 설치 파일 배포는 [릴리스 안내](releases.md)를 따릅니다.

공통 어댑터는 UI가 특정 CLI의 출력 형식과 저장소별 동작을 직접 알지 않도록 나눕니다. 추가 제공자를 구현할 때에는 각 기능이 정확히 가능한 경우에만 해당 기능을 표시하고, 선택하지 않은 파일을 커밋하거나 되돌리는 동작을 추가하지 마세요.
