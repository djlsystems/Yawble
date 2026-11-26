# Deploying Yawble in an organization

How IT installs Yawble on Windows computers for everyone who uses them, keeps it updated, and removes it.

## The installer

Each release on the [Releases](https://github.com/djlsystems/Yawble/releases) page has
`Yawble-Setup-<version>-x64.exe` and `SHA256SUMS`.

- **Signed:** the installer and every `.exe` and `.dll` it installs are Authenticode-signed by **DJL Systems, Inc.**
  (Azure Artifact Signing), except the files their own maker signed (the .NET runtime and the sandbox, by
  Microsoft). Publisher rules (AppLocker, App Control for Business, Smart App Control) can allow it by that publisher.
- **Checked:** `SHA256SUMS` lists the installer's SHA-256.

## Installing for everyone on a computer

Run as an administrator, or as SYSTEM from a deployment tool (Intune, Configuration Manager and similar):

```
Yawble-Setup-<version>-x64.exe /S /allusers
```

- **Program:** `%ProgramFiles%\Yawble` (the app is `yawble.exe`).
- **Shortcut:** Start menu **Yawble Desktop**, for every user.
- **Uninstall entry:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\a9c36938-295b-5b49-add6-0a346cf319da`,
  with `DisplayVersion` the installed version, for a detection rule. A file rule on
  `%ProgramFiles%\Yawble\yawble.exe` works too.
- **Exit code:** 0 when installed.

Without `/allusers`, or with `/currentuser`, the installer installs for the user who runs it only, to
`%LOCALAPPDATA%\Programs\Yawble`, without administrator rights. Run interactively, the installer asks which.

## What stays per user

Each person who opens Yawble has their own data in `%LOCALAPPDATA%\Yawble`: teams, chats, settings, the database,
transcripts and logs. Nothing of one user's is visible to another.

What each person sets up for themselves:

- **Agent CLIs** (Claude Code, Codex, Grok, GitHub Copilot, Antigravity): installed and signed in to by each person, or
  deployed by IT where their makers offer an all-users install. Yawble finds them on PATH and in each maker's install
  folders.
- **Sign-ins and connections:** kept in that person's Windows Credential Manager.

## Requirements

- **Windows 11** 24H2 or later, with the August 2026 update or later (the sandbox needs it).
- **git** 2.38 or later on PATH. Git for Windows deployed by IT for all users is found as it is.

## Updates

By default Yawble checks GitHub Releases on start and every 6 hours, downloads a newer installer to
`%LOCALAPPDATA%\Yawble\data\updates`, keeps it only if its SHA-256 matches `SHA256SUMS` and it is signed by DJL Systems,
Inc., and offers **Restart to update**:

- **Installed for one user:** the update installs without any prompt.
- **Installed for everyone:** the update asks Windows for administrator permission. Someone who is not an administrator
  is told their administrator installs updates, and is not offered the update. Before updating, Yawble names other
  people signed in to the computer who have it open: the update closes it for them.

To deploy new versions yourselves instead, turn Yawble's own update checks off with this policy (Group Policy
Preferences, an Intune configuration profile or a registry deployment):

```
HKLM\SOFTWARE\Policies\DJL Systems\Yawble
  UpdateChecks  (DWORD)  0
```

With it set, Yawble checks nothing, downloads nothing, and says in **Settings > General > Updates** that updates are
managed by your organization. Then deploy each new installer with the same `/S /allusers` command: it installs over the
installed version and keeps every user's data.

## Uninstalling

An uninstall removes Yawble and its data. Updates and reinstalls never remove data.

### Windows

Uninstall from **Settings > Apps**, or run the uninstaller:

```
"%ProgramFiles%\Yawble\Uninstall Yawble.exe" /S /allusers
"%ProgramFiles%\Yawble\Uninstall Yawble.exe" /S /allusers --keep-data
```

A per-user install's uninstaller is `%LOCALAPPDATA%\Programs\Yawble\Uninstall Yawble.exe` (`/S /currentuser`).

- **What is removed:** the program, and the data of the account that runs the uninstaller:
  - `%LOCALAPPDATA%\Yawble`: teams, chats, settings, the database, logs, backups, and the teams' copies of
    repositories.
  - `%APPDATA%\yawble-companion`: the companion's pairing keys.

  Other users' data on the same computer stays. To remove it, delete those two folders in each profile.
- **Keep my data:** run interactively, the uninstaller asks whether to remove the data. **No** keeps it, for a
  reinstall that picks up where it left off. `--keep-data` keeps it without asking, with or without `/S`.
- **Updates:** an update runs the installed version's uninstaller and always keeps the data. So does any run started by an
  installer (`--updated` or `/KEEP_APP_DATA`).
- **Folders you chose:** a folder named by `YAWBLE_DATA_DIR` or `YAWBLE_HOME` is never removed. If one of the two data
  folders is inside such a folder, or contains it, that data folder is kept too, and the uninstall log says so.
- **Yawble running:** the uninstaller first asks to close Yawble in this install. If `yawble.exe` or
  `yawble-core.exe` is still running for the user after that, for example from another install, it waits a few
  seconds, then asks you to quit Yawble and choose **Retry**. **Cancel**, or a silent uninstall, stops with exit code 2
  and removes nothing.
- **Versions without the data question:** a build whose uninstaller does not ask keeps all data. To remove it, delete
  `%LOCALAPPDATA%\Yawble` and `%APPDATA%\yawble-companion`.

### Linux

Run `uninstall.sh` in the install folder, as the user who installed Yawble:

```
<install dir>/uninstall.sh
<install dir>/uninstall.sh --keep-data
```

- **What is removed:** the install folder, the `yawble` launcher, and the menu entry. Without `--keep-data`, it also
  removes the data:
  - `~/.local/share/yawble` (or `$XDG_DATA_HOME/yawble`): the app's folder.
  - `~/.config/yawble-companion`: the companion's pairing keys.
  - `~/.config/Yawble`: the folder older builds used.
- **Folders you chose:** a `YAWBLE_DATA_DIR` folder is never removed.
- **AppArmor profile:** if it is installed for this install, remove it first with
  `sudo '<install dir>/uninstall-apparmor.sh'`. `uninstall.sh` refuses until you do.
- **Yawble running:** `uninstall.sh` refuses while Yawble runs from the install. Unless you pass `--keep-data`, it also
  refuses while any Yawble uses its data folder.

### macOS

The macOS build has no uninstaller yet. To uninstall, move `Yawble.app` to the Trash. The data stays in
`~/Library/Application Support/Yawble` and `~/Library/Application Support/yawble-companion`; delete both folders to
remove it.

When macOS packaging gets an uninstaller, it will follow the Linux rules: data is removed by default, `--keep-data`
keeps it, and the uninstall refuses while Yawble runs.

## Network

On the computer itself, Yawble reaches:

- **GitHub Releases** (`api.github.com`, `github.com` and the download host it redirects to), for its update checks,
  unless the policy above turns them off.
- **The agents' model and sign-in services**, through a gateway Yawble runs on the computer for each agent run (the
  sandbox gives agents no other network path). The hosts are the agent CLIs' own, such as `api.anthropic.com`.
- **Hosts the person allows** in Settings > Guardrails, for agents (package registries, for example).
- **`cloud.yawble.ai`**, only if the person turns on the Cloud Link.

## Smart App Control and SmartScreen

Every file Yawble installs is signed, which Smart App Control requires. SmartScreen may still warn on a downloaded
installer while the publisher builds reputation; an installer deployed by IT does not pass through SmartScreen.

Smart App Control also applies to what agents build: a new, unsigned program (a `.exe` an agent just compiled) is
blocked from running. Agents know this and run .NET programs through the SDK (`dotnet run`, `dotnet <program>.dll`);
the built `.exe` itself runs only where Smart App Control is off or a policy allows it.
