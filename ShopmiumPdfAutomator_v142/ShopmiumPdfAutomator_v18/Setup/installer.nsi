; ============================================================
; Shopmium PDF Automator — Installeur NSIS v2 (CORRIGÉ)
; ============================================================
Unicode True
!include "MUI2.nsh"
!define APPNAME    "Shopmium PDF Automator"
!define APPVERSION "1.0.0"
!define PUBLISHER  "KUTORAMO"
!define APPEXE     "ShopmiumPdfAutomator.exe"
!define UNINSTKEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
!define REGKEY     "Software\${APPNAME}"
Name              "${APPNAME} ${APPVERSION}"
OutFile           "ShopmiumPdfAutomator_Setup_v${APPVERSION}.exe"
InstallDir        "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey  HKLM "${REGKEY}" "InstallDir"
RequestExecutionLevel admin
SetCompressor     /SOLID lzma
!define MUI_ICON   "..\ShopmiumPdfAutomator\Resources\icon.ico"
!define MUI_UNICON "..\ShopmiumPdfAutomator\Resources\icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Lancer ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

; ============================================================
; WELCOME CUSTOM TEXT
; ============================================================
Function .onInit
FunctionEnd

Function WelcomeShow
    !insertmacro MUI_HEADER_TEXT "Bienvenue dans ${APPNAME} ${APPVERSION}" ""
    FindWindow $0 "#32770" "" $HWNDPARENT
    GetDlgItem $1 $0 1000
    SendMessage $1 ${WM_SETTEXT} 0 "STR:Cet assistant va installer ${APPNAME} sur votre ordinateur.$\r$\n$\r$\nPré-requis : Adobe Photoshop CC doit être installé."
FunctionEnd

; ============================================================
; INSTALLATION
; ============================================================
Section "Application" SecMain
    SectionIn RO
    SetOutPath "$INSTDIR"

    ; ── Copier tous les fichiers buildés ──────────────────────────────────
    File /r "files\*.*"

    ; ── Raccourcis bureau ─────────────────────────────────────────────────
    CreateShortcut "$DESKTOP\${APPNAME}.lnk" \
        "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0

    ; ── Menu démarrer ─────────────────────────────────────────────────────
    CreateDirectory "$SMPROGRAMS\${APPNAME}"
    CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" \
        "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
    CreateShortcut "$SMPROGRAMS\${APPNAME}\Désinstaller.lnk" \
        "$INSTDIR\Uninstall.exe"

    ; ── Registre ─────────────────────────────────────────────────────────
    WriteRegStr  HKLM "${REGKEY}" "InstallDir" "$INSTDIR"
    WriteRegStr  HKLM "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
    WriteRegStr  HKLM "${UNINSTKEY}" "DisplayVersion"  "${APPVERSION}"
    WriteRegStr  HKLM "${UNINSTKEY}" "Publisher"       "${PUBLISHER}"
    WriteRegStr  HKLM "${UNINSTKEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr  HKLM "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE}"
    WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair"  1

    WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

; ============================================================
; UNINSTALL
; ============================================================
Section "Uninstall"
    RMDir /r "$INSTDIR"
    Delete "$DESKTOP\${APPNAME}.lnk"
    RMDir /r "$SMPROGRAMS\${APPNAME}"
    DeleteRegKey HKLM "${REGKEY}"
    DeleteRegKey HKLM "${UNINSTKEY}"
SectionEnd
