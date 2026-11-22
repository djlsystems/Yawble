# B125 verify (card 563): folder trigger "Fire on" (added files / added or changed / any change)

Tester: TesterWren, 2026-11-22. Base: bundle main `d6756c0`. Scratch branch `scratch/b125-verify` from it, merging
unchanged DeveloperMarlo's `members/yawble-engineering-team/developermarlo` (`5f01691`; B125 commits `750a1a6` core +
`5f01691` tests) and then DeveloperInes's `members/yawble-engineering-team/developerines` (`f6212a8`, B125 shell).
Merge commit tested: `a570edb`. Both developer branches also carry already-verified B123/B124 commits from the team
branch; only the B125 commits are reviewed here. yawble-cloud: no change for B125.

## 1. Diff review against B125 "What to build": PASS

Reproduce: `git show 750a1a6 5f01691 f6212a8`.

| B125 point | Result | Where |
|---|---|---|
| Fire on: Added files / Added or changed / Any change | OK: `FolderWatch.Added = "added"`, `AddedOrChanged = "added-or-changed"`, `AnyChange = "any"`, `FireOnValues` | Triggers/FolderWatch.cs |
| New triggers default to Added files | OK, twice: UiRpc `TriggerDraftOf` sends `"added"` when `fireOn` is absent and there is no `id`; `BusEngine` save fills `added` for a new draft with an empty value | Ui/UiRpc.cs:246, Bus/BusEngine.Triggers.cs:186-188 |
| Edit without it keeps its value | OK: UiRpc passes `""` for an edit without `fireOn`; save replaces `""`/null with the stored trigger's `Watch.FireOn` (a trigger that was not a folder before gets `added`) | same |
| Stored in a migration; old rows keep Any change | OK: `0055_trigger_fire_on.sql` adds `fire_on TEXT NULL CHECK (fire_on IS NULL OR fire_on IN ('added','added-or-changed','any'))` and sets `'any'` where `watch_root IS NOT NULL`; the reader also maps a NULL to `any` | Storage/Migrations/0055, Bus/BusStore.Triggers.cs:134-135 |
| Insert and update write it | OK: `$fo` in INSERT and UPDATE | Bus/BusStore.Triggers.cs |
| CheckWatch validates it | OK: anything not in `FireOnValues` is refused "fire on is one of added, added-or-changed, any" | Bus/BusEngine.Triggers.cs:224 |
| Baseline always takes every change; non-matching change neither fires nor stays pending | OK: `Look` diffs, filters with `FolderChange.Only(fireOn)`, and always returns the new baseline `State(now, null, null)`; the engine stores it when nothing fires (`SetWatch`) | Triggers/FolderWatch.cs `Look`, BusEngine.Triggers.cs:117-121 |
| Remove + add in one quiet period fires for and lists the add only | OK: `Only("added")` keeps `Added`, empties `Removed`/`Changed` | FolderWatch.cs `Only` |
| Describe lists only the kinds that fire | OK: `Describe` runs on the filtered change | FolderWatch.cs |
| A skipped fire (busy, paused) fires at the next look with the same filtered list | OK: baseline is kept only when the outcome is `fired`, so the next look diffs from the old baseline and filters again | BusEngine.Triggers.cs:124 |
| UI: Triggers > Folder "Fire on" select | OK: `<select data-test="trigger-watch-fire-on">` from `WATCH_FIRE_ON_LABELS`; new trigger `added`; edit loads `fireOnOf(t.watch)`; hint text follows the choice | shell TriggersDialog.vue |
| Trigger list shows it; cloud and phone read-only | OK: list row `data-test="trigger-fire-on"` "Fire on: <label>" for folder triggers. The phone/cloud remote view is the same dialog (per the developer); no separate cloud view, so no yawble-cloud change | TriggersDialog.vue |
| docs/features.md | OK: the Folder kind describes Fire on, its three choices, the default and the old-trigger rule | docs/features.md:715-719 |
| Tests use fakes only | OK: `FolderWatcher.Look` with in-memory listings, and an engine rig over a temp documents folder; no system program | FolderTriggerTests.cs |

### Contract between core and shell

- **triggers.list returns `watch.fireOn`**: OK. `triggers.list` is `S(await bus.TriggersAsync(...))`, serialised with
  `RpcDispatcher.Json` (camelCase, the same path that already gives the shell `watch.quietSec`), so the record's
  `FireOn` reaches the shell as `watch.fireOn`. The shell type is `fireOn?: WatchFireOn | null` and `fireOnOf` maps a
  missing value to `any`.
- **triggers.save reads a flat `fireOn`**: OK. The shell's `payload()` sends `fireOn` next to `watchRoot`, `watchPath`,
  `watchGlob`, `quietSec` (flat, folder kind only); core reads `OptStr(p, "fireOn")` in the same block as those.
- **triggers.preview**: core does NOT use `fireOn` there, and that is fine. `"triggers.preview" =>
  TriggerPreview(TriggerDraftOf(p, preview: true).Schedule)`: the preview takes only the schedule (the next times), so
  the extra `fireOn` field the shell sends is parsed into the draft's watch and thrown away. Fire on does not change
  when a folder trigger looks, so there is nothing for the preview to show. No fault; noted for the record.

