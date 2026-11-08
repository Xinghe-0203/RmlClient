; Raft Mod Loader Client 安装包脚本
;
; 构建方式（先发布，再编译安装包）：
;   dotnet publish src\RmlClient.App\RmlClient.App.csproj -c Release -r win-x64 ^
;     -p:Platform=x64 --self-contained true -o publish\app
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\RmlClient.iss
;
; 产物：dist\RmlClient-Setup-<版本>.exe

#define AppName        "Raft Mod Loader Client"
#define AppVersion     "1.0.2"
#define AppPublisher   "RML Client"
#define AppExe         "RmlClient.exe"
; 升级判断依据，换掉它会让新版本装成另一套程序，不要再改。
#define AppId          "{{09F8DE1C-E97B-4581-929F-F658D66B36FB}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; 默认装到当前用户（PrivilegesRequired=lowest 时 {autopf} = %LOCALAPPDATA%\Programs），
; 不需要管理员权限；用户也可以在向导里主动选择「为所有用户安装」。
DefaultDirName={autopf}\Raft Mod Loader Client
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 客户端是 x64 自包含发布，不依赖目标机装 .NET 运行时。
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=RmlClient-Setup-{#AppVersion}
SetupIconFile=..\src\RmlClient.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto

; 安装/卸载时如果客户端还开着，先让它正常退出，避免文件占用导致安装失败。
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; publish\app 是自包含的框架依赖目录（约 210 MB / 500 余个文件）。
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清安装目录。%APPDATA%\RaftModLoader 里是用户的 mod、配置和日志，
; 而且和官方启动器共用，默认不碰（卸载时会单独询问）。
Type: filesandordirs; Name: "{app}"

[Code]
const
  UserDataDir = '\RaftModLoader';

// 卸载时询问是否一并删除 %APPDATA%\RaftModLoader。
// 那个目录和官方启动器共用，误删会丢掉全部已装 mod，所以默认「否」。
//
// 注意：静默卸载（/VERYSILENT）时**不问也不删**。
// 早期版本在静默卸载时照样弹 MsgBox，结果无人应答的对话框把卸载进程挂住，
// 调用方用 -Wait 等不到，误以为已完成，用户数据就在这种「卡住的卸载」里被清掉过。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if UninstallSilent then
      Exit;

    DataDir := ExpandConstant('{userappdata}') + UserDataDir;
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除 Raft Mod Loader 的用户数据？' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                '这里面包含已安装的 mod、启动器配置和日志。' + #13#10 +
                '如果你还在用官方启动器，请选择「否」，否则两边的 mod 会一起消失。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
