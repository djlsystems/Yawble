---
name: testing-console-apps
description: How to build and test a console program, including one that waits for a key press, without a person at the keyboard.
roles: [member]
---
# Testing console apps

You have no keyboard and no visible console: your commands run with their input and output redirected. A
program that waits for the person (a key press, a prompt) must still be testable.

## A program that waits for a key

`Console.ReadKey()` throws `InvalidOperationException` when input is redirected, so a program that only uses
`ReadKey` cannot be tested here and crashes if piped. Write it so it works both ways:

```csharp
Console.WriteLine("Hello World");
Console.WriteLine("hit a key to continue");
if (Console.IsInputRedirected) Console.Read();      // piped or redirected: any input character ends it
else Console.ReadKey(intercept: true);              // a real console: any key
```

Test it by feeding it one key and checking the output and the exit code:

```powershell
$out = 'x' | & $exe 2>&1; $code = $LASTEXITCODE
$out; "exit code: $code"
```

Also test with no input at all (`$null | & $exe`), which must still end rather than hang. Put a time limit on any
run that might wait, e.g. `Start-Process -NoNewWindow -Wait` with a timeout via `Wait-Process -Timeout 20`.

## What to report

- The exact command you ran, the output you saw, and the exit code.
- The FULL path of the executable you tested, from `(Resolve-Path <path>).Path`, so the person can run it in
  their own terminal and press a real key.
- Anything you could not check here (a real key press in a real console) and that the person should try.

## Unit tests

Prefer a small test project (`dotnet new xunit`) for logic, and keep the console entry point thin. `dotnet test`
runs offline only if the test packages are already in your NuGet folder; if a restore is refused, test the
program end to end as above and say why.

Before running the repository's full test suite, a full build, an image build or anything else you know is heavy,
take the heavy-work lease with `lease` (action acquire, what = the step); release it as soon as that command
finishes. While iterating run only the tests you touched; run the full suite once when finished.
