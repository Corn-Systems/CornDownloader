; ─────────────────────────────────────────────────────────────────────────
; CornDownloader.iss — Corn Systems installer script (Inno Setup 6.x)
;
; Lives in:  CornDownloader\CornDownloader\  (next to the .csproj)
;
; USAGE:
;   1. Publish (from CornDownloader\CornDownloader\). All single-file /
;      self-contained settings live in the .csproj, so this is just:
;        dotnet publish -c Release
;      Output: bin\Release\net10.0-windows\win-x64\publish\CornDownloader.exe
;   2. Compile:
;        ISCC.exe CornDownloader.iss
;   3. Output: installer_output\CornDownloader-Setup-<version>.exe
;
; The version is read from the published exe so it can't drift from the .csproj.
; ─────────────────────────────────────────────────────────────────────────

#define MyAppName      "Corn Downloader"
#define MyAppPublisher "Corn Systems"
#define MyAppURL       "https://github.com/Corn-Systems/CornDownloader"
#define MyAppExeName   "CornDownloader.exe"
#define PublishDir     "bin\Release\net10.0-windows\win-x64\publish"
#define MyAppVersion   GetVersionNumbersString(PublishDir + "\" + MyAppExeName)

[Setup]
; Fixed GUID — never change between releases; Windows (and the CornTools
; launcher) use it to detect upgrades / uninstall.
AppId={{B7E4C2A9-5D31-4F8E-9A6B-2C7D8E1F0A43}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}

; Per-machine, 64-bit only, Program Files\Corn Downloader
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppPublisher}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809+ — the floor for winget (App Installer)
MinVersion=10.0.17763

OutputDir=installer_output
OutputBaseFilename=CornDownloader-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; Prompt to close a running instance instead of failing mid-copy
CloseApplications=yes
RestartApplications=no

#ifexist "..\LICENSE"
LicenseFile=..\LICENSE
#endif
#ifexist "Assets\CornDownloader.ico"
SetupIconFile=Assets\CornDownloader.ico
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut on by default (no "unchecked" flag)
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Picks up the single self-contained exe (or a full folder if you ever publish framework-dependent)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifexist "..\LICENSE"
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}";           Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";     Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; runasoriginaluser is REQUIRED here: the app probes for winget via the per-user
; App Execution Alias (%LOCALAPPDATA%\Microsoft\WindowsApps). Launching it from
; the elevated Setup process would run it as the admin account and it would
; report "winget not found" on first launch.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; Leave %AppData%\CornSystems\CornDownloader alone (per-user settings + logs;
; survives reinstall). Only sweep leftovers inside the install dir.
Type: files; Name: "{app}\*.log"
