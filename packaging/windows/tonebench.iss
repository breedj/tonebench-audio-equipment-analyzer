; Tonebench - Audio Equipment Analyzer — Windows installer (Inno Setup 6).
;
; Built by packaging/windows/build.ps1 (locally and in .github/workflows/release.yml), which passes
; /DAppVersion, /DPublishDir and /DOutputDir. Compiling the script by hand from this folder uses the
; defaults below, i.e. a publish already made into code/publish/win-x64.
;
; Per-user on purpose: the app needs nothing an ordinary user cannot do (a loopback-only web server,
; a WinUSB device the user can already open, files under the user's own profile), so it installs to
; {localappdata}\Programs with no UAC prompt and no HKLM writes. It also writes nothing under {app}
; at runtime — logs, diagnostics and the configuration database live in %LOCALAPPDATA%\Tonebench
; (TonebenchAppData), projects in Documents\Tonebench Projects — which is what makes an upgrade a plain
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
#define AppName "Tonebench - Audio Equipment Analyzer"
; The display name is the full one; everything that becomes a path, a Start-menu group or a
; shortcut label uses the short one. The full name carries an em dash, which Windows allows in a
; directory name and which nobody wants to type at a prompt.
#define AppShortName "Tonebench"
#define AppExe "Tonebench.App.exe"
#define AppUrl "https://github.com/breedj/tonebench-audio-equipment-analyzer"

[Setup]
; Identifies the app across upgrades. Never change it for a version bump.
; Changed exactly once, 2026-10-04, when the app was renamed from QA403 Audio Analyzer to
; Tonebench: a rename under the old AppId would have upgraded in place and then kept installing
; into {localappdata}\Programs\QA403 Audio Analyzer for ever (UsePreviousAppDir), while
; UsePreviousAppDir=no would have orphaned the old folder's files with no uninstaller left that
; knew about them. A new id means the pre-rename install stays listed until it is uninstalled,
; which is the honest outcome and is what INSTALL.md now tells the user to do. Old id, for the
; record: {{ABEA4625-091A-4FED-84D9-7ACC913162E4}
AppId={{571F7AAF-75FC-4BBB-96DC-24B7D5919A88}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Joost Breed
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppShortName}
DefaultGroupName={#AppShortName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=tonebench-{#AppVersion}-win-x64-setup
SetupIconFile=tonebench.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; A running Tonebench.App.exe holds its own file open, so an upgrade over a running app would fail.
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
Name: "{group}\{#AppShortName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Start the analyzer with a QA403 or a sound card"
Name: "{group}\{#AppShortName} (simulator)"; Filename: "{app}\{#AppExe}"; Parameters: "--simulate"; WorkingDir: "{app}"; Comment: "Start the analyzer against the simulated device"
Name: "{group}\Uninstall {#AppShortName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
