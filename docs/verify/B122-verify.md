# B122 verify (card 553): a macOS run's processes end with the run

- Tested: members/yawble-engineering-team/developerines at 2ecf6f4 (card 552), merged unchanged into members/yawble-engineering-team/testerwren. Base: main d6756c0.
- Machine: Windows sandbox. The fix is macOS-only, so the real macOS path (libproc, kill(2)) is checked on the Mac mini (section 5). Here it is checked through the fake process table.
- Build: `dotnet build core/tests/Yawble.Core.Tests/Yawble.Core.Tests.csproj -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false` built with 0 errors.

## 1. Diff review against the B122 spec

Files changed: `Launch/RunTree.cs` (new), `Launch/RunSupervisor.cs`, `Launch/SandboxLauncher.cs`, `Tests/Launch/RunTreeTests.cs` (new), `Tests/Launch/LauncherTests.cs` (LaunchKit seam only).

| Spec point | Where | Result |
|---|---|---|
| The whole tree is ended (TERM, then KILL after a grace) before the run is reported done | `RunTree.Reap`: SIGTERM to every recorded live process and the group it leads, a 2 s grace (newcomers also get TERM), SIGKILL to what is left, up to 2 s more. `RunSupervisor.RunAsync` runs `EndTree(Supervise(...))` before it returns, so this happens before `Finish` and the run record. It runs however the run ended (own exit, timeout, idle, Stop, shutdown). | OK |
| Descendants that left the group are caught | `Track` records every descendant by a ppid walk on each poll (throttled to 250 ms), with its start time, so a process later reparented to launchd stays recorded. It also records members of a group that a recorded process leads, even when their parent was never seen. | OK |
| The temp folder is removed only after that | The `treeGone` callback (`RunTempFolder.Remove(runTemp)`) runs in `EndTree`'s `finally`, after `Reap`. The launcher's old `finally` removal still runs afterwards and does nothing then. | OK |
| treeSurvived is true if anything survived | `TreeSurvived \|= Survivors.Count > 0`, `LeftBehind = count`. The reason says "N of the run's processes were still running after SIGTERM and SIGKILL" and goes to `run_records.tree_survived`. | OK |
| Windows (and Linux) unchanged | `TreeFor` makes a `RunTree` only when `host.Os == MacOS && OperatingSystem.IsMacOS()`, otherwise null, so every `_tree?.` call does nothing. `runTemp` is only set on macOS, so `treeGone` does nothing elsewhere. | OK |
| Tests use fakes only, no real system program | `FakeProcessTable` in-process. LaunchKit passes `_ => null` for the scripted backend so a fake pid never reaches the real table. `MacProcessTableListsThisProcess` reads libproc for this process only (no signal) and is skipped off macOS. | OK |
| Safety | Never signals pid ≤ 1, the core, or the core's group. A pid whose start time differs is skipped (reuse). If the root is not the core's child at the first read, nothing is followed. | OK |
| `proc_bsdinfo` offsets | status 4, pid 12, ppid 16, pgid 100, start_tvsec 120, start_tvusec 128, size 136. These match `sys/proc_info.h`. | OK |

Notes (not faults, nothing to fix for this card):
- A process that is started and orphaned inside one 250 ms observe window is caught only through its group (if a recorded process leads that group) or at the final `Track` in `Reap` (if it is still a descendant then). A double-forked daemon that calls setsid within 250 ms could escape. The Mac mini check (section 5) covers the real hung-build case.
- If `Supervise` throws, `EndTree` is not reached. The launcher's `finally` still removes the folder, but the tree is not reaped. This is the same as before the fix.
- The card asks for "Launch and Tasks classes". The developer changed no Tasks file (host tasks keep `PosixSpawn`'s killpg), so only the Launch classes were run.

## 2. Mutation check

Each mutation was edited into the working tree, rebuilt, and run with `Yawble.Core.Tests.exe -class Yawble.Core.Tests.Launch.RunTreeTests`, then reverted with `git checkout -- <file>`. After the revert the rebuild ran 12 tests, 0 failed.

| Mutation | Result |
|---|---|
| A: drop the SIGKILL escalation (both `SignalAll(alive, SIGKILL);` in `RunTree.Reap` removed) | 4 failed of 12: `AChildThatIgnoresSigtermIsSigkilledAfterTheGrace`, `AGrandchildInItsOwnGroupIsKilledEvenAfterItsParentEnded`, `AProcessInARecordedGroupIsKilledThoughItsParentWasNeverSeen`, `TheRunsTempFolderGoesOnlyAfterTheTreeIsGoneAndTheRunEndsAfterThat` |
| B: remove the temp folder before the kill (`_treeGone?.Invoke()` moved ahead of `Reap` in `RunSupervisor.EndTree`) | 1 failed of 12: `TheRunsTempFolderGoesOnlyAfterTheTreeIsGoneAndTheRunEndsAfterThat` |

Both regressions are caught.

## 3. Changed classes and NameLeakTests

Commit 2ecf6f4 merged; `core\tests\Yawble.Core.Tests\bin\Debug\net10.0\Yawble.Core.Tests.exe -class <name>`. Each class name was checked with `git grep "class <Name>"` first.

| Class | Total | Failed | Skipped |
|---|---|---|---|
| Yawble.Core.Tests.Launch.RunTreeTests | 12 | 0 | 1 (`MacProcessTableListsThisProcess`, macOS only) |
| Yawble.Core.Tests.Launch.RunSupervisorTests | 8 | 0 | 4 (OS-specific, skipped as before) |
| Yawble.Core.Tests.Launch.LauncherTests | 19 | 0 | 2 |
| Yawble.Core.Tests.NameLeakTests | 2 | 0 | 0 |
