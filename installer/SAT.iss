; Inno Setup script for SAT (Site Assignment Tracker).
;
; Build it with:   .\publish.ps1 -Installer            (bump + publish + installer)
;            or:   .\publish.ps1 -Bump none -Installer (installer for the current version)
;            or:   open this file in the Inno Setup IDE and press Compile (after publishing)
;
; The version is read from the published PMDashboard.exe, so it always matches the build.
; Output: installer\Output\SAT-Setup-<version>.exe
;
; Never change AppId — it is how Windows knows a new installer is an upgrade of the old one.

#define AppName      "SAT"
#define AppFullName  "SAT - Site Assignment Tracker"
#define AppPublisher "IEM"
#define AppExe       "PMDashboard.exe"
#define PublishDir   AddBackslash(SourcePath) + "..\bin\Release\net8.0-windows\win-x64\publish"

#ifndef AppVersion
  #if !FileExists(PublishDir + "\" + AppExe)
    #error "Published build not found - run .\publish.ps1 first."
  #endif
  ; File version is "1.10.0.0" — RemoveFileExt drops the last ".0" to match <Version> in the csproj.
  #define AppVersion RemoveFileExt(GetVersionNumbersString(PublishDir + "\" + AppExe))
#endif

[Setup]
AppId={{C21E29AA-2417-458D-A762-8C77492FC8D4}
AppName={#AppFullName}
AppVersion={#AppVersion}
AppVerName={#AppFullName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppFullName}

; Installs per user by default (no admin rights needed); the user can choose "all users" if they are an admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\SAT
; Fixed folder: [InstallDelete] below clears it on upgrade, so it must never be a folder the user picked.
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=Output
OutputBaseFilename=SAT-Setup-{#AppVersion}
SetupIconFile=..\favicon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppFullName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Close a running SAT before replacing its files, and offer to restart it afterwards.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Remove the previous version's files first so DLLs dropped from a newer build don't linger.
; User data is NOT here — it lives in %LOCALAPPDATA%\PMDashboard and %APPDATA%\PMDashboard (or the network share).
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppFullName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppFullName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Uninstall leaves the database, settings and logs in %LOCALAPPDATA% / %APPDATA% alone on purpose.
