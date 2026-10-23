#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputPath
  #define OutputPath SourcePath
#endif
#define AppVersion GetStringFileInfo(PayloadDir + "\Hzip.exe", "ProductVersion")

[Setup]
AppId={{51338024-AC9E-4C7D-80F4-094B07238940}
AppName=Hzip
AppVersion={#AppVersion}
AppVerName=Hzip {#AppVersion}
AppPublisher=Hzip
AppPublisherURL=https://github.com/RUI-JACK/Hzip
AppSupportURL=https://github.com/RUI-JACK/Hzip/issues
AppUpdatesURL=https://github.com/RUI-JACK/Hzip/releases/latest
DefaultDirName={localappdata}\Programs\Hzip
DefaultGroupName=Hzip
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.17763
OutputDir={#OutputPath}
OutputBaseFilename=Hzip-Setup-{#AppVersion}-x64
SetupIconFile={#PayloadDir}\Hzip.ico
UninstallDisplayIcon={app}\Hzip.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=yes
Uninstallable=yes
VersionInfoDescription=Hzip 中文安装程序
VersionInfoVersion={#AppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："
Name: "classicmenu"; Description: "启用经典右键菜单"; GroupDescription: "资源管理器集成（可选）："; Flags: unchecked
Name: "modernmenu"; Description: "启用 Windows 11 原生右键菜单（需要管理员确认）"; GroupDescription: "资源管理器集成（可选）："; Flags: unchecked; Check: IsWindows11

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Hzip"; Filename: "{app}\Hzip.exe"; WorkingDir: "{app}"; AppUserModelID: "Hzip.Desktop"
Name: "{group}\卸载 Hzip"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Hzip"; Filename: "{app}\Hzip.exe"; WorkingDir: "{app}"; AppUserModelID: "Hzip.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\Hzip.exe"; Description: "启动 Hzip"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var IntegrationFailed: Boolean;

function IsWindows11: Boolean;
var Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Build >= 22000;
end;

function RunHelper(Action: String): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\Hzip.SetupHelper.exe'), Action, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0);
  if not Result then Log('Hzip integration failed: ' + Action + ', exit code: ' + IntToStr(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if WizardIsTaskSelected('classicmenu') then
      if not RunHelper('classic') then IntegrationFailed := True;
    if WizardIsTaskSelected('modernmenu') then
      if not RunHelper('modern') then IntegrationFailed := True;
    if IntegrationFailed then
      SuppressibleMsgBox('Hzip 已安装，但部分右键菜单未启用。可以稍后在“设置与集成”中重试。', mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if IntegrationFailed then Result := 20 else Result := 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if FileExists(ExpandConstant('{app}\Hzip.SetupHelper.exe')) then
      if not RunHelper('cleanup') then
        RaiseException('无法清理此副本的右键菜单，已停止卸载。请关闭 Hzip 后重试，并完成可能出现的管理员确认。');
end;
