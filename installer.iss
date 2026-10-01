; Stedjcast installer (Inno Setup 6).
; Build: dotnet publish (see README), then: ISCC installer.iss  ->  dist\Stedjcast-<version>-Setup.exe

#define AppExe "publish\Stedjcast.exe"
#define AppVersion Copy(GetStringFileInfo(AppExe, "ProductVersion"), 1, Pos("+", GetStringFileInfo(AppExe, "ProductVersion") + "+") - 1)

[Setup]
AppId={{6E0B7C1A-3F52-4D8E-9A61-5C2F4B7D9E13}
AppName=Stedjcast
AppVersion={#AppVersion}
AppPublisher=beppeilgommista
AppPublisherURL=https://github.com/beppeilgommista/stedjcast
DefaultDirName={autopf}\Stedjcast
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\Stedjcast.exe
OutputDir=dist
OutputBaseFilename=Stedjcast-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Native VST3 bridge shipped up to 0.3, no longer used.
Type: files; Name: "{app}\Vst3Pont.dll"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Stedjcast"; Filename: "{app}\Stedjcast.exe"
Name: "{autodesktop}\Stedjcast"; Filename: "{app}\Stedjcast.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Stedjcast.exe"; Description: "{cm:LaunchProgram,Stedjcast}"; Flags: nowait postinstall skipifsilent
