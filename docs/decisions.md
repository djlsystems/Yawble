# Decisions

Choices that are not obvious from the code, with what was considered. Newest last.

## 2026-10: Where the sign-in page gets Chromium (replaced below)

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

## 2026-10: Which browser the sign-in page runs (replaces the choice above)

**Context.** A site a person signs in to through the sign-in page must not be able to tell that
browser from the person's own Chrome. `chrome-headless-shell` says so itself ("HeadlessChrome" in the
user agent and client hints) and has other headless and automation tells, so the choice above no
longer holds. The image has Xvfb; it has no Chrome or Chromium installed system-wide.

**Decision.** **Google Chrome stable**, run **headed** on an Xvfb display of its own, with Chrome's
own sandbox on.

- **Which build, and why branded Chrome.** Google's own stable build is what most people run: its
  `Sec-CH-UA` brands say "Google Chrome", it plays H.264/AAC and has Widevine, as a person's does.
  Chromium (or Playwright's `chromium`, which is now Chrome for Testing) brands itself differently,
  lacks the codecs, and Chrome for Testing shows a "for automated testing" bar. Checked here, before
  any code: Chrome 156 runs headed under Xvfb as a non-root user with every library it needs already
  in the image (only its optional Qt shims are not), and `chrome://sandbox` reports the namespace and
  seccomp-BPF sandbox on, without `--no-sandbox`.
- **Where it comes from.** `scripts/install-google-chrome.sh` reads the newest stable version, its
  `.deb` path and its SHA-256 from Google's apt index (`dl.google.com/linux/chrome/deb`, over HTTPS),
  downloads the `.deb`, checks the hash and unpacks it without root (`dpkg-deb -x`). The setuid
  sandbox helper is left out: Chrome uses its user-namespace sandbox.
- **Where it is stored.** `/data/google-chrome/<version>/` on the volume (`HARNESS_SIGN_IN_CHROME`
  overrides), owned by the `worker` user, readable by `agent` and writable by no agent
  (`prepare-volume.sh`): an agent that could replace the browser could read every session typed into
  it. Nothing is added to the image; the entrypoint creates the folder and runs the script.
- **How it stays current.** Every worker start (and `all`) runs the script as `worker`, under a lock
  all workers share. It installs a newer stable beside the old one whenever Google's index has one,
  so the version - and with it the user agent and client hints a site sees - follows real Chrome
  stable within one restart. It keeps the newest two, so a browser still open on the older one is not
  pulled from under it. There is no pinned version: being current *is* the requirement (a stale
  Chrome is itself a tell). The version on each start is recorded in `cli-versions.jsonl`
  (`googleChrome`). A start with no network keeps what it has.
- **What makes it ordinary, with real settings, not page patches.** Headed, never `--headless`; no
  `--enable-automation` (the worker drives Chrome over its own pipe, not Playwright, so no default
  automation switches are added); `--disable-blink-features=AutomationControlled`, because a browser
  with a DevTools pipe otherwise reports `navigator.webdriver = true`, with `--test-type` so Chrome
  does not show a warning bar about that flag (it would take height from the page); WebGL through the
  machine's own OpenGL (`--use-angle=gl`, Mesa llvmpipe, as on a Linux desktop without a graphics
  card), not none at all; a 1920x1080 screen and a real window around the 1280x800 page, no device
  metrics override. The person's languages are Chrome's own `intl.accept_languages` preference and
  `--lang`, the time zone the process's `TZ`. Nothing runs in the page's own JavaScript world: what
  the platform reads (the identity it saves) is read in an isolated world.
- **The claim is coherent: Linux x86_64.** The user agent, the client hints and `Sec-CH-UA-Platform`
  say Linux; so do the fonts and the WebGL renderer. A Windows or macOS claim would contradict them,
  and faking those is exactly the brittle patching we do not do.
- **The display is private.** Each browser gets its own Xvfb (`-nolisten tcp`), which accepts only
  the holder of a fresh cookie kept in the browser's 0700 folder, so no agent can watch or type.
- **One profile per row**, `/data/sign-in-profiles/<member hash>/<row hash>/`, `worker:worker` 0700
  all the way down; reused on every sign-in for the row, one browser at a time (a file lock beside
  it), and removed with the row, the member or its team (`DeleteSignInProfiles`, sent to every
  connected worker).

**Considered.** Playwright's `chromium` / Chrome for Testing (already on the volume: a test build,
its own brand and warning bar, no proprietary codecs); Chromium from a distribution (needs root to
install, and is not what most people run); a Windows or macOS user agent (contradicted by the fonts,
WebGL and platform hints); JavaScript patches to hide headless or automation (brittle, and themselves
detectable); Google Chrome in the image (the image would grow by the browser and fall behind stable
between image builds).

**Left open.** A worker that is not connected when a row or member is deleted is not told to remove
that profile; it stays on the volume (still the worker user's alone) until the same row name is used
again, which would then reuse it.
