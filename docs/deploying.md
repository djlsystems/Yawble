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

```
"%ProgramFiles%\Yawble\Uninstall Yawble.exe" /S /allusers
```

Each user's data in `%LOCALAPPDATA%\Yawble` is kept. Delete those folders to remove it.

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
