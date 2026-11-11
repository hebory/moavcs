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

v0.9.0부터 설치 파일과 포터블 ZIP에는 `shell/MoaVcsShellSetup.exe`가 포함됩니다. 사용자가 앱 설정에서 배지를 켤 때만 관리자 권한으로 실행합니다. 이 구성 요소는 네이티브 x64 DLL을 Common Program Files의 버전별 폴더에 설치하고 시스템 전체 COM 및 오버레이 등록을 관리합니다. 앱의 사용자별 설치 권한은 바꾸지 않습니다. 다른 앱의 등록은 유지하며, 사용자가 선택한 경우에만 MoaVCS의 세 배지를 앞순위로 등록합니다.

격리된 Windows CI는 기존 앱 설치 검사 외에 별도 셸 확장 설치·실제 COM 인터페이스 조회·상태 캐시 공유·일반/우선순위 전환·제거를 검사합니다. 실행 중인 탐색기에 DLL이 로드되면 업데이트는 버전별 DLL 경로를 사용하고 제거 시 파일 삭제가 다음 재부팅으로 미뤄질 수 있습니다.

검증 단계는 SHA-256을 고정한 공식 Lore 0.13.0 CLI로 오프라인 저장소의 좌우 비교·Blame과 WPF 파일 도구를 검사합니다. 테스트용 CLI는 설치 파일이나 포터블 ZIP에 포함하지 않습니다.

탐색기 메뉴 검사는 공백이 있는 설치 경로에서 폴더·배경·파일별 메뉴 구성, 커밋·업데이트 바로가기, 단일 선택 설정, 구분선의 레지스트리 자료형을 확인합니다. 바로가기를 껐을 때 하위 메뉴로 복귀하는지, 구버전 메뉴가 중복 없이 교체되는지 검사합니다. 다른 실행 파일이 등록한 메뉴를 보존하고 현재 설치본을 제거하면 소유 메뉴 키가 남지 않는지도 확인합니다.

v0.8.1의 동작별 탐색기 아이콘은 설치 파일과 포터블 ZIP 모두에 포함됩니다. 설치 검사는 13개 ICO의 존재·헤더·이미지 범위·다중 크기와 각 메뉴의 아이콘 경로를 확인합니다.

CI는 master와 codex/* 브랜치 푸시, master 대상 pull request에서 scripts/verify.ps1을 실행합니다. v<major>.<minor>.<patch> 태그의 릴리스 workflow는 같은 검증을 수행한 다음 self-contained publish, Inno Setup 설치 파일, ZIP 및 해시를 만듭니다. 마지막으로 별도의 포터블 Windows runner 폴더에 설치·제거하고 파일 설치, 사용자 레지스트리 메뉴 등록 및 제거를 검사한 뒤 GitHub Release를 공개합니다. 이 설치 검사는 GitHub Actions의 격리된 Windows runner에서만 실행됩니다.

로컬에서는 아래 명령으로 같은 설치 파일과 포터블 패키지를 만들 수 있습니다. Inno Setup Compiler가 필요합니다.

    .\scripts\package.ps1 -Version 0.1.0
