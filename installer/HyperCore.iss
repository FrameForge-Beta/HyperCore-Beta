#define AppName "HyperCore"
#define AppVersion "0.5.3"
#define AppPublisher "HyperCore"
#define AppExe "HyperCore.exe"
; Nome antigo da app (até à 0.3.50): binários, atalhos e tarefa agendada a limpar ao atualizar.
#define LegacyName "FrameForge"

[Setup]
; MANTER este AppId: é o mesmo do FrameForge, para a atualização substituir a versão antiga em
; Programas e Funcionalidades em vez de instalar lado a lado.
AppId={{1A6A35B4-CE8B-4A2D-A135-F1A340C0D164}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}
; Instalação nova: Programas\HyperCore. A atualizar por cima do FrameForge, o Inno reutiliza a pasta
; anterior (UsePreviousAppDir, por defeito) e o [InstallDelete] remove lá os binários antigos.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
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

[InstallDelete]
; Binários do FrameForge (nome antigo): sem isto o FrameForge.exe antigo continuava na pasta e a funcionar.
Type: files; Name: "{app}\{#LegacyName}.exe"
Type: files; Name: "{app}\{#LegacyName}.dll"
Type: files; Name: "{app}\{#LegacyName}.pdb"
Type: files; Name: "{app}\{#LegacyName}.deps.json"
Type: files; Name: "{app}\{#LegacyName}.runtimeconfig.json"
; Atalhos antigos (instalador Inno e NSIS; menu Iniciar e ambiente de trabalho, de todos os utilizadores e do atual)
Type: files; Name: "{autoprograms}\{#LegacyName}.lnk"
Type: files; Name: "{autoprograms}\{#LegacyName}\{#LegacyName}.lnk"
Type: dirifempty; Name: "{autoprograms}\{#LegacyName}"
Type: files; Name: "{userprograms}\{#LegacyName}\{#LegacyName}.lnk"
Type: dirifempty; Name: "{userprograms}\{#LegacyName}"
Type: files; Name: "{autodesktop}\{#LegacyName}.lnk"
Type: files; Name: "{userdesktop}\{#LegacyName}.lnk"

[Files]
; Só interessam os binários de Windows x64. Exclui runtimes de outras plataformas/arquiteturas
; e o .pdb (reduz o instalador e o trabalho do antivírus na pasta temporária).
Source: "..\publicar\*"; DestDir: "{app}"; Excludes: "*.pdb,runtimes\android-*\*,runtimes\linux-*\*,runtimes\maccatalyst-*\*,runtimes\osx*\*,runtimes\browser\*,runtimes\unix\*,runtimes\win-arm64\*,runtimes\win-x86\*,arm64\*,x86\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; A tarefa agendada antiga "FrameForge" (arranque com o Windows) apontava para o FrameForge.exe, que deixa de existir.
; Se o arranque com o Windows estiver ligado, a app cria a tarefa "HyperCore" quando voltar a abrir (por exemplo, já a seguir).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#LegacyName}"" /F"; Flags: runhidden waituntilterminated; StatusMsg: "A remover a tarefa de arranque antiga..."
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
; Remove as tarefas de arranque com o Windows (nome novo e antigo). Os dados em %AppData% ficam.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DelTaskHyperCore"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#LegacyName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DelTaskFrameForge"
