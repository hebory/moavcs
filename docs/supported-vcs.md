# VCS 지원 범위

이 표는 현재 구현 범위를 설명합니다. 저장소를 열 때 해당 CLI가 PATH에 있거나 설정한 실행 파일 경로가 유효해야 합니다.

| 시스템 | 저장소 감지 및 상태 | Diff / 이력 | 파일 등록 / 해제 | 커밋 | Update | Push / Clone / Initialize |
|---|---|---:|---:|---:|---:|---:|
| Git | 지원 | 지원 / 지원 | 지원 / 지원 | 인덱스 변경만 | fast-forward only | push / clone / init |
| Subversion | 지원 | 지원 / 지원 | svn add / 해당 없음 | 선택 경로 커밋 | update | 해당 없음 / checkout / 해당 없음 |
| Perforce | 워크스페이스 안에서 지원 | 지원 / 지원 | p4 reconcile / p4 revert -k | 현재 GUI에서 미지원 (P4V 또는 p4 CLI 사용) | sync | 해당 없음 / 해당 없음 / 해당 없음 |
| Epic Games Lore | 지원 | 지원 / 지원 | 지원 / 지원 | 인덱스에 등록한 모든 파일 | sync | push / clone / 저장소 생성 |

Git은 인덱스(staging area)에 파일을 올린 뒤, 이미 인덱스에 들어 있는 변경만 커밋합니다. Subversion과 Perforce에는 Git과 동일한 stage 영역이 없어서 같은 표현을 재사용하지 않습니다. Perforce는 제출이 changelist 단위이므로 GUI 제출 기능은 changelist 범위를 안전하게 보장할 수 있을 때 추가합니다. Lore의 commit은 선택 여부와 관계없이 인덱스에 등록된 파일 전체를 포함하므로 실행 전에 화면에 이 범위를 표시합니다. Lore는 Epic Games의 lore CLI를 의미합니다. 기능을 추가할 때 이 표를 함께 업데이트하세요.

검증 수준: Git은 실제 로컬 저장소에서 통합 테스트합니다. SVN·Perforce·Lore는 공식 CLI 문서/소스에 맞춘 명령 및 출력 fixture 테스트이며, 실제 서버와 설치된 CLI 조합에서의 연결 검증은 아직 수행하지 않았습니다. 특히 Lore는 CLI 출력이 바뀌면 호환성 오류를 표시하도록 구현했습니다.

현재 포함하지 않는 기능: 충돌 해결 편집기, 브랜치 생성·전환 UI, Perforce changelist 제출, 파일 상태 오버레이 아이콘, Windows 11 최상위 현대식 메뉴용 COM/MSIX 확장. 기존 CLI의 인증 설정을 사용하며 GUI 로그인·비밀번호 저장은 제공하지 않습니다.

명령 기준: [Git 문서](https://git-scm.com/docs), [SVN 명령 참조](https://svnbook.red-bean.com/en/1.8/svn.ref.html), [Perforce 명령 참조](https://help.perforce.com/helix-core/server-apps/cmdref/current/Content/CmdRef/Home-cmdref.html), [Epic Lore CLI](https://epicgames.github.io/lore/reference/lore-cli-commands/).
