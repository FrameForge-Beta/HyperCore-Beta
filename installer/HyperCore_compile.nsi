; HyperCore NSIS Installer - Versão melhorada
; Compilar com: makensis HyperCore.nsi
; Requer NSIS 3.08+ com plugins: MUI2, LogicLib, nsExec, x64

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "WinMessages.nsh"

; =============================================================================
; CONFIGURAÇÕES DO PRODUTO
; =============================================================================
!define APP_NAME "HyperCore"
!define APP_VERSION "0.5.5"
!define APP_PUBLISHER "HyperCore"
!define APP_EXE "HyperCore.exe"
!define LEGACY_NAME "FrameForge"
!define APP_ID "{1A6A35B4-CE8B-4A2D-A135-F1A340C0D164}"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

; =============================================================================
; CONFIGURAÇÕES DO INSTALADOR
; =============================================================================
Name "${APP_NAME} ${APP_VERSION}"
OutFile "..\artifacts\installer\HyperCore-Setup-${APP_VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64
SetDatablockOptimize on
BrandingText "${APP_NAME}"

; Ícones
!define MUI_ICON "..\Assets\app.ico"
!define MUI_UNICON "..\Assets\app.ico"

; Interface
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Bem-vindo ao Assistente de Instalacao do ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "Este assistente ira guiar-lo pela instalacao do ${APP_NAME} ${APP_VERSION}.\n\nRecomenda-se fechar outros programas antes de continuar."
!define MUI_FINISHPAGE_TITLE "Instalacao Concluida"
!define MUI_FINISHPAGE_TEXT "O ${APP_NAME} foi instalado com sucesso no seu computador."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Executar ${APP_NAME}"
!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchApp"
!define MUI_FINISHPAGE_LINK "Visite nosso site"
!define MUI_FINISHPAGE_LINK_LOCATION "https://github.com/HyperCore"

; Paginas
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Portuguese"

; =============================================================================
; INFORMATICOES DE VERSAO DO EXECUTAVEL
; =============================================================================
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "Instalador do ${APP_NAME}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (C) ${APP_PUBLISHER}"
VIAddVersionKey "LegalTrademarks" "${APP_NAME} e marca registrada de ${APP_PUBLISHER}"
VIAddVersionKey "OriginalFilename" "${APP_NAME}-Setup-${APP_VERSION}.exe"
VIAddVersionKey "InternalName" "${APP_NAME} Setup"
VIAddVersionKey "Comments" "Instalador oficial do ${APP_NAME} - Monitoramento, diagnostico e desempenho para jogos"
VIAddVersionKey "BuildDate" "__DATE__ __TIME__"

; =============================================================================
; VARIAVEIS GLOBAIS
; =============================================================================
Var /GLOBAL InstallMode
Var /GLOBAL PreviousInstallDir
Var /GLOBAL CreateDesktopShortcut
Var /GLOBAL CreateStartMenuShortcut
Var /GLOBAL AutoStartWithWindows
Var /GLOBAL CheckForUpdates
Var /GLOBAL SilentInstall

; =============================================================================
; FUNCOES AUXILIARES
; =============================================================================

; Detecta instalacao anterior
Function DetectPreviousInstall
    Push $0
    Push $1
    
    StrCpy $PreviousInstallDir ""
    
    SetRegView 64
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
    ${If} $0 == ""
        SetRegView 32
        ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
        SetRegView 64
    ${EndIf}
    ${If} $0 != ""
        StrCpy $PreviousInstallDir $0
    ${Else}
        IfFileExists "$PROGRAMFILES64\${LEGACY_NAME}\${LEGACY_NAME}.exe" FoundLegacy
        IfFileExists "$PROGRAMFILES64\${APP_NAME}\${APP_EXE}" FoundNew
        IfFileExists "$LOCALAPPDATA\${LEGACY_NAME}\${LEGACY_NAME}.exe" FoundLegacyLocal
        IfFileExists "$LOCALAPPDATA\${APP_NAME}\${APP_EXE}" FoundNewLocal
        Goto DetectDone
        
        FoundLegacy:
        StrCpy $PreviousInstallDir "$PROGRAMFILES64\${LEGACY_NAME}"
        Goto DetectDone
        
        FoundNew:
        StrCpy $PreviousInstallDir "$PROGRAMFILES64\${APP_NAME}"
        Goto DetectDone
        
        FoundLegacyLocal:
        StrCpy $PreviousInstallDir "$LOCALAPPDATA\${LEGACY_NAME}"
        Goto DetectDone
        
        FoundNewLocal:
        StrCpy $PreviousInstallDir "$LOCALAPPDATA\${APP_NAME}"
        
    DetectDone:
    ${EndIf}
    
    ${If} $PreviousInstallDir != ""
        StrCpy $InstallMode "upgrade"
    ${Else}
        StrCpy $InstallMode "fresh"
    ${EndIf}
    
    Pop $1
    Pop $0
