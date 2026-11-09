# Moa Launcher 등록

저장소 루트의 icon.png와 cover.png를 공개 저장소에 게시하면 Moa Launcher에서 앱 아이콘과 상세 커버로 사용할 수 있습니다. 아이콘 권장 크기는 512×512입니다. 커버는 약 16:9 비율을 권장합니다.

현재 icon.png는 투명 배경 PNG 512×512이며, cover.png는 1672×941 PNG입니다. 아이콘 원본은 assets/branding/icon.xaml이고, .\scripts\New-BrandIcon.ps1로 PNG를 다시 만들 수 있습니다. 커버 제작 프롬프트는 assets/branding/cover-prompt.txt에 보관합니다.

moalauncher.json은 Moa Launcher의 앱 카탈로그 가져오기 형식입니다. **설정 → 카탈로그 가져오기**에서 이 파일을 선택하면 MoaVCS를 사용자 카탈로그에 추가할 수 있습니다. 이 파일만 저장소에 올려도 Moa Launcher의 기본 목록은 바뀌지 않습니다. 기본 목록에 자동으로 포함하려면 Moa Launcher 저장소의 기본 카탈로그도 별도로 갱신해야 합니다.

이미지 URL은 현재 master 브랜치의 다음 경로를 사용합니다.

- https://raw.githubusercontent.com/hebory/moavcs/master/icon.png
- https://raw.githubusercontent.com/hebory/moavcs/master/cover.png

두 파일을 저장소 루트에 두고 해당 브랜치에 게시된 상태로 유지하세요. 이 주소는 파일이 변경되어도 동일하게 유지되므로 아이콘이나 커버를 교체하면 카탈로그를 다시 가져오지 않아도 다음 이미지 갱신 때 반영됩니다.

카탈로그는 공개 GitHub 저장소 hebory/moavcs의 최신 안정 릴리스에서 moavcs-*-win-x64-setup.exe 설치 파일을 찾습니다. GitHub Release에 설치 파일과 SHA256SUMS.txt를 함께 게시해야 합니다. 설치 후 실행 파일은 %LOCALAPPDATA%\Programs\moavcs\moavcs.exe에 있습니다.
