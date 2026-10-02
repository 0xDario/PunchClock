; Inno Setup 6 script for the PunchClock v2 Windows installer.
; Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=2.0.0 /DPublishDir=<publish root> /DOutputDir=<dist> installer\PunchClock.iss
; <publish root>\app       self-contained PunchClock.exe (WPF kiosk)
; <publish root>\migration self-contained PunchClock.Import.exe, plus export\ with the legacy exporter

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<folder with app\ and migration\>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Never change AppId: Windows uses it to recognise upgrades of the same app.
AppId={{84017BE2-EF8B-46C3-911F-32859E4B5C20}
AppName=PunchClock
AppVersion={#AppVersion}
AppVerName=PunchClock {#AppVersion}
AppPublisher=PunchClock
AppPublisherURL=https://github.com/0xDario/PunchClock
AppSupportURL=https://github.com/0xDario/PunchClock#moving-from-the-old-punchclock-app
DefaultDirName={autopf}\PunchClock
DefaultGroupName=PunchClock
DisableProgramGroupPage=yes
; Machine-wide install: the database in %ProgramData% is shared by every Windows account.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=PunchClock-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PunchClock.exe
; The legacy v1 app was a copied folder, not an installer, so nothing here touches it.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Dirs]
; The database folder outlives the app: uninstalling never deletes punches.
; Users-modify lets every account on the kiosk PC punch, not only whoever ran setup.
Name: "{commonappdata}\PunchClock"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#PublishDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\migration\*"; DestDir: "{app}\Migration"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PunchClock"; Filename: "{app}\PunchClock.exe"
Name: "{group}\Move data from the old app\1. Export old data"; Filename: "{app}\Migration\export\run-export.cmd"; WorkingDir: "{app}\Migration\export"; Check: ExporterBundled
Name: "{group}\Move data from the old app\2. Import into PunchClock"; Filename: "{app}\Migration\PunchClock.Import.exe"; WorkingDir: "{app}\Migration"
Name: "{autodesktop}\PunchClock"; Filename: "{app}\PunchClock.exe"; Tasks: desktopicon

[Code]
function ExporterBundled: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Migration\export\run-export.cmd'));
end;
