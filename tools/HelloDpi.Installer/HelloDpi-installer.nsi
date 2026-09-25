; Hello DPI Installer
; Generated installer script for Hello DPI application

!include "MUI2.nsh"

; General
Name "Hello DPI"
OutFile "HelloDpi-Setup.exe"
InstallDir "$PROGRAMFILES64\Hello DPI"
RequestExecutionLevel admin
CRCCheck on

; Version Information
VIProductVersion "0.6.33.0"
VIAddVersionKey "ProductName" "Hello DPI"
VIAddVersionKey "FileDescription" "Hello DPI - DPI Bypass Utility"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ignatovmax1"
VIAddVersionKey "FileVersion" "0.6.33.0"

; Modern UI
!include "x64.nsh"
!define PUBLISH_DIR "C:\deskmax\HelloDpi\publish"
!include "WordFunc.nsh"
!include "nsDialogs.nsh"

!define MUI_ABORTWARNING
!define MUI_HEADERIMAGE
!define MUI_ICON "C:\deskmax\HelloDpi\tools\HelloDpi.Installer\appicon.ico"

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "C:\deskmax\HelloDpi\tools\HelloDpi.Installer\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"

; Install section
Section "Install"
  SetOutPath "$INSTDIR"
  
  ; Main executable (single-file publish)
  File "${PUBLISH_DIR}\HelloDpi.exe"
  
  ; Supporting files
  File "${PUBLISH_DIR}\LICENSE"
  File "${PUBLISH_DIR}\README-RU.txt"
  File "${PUBLISH_DIR}\THIRD_PARTY_NOTICES.md"
  
  ; Registry for uninstall
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "DisplayName" "Hello DPI"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "DisplayIcon" "$INSTDIR\HelloDpi.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI" \
    "Publisher" "ignatovmax1"
  
  ; Create start menu shortcut
  CreateDirectory "$SMPROGRAMS\Hello DPI"
  CreateShortcut "$SMPROGRAMS\Hello DPI\Hello DPI.lnk" "$INSTDIR\HelloDpi.exe"
  CreateShortcut "$SMPROGRAMS\Hello DPI\Uninstall.lnk" "$INSTDIR\uninstall.exe"
  
  ; Create desktop shortcut (optional)
  CreateShortcut "$DESKTOP\Hello DPI.lnk" "$INSTDIR\HelloDpi.exe"
  
  ; Write uninstaller
  WriteUninstaller "$INSTDIR\uninstall.exe"
SectionEnd

; Uninstall section
Section "Uninstall"
  ; Kill running process
  nsExec::Exec 'taskkill /F /IM HelloDpi.exe'
  
  ; Remove files
  Delete "$INSTDIR\HelloDpi.exe"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\README-RU.txt"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\uninstall.exe"
  
  ; Remove directories
  RMDir "$INSTDIR"
  
  ; Remove shortcuts
  Delete "$SMPROGRAMS\Hello DPI\Hello DPI.lnk"
  Delete "$SMPROGRAMS\Hello DPI\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Hello DPI"
  Delete "$DESKTOP\Hello DPI.lnk"
  
  ; Remove registry
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hello DPI"
SectionEnd
