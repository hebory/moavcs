# MoaVCS 개발 규칙

- 기존 구조와 제공자별 기능 차이를 유지하고 요청 범위만 변경한다.
- 작업을 기능·수정 단계로 나누고, 관련 검증을 통과한 단계마다 한글 커밋 메시지로 커밋한 뒤 현재 원격 브랜치에 푸시한다. 무관한 변경을 함께 커밋하지 않는다.
- Windows 검증은 `scripts/verify.ps1`을 사용한다. UI만 변경했으면 먼저 WPF smoke 검사를 수행한다.
- 배포 태그는 `v<major>.<minor>.<patch>` 형식이다. GitHub Release의 설치 파일은 `.github/workflows/release.yml`과 `scripts/package.ps1`에서 생성한다.
- 각 VCS의 CLI가 없는 환경에서는 fixture 검증과 실제 연결 검증을 구분해서 보고한다.
- VCS 다운로드 버튼은 검증한 공식 배포자의 HTTPS 페이지로 연결한다.
- 사용자 변경 사항을 되돌리거나 다른 저장소·설치본의 설정을 제거하지 않는다.
