#ifndef AppVersion
  #error "AppVersion is required"
#endif
#define ShellDirectory "{commonpf64}\MoaVCS\Shell"
#define ShellDll "{commonpf64}\MoaVCS\Shell\" + AppVersion + "\MoaVcsShell.dll"
#define OverlayRoot "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers"
#define ConflictId "{A25E0B81-8C4E-4C9B-9D3A-DF43085C2B11}"
#define ModifiedId "{B7DA0B46-1295-42B0-B3A7-3D55F6E83F22}"
#define UntrackedId "{CF3E2A97-E680-49B8-A2C4-7A9F65C4D833}"
#define ConflictNormal "MoaVCS1Conflict"
#define ModifiedNormal "MoaVCS2Modified"
#define UntrackedNormal "MoaVCS3Untracked"
#define ConflictPriority "    MoaVCS1Conflict"
#define ModifiedPriority "    MoaVCS2Modified"
#define UntrackedPriority "    MoaVCS3Untracked"

[Setup]
AppId={{6E49FD9A-4031-47E1-AE8C-8529DF478227}
AppName=MoaVCS 탐색기 상태 배지
AppVersion={#AppVersion}
AppPublisher=moavcs
DefaultDirName={#ShellDirectory}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=..\artifacts\publish\win-x64\shell
OutputBaseFilename=MoaVcsShellSetup
UninstallFilesDir={#ShellDirectory}\uninstall
UninstallDisplayName=MoaVCS 탐색기 상태 배지
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=yes
SetupLogging=yes

[Files]
Source: "..\artifacts\shell\win-x64\MoaVcsShell.dll"; DestDir: "{#ShellDirectory}\{#AppVersion}"; Flags: ignoreversion uninsrestartdelete

[Registry]
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ConflictId, "{", "{{")}"; ValueType: string; ValueName: ""; ValueData: "MoaVCS Conflict Overlay"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ConflictId, "{", "{{")}"; ValueType: string; ValueName: "MoaVcsOwner"; ValueData: "MoaVcs.Overlays.v1"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ConflictId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{#ShellDll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ConflictId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ModifiedId, "{", "{{")}"; ValueType: string; ValueName: ""; ValueData: "MoaVCS Modified Overlay"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ModifiedId, "{", "{{")}"; ValueType: string; ValueName: "MoaVcsOwner"; ValueData: "MoaVcs.Overlays.v1"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ModifiedId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{#ShellDll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(ModifiedId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(UntrackedId, "{", "{{")}"; ValueType: string; ValueName: ""; ValueData: "MoaVCS Untracked Overlay"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(UntrackedId, "{", "{{")}"; ValueType: string; ValueName: "MoaVcsOwner"; ValueData: "MoaVcs.Overlays.v1"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(UntrackedId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{#ShellDll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#StringChange(UntrackedId, "{", "{{")}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"

Root: HKLM; Subkey: "{#OverlayRoot}\{#ConflictNormal}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(ConflictId, "{", "{{")}"; Check: not IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ConflictNormal}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: not IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ModifiedNormal}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(ModifiedId, "{", "{{")}"; Check: not IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ModifiedNormal}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: not IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#UntrackedNormal}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(UntrackedId, "{", "{{")}"; Check: not IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#UntrackedNormal}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: not IsPriorityInstall()

Root: HKLM; Subkey: "{#OverlayRoot}\{#ConflictPriority}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(ConflictId, "{", "{{")}"; Check: IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ConflictPriority}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ModifiedPriority}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(ModifiedId, "{", "{{")}"; Check: IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#ModifiedPriority}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#UntrackedPriority}"; ValueType: string; ValueName: ""; ValueData: "{#StringChange(UntrackedId, "{", "{{")}"; Check: IsPriorityInstall()
Root: HKLM; Subkey: "{#OverlayRoot}\{#UntrackedPriority}"; ValueType: string; ValueName: "MoaVcsDll"; ValueData: "{#ShellDll}"; Check: IsPriorityInstall()

Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"; ValueType: string; ValueName: "{#StringChange(ConflictId, "{", "{{")}"; ValueData: "MoaVCS Conflict Overlay"
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"; ValueType: string; ValueName: "{#StringChange(ModifiedId, "{", "{{")}"; ValueData: "MoaVCS Modified Overlay"
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"; ValueType: string; ValueName: "{#StringChange(UntrackedId, "{", "{{")}"; ValueData: "MoaVCS Untracked Overlay"

[Code]
function IsPriorityInstall(): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PRIORITY=1') = 0 then
      Result := True;
end;

function CheckOwned(Id, Name: String): Boolean;
var Value: String; ClassPath, HandlerPath: String;
begin
  ClassPath := 'Software\Classes\CLSID\' + Id;
  HandlerPath := '{#OverlayRoot}\' + Name;
  Result := True;
  if RegKeyExists(HKLM64, ClassPath) then
    Result := RegQueryStringValue(HKLM64, ClassPath, 'MoaVcsOwner', Value) and (Value = 'MoaVcs.Overlays.v1');
  if Result and RegKeyExists(HKLM64, HandlerPath) then
    Result := RegQueryStringValue(HKLM64, HandlerPath, '', Value) and (Value = Id) and
      RegQueryStringValue(HKLM64, ClassPath, 'MoaVcsOwner', Value) and (Value = 'MoaVcs.Overlays.v1');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CheckOwned('{#ConflictId}', '{#ConflictNormal}') or
     not CheckOwned('{#ModifiedId}', '{#ModifiedNormal}') or
     not CheckOwned('{#UntrackedId}', '{#UntrackedNormal}') or
     not CheckOwned('{#ConflictId}', '{#ConflictPriority}') or
     not CheckOwned('{#ModifiedId}', '{#ModifiedPriority}') or
     not CheckOwned('{#UntrackedId}', '{#UntrackedPriority}') then
    Result := '다른 프로그램의 셸 확장 등록을 덮어쓸 수 없습니다.';
end;

procedure RemoveOwnedHandler(Id, Name: String; RequireCurrentDll: Boolean);
var Value, ClassPath, HandlerPath: String;
begin
  ClassPath := 'Software\Classes\CLSID\' + Id;
  HandlerPath := '{#OverlayRoot}\' + Name;
  if RegQueryStringValue(HKLM64, ClassPath, 'MoaVcsOwner', Value) and (Value = 'MoaVcs.Overlays.v1') and
     RegQueryStringValue(HKLM64, HandlerPath, '', Value) and (Value = Id) then
  begin
    if not RequireCurrentDll or
       (RegQueryStringValue(HKLM64, HandlerPath, 'MoaVcsDll', Value) and
        (CompareText(Value, ExpandConstant('{#ShellDll}')) = 0)) then
      RegDeleteKeyIncludingSubkeys(HKLM64, HandlerPath);
  end;
end;

procedure RemoveOwnedClass(Id: String);
var Value, ClassPath, ServerPath: String;
begin
  ClassPath := 'Software\Classes\CLSID\' + Id;
  ServerPath := ClassPath + '\InprocServer32';
  if RegQueryStringValue(HKLM64, ClassPath, 'MoaVcsOwner', Value) and (Value = 'MoaVcs.Overlays.v1') and
     RegQueryStringValue(HKLM64, ServerPath, '', Value) and
     (CompareText(Value, ExpandConstant('{#ShellDll}')) = 0) then
  begin
    RegDeleteKeyIncludingSubkeys(HKLM64, ClassPath);
    RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved', Id);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if IsPriorityInstall() then
    begin
      RemoveOwnedHandler('{#ConflictId}', '{#ConflictNormal}', False);
      RemoveOwnedHandler('{#ModifiedId}', '{#ModifiedNormal}', False);
      RemoveOwnedHandler('{#UntrackedId}', '{#UntrackedNormal}', False);
    end
    else
    begin
      RemoveOwnedHandler('{#ConflictId}', '{#ConflictPriority}', False);
      RemoveOwnedHandler('{#ModifiedId}', '{#ModifiedPriority}', False);
      RemoveOwnedHandler('{#UntrackedId}', '{#UntrackedPriority}', False);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedHandler('{#ConflictId}', '{#ConflictNormal}', True);
    RemoveOwnedHandler('{#ModifiedId}', '{#ModifiedNormal}', True);
    RemoveOwnedHandler('{#UntrackedId}', '{#UntrackedNormal}', True);
    RemoveOwnedHandler('{#ConflictId}', '{#ConflictPriority}', True);
    RemoveOwnedHandler('{#ModifiedId}', '{#ModifiedPriority}', True);
    RemoveOwnedHandler('{#UntrackedId}', '{#UntrackedPriority}', True);
    RemoveOwnedClass('{#ConflictId}');
    RemoveOwnedClass('{#ModifiedId}');
    RemoveOwnedClass('{#UntrackedId}');
  end;
end;
