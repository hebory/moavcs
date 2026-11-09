#ifndef AppVersion
  #error "AppVersion must be passed to ISCC as /DAppVersion=..."
#endif

#define AppName "moavcs"
#define PublishDirectory "..\artifacts\publish\win-x64"

[Setup]
AppId={{A5309FC4-749E-437D-8A6A-9526279B4A14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=moavcs
DefaultDirName={localappdata}\Programs\moavcs
DefaultGroupName=moavcs
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\moavcs.exe
OutputDir=..\artifacts\release
OutputBaseFilename=moavcs-{#AppVersion}-win-x64-setup
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
UninstallLogging=yes

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로 가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked
Name: "explorermenu"; Description: "탐색기 우클릭 메뉴 등록"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\moavcs"; Filename: "{app}\moavcs.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\moavcs"; Filename: "{app}\moavcs.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\moavcs.exe"; Description: "moavcs 실행"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{app}\moavcs.exe"; Parameters: "--uninstall-context-menu --headless"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "RemoveMoaVcsExplorerMenu"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('explorermenu') then
  begin
    if not Exec(ExpandConstant('{app}\moavcs.exe'),
      '--install-context-menu --headless', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('탐색기 메뉴를 등록할 수 없습니다. %LOCALAPPDATA%\MoaVcs\setup-error.log 파일을 확인하세요.');
    if ResultCode <> 0 then
      RaiseException(Format('탐색기 메뉴 등록이 종료 코드 %d로 실패했습니다. %LOCALAPPDATA%\MoaVcs\setup-error.log 파일을 확인하세요.', [ResultCode]));
  end;
end;
