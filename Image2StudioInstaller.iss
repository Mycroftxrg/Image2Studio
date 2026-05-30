#define MyAppName "Image2 Studio"
#define MyAppVersion "1.2"
#define MyAppPublisher "Image2Studio"
#define MyAppExeName "Image2Studio.exe"
#define SourceDir "C:\Users\ASUS\Desktop\Image2Studio\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{9D14816D-6829-4E6C-934B-D8142A633F26}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir=C:\Users\ASUS\Desktop\Image2Studio\installer
OutputBaseFilename=Image2StudioSetup-{#MyAppVersion}-win-x64
SetupIconFile={#SourceDir}\appicon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartIfNeededByRun=no
UsePreviousAppDir=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\appicon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\appicon.ico"; Tasks: desktopicon

[Run]
Filename: "{sys}\cmd.exe"; Parameters: "/C start """" /MIN cmd /C ""timeout /t 8 /nobreak >nul & del /F /Q """"{srcexe}"""""""; Flags: runhidden; Check: IsAutoUpdateSource
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsAutoUpdateSource: Boolean;
begin
  Result := Pos('\updates\', Lowercase(ExpandConstant('{srcexe}'))) > 0;
end;
