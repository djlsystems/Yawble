# Decisions

Choices that are not obvious from the code, with what was considered. Newest last.

## 2026-10: Where the sign-in page gets Chromium

**Context.** The interactive sign-in page (see "Signing in to a site" in `plugins.md`) runs a headless
Chromium for a person to sign in through. The platform already has one: the container image installs
Chromium's system libraries (`playwright install-deps chromium` in the `Containerfile`), and
`scripts/ensure-agent-clis.sh` installs Playwright's `chrome-headless-shell` onto the data volume at
`PLAYWRIGHT_BROWSERS_PATH` (`/data/ms-playwright`) once, at container start, for agents testing web
apps. The image itself carries no browser.

**Decision.** The sign-in browser uses that same `chrome-headless-shell`: the newest
`chromium_headless_shell-*` under `PLAYWRIGHT_BROWSERS_PATH` (`ChromiumSignInBrowser.Find`). Nothing
is added to the container image. No .NET Playwright package either: the worker drives Chromium over
its own DevTools pipe (`--remote-debugging-pipe`, `CdpPipe`), so there is no debugging port for
anything else in the container to reach and no second browser download.

- **Sandboxed.** Chromium runs with its own sandbox on (user namespaces and seccomp), never
  `--no-sandbox`; it works as the non-root user the worker runs as. A browser that cannot sandbox
  fails to start, and the page says so, rather than starting without it.
- **On a worker, not on control.** Control starts no process but git (`WorkerArchitectureTests`), so
  the browser runs in `Harness.Worker` behind the run protocol (`StartSignInBrowser`,
  `SaveSignInBrowser`, `StopSignInBrowser`), and its screen comes back as a worker stream.
- **One profile per session**, a fresh `0700` folder of the worker's, removed when the browser
  closes.
- **No Chromium installed** (the volume install failed or was removed): the page answers that no
  browser can start on that worker, and the person can try again once `ensure-agent-clis.sh` has
  installed it.

**Considered.** Microsoft.Playwright for .NET (a second driver and browser download for what a pipe
and a dozen DevTools commands do); Chromium in the image (the image would grow by the browser, and
the volume install already exists and is kept up to date).
