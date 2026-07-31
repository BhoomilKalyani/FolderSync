; Inno Setup script for Folder Sync
; Built by build-installer.ps1, which publishes the app and passes AppVersion in.
;
; Design notes:
;  * Per-user install (PrivilegesRequired=lowest) - no UAC prompt, and it matches the
;    per-user scheduled tasks the app creates.
;  * The install path must stay STABLE across versions: the scheduled tasks store an
;    absolute path to FolderSync.exe, so a versioned folder would break them on update.
;  * Uninstall removes the scheduled tasks but keeps config and logs in %LOCALAPPDATA%.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "Folder Sync"
#define AppExe "FolderSync.exe"
#define AppPublisher "Bhoomil Kalyani"
#define AppUrl "https://github.com/BhoomilKalyani/FolderSync"

[Setup]
; A fixed AppId is what makes an install an *upgrade* rather than a second copy - never change it.
AppId={{8F2C6A41-3D5B-4E27-9A6E-27B9C0F1D4A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright © 2026 Bhoomil Kalyani
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={localappdata}\Programs\FolderSync
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=FolderSyncSetup-{#AppVersion}
SetupIconFile=..\FolderSync.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Let Setup shut the app (and its tray icon) down instead of failing on locked files.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupsync"; Description: "Sync automatically at sign-in and when I unlock the screen"; GroupDescription: "Automatic sync:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Re-point any schedule left over from a previous/other install location at this copy.
Filename: "{app}\{#AppExe}"; Parameters: "--repair-schedules"; Flags: runhidden waituntilterminated; StatusMsg: "Updating scheduled tasks..."
; Optional: register the logon/unlock schedule straight away, pointing at the installed exe.
Filename: "{app}\{#AppExe}"; Parameters: "--register-logon"; Flags: runhidden waituntilterminated; Tasks: startupsync; StatusMsg: "Setting up automatic sync..."
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the scheduled tasks so nothing is left pointing at a deleted program.
Filename: "{app}\{#AppExe}"; Parameters: "--remove-schedules"; RunOnceId: "RemoveSchedules"; Flags: runhidden
