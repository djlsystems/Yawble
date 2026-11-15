// The NSIS include packaging/build.mjs hands electron-builder (nsis.include). Pure: it only builds the text, so
// packaging/tests/nsis-include.test.mjs checks it without makensis or an installer.
//
// customInstall: the uninstall entry's DisplayVersion is the build's version (electron-builder writes package.json's,
// and yyyy.mm.dd.N is not semver).
//
// customUnInstall: a real uninstall (Settings > Apps, or "Uninstall Yawble.exe") also removes the person's data, like
// packaging/linux/uninstall.sh: the app's folder %LOCALAPPDATA%\Yawble (shell/src/main/app-folder.ts: database, logs,
// backups, teams and their copies of repositories, sign-in sessions) and the companion's pairing folder
// %APPDATA%\yawble-companion (core/src/Yawble.Core/Companion/ControlDiscovery.cs). electron-builder's own
// deleteAppDataOnUninstall stays off: it removes %APPDATA%\Yawble, which is not where the app keeps anything.
// - Never when an installer runs the old uninstaller (an update or a reinstall): electron-builder passes it --updated
//   (${isUpdated}) and /KEEP_APP_DATA; either one keeps the data.
// - "Keep my data": run interactively, the uninstaller asks (Yes removes the data, No keeps it); --keep-data keeps it
//   without asking, for silent /S uninstalls by IT.
// - Never a folder the person chose: YAWBLE_DATA_DIR (the app's data folder) and YAWBLE_HOME (the companion's) are
//   theirs. A folder that lies inside one of them, or holds one, is kept and the uninstaller says so.
// - Yawble must not be running: electron-builder's check has already closed what runs from this install (it asks
//   first); a yawble.exe or yawble-core.exe still running for this user (another install, or a core slow to exit)
//   would keep working on deleted files. The uninstaller waits a few seconds for it, then asks to quit it and retry;
//   Cancel (and a silent uninstall) stops with nothing removed and exit code 2.
// Per-machine installs: the uninstaller runs as the account that runs it, so it removes that account's data only.
// The ${If}/${GetOptions}/${isUpdated}/${Silent} macros and $installMode come from electron-builder's own script,
// which is included before customUnInstall is inserted.

/** The processes that work on the data folder: the window and the core (the companion runs only from an install). */
export const runningImages = ['yawble.exe', 'yawble-core.exe'];

/** The command-line flag that keeps the data on an uninstall. */
export const keepDataFlag = '--keep-data';

/** The folders a real uninstall removes, as NSIS paths in the current user's shell context. */
export const dataFolders = { app: '$LOCALAPPDATA\\Yawble', companion: '$APPDATA\\yawble-companion' };

/** Variables that name a folder the person chose: never removed, nor what lies inside them or holds them. */
export const chosenFolderVariables = ['YAWBLE_DATA_DIR', 'YAWBLE_HOME'];

const removeDataQuestion =
  'Also remove your Yawble data?$\\r$\\n$\\r$\\n' +
  'Your teams, chats, settings, logs, backups and the copies of your repositories in %LOCALAPPDATA%\\Yawble, and ' +
  'the companion\'s pairing keys in %APPDATA%\\yawble-companion.$\\r$\\n$\\r$\\n' +
  'Yes: remove everything.$\\r$\\n' +
  'No: keep my data, for a later reinstall that picks up where it left off.';

const stillRunning =
  'Yawble is still running ($R3). Quit it (the tray icon, then Quit), then choose Retry.$\\r$\\n$\\r$\\n' +
  'Cancel stops the uninstall: nothing is removed.';

