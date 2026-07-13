; Inno Setup - Instalador do Klip
; Gera um .exe de instalacao para Windows 11 (x64).
; Compilar com:  iscc installer\Klip.iss   (ou o script tools\build-installer.ps1)

#define AppName "Klip"
#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#define AppPublisher "PoBruno"
#define AppUrl "https://github.com/PoBruno/klip"
#define AppExeName "Klip.exe"

[Setup]
; AppId identifica o produto para upgrades/desinstalacao (nao mudar entre versoes)
AppId={{8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; Installs per user (no admin needed); the app only asks for UAC when it really needs it
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Klip-Setup-{#AppVersion}
SetupIconFile=..\src\Klip.App\Assets\Klip.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Fecha o app automaticamente se estiver rodando durante o update/desinstalacao
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "portugues"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Iniciar o Klip com o Windows"; GroupDescription: "Inicializacao:"

[Files]
; O executavel self-contained (gerado por tools\build-exe.ps1 em publish\)
Source: "..\publish\Klip.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Autostart (opcional via task): a chave Run e limpa na desinstalacao (uninsdeletevalue)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Klip"; ValueData: """{app}\{#AppExeName}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Oferece abrir o app ao final da instalacao
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; before removing, the app reverts the shortcut keys it took over
; (Win+V / Print Screen), putting Windows back to how it was.
Filename: "{app}\{#AppExeName}"; Parameters: "--revert-registry"; Flags: runhidden waituntilterminated; RunOnceId: "RevertRegistry"