FunctionEnd

; Limpa ficheiros da versao antiga (FrameForge)
Function CleanLegacyFiles
    Push $0
    
    Delete "$INSTDIR\${LEGACY_NAME}.exe"
    Delete "$INSTDIR\${LEGACY_NAME}.dll"
    Delete "$INSTDIR\${LEGACY_NAME}.pdb"
    Delete "$INSTDIR\${LEGACY_NAME}.deps.json"
    Delete "$INSTDIR\${LEGACY_NAME}.runtimeconfig.json"
    
    Delete "$SMPROGRAMS\${LEGACY_NAME}\${LEGACY_NAME}.lnk"
    RMDir "$SMPROGRAMS\${LEGACY_NAME}"
    Delete "$DESKTOP\${LEGACY_NAME}.lnk"
    
    nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${LEGACY_NAME}" /F'
    Pop $0
    
    Pop $0
FunctionEnd

; Cria atalhos
Function CreateShortcuts
    Push $0
    
    ${If} $CreateStartMenuShortcut != 0
        CreateDirectory "$SMPROGRAMS\${APP_NAME}"
        CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
        CreateShortcut "$SMPROGRAMS\${APP_NAME}\Desinstalar ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
    ${EndIf}
    
    ${If} $CreateDesktopShortcut != 0
        CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
    ${EndIf}
    
    Pop $0
FunctionEnd

; Executa o aplicativo instalado (sem abrir a pasta de instalacao)
Function LaunchApp
    SetOutPath "$INSTDIR"
    Exec '"$INSTDIR\${APP_EXE}"'
FunctionEnd

; Configura inicializacao com Windows
Function SetupAutoStart
    Push $0
    Push $1
    
    ${If} $AutoStartWithWindows != 0
        nsExec::Exec '"$SYSDIR\schtasks.exe" /Create /TN "${APP_NAME}" /TR "\"$INSTDIR\${APP_EXE}\" --minimized" /SC ONLOGON /RL HIGHEST /F'
        Pop $0
    ${Else}
        nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${APP_NAME}" /F'
        Pop $0
    ${EndIf}
    
    Pop $1
    Pop $0
FunctionEnd

; =============================================================================
; INICIALIZACAO
; =============================================================================
Function .onInit
    ; Verifica se e instalacao silenciosa
    StrCpy $0 $CMDLINE
    StrCpy $SilentInstall 0
    ${If} $0 == "/S"
        StrCpy $SilentInstall 1
    ${ElseIf} $0 == "/silent"
        StrCpy $SilentInstall 1
    ${ElseIf} $0 == "--silent"
        StrCpy $SilentInstall 1
    ${EndIf}
    
    ; Detecta instalacao anterior
    Call DetectPreviousInstall
    
    ; Se atualizacao, reutiliza pasta anterior
    ${If} $PreviousInstallDir != ""
        StrCpy $INSTDIR $PreviousInstallDir
    ${EndIf}
    
    ; Aviso para fechar app se estiver a correr
    ${If} $SilentInstall == 0
        ${If} $InstallMode == "upgrade"
            MessageBox MB_OK|MB_ICONINFORMATION "Antes de continuar, feche o HyperCore (ou FrameForge) se estiver aberto. Se estiver em execucao durante a atualizacao, o Windows pode impedir a substituicao de alguns ficheiros."
        ${EndIf}
    ${EndIf}
    
    ; Fecha processos da app
    ${If} $InstallMode == "upgrade"
        IfFileExists "$INSTDIR\${APP_EXE}" ForceCloseApp
    ${EndIf}
    Goto InitDone
    
ForceCloseApp:
    ${If} $SilentInstall == 1
        nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
        Pop $0
        Sleep 1000
    ${Else}
        MessageBox MB_YESNO|MB_ICONQUESTION "O HyperCore parece estar em execucao. Deseja que o instalador tente fecha-lo automaticamente?"
        Pop $0
        ${If} $0 == IDYES
            nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
            Pop $0
            Sleep 1000
        ${EndIf}
    ${EndIf}
    
InitDone:
FunctionEnd

