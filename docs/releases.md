# 릴리스

Windows x64 공개 릴리스는 안정 버전 태그를 원격 저장소에 푸시할 때 생성됩니다. 예를 들어 v0.1.0 형식의 태그를 사용합니다. CI와 릴리스 workflow가 별도로 Windows에서 검증을 실행합니다.

    git tag v0.1.0
    git push origin v0.1.0

태그 빌드가 성공하면 공개 GitHub Release에 아래 세 파일이 첨부됩니다.

- moavcs-버전-win-x64-setup.exe: .NET 런타임을 포함한 per-user 설치 파일
- moavcs-버전-win-x64-portable.zip: 압축 해제 후 실행하는 포터블 앱
- SHA256SUMS.txt: 설치 파일과 ZIP의 SHA-256 해시

설치 폴더 기본값은 %LOCALAPPDATA%\Programs\moavcs입니다. 관리자 권한이 필요하지 않습니다. 바탕 화면 바로 가기와 탐색기 우클릭 메뉴는 설치 과정에서 선택할 수 있습니다. 제거할 때 탐색기 메뉴가 등록된 앱 경로와 일치하는 경우에만 지우며, %LOCALAPPDATA%\MoaVcs의 앱 설정은 보존합니다. Git, SVN, Perforce, Lore 도구와 사용자 로그인 정보는 설치 파일에 포함하지 않습니다.

설치 파일은 현재 코드 서명 인증서로 서명하지 않습니다. Windows SmartScreen에서 게시자를 확인할 수 없다는 경고가 나타날 수 있습니다. 배포 파일의 해시는 Release에 첨부된 SHA256SUMS.txt와 비교할 수 있습니다.

## 자동 검증 및 패키징

검증 단계는 SHA-256을 고정한 공식 Lore 0.13.0 CLI로 오프라인 저장소의 좌우 비교·Blame과 WPF 파일 도구를 검사합니다. 테스트용 CLI는 설치 파일이나 포터블 ZIP에 포함하지 않습니다.

탐색기 메뉴 검사는 공백이 있는 설치 경로에서 폴더·배경·파일별 메뉴 구성, 저장소 하위 메뉴, 단일 선택 설정, 구분선의 레지스트리 자료형을 확인합니다. 구버전 메뉴 항목을 추가한 뒤 다시 등록하여 중복 없이 교체되는지 검사하고, 제거 후 소유 메뉴 키가 남지 않는지도 확인합니다.

CI는 master와 codex/* 브랜치 푸시, master 대상 pull request에서 scripts/verify.ps1을 실행합니다. v<major>.<minor>.<patch> 태그의 릴리스 workflow는 같은 검증을 수행한 다음 self-contained publish, Inno Setup 설치 파일, ZIP 및 해시를 만듭니다. 마지막으로 별도의 포터블 Windows runner 폴더에 설치·제거하고 파일 설치, 사용자 레지스트리 메뉴 등록 및 제거를 검사한 뒤 GitHub Release를 공개합니다. 이 설치 검사는 GitHub Actions의 격리된 Windows runner에서만 실행됩니다.

로컬에서는 아래 명령으로 같은 설치 파일과 포터블 패키지를 만들 수 있습니다. Inno Setup Compiler가 필요합니다.

    .\scripts\package.ps1 -Version 0.1.0
