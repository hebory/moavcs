# VCS 지원 범위

이 표는 현재 구현 범위를 설명합니다. 저장소를 열 때 해당 CLI가 PATH에 있거나 설정한 실행 파일 경로가 유효해야 합니다.

| 시스템 | 저장소 감지 및 상태 | Diff / 이력 | 파일 등록 / 해제 | 커밋 | Update | Push / Clone / Initialize |
|---|---|---:|---:|---:|---:|---:|
| Git | 지원 | 지원 / 지원 | 지원 / 지원 | 인덱스 변경만 | fast-forward only | push / clone / init |
| Subversion | 지원 | 지원 / 지원 | svn add / 해당 없음 | 선택 경로 커밋 | update | 해당 없음 / checkout / 해당 없음 |
| Perforce | 워크스페이스 안에서 지원 | 지원 / 지원 | p4 reconcile / p4 revert -k | 현재 GUI에서 미지원 (P4V 또는 p4 CLI 사용) | sync | 해당 없음 / 해당 없음 / 해당 없음 |
| Epic Games Lore | 지원 | 지원 / 지원 | 지원 / 지원 | 인덱스에 등록한 모든 파일 | sync | push / clone / 저장소 생성 |

Git은 인덱스(staging area)에 파일을 올린 뒤, 이미 인덱스에 들어 있는 변경만 커밋합니다. Subversion과 Perforce에는 Git과 동일한 stage 영역이 없어서 같은 표현을 재사용하지 않습니다. Perforce는 제출이 changelist 단위이므로 GUI 제출 기능은 changelist 범위를 안전하게 보장할 수 있을 때 추가합니다. Lore의 commit은 선택 여부와 관계없이 인덱스에 등록된 파일 전체를 포함하므로 실행 전에 화면에 이 범위를 표시합니다. Lore는 Epic Games의 lore CLI를 의미합니다. 기능을 추가할 때 이 표를 함께 업데이트하세요.

검증 수준: Git과 Lore 0.13.0은 실제 CLI로 로컬 저장소 통합 테스트합니다. Lore는 오프라인 저장소의 커밋·상태·좌우 비교·Blame을 검증하며, 원격 서버 연결은 검증하지 않았습니다. SVN·Perforce는 공식 CLI 문서/소스에 맞춘 명령 및 출력 fixture 테스트입니다. Lore는 CLI 출력이 바뀌면 호환성 오류를 표시합니다.

현재 포함하지 않는 기능: 충돌 해결 편집기, 브랜치 생성·전환 UI, Perforce changelist 제출, 파일 상태 오버레이 아이콘, Windows 11 최상위 현대식 메뉴용 COM/MSIX 확장. 기존 CLI의 인증 설정을 사용하며 GUI 로그인·비밀번호 저장은 제공하지 않습니다.

## 원격 주소 설정

Git 저장소 화면과 탐색기의 **원격 주소…**에서 `origin`을 조회·추가·수정합니다. 주소 편집에는 Git 2.30 이상이 필요합니다. 편집 대상은 저장된 기본 fetch URL 하나입니다. 추가 fetch URL, 별도의 `pushurl`, 다른 remote, 브랜치 추적 설정을 보존합니다. 별도 push URL이 있으면 기본 주소를 바꿔도 push 대상은 유지되며 창에 이 내용을 표시합니다. HTTPS·SSH·Git의 SCP 형식·로컬 경로를 입력할 수 있습니다.

Git의 `insteadOf` / `pushInsteadOf` 규칙이 적용될 수 있으므로 저장된 주소와 실제 사용 주소를 함께 확인할 수 있습니다. 전역 설정·include·worktree 설정, 기본 주소가 중복 등록된 경우 등 편집기에서 안전하게 변경할 수 없는 구성은 이유와 함께 읽기 전용으로 표시합니다. 창을 연 뒤 다른 도구가 원격 설정을 변경했다면 저장을 중단하고 다시 열도록 안내합니다.

주소 저장은 로컬 설정만 변경하며 네트워크 연결, 인증 확인, fetch, pull, push를 수행하지 않습니다. 실제 서버 연결 가능 여부는 이후 사용자가 실행하는 동기화 작업에서 확인합니다. 취소하면 아무 설정도 변경하지 않습니다.

SVN의 relocate와 Perforce의 P4PORT·워크스페이스 설정은 origin과 다른 기능이므로 이 편집기에서 변경하지 않습니다. Lore 0.13.0에는 원격 주소 설정 CLI가 없으며, [공식 설정 문서](https://github.com/EpicGames/lore/blob/main/docs/reference/lore-cli-config.md#remote_url)는 기존 저장소의 `remote_url`을 고정값으로 취급하도록 안내합니다. Lore의 주소를 옮기려면 원하는 원격에서 새 작업 공간을 복제하는 방식을 사용하세요.

Git 동작 기준: [git remote](https://git-scm.com/docs/git-remote), [git config](https://git-scm.com/docs/git-config). 테스트는 임시 Git 저장소의 주소 설정과 기존 설정 보존을 검증하며 원격 서버에는 연결하지 않습니다.

## 파일 비교와 Blame

| 시스템 | 내장 좌우 비교 | 내장 Blame 기준 |
|---|---|---|
| Git | 인덱스 → 작업 트리, 스테이지 비교 시 HEAD → 인덱스 | HEAD에 커밋된 파일. 스테이지된 이름 변경은 이전 경로의 이력 |
| SVN | BASE → 작업 복사본 | 작업 복사본의 BASE |
| Perforce | 마지막 동기화 리비전 → 작업 공간 | 마지막 동기화한 파일 리비전 |
| Lore 0.13.0 이상 | 현재 커밋 → 작업 파일 (스테이지된 변경 포함) | 현재 커밋의 파일. 스테이지된 이름 변경은 이전 경로의 이력 |

텍스트 스냅샷은 UTF-8 파일당 1 MiB, 좌우 비교와 blame은 50,000줄까지 지원합니다. 바이너리·다른 인코딩·지원 범위를 벗어나는 파일은 이유를 표시하며 내용이나 이력을 추측하지 않습니다. Blame에는 아직 커밋하지 않은 작업 내용이 포함되지 않습니다. 충돌 중인 Git 인덱스의 좌우 비교는 지원하지 않습니다.

외부 diff 도구에는 두 텍스트 스냅샷 또는 통합 패치를, 외부 blame 도구에는 이력 TSV 또는 원본 파일 경로를 전달할 수 있습니다. Lore도 `{left}`·`{right}` 스냅샷과 `{blame}` TSV를 지원합니다.

Lore는 조회를 시작할 때 현재 리비전 서명을 고정하고, `file info`와 별도 임시 경로를 지정한 `file write`로 커밋 내용을 읽습니다. Blame은 `file audit` JSON의 줄 범위와 리비전 메타데이터를 결합합니다. 작성자는 저장된 identity를 표시하며 서버의 표시 이름을 조회하지 않습니다. 과거 내용이나 링크에 접근할 수 없는 범위는 `이력 확인 불가`와 CLI의 원인을 표시합니다. 다른 연결 저장소에서 온 줄은 리비전만 보존하고 `연결 저장소 이력 (작성자 조회 미지원)`으로 표시합니다.

명령 기준: [Git 문서](https://git-scm.com/docs), [SVN blame 참조](https://svnbook.red-bean.com/en/1.8/svn.ref.svn.c.blame.html), [Perforce annotate 참조](https://help.perforce.com/helix-core/server-apps/cmdref/current/Content/CmdRef/p4_annotate.html), [Epic Lore CLI](https://epicgames.github.io/lore/reference/lore-cli-commands/).