; =============================================================================
; SECAO PRINCIPAL
; =============================================================================
Section "Instalar ${APP_NAME}" SEC_MAIN
    SectionIn RO ; Obrigatorio
    
    SetRegView 64
    SetOutPath "$INSTDIR"
    SetOverwrite on

    ; Garante que o HyperCore/FrameForge nao esteja bloqueando DLLs durante a atualizacao
    nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
    Pop $0
    nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${LEGACY_NAME}.exe"'
    Pop $0
    Sleep 600
    
    ; Limpa ficheiros legados se for atualizacao
    ${If} $InstallMode == "upgrade"
        Call CleanLegacyFiles
    ${EndIf}
    
    ; Copia binarios publicados (exclui PDB)
    File /r /x "*.pdb" "..\publicar\*.*"
    
    ; Cria desinstalador
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    
    ; Regista no Windows
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
    WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
    WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegStr HKLM "${UNINST_KEY}" "HelpLink" "https://github.com/HyperCore"
    WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "https://github.com/HyperCore"
    WriteRegStr HKLM "${UNINST_KEY}" "URLUpdateInfo" "https://github.com/HyperCore/releases"
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
    WriteRegDWORD HKLM "${UNINST_KEY}" "SystemComponent" 0
    
SectionEnd

; =============================================================================
; COMPONENTES OPCIONAIS
; =============================================================================

Section "Atalho no Menu Iniciar" SEC_STARTMENU
    StrCpy $CreateStartMenuShortcut 1
SectionEnd

Section "Atalho na Area de Trabalho" SEC_DESKTOP
    StrCpy $CreateDesktopShortcut 1
SectionEnd

Section "Iniciar com o Windows" SEC_AUTOSTART
    StrCpy $AutoStartWithWindows 1
SectionEnd

Section "Verificar atualizacoes automaticamente" SEC_AUTOUPDATE
    StrCpy $CheckForUpdates 1
    WriteRegDWORD HKCU "Software\${APP_PUBLISHER}\${APP_NAME}" "AutoCheckUpdates" 1
SectionEnd

Section -Post
    ; Cria atalhos e auto-start depois das seções opcionais definirem as flags
    Call CreateShortcuts
    Call SetupAutoStart
SectionEnd

; =============================================================================
; DESINSTALADOR
; =============================================================================
Section "Uninstall"
    Call un.RemoveAutoStart
    Call un.RemoveShortcuts
    RMDir /r "$INSTDIR"
    DeleteRegKey HKLM "${UNINST_KEY}"
    DeleteRegKey HKCU "Software\${APP_PUBLISHER}\${APP_NAME}"
SectionEnd

; =============================================================================
; FUNCOES DO DESINSTALADOR
; =============================================================================
Function un.RemoveAutoStart
    Push $0
    
    nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${APP_NAME}" /F'
    Pop $0
    nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${LEGACY_NAME}" /F'
    Pop $0
    
    Pop $0
FunctionEnd

Function un.RemoveShortcuts
    Push $0
    
    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\Desinstalar ${APP_NAME}.lnk"
    RMDir "$SMPROGRAMS\${APP_NAME}"
    Delete "$DESKTOP\${APP_NAME}.lnk"
    
    Pop $0
FunctionEnd

Function un.onInit
    StrCpy $0 $CMDLINE
    StrCpy $SilentInstall 0
    ${If} $0 == "/S"
        StrCpy $SilentInstall 1
    ${ElseIf} $0 == "/silent"
        StrCpy $SilentInstall 1
    ${ElseIf} $0 == "--silent"
        StrCpy $SilentInstall 1
    ${EndIf}
    
    nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
    Pop $0
    Sleep 500
FunctionEnd

Function un.onUninstSuccess
    ${If} $SilentInstall == 0
        MessageBox MB_OK|MB_ICONINFORMATION "${APP_NAME} foi desinstalado com sucesso. Os dados do utilizador em %AppData% foram preservados."
    ${EndIf}
FunctionEnd

; =============================================================================
; TEXTOS PERSONALIZADOS (PT-BR)
; =============================================================================
LangString DESC_SecMain ${LANG_PORTUGUESE} "Arquivos principais do ${APP_NAME} (obrigatorio)"
LangString DESC_SecStartMenu ${LANG_PORTUGUESE} "Cria atalho no Menu Iniciar para todos os usuarios"
LangString DESC_SecDesktop ${LANG_PORTUGUESE} "Cria atalho na Area de Trabalho para todos os usuarios"
LangString DESC_SecAutoStart ${LANG_PORTUGUESE} "Inicia o ${APP_NAME} automaticamente ao fazer login no Windows"
LangString DESC_SecAutoUpdate ${LANG_PORTUGUESE} "Verifica automaticamente por atualizacoes na inicializacao"