Observations (not faults):
- No core test drives `triggers.save`/`triggers.list` through UiRpc JSON; the default/keep rules are tested at the
  engine level (`BusEngine` save), and UiRpc's part is a one-line mapping reviewed above.
- An edit resets `watch_state` (as before B125), so changing Fire on starts from a fresh baseline: no surprise fire.

## 2. Targeted tests: PASS

Built `dotnet build core/tests/Yawble.Core.Tests/Yawble.Core.Tests.csproj -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false`
(0 warnings, 0 errors), then `core\tests\Yawble.Core.Tests\bin\Debug\net10.0\Yawble.Core.Tests.exe -class <name>`:

| Class | Result |
|---|---|
| `Yawble.Core.Tests.Bus.FolderTriggerTests` | 15/15 passed |
| `Yawble.Core.Tests.SchemaMigratorTests` | 2/2 passed |
| `Yawble.Core.Tests.BackupTests` | 30/30 passed |
| `Yawble.Core.Tests.NameLeakTests` | 2/2 passed (the long-path AStrayTestOutputFile test passed this time too) |

Shell (`shell/`):
- `npx vitest run tests/unit/triggers.test.ts tests/unit/loading-everywhere.test.ts`: 2 files, **37/37 passed**.
- `npx vue-tsc -p tsconfig.renderer.json --noEmit`: exit 0, no errors.

## 3. Smoke-sessions test and old rows: PASS

- `SmokeSessions_TheMemberMovesTheFileOutAfterTheFire_AndNoSecondFireHappens` (FolderTriggerTests.cs:257) runs the real
  engine sweep over a temp documents folder with an Added-files trigger: `IMG_0001.HEIC` arrives and fires ("added (1):
  IMG_0001.HEIC"); the file is then moved out to an archive folder and the trigger is swept through settle plus two more
  looks: `LastSeq` and `LastFiredAt` are unchanged (no second fire); `IMG_0002.HEIC` then fires a new run whose
  instruction lists "added (1): IMG_0002.HEIC" and does not mention IMG_0001. Passed.
- `AFolderTriggerSavedBefore0055_ReadsAsAnyChange_AndOtherKindsKeepNone` (line 325) drops `fire_on` and the 0055 row
  from `schema_migrations`, lets `SchemaMigrator.Migrate` re-apply 0055, and checks the folder trigger reads as `any`,
  the Every trigger keeps `fire_on` NULL and no watch, and the CHECK refuses `'removed'`. Passed. (Note: the reader also
  maps NULL to `any`, so this read alone would pass without the migration's UPDATE; the UPDATE is correct by reading
  `0055_trigger_fire_on.sql`. Not a fault.)
- Also passed: `ASkippedFire_FiresAtTheNextLook_WithTheSameFilteredList` (paused team: skipped, then after resume fires
  with "added (1): new.jpg" and no "removed"/old.jpg), the added-only / added-or-changed / any `Look` tests, and the
  new/edit/unknown save rules.

## 4. Full core suite: PASS (at the environmental baseline)

Under the heavy-work lease, `core\tests\Yawble.Core.Tests\bin\Debug\net10.0\Yawble.Core.Tests.exe` (no filter) in the
background to a file, at `a570edb`:

**Total: 4301, Errors: 0, Failed: 210, Skipped: 118, Not Run: 0, Time: 666.7 s.**

- 210 failures is inside the sandbox's environmental baseline of 200-305 (B122 verify on base d6756c0: 209 of 4286).
  The 15 more tests are the merged B123/B124/B125 tests.
- The failing classes are the same known groups as in B122-verify: Concierge (ConciergeActionTests 31, ReadTools 7),
  Companion (Server 22, Service 6, Cli 3), the Teams git classes (TeamSync, TeamRepos, TeamBranch*, TeamGit*,
  TeamPublish, TeamBehind and others), IpcServer/IpcEndpoint, CoreProcess, LauncherTeamRepoTests (fails on base too),
  WebsiteHostTests and a few singles: the long-path, msys sh and loopback/pipe failures.
- **No failure** in any class B125 touches or depends on: Bus.FolderTriggerTests, SchemaMigratorTests, BackupTests,
  the Triggers/Scheduling trigger tests, NameLeakTests. Also none in the B123/B124 classes merged in through the
  developer branches (DecisionOptionsTests, StatusLineRestartTests, ClaudeKeychainSignInTests,
  MacHostCredentialReaderTests, SignInTests, NeverEmitTests, RunTreeTests, LauncherTests).
- New failures caused by B125: **none found**.

## Verdict: PASS

B125 does what "What to build" asks: Fire on is stored (migration 0055, old folder rows `any`), validated, defaulted to
`added` for a new trigger and kept on an edit without it; the watcher always takes the new baseline and fires and lists
only the matching kinds; the shell has the Fire on select, sends a flat `fireOn`, and shows it in the list. The
Smoke-sessions test passes (moved out after the fire: no second fire; the next photo lists only itself). Targeted core
classes and the shell specs pass (15/15 FolderTriggerTests, 37/37 vitest, vue-tsc clean), and the full core suite is at
the environmental baseline with no new failure. Open note only: `triggers.preview` ignores the `fireOn` the shell sends,
which is correct since the preview shows schedule times only.
