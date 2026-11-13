---
name: testing-a-web-app
description: How to start and check a web app here - HTTP checks, the project's own tests, and a browser only if the project and the network allow one.
roles: [member]
---
# Testing a web app

The app gives you no browser and no display. What you can use is what the repository and its toolchain
offer, within what the person's network policy allows. Say plainly what you could and could not check.

## Start the app

1. Pick a free port in 3000-3999: `Get-NetTCPConnection -State Listen | Select-Object LocalPort`.
2. Start it in the background with its output in a file, and keep the process:

   ```powershell
   $app = Start-Process -FilePath dotnet -ArgumentList 'run','--urls','http://127.0.0.1:3100' `
       -RedirectStandardOutput app.log -RedirectStandardError app.err -NoNewWindow -PassThru
   ```

   (`npm run dev -- --port 3100` and the like work the same way.) Output goes to a file: a server holding your
   output never lets your run finish. Keep the log files out of commits.
3. Wait until it answers before you test, and give up after a minute with the log's tail:
   `Invoke-WebRequest http://127.0.0.1:3100/ -UseBasicParsing` in a short loop.

On macOS and Linux your shell is sh, not PowerShell: start it with
`nohup npm run dev -- --port 3100 > app.log 2> app.err & echo $! > app.pid`, find a free port with `lsof -iTCP -sTCP:LISTEN`,
check with `curl -fsS http://127.0.0.1:3100/`, and stop it with `kill "$(cat app.pid)"`.

## Check it

In this order, and go as far as you can:

- **The project's own tests** (`dotnet test`, `npm test`), including any integration tests that start the app.
- **HTTP checks** with `Invoke-WebRequest` or `curl.exe`: status codes, redirects, that a page contains the
  text a person should see, that an API returns the JSON you expect. Save what you fetched when it helps.
- **A browser**, only if the project has one set up, e.g. Playwright in its `package.json`. Use the project's
  Playwright tests when they exist; otherwise a short script: open the page, act, assert what a person would
  see. Run headless. Set the viewport for layout checks (390 wide for a phone). A single-page app is blank at
  first load: wait for an element you expect before you assert or screenshot.

Installing Playwright or its browser needs the npm registry and the browser download hosts allowed in the
network policy. If a download is refused, do not try another source: test with HTTP checks and the project's
tests, and say in your report which host the person would need to allow (see `dev-tools`).

Before running the repository's full test suite, a full build, an image build or anything else you know is heavy,
take the heavy-work lease with `lease` (action acquire, what = the step); release it as soon as that command
finishes. While iterating run only the tests you touched; run the full suite once when finished.

## Screenshots

Save screenshots into `YAWBLE_TEAM_DOCS` (e.g. `$env:YAWBLE_TEAM_DOCS\screens\checkout-phone.png`) so the person can
see them in the app, and name each one in your report.

## Stop it

Before your run ends, stop what you started by the process you kept: `Stop-Process -Id $app.Id` (or
`kill "$(cat app.pid)"`). Never stop processes by name.

## What to report

- The URL and port you used, what you checked and how (commands, test names), what passed and what failed.
- Where the screenshots are, and anything you could not check here (a real browser, a host the network
  refused) that the person should try themselves.
