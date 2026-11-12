#define AppName "FrameForge"
#define AppVersion "0.3.50"
#define AppPublisher "FrameForge"

[Setup]
AppId={{1A6A35B4-CE8B-4A2D-A135-F1A340C0D164}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FrameForge
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\FrameForge.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=FrameForge-Setup-0.3.50
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UseSetupLdr=yes
; Escreve sempre um log em %TEMP%\Setup Log*.txt para diagnosticar falhas de permissões/temporários.
SetupLogging=yes

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho no ambiente de trabalho"; GroupDescription: "Atalhos:"

[Files]
; Só interessam os binários de Windows x64. Exclui runtimes de outras plataformas/arquiteturas
; e o .pdb (reduz o instalador e o trabalho do antivírus na pasta temporária).
Source: "..\publicar\*"; DestDir: "{app}"; Excludes: "*.pdb,runtimes\android-*\*,runtimes\linux-*\*,runtimes\maccatalyst-*\*,runtimes\osx*\*,runtimes\browser\*,runtimes\unix\*,runtimes\win-arm64\*,runtimes\win-x86\*,arm64\*,x86\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FrameForge"; Filename: "{app}\FrameForge.exe"
Name: "{autodesktop}\FrameForge"; Filename: "{app}\FrameForge.exe"; Tasks: desktopicon
