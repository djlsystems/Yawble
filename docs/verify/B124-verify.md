# B124 verify (card 550): Claude Code's Keychain sign-in read with macOS's `security` tool

Tester: TesterWren, 2026-11-22. Base main `d6756c0`. Tested DeveloperInes's card 549 commits `93f3093` + `2638622`
unchanged, as merged into `members/yawble-engineering-team/testerwren` (merge `377154f`, tested at `81338ca`).
Windows sandbox: the macOS code paths are checked with the fake security tool and by reading the code; the real
Keychain behaviour is owed to the Mac mini checklist at the end.

## 1. Diff review against B124: PASS

Files: `core/src/Yawble.Core/Credentials/MacSecurityTool.cs` (new), `HostCredentialReader.cs`, `MacKeychainStore.cs`,
`ClaudeHostSession.cs`, `docs/features.md`, tests in `Credentials/MacHostCredentialReaderTests.cs`,
`Credentials/ClaudeKeychainSignInTests.cs`, `Agents/SignInTests.cs`, `Policy/NeverEmitTests.cs`.
Reproduce: `git diff 93f3093^1 2638622 -- core docs`.

| B124 point | Result | Where |
|---|---|---|
| `/usr/bin/security` absolute path only, no PATH lookup, no shell | OK: `ProcessStartInfo(ProcessSecurityTool.Path)`, `UseShellExecute = false` | MacSecurityTool.cs `ProcessSecurityTool` |
| argv list | OK: `psi.ArgumentList.Add(a)`; argv is `find-generic-password -s <service> [-w]` | `MacHostCredentialReader.Arguments` |
| 5 s timeout with kill | OK: `Timeout = 5 s`; `WaitForExit(timeout)` else `Kill(entireProcessTree: true)`; stdin closed, stderr drained and dropped | MacSecurityTool.cs |
| exit 44 = not signed in | OK: `Read` returns null, `Targets` returns empty | HostCredentialReader.cs |
| other failures give the exit code only | OK: "exited with code N" / "did not answer within 5 s"; `SecurityToolResult.ToString()` never shows stdout | HostCredentialReader.cs, MacSecurityTool.cs |
| value never reaches a log/error/exception/file | OK: stdout kept in memory only; `ClaudeHostSession` logs only `CredentialStoreException.Message` (app text); JSON errors are swallowed unlogged | ClaudeHostSession.cs `Read` |
| SecKeychain* no longer used for "Claude Code-credentials" | OK: `ItemExists`/`ReadItem` and the `SecKeychainFindGenericPasswordItem` import removed; `MacKeychainStore` constructor refuses the service | MacKeychainStore.cs |
| SecKeychain* still used for the app's own store | OK: Get/Has/Set/Delete unchanged apart from the guard | MacKeychainStore.cs |
| no write/modify/delete/ACL path for that service | OK: grep of core/src finds no `add-/delete-/set-*-partition-list`, `SecItem*`, `SecAccess*`; Set and Delete call `ForeignKeychainItems.EnsureWritable` first | MacHostCredentialReaderTests `NoCodePathCanWriteAnotherProgramsKeychainItem` |
| caching only near RenewMargin, after a refresh or a 401 | OK: `CurrentAsync` returns the kept token while it is not within `RenewMargin`; `RefreshAsync(force: true)` (401) drops it before and, if not renewed, after; a renewal reads it again | ClaudeHostSession.cs `Kept`, `RefreshAsync` |
| tests use fakes only | OK: every test builds `MacHostCredentialReader(FakeSecurityTool)`; `ProcessSecurityTool` is never constructed in tests | tests above |
| Sandbox unchanged | OK: `core/src/Yawble.Core/Guardrail/GuardrailCheck.cs` and `bridge/src/Yawble.GuardCheck/*` untouched since base (`git diff d6756c0 HEAD --stat` does not list them); the expectation `credstore.keychain:Claude Code-credentials` = Blocked is still at GuardrailCheck.cs:430 | |
| docs/features.md | OK: "Always Allow" gone (grep of the repo finds no match); says the gateway reads the item "with macOS's own `security` tool", keeps the token in memory, never changes the item | docs/features.md:149-153 |

Notes (not faults):
- `Read` decodes a hex-printed password only when it decodes to UTF-8 text starting with `{`; Claude Code's JSON is
  printable, so this fallback should not normally run.
- The never-write guard is a source-text scan: it catches the literal verbs and API names, not a verb assembled from
  parts. That is the same strength as NameLeakTests and is fine for review purposes.

## 2. Never-write mutation check: PASS (the test catches both mutations; both reverted)

Mutation A, a delete path in the reader: added to `MacHostCredentialReader` in HostCredentialReader.cs
`public void Forget(string service) => _tool.Run(["delete-generic-password", "-s", service], Timeout);`
then built (OK) and ran
`Yawble.Core.Tests.exe -class "Yawble.Core.Tests.Credentials.MacHostCredentialReaderTests"`:
`NoCodePathCanWriteAnotherProgramsKeychainItem [FAIL]` with "Yawble.Core/Credentials/HostCredentialReader.cs:
delete-generic-password" (14 run, 1 failed).

Mutation B, the guard removed from the app store's `Delete`: deleted the line
`ForeignKeychainItems.EnsureWritable(service, "a delete");` in MacKeychainStore.cs:
`NoCodePathCanWriteAnotherProgramsKeychainItem [FAIL]` (Assert.Contains: sub-string not found).

