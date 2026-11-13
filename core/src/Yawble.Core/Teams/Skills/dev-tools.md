---
name: dev-tools
description: The developer tools you can run in your sandbox (git, .NET, Node.js, Python), what the network allows, and paths.
roles: [any]
---
# Developer tools in your sandbox

You run in a sandbox on the person's own computer. The tools the person installed are on your PATH, read-only:

- **git**, always.
- **.NET SDK** (`dotnet`) when installed: `dotnet new console -o <name>`, `dotnet build`, `dotnet test`,
  `dotnet run`. NuGet's package folder is in your home. Building a project that uses no NuGet packages works
  offline; a package download works only if the person allowed `api.nuget.org` in the agent's network policy.
- **Node.js** (`node`, `npm`) and **Python** (`python`) when installed. Package downloads need the registry host
  allowed by the person, like NuGet.

Check before relying on one: `dotnet --version`, `node --version`, `python --version`. If a tool you need is
missing, say so with `blocked`; do not try to download or install one.

## Heavy work

Other agents share this computer. Before running the repository's full test suite, a full build, an image build or
anything else you know is heavy, take the heavy-work lease with `lease` (action acquire, what = the step); release it
as soon as that command finishes. While iterating run only the tests you touched; run the full suite once when
finished. This holds for any repository and toolchain (`dotnet test`, `npm test`, `docker build`, ...).

## Windows

- Your shell tool is PowerShell (Git Bash and other MSYS2 programs cannot start in the sandbox). Use PowerShell
  syntax: `$env:NAME`, `Get-ChildItem`, `;` between commands.
- Paths are ordinary Windows paths on this computer. Your folders (clone, home, temp) are the same paths the person
  sees, so report the FULL path of anything they should open or run, e.g.
  `C:\Users\...\teams\demo\members\DeveloperPriya\repo\Hello\bin\Release\net10.0\Hello.exe`.
  Get it with `(Resolve-Path <relative path>).Path`.
- Windows with Smart App Control on blocks a program you just built (`HelloApp.exe`: "An Application Control policy
  has blocked this file"), because it is new and unsigned. That is the computer's setting, not a fault in your build:
  run .NET programs through the SDK instead (`dotnet run --project <project>`, `dotnet <path>\HelloApp.dll`), make
  tests run the program the same way, and tell the person the exe itself is blocked on this computer.

## Network

Only the model's own traffic and the team bus are open, plus any hosts the person allowed. Everything else is
refused, so do not try other sites, and never try to get around a refusal.

Allowed hosts are reached through the run's proxy, which the sandbox gives you in `HTTPS_PROXY` / `HTTP_PROXY`.
`dotnet`, `npm`, `pip`, `git` and `curl.exe` use it by themselves. Windows PowerShell's `Invoke-WebRequest` and
`Invoke-RestMethod` do NOT: they try a direct connection, which the sandbox always refuses ("Unable to connect to
the remote server"), even for an allowed host. So never check the network with them: check with the tool you need
(`dotnet restore`, `npm ping`) or with `curl.exe -sS -o NUL -w "%{http_code}" https://api.nuget.org/v3/index.json`.
A host that is not allowed answers through the proxy with 403 "host not on this agent's allowlist": that, and only
that, means the person must allow it.

## What you cannot reach

The person's own folders (Documents, Desktop, ...), other members' folders, the team's repository, the app's
data, and any credential store. That is by design: report what you need instead.