/** The include's text, CRLF. buildVersion is the build's version (scripts/build-version.mjs). */
export function nsisInclude({ buildVersion }) {
  const findImage = (image) => [
    `      nsExec::Exec \`"$SYSDIR\\cmd.exe" /C tasklist /FI "USERNAME eq %USERNAME%" /FI "IMAGENAME eq ${image}" /FO CSV /NH | "$SYSDIR\\findstr.exe" /B /I /C:"\\"${image}\\""\``,
    '      Pop $R2',
    '      ${If} $R2 == 0',
    `        StrCpy $R3 "${image}"`,
    '      ${EndIf}',
  ];
  // Keeps a folder ($R8) that a chosen folder ($R7, raw from the environment) lies in or holds; $R9 says "yes" to keep.
  const guardChosen = (variable, folderRegister) => [
    `    \${If} ${folderRegister} != ""`,
    `      ReadEnvStr $R7 ${variable}`,
    `      StrCpy $R8 ${folderRegister}`,
    '      !insertmacro _yawbleOverlaps',
    '      ${If} $R9 == "yes"',
    `        DetailPrint "Kept ${folderRegister}: ${variable} is $R7"`,
    `        StrCpy ${folderRegister} ""`,
    '      ${EndIf}',
    '    ${EndIf}',
    '    ClearErrors',
  ];
  return [
    '!macro customInstall',
    `  WriteRegStr SHELL_CONTEXT "\${UNINSTALL_REGISTRY_KEY}" "DisplayVersion" "${buildVersion.replace(/[^0-9A-Za-z.+-]/g, '')}"`,
    '!macroend',
    '',
    '; $R9 = "yes" when $R7 (a folder the person named, may be empty or relative) and $R8 (a folder the uninstaller',
    '; would remove) are the same, or one lies inside the other. A name that cannot be resolved counts as "yes".',
    '!macro _yawbleOverlaps',
    '  Push $0',
    '  Push $1',
    '  Push $2',
    '  StrCpy $R9 "no"',
    '  ${If} $R7 != ""',
    "    System::Call 'kernel32::GetFullPathNameW(w R7, i ${NSIS_MAX_STRLEN}, w .r0, i 0) i .r1'",
    '    ${If} $1 == 0',
    '      StrCpy $R9 "yes"',
    '    ${Else}',
    '      StrCpy $2 $0 "" -1',
    '      ${If} $2 != "\\"',
    '        StrCpy $0 "$0\\"',
    '      ${EndIf}',
    '      StrCpy $1 "$R8\\"',
    '      StrLen $2 $1',
    '      StrCpy $2 $0 $2',
    '      ${If} $2 == $1',
    '        StrCpy $R9 "yes"',
    '      ${EndIf}',
    '      StrLen $2 $0',
    '      StrCpy $2 $1 $2',
    '      ${If} $2 == $0',
    '        StrCpy $R9 "yes"',
    '      ${EndIf}',
    '    ${EndIf}',
    '  ${EndIf}',
    '  Pop $2',
    '  Pop $1',
    '  Pop $0',
    '!macroend',
    '',
    '!macro customUnInstall',
    '  ; $R1: "remove" (a real uninstall), "keep" (Keep my data, --keep-data) or "update" (an installer runs this).',
    '  StrCpy $R1 "remove"',
    '  ${GetParameters} $R0',
    '  ${If} ${isUpdated}',
    '    StrCpy $R1 "update"',
    '  ${EndIf}',
    '  ClearErrors',
    '  ${GetOptions} $R0 "/KEEP_APP_DATA" $R2',
    '  ${IfNot} ${Errors}',
    '    StrCpy $R1 "update"',
    '  ${EndIf}',
    '  ClearErrors',
    `  \${GetOptions} $R0 "${keepDataFlag}" $R2`,
    '  ${IfNot} ${Errors}',
    '  ${AndIf} $R1 == "remove"',
    '    StrCpy $R1 "keep"',
    '  ${EndIf}',
    '  ClearErrors',
    '  ${If} $R1 == "remove"',
    '  ${AndIfNot} ${Silent}',
    `    MessageBox MB_YESNO|MB_ICONQUESTION "${removeDataQuestion}" IDYES +2`,
    '    StrCpy $R1 "keep"',
    '  ${EndIf}',
    '',
    '  ${If} $R1 == "remove"',
    '    ; Nothing may still work on the data: wait up to 5 s, then ask (a silent uninstall stops).',
    '    StrCpy $R4 0',
    '    ${Do}',
    '      StrCpy $R3 ""',
    ...runningImages.flatMap(findImage),
    '      ${If} $R3 == ""',
    '        ${ExitDo}',
    '      ${EndIf}',
    '      ${If} $R4 < 5',
    '        IntOp $R4 $R4 + 1',
    '        Sleep 1000',
    '        ${Continue}',
    '      ${EndIf}',
    `      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${stillRunning}" /SD IDCANCEL IDRETRY +2`,
    '      Abort "Yawble is running ($R3): nothing was removed. Quit it, then uninstall again."',
    '    ${Loop}',
    '',
    '    ; The person\'s own folders, whatever the install mode (a per-machine uninstall runs in the "all" context).',
    '    ${If} $installMode == "all"',
    '      SetShellVarContext current',
    '    ${EndIf}',
    '    StrCpy $R5 ""',
    '    StrCpy $R6 ""',
    '    ${If} $LOCALAPPDATA != ""',
    `      StrCpy $R5 "${dataFolders.app}"`,
    '    ${EndIf}',
    '    ${If} $APPDATA != ""',
    `      StrCpy $R6 "${dataFolders.companion}"`,
    '    ${EndIf}',
    '    ${If} $installMode == "all"',
    '      SetShellVarContext all',
    '    ${EndIf}',
    ...chosenFolderVariables.flatMap((v) => [...guardChosen(v, '$R5'), ...guardChosen(v, '$R6')]),
    '    ${If} $R5 != ""',
    '      DetailPrint "Removing $R5"',
    '      RMDir /r "$R5"',
    '    ${EndIf}',
    '    ${If} $R6 != ""',
    '      DetailPrint "Removing $R6"',
    '      RMDir /r "$R6"',
    '    ${EndIf}',
    '  ${ElseIf} $R1 == "keep"',
    '    DetailPrint "Your Yawble data is kept in %LOCALAPPDATA%\\Yawble and %APPDATA%\\yawble-companion"',
    '  ${EndIf}',
    '!macroend',
    '',
  ].join('\r\n');
}