After `git checkout --` of both files and a rebuild: the class passes, 14/14.

## 3. Changed classes, NeverEmitTests, NameLeakTests: PASS

Binary: `core\tests\Yawble.Core.Tests\bin\Debug\net10.0\Yawble.Core.Tests.exe`, built from `81338ca` with
`dotnet build core/tests/Yawble.Core.Tests/Yawble.Core.Tests.csproj -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false`.
Each run as `Yawble.Core.Tests.exe -class "Yawble.Core.Tests.<class>"`:

| Class | Total | Failed |
|---|---|---|
| Credentials.MacHostCredentialReaderTests | 14 | 0 |
| Credentials.ClaudeKeychainSignInTests | 16 | 0 |
| Agents.SignInTests | 24 | 0 |
| Policy.NeverEmitTests | 130 | 0 |
| NameLeakTests | 2 | 0 |
| Guardrail.GuardrailCheckTests (extra) | 18 (5 skipped) | 1: `TheDecoyListensOnIPv4Loopback_WhereTheProbeConnects` |

- NameLeakTests passed in full here, including the stray-output-file test Ines saw fail with "Filename too long".
- The one GuardrailCheckTests failure is the sandbox's loopback denial Ines reported. Neither
  `core/src/Yawble.Core/Guardrail` nor `core/tests/Yawble.Core.Tests/Guardrail` changed since base `d6756c0`
  (`git diff d6756c0 HEAD --stat -- core/src/Yawble.Core/Guardrail core/tests/Yawble.Core.Tests/Guardrail` is empty), so it is the same
  code and the same failure on base. Environmental, not caused by B124.

## 4. Full core suite once, under the heavy-work lease: PASS (no new failures)

`Yawble.Core.Tests.exe -longRunning 120 -xml %TEMP%\b124-full.xml` at `81338ca` (lease 16:27-16:40 UTC, released):
**Total 4274, Failed 211, Skipped 117, Errors 0, 721 s.** 211 is inside the sandbox's 200-305 environmental baseline.

- No failure in any B124 class: Credentials.*, Agents.SignInTests, Policy.NeverEmitTests, NameLeakTests all pass.
- The failures are the sandbox's known kinds: git in the sandbox ("Filename too long", "Could not read from remote
  repository", so TeamGit checks report "could not run"), loopback/named pipes (CompanionServer*, IpcServer*,
  IpcEndpoint*, WebsiteHost*, GuardrailCheckTests' IPv4 decoy), and child processes (CoreProcessTests). Largest groups:
  ConciergeActionTests 31, CompanionServerTests 22, TeamReposTests 11, TeamSyncTests 11, TeamBranchBlockingTests 9.
- The five failing tests whose names contain "Credential" (TeamReposTests x2, TeamPublishTests x1,
  ConciergeReadToolsTests x1, plus GuardrailCheckTests' decoy) are git-remote or loopback tests in code B124 did not touch.
- ConciergeReadToolsTests (7) is not named in baseline-test-report.md; its failing tests are the TeamGit ones, failing on
  git fetch in the sandbox. The only change since base in ConciergeReadTools.cs is B123's `DecisionText` (f3a6bee),
  which these tests do not reach. Not B124.
- No test hung: the only long-running report was `Bus.Card478B104VerifyTests.TheLimit_DefaultsTo200_IsClamped_AndNeverCutsAnOpenRow`
  (over 5 min, then finished; it is not a B124 test).

## 5. Mac mini checklist for the person (signed Mac build): owed

The sandbox is Windows, so the real `/usr/bin/security` run and macOS's Keychain dialog cannot be checked here.
On the Mac mini, with a Developer ID–signed build that contains `93f3093` + `2638622`:

1. Make the old trust go away, so the test is fair: open **Keychain Access**, find the login item
   **Claude Code-credentials**, **Get Info > Access Control**, and remove **yawble-core** from the list
   (or wait until Claude Code has refreshed its token at least once since the update, which rewrites the item and resets the list).
2. Quit and start Yawble. **Settings > Agents**: Claude Code shows as signed in ("the login Keychain item
   "Claude Code-credentials"").
3. Start a team and let agents run for a while, long enough to cross a Claude token refresh (the access token lives
   about 8 hours; or sign in again in a terminal with `claude` mid-run to force a rewrite of the item).
4. Throughout: **no** macOS dialog "yawble-core wants to use … Claude Code-credentials" appears, at start, during runs,
   or after the refresh. Agents keep getting model answers after the refresh (no "not signed in" error).
5. Open the run's **guardrail check** from a run: it still shows `credstore.keychain:Claude Code-credentials` as
   **Blocked** (a run cannot find the item).
6. Optional: in Keychain Access, the item's Access Control list was not changed by Yawble (it lists only what Claude
   Code put there), and the app log has lines "claude sign-in: ..." but never a token value.

If a dialog appears at step 4 or the check at step 5 is not Blocked, B124 fails on the Mac: report which step.


## Verdict

**PASS** on everything a Windows sandbox can check (sections 1-4). The real macOS behaviour (no Keychain dialog across a token refresh, guardrail still Blocked) is owed to the Mac mini checklist in section 5.
