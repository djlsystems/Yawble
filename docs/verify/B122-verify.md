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

## 4. Full core suite

(running)

## 5. Mac mini checklist (the real macOS path)

Use a build of the core that contains 2ecf6f4, on the Mac mini, with any member.

1. Give the member an instruction whose agent runs a build that hangs, for example: create a project whose build target runs `<Exec Command="sleep 600" />`, then run `dotnet build -m:4 -nodeReuse:false` and end your turn without waiting for it. Alternatively, have the agent start the build in the background (`dotnet build -m:4 -nodeReuse:false &`) and exit.
2. While the run is live, note the run's `yr-*` folder (under `$(getconf DARWIN_USER_TEMP_DIR)`) and the build's pids: `ps -axo pid,ppid,pgid,etime,command | grep -E 'dotnet|MSBuild|sleep|zsh'`.
3. End the run: let the CLI exit on its own (the B122 case), and repeat once with Stop from the app.
4. 10 s after the run shows as done:
   - `ps -axo pid,ppid,pgid,etime,command | grep -E 'dotnet|MSBuild|sleep|zsh'`: no process from the run may be left. That includes none with ppid 1 and none in the build's old pgid. The core's own processes and unrelated dotnet servers are not counted.
   - `lsof +D "<the yr-* folder>"`: the folder must be gone ("No such file or directory"), or show no open files.
5. Run record: `sqlite3 <core db> "select run_number, end, tree_survived, reason from run_records order by run_number desc limit 2;"` must show `tree_survived = 0` and no "still running after SIGTERM and SIGKILL" reason.
6. Negative check (optional): make a process that cannot be killed, for example one stopped in uninterruptible I/O. This is hard to stage. If it can be staged, `tree_survived = 1` and the reason must name the count.
7. Also check that the core itself and other members' runs kept running (the core's group is never signalled). Run `MacProcessTableListsThisProcess` (`-class Yawble.Core.Tests.Launch.RunTreeTests`): it must pass on macOS, not skip.
