; kNot C# Installer — Inno Setup

#define MyAppName "kNot"
#define MyAppVersion "1.0.0"
#define MyAppExeName "knot.exe"

[Setup]
AppId={{kNot-CS-2024-DPI-WARP}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\kNot
DefaultGroupName=kNot
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=kNot-Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
UninstallDisplayIcon={app}\knot.exe
UninstallDisplayName=kNot

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "bin\Release\net10.0\win-x64\publish\knot.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "data\dpi\WinDivert.dll"; DestDir: "{app}\data\dpi"; Flags: ignoreversion
Source: "data\dpi\WinDivert64.sys"; DestDir: "{app}\data\dpi"; Flags: ignoreversion
Source: "data\dpi\quic_initial_www_google_com.bin"; DestDir: "{app}\data\dpi"; Flags: ignoreversion
Source: "data\dpi\quic_initial_dbankcloud_ru.bin"; DestDir: "{app}\data\dpi"; Flags: ignoreversion
Source: "data\dpi\tls_clienthello_www_google_com.bin"; DestDir: "{app}\data\dpi"; Flags: ignoreversion

[Dirs]
Name: "{app}\data\conf"
Name: "{app}\data\original"
Name: "{app}\data\cache"
Name: "{app}\data\logs"

[Icons]
Name: "{group}\kNot"; Filename: "{app}\knot.exe"; IconFilename: "{app}\knot.exe"
Name: "{group}\Uninstall kNot"; Filename: "{uninstallexe}"
Name: "{commondesktop}\kNot"; Filename: "{app}\knot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\knot.exe"; Description: "Запустить kNot"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "sc"; Parameters: "stop wiresock-client-service"; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c taskkill /f /im knot.exe 2>nul"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Create settings.json
    SaveStringToFile(ExpandConstant('{app}\data\settings.json'),
      '{'#13#10'  "first_run": true,'#13#10'  "dpi_strategy": "fake"'#13#10'}', False);

    // Install WireSock if not present
    if not FileExists(ExpandConstant('{pf}\WireSock Secure Connect\bin\wiresock-client.exe')) then
    begin
      WizardForm.StatusLabel.Caption := 'Установка WireSock VPN...';
      Exec(ExpandConstant('{cmd}'), '/c winget install NTKERNEL.WireSockVPNClient --accept-package-agreements --accept-source-agreements --silent',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
