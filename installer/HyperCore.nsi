Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

!define APP_NAME "HyperCore"
!define APP_VERSION "0.5.0"
!define APP_PUBLISHER "HyperCore"
; MANTER este APP_ID: é o mesmo do FrameForge, para a atualização substituir a versão antiga em Programas e Funcionalidades.
!define APP_ID "{1A6A35B4-CE8B-4A2D-A135-F1A340C0D164}"
!define APP_EXE "HyperCore.exe"
; Nome antigo da app (até à 0.3.50): binários, atalhos e tarefa agendada a limpar ao atualizar.
!define LEGACY_NAME "FrameForge"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "..\artifacts\installer\${APP_NAME}-Setup-${APP_VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${APP_NAME}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\Assets\app.ico"
!define MUI_UNICON "..\Assets\app.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "Portuguese"

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "Instalador do ${APP_NAME}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (C) ${APP_PUBLISHER}"

Function .onInit
  ; A atualizar por cima do FrameForge: reutiliza a pasta da instalação anterior (mesmo APP_ID)
  ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
  MessageBox MB_OK|MB_ICONINFORMATION "Antes de continuar, fecha o ${APP_NAME} (ou o ${LEGACY_NAME}) se estiver aberto. Se estiver em execução durante a atualização, o Windows pode impedir a substituição de alguns ficheiros."
FunctionEnd

Section "Instalar ${APP_NAME}" SEC_MAIN
  SectionIn RO
  SetOutPath "$INSTDIR"

  ; Binários e atalhos do FrameForge (nome antigo): sem isto o FrameForge.exe antigo continuava a funcionar
  Delete "$INSTDIR\${LEGACY_NAME}.exe"
  Delete "$INSTDIR\${LEGACY_NAME}.dll"
  Delete "$INSTDIR\${LEGACY_NAME}.pdb"
  Delete "$INSTDIR\${LEGACY_NAME}.deps.json"
  Delete "$INSTDIR\${LEGACY_NAME}.runtimeconfig.json"
  Delete "$SMPROGRAMS\${LEGACY_NAME}\${LEGACY_NAME}.lnk"
  RMDir "$SMPROGRAMS\${LEGACY_NAME}"
  Delete "$DESKTOP\${LEGACY_NAME}.lnk"
  ; Tarefa agendada antiga (arranque com o Windows); a app cria a "HyperCore" ao abrir, se o arranque estiver ligado
  nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${LEGACY_NAME}" /F'
  Pop $0

  ; Conteúdo publicado pelo .NET; o utilizador final não precisa do compilador NSIS.
  File /r "..\publicar\*.*"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  ; Tarefas de arranque com o Windows (nome novo e antigo)
  nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${APP_NAME}" /F'
  Pop $0
  nsExec::Exec '"$SYSDIR\schtasks.exe" /Delete /TN "${LEGACY_NAME}" /F'
  Pop $0

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${LEGACY_NAME}\${LEGACY_NAME}.lnk"
  RMDir "$SMPROGRAMS\${LEGACY_NAME}"
  Delete "$DESKTOP\${LEGACY_NAME}.lnk"
  DeleteRegKey HKLM "${UNINST_KEY}"
  ; Não remove dados do utilizador em AppData.
  RMDir /r "$INSTDIR"
SectionEnd
