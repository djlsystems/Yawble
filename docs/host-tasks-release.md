# Release and deploy host tasks

How to set up a project's release and deploy jobs as **host tasks**, so that "make a release" or "deploy the cloud"
is one request, from the Concierge or a team's agent, that you approve and the app runs on this computer.

A host task is defined in **Settings > Integrations**, below the tabs (see
[Connections and integrations](connections-and-integrations.md#host-tasks) for the full rules). An agent or the
Concierge never defines or changes a task: it can only name one and give a reason. This page is about the shape a
release or deploy task should have.

## The rules that shape a release task

- **One script per task.** A task runs one program with a fixed list of arguments, never a shell line: `pwsh` with
  `-NoProfile`, `-File`, `scripts/release.ps1` is fine; `pwsh -Command "build; deploy"` is refused, because an inline
  command cannot be pinned. When a job is a chain (build an image, then deploy it), the chain lives **inside one
  script in the repository**, and the task runs that script. Then the whole chain is pinned and shown in the approval.
- **It runs on the merged base branch, clean.** The task runs in your checkout of the repository (not an agent's
  clone), only when that checkout is on its base branch with no uncommitted changes. Release scripts that also check
  "HEAD is origin/main" fit this well.
- **stdin is closed; nothing can prompt.** There is no person at the task's keyboard. A script that calls
  `Read-Host`, asks "Are you sure? [y/N]", or opens a sign-in window fails or waits until its time limit. Every
  choice must be a parameter or a variable; every sign-in (`az login`, `gh auth login`) must already be done, or come
  from a Connection.
- **A time limit.** 10 to 7200 seconds; there is no "no limit". Give a deploy that builds an image in a registry
  enough (30 to 60 minutes); the whole process tree is killed at the limit.
- **A small environment.** A task gets only the basic variables (`PATH`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`,
  `TEMP`, `ProgramFiles`, ...; `TERM=dumb`, `CI=1`, `NO_COLOR=1` are set), plus:
  - **Environment variable names to pass on**: names (never values) taken from the app's own environment;
  - **Environment from a Connection**: a variable whose value is a secret field of a Connection, read when the task
    starts, masked in the log, never shown to an agent. Use this for tokens (`GH_TOKEN`).

  A name starting with `YAWBLE_` (or another app or model provider prefix such as `ANTHROPIC_`, `OPENAI_`,
  `HTTPS_PROXY`) can never be given to a task, either way: those belong to the app. A script that reads such a
  variable must work without it (see the desktop example).
  Because `USERPROFILE` and `APPDATA` are passed, a CLI you are already signed in to on this computer (`az`, `gh`,
  `git`'s credential manager) is signed in for the task too.
- **Approval: Run once or Always.** Each request shows the command, the pinned files and the bindings, and asks
  **Run once** or **Always for this team**. Always is a standing approval tied to exactly what was shown:
  - **pin mode `files`** (the usual choice): it asks again when the command line, the script (the entry-point file),
    a path under **Also pin these paths**, or a binding changes;
  - **pin mode `commit`**: it asks again after every new commit on the base branch.

  **Editing the task** (pressing Save on it) always removes an Always approval, and **editing the script** in the
  repository makes the next request ask again and show the diff. Replacing an expired token inside a Connection does
  not ask. **Revoke Always** on the task row goes back to asking every time.
- **Description and What to check after.** Two optional one-line fields of a task:
  - **Description** says what the task does ("Builds, signs and publishes the desktop release"). It is shown on the
    task row, and it is how the Concierge and the agents tell your tasks apart when you ask in your own words.
  - **What to check after** says how to see that it worked: a health URL, a release page, a version to look for. It
    is shown on the task row, so whoever ran the task (you, the Concierge, an agent) knows what to look at once the
    run ends. The task's exit code says the script finished; this says the result is live.

  Leave either empty and it stays empty.

## Asking for it

Once the task is saved, ask in your own words: "make a release", "deploy the cloud". The Concierge, or a team's
Manager, asks for the task by its name with a reason; the request appears on the team's page with the command, the
pinned files and the bindings. Approve **Run once** (or **Always for this team** for a job you run often). The result
and the log path come back when the run ends; then check what **What to check after** says.

You can also press **Run now** on the task row: the same checks, no request.

## Example: the Yawble desktop release

The repository's `scripts/release.ps1` checks that the checkout is clean and on `origin/main`, runs the suites, tags
`v<yyyy.mm.dd.N>`, pushes the tag, builds the signed Windows installer (`packaging/build.mjs --sign`) and publishes
the GitHub release with `Yawble-Setup-<version>-x64.exe` and `SHA256SUMS`. It takes no input; `-DryRun` checks and
tests only, `-TagOnly` stops after the tag.

1. **A Connection for GitHub.** Make a fine-grained token with **Contents: write** on the Yawble repository only. In
   **Settings > Integrations > Connections**, add an **API key or token** Connection, for example
   `GitHub release (Yawble)`, with the token in its `token` field.
2. **Signing.** The signer signs in through Azure: run `az login` once on this computer with an account that has the
   "Artifact Signing Certificate Profile Signer" role; the task uses that sign-in. Install the Artifact Signing client
   tools and the Windows SDK (for `signtool.exe`). In their default places
   (`%LOCALAPPDATA%\Microsoft\MicrosoftArtifactSigningClientTools`, and the newest x64 `signtool.exe` under
   `Windows Kits\10\bin`) the task needs nothing more.
   For a tool in another place, `packaging/windows-signing.cjs` reads `SIGNING_DLIB` (the full path of
   `Azure.CodeSigning.Dlib.dll`) and `SIGNTOOL` (the full path of `signtool.exe`). Give them to the task like any other
   variable: under **Environment variable names to pass on** when they are set in your environment, or under
   **Environment from a Connection** from a field of a Connection. (Built by hand, the script also reads
   `YAWBLE_SIGNING_DLIB` and `YAWBLE_SIGNTOOL`; a task can never be given a `YAWBLE_` name.)
3. **The task** (team of your choice, repository `main`):

   | Field | Value |
   |---|---|
   | Name | `release` |
   | Program | `pwsh` (or `powershell`) |
   | Arguments | `-NoProfile`, `-File`, `scripts/release.ps1` (one per line) |
   | Working folder | `.` |
   | Time limit | `3600` |
   | Pin mode | `files` |
   | Also pin these paths | `scripts/release-functions.ps1`, `packaging/**` |
   | Environment from a Connection | `GH_TOKEN` ← `GitHub release (Yawble)` (`token`) |
   | Description | `Tests, tags and publishes the signed Yawble desktop release` |
   | What to check after | `https://github.com/djlsystems/Yawble/releases/latest shows the new version with the installer and SHA256SUMS` |

   `gh release create` reads `GH_TOKEN`, so `gh` needs no separate sign-in for the task.
4. Optionally a second task `release-dry-run` with the arguments `-NoProfile`, `-File`, `scripts/release.ps1`,
   `-DryRun`, to see the next version and run the suites without tagging anything. Its approval is separate.

Ask: "make a release". Approve Run once; the run takes as long as the suites and the signed build.

## Example: deploying yawble-cloud

In the yawble-cloud repository, two steps make a deploy:

1. **The desktop screens image** `djlacr.azurecr.io/yawble-remote-ui:<commit>`, where `<commit>` is `commit` in
   `remote-ui.lock.json`. It is built only when the lock's commit changes, from a git archive of the public Yawble
   repository at that commit with yawble-cloud's `remote-ui-build/` folder copied in (and its `context.dockerignore`
   copied as `.dockerignore`), by `az acr build --registry djlacr --image yawble-remote-ui:<commit>
   --build-arg GIT_SHA=<commit> --build-arg APP_VERSION=<version> -f remote-ui-build/Dockerfile .` run inside that
   folder. `scripts/deploy-with-screens.ps1` does this step when it is needed (below); yawble-cloud's README (infra
   section 5) and `scripts/deploy.ps1`'s error message also give the command.
2. **`scripts/deploy.ps1`**: checks the tree is clean and on `origin/main`, checks the screens image exists (and stops,
   printing the step 1 command, if not), prints the rollback command, builds `yawble-cloud:<short sha>` in the
   registry `djlacr` with `az acr build`, rolls the container app `yawble-cloud` (resource group `yawble-cloud`) onto
   it and waits for `https://cloud.yawble.ai/api/health` to answer the new version. `-Version <yyyy.mm.dd.N>`
   ships with that desktop release, `-Release` is a cloud-only release, `-DryRun` prints what it would do. It takes
   no input.

(`scripts/build-remote-ui.ps1` builds the screens into `remote-ui/` locally, for development; it is not part of a
deploy.)

**Setup.** Sign in once on this computer with `az login`, as an account with rights on the resource group
`yawble-cloud` and the registry `djlacr`. `git` must be able to read `origin` (your credential manager). No token
goes into the task.

**The everyday deploy** (repository `yawble-cloud`; the screens image already exists):

| Field | Value |
|---|---|
| Name | `deploy-cloud` |
| Program | `pwsh` |
| Arguments | `-NoProfile`, `-File`, `scripts/deploy.ps1` |
| Working folder | `.` |
| Time limit | `2400` (the script waits up to 20 minutes for the image build and 5 for the new revision) |
| Pin mode | `files` |
| Also pin these paths | `scripts/version-functions.ps1`, `Dockerfile`, `remote-ui.lock.json` |
| Description | `Builds the yawble-cloud image in djlacr and rolls the container app onto it` |
| What to check after | `https://cloud.yawble.ai/api/health answers 200 with the new version` |

A `-DryRun` twin (`deploy-cloud-dry-run`) is a cheap first task to approve: it runs no `az` that changes anything.

**When the lock's commit changed** (a new desktop release pinned for the screens), the two steps form one chain, and
the chain lives in one script: yawble-cloud's **`scripts/deploy-with-screens.ps1`**. It takes the same parameters as
`scripts/deploy.ps1` (`-Version <yyyy.mm.dd.N>`, `-Release`, `-DryRun`) and no input. It:

1. reads the lock's commit from `remote-ui.lock.json`;
2. builds the screens image `yawble-remote-ui:<lock commit>` in `djlacr` (step 1 above) **only when that tag is
   missing** from the registry, and stops if that build fails; when the tag is already there it builds nothing;
3. then runs `scripts/deploy.ps1` with the parameters it was given, and fails if that fails.

So it is safe to use for every deploy: when the screens image already exists it is the everyday deploy with one
registry check first.

| Field | Value |
|---|---|
| Name | `deploy-cloud-with-screens` |
| Program | `pwsh` |
| Arguments | `-NoProfile`, `-File`, `scripts/deploy-with-screens.ps1` (add `-Version`, `<yyyy.mm.dd.N>` or `-Release` as for `deploy-cloud`) |
| Working folder | `.` |
| Time limit | `3600` (a screens image build, then the deploy's image build and rollout) |
| Pin mode | `files` |
| Also pin these paths | `scripts/deploy.ps1`, `scripts/version-functions.ps1`, `Dockerfile`, `remote-ui.lock.json`, `remote-ui-build/**` |
| Description | `Builds the desktop screens image in djlacr when the lock's commit has none, then deploys yawble-cloud` |
| What to check after | `https://cloud.yawble.ai/api/health answers 200 with the new version` |

Pinning `scripts/deploy.ps1` and `remote-ui-build/**` as well as the entry script means a change to any step asks you
again. The setup is the same as for `deploy-cloud` (`az login` with rights on `yawble-cloud` and `djlacr`). A
`-DryRun` twin (`deploy-cloud-with-screens-dry-run`) is again a cheap first task to approve.

Do not make two tasks that must run in order (a screens build task, then `deploy-cloud`): each would be approved and
run on its own, and nothing would stop the second one running when the first one failed.

Ask: "deploy the cloud". Approve; the result holds the rollback command the script printed, and **What to check
after** says where to confirm it is live.
