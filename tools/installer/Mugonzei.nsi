Unicode true
RequestExecutionLevel user
ManifestDPIAware true
Name "Mugonzei Honyakuki"
OutFile "${OUTPUT}"
Icon "${PRODUCT_ICON}"
UninstallIcon "${PRODUCT_ICON}"
InstallDir "$LOCALAPPDATA\Programs\Mugonzei Honyakuki"
SetCompressor zlib
SetCompress off ; The embedded release ZIP is already compressed.
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${FILEVERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "Mugonzei Honyakuki"
VIAddVersionKey /LANG=1033 "FileDescription" "Mugonzei Honyakuki Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Mugonzei Honyakuki"

!include LogicLib.nsh
!include x64.nsh
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MugonzeiHonyakuki"
!define LINK "Mugonzei Honyakuki.lnk"
Var Existing

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles
LoadLanguageFile "${NSISDIR}\Contrib\Language files\English.nlf"
LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"
LoadLanguageFile "${NSISDIR}\Contrib\Language files\Japanese.nlf"

!macro Initialize
  SetShellVarContext current
  SetRegView 64
  System::Call 'kernel32::CreateMutexW(p0,i0,w "Local\MugonzeiHonyakukiSetup")p.r0 ?e'
  Pop $1
  ${If} $0 == 0
  ${OrIf} $1 == 183
    MessageBox MB_ICONSTOP|MB_OK "Another setup is running. / 另一个安装程序正在运行。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

Function .onInit
  !insertmacro Initialize
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "Requires 64-bit Windows. / 需要 64 位 Windows。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ReadRegStr $Existing HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $Existing != ""
    StrCpy $INSTDIR $Existing
  ${EndIf}
FunctionEnd

Function .onVerifyInstDir
  ${If} $Existing != ""
  ${AndIf} $INSTDIR != $Existing
    Abort ; Upgrades reuse this user's original installation.
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro Initialize
FunctionEnd

Section "Mugonzei Honyakuki"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "/oname=payload.exe" "${HELPER}"
  File "/oname=release.zip" "${ARCHIVE}"
  AddSize ${INSTALLED_KB}
  DetailPrint "Verifying and installing application and offline OCR models..."
  nsExec::ExecToStack '"$PLUGINSDIR\payload.exe" install "$INSTDIR" "$PLUGINSDIR\release.zip" ${DIGEST}'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "安装未完成 / Installation failed.$\r$\n$1" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ClearErrors
  SetOutPath "$INSTDIR\app"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${LINK}" "$INSTDIR\app\SteamVrTranslator.exe" "" "$INSTDIR\app\SteamVrTranslator.exe" 0
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Mugonzei Honyakuki"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\SteamVrTranslator.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALLED_KB}
  ${If} ${Errors}
    MessageBox MB_ICONSTOP|MB_OK "Could not register the installation. Run Setup again to repair it." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
SectionEnd

Section "Uninstall"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "/oname=payload.exe" "${HELPER}"
  nsExec::ExecToStack '"$PLUGINSDIR\payload.exe" remove "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "卸载未完成 / Uninstall failed.$\r$\n$1" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    Delete "$SMPROGRAMS\${LINK}"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR" ; User-created files are preserved.
SectionEnd
