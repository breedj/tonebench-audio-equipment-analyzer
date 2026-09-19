; QA403 Audio Analyzer — Windows installer (Inno Setup 6).
;
; Built by packaging/windows/build.ps1 (locally and in .github/workflows/release.yml), which passes
; /DAppVersion, /DPublishDir and /DOutputDir. Compiling the script by hand from this folder uses the
; defaults below, i.e. a publish already made into code/publish/win-x64.
;
; Per-user on purpose: the app needs nothing an ordinary user cannot do (a loopback-only web server,
; a WinUSB device the user can already open, files under the user's own profile), so it installs to
; {localappdata}\Programs with no UAC prompt and no HKLM writes. It also writes nothing under {app}
; at runtime — logs, diagnostics and the configuration database live in %LOCALAPPDATA%\Qa403
; (Qa403AppData), projects in Documents\QA403 Projects — which is what makes an upgrade a plain
; file replacement and an uninstall safe: there is no [UninstallDelete] here and there must not be.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#define AppName "QA403 Audio Analyzer"
#define AppExe "Qa403.App.exe"
#define AppUrl "https://github.com/breedj/qa40x-audio-analyzer-cp"

[Setup]
; Generated once; identifies the app across upgrades. Never change it.
AppId={{ABEA4625-091A-4FED-84D9-7ACC913162E4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Joost Breed
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=qa403-audio-analyzer-{#AppVersion}-win-x64-setup
SetupIconFile=qa403.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; A running Qa403.App.exe holds its own file open, so an upgrade over a running app would fail.
; Restart Manager finds it and closes it — verified 2026-09-19: the app's log shows the whole
; teardown through "Host stopped cleanly" before the exe is replaced, even under /VERYSILENT. Not
; relaunched afterwards: a relaunch from inside the installer would open a browser tab, and [Run]
; below already offers that on a fresh install.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; A whitelist rather than {#PublishDir}\*: the publish folder also holds a loose wwwroot\ copy (the
; UI is embedded in the exe already), appsettings.Development.json and the static-web-assets manifest.
; libusb-1.0.dll is inside the single-file exe (self-extracted at startup); QuestPDF's default font
; archive is not, because the bundler only absorbs native libraries, so it ships beside the exe where
; QuestPDF looks for it. build.ps1 checks both before compiling this.
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\QuestPDF.Fonts.Lato.br"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Every shortcut starts the exe DIRECTLY — no cmd /c, no launcher. The console window's close button
; is the app's graceful-shutdown path, and only a process that owns its console gets the ~5 s Windows
; allows a close handler (see the dotnet-run rule in run.bat and CLAUDE.md).
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Start the analyzer with the QA403"
Name: "{group}\{#AppName} (simulator)"; Filename: "{app}\{#AppExe}"; Parameters: "--simulate"; WorkingDir: "{app}"; Comment: "Start the analyzer against the simulated device"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
