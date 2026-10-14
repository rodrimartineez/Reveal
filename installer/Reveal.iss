; Reveal - Windows Vocabulary Companion
; Inno Setup 6 script. Build with:  powershell -File installer\build-installer.ps1
; Per-user install (no administrator rights): %LOCALAPPDATA%\Programs\Reveal

#define MyAppName "Reveal"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppExe "Reveal.exe"

[Setup]
AppId={{8F6C3B1E-5A44-4D0B-9C57-2E1D7A0B4C11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Reveal
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Reveal-Setup-{#MyAppVersion}
SetupIconFile=..\src\Reveal.App\Assets\reveal.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
Compression=lzma2/ultra
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.19041

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar Reveal con Windows"; GroupDescription: "Opciones:"; Flags: unchecked
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Opciones:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The Start-menu entry is what makes Reveal findable by typing "Reveal" in the Windows search box.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Comment: "Windows Vocabulary Companion"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Reveal"; \
  ValueData: """{app}\{#MyAppExe}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Abrir Reveal"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopReveal;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup: Boolean;
begin
  StopReveal;
  Result := True;
end;

function InitializeUninstall: Boolean;
begin
  StopReveal;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Never remove the user's vocabulary document. Settings and downloaded models are optional to keep.
    if MsgBox('¿Quieres eliminar también la configuración de Reveal y los modelos de traducción descargados (≈ 230 MB)?'#13#10 +
              'Tu documento de vocabulario no se toca.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      Data := ExpandConstant('{localappdata}\Reveal');
      DelTree(Data, True, True, True);
      DelTree(ExpandConstant('{userappdata}\Reveal'), True, True, True);
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Reveal');
    end;
  end;
end;
