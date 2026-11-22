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
