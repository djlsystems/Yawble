# W27510 independent verification

Product commit tested: c07ddbeef89d304e83b339969f85a619f4f87abb.
Baseline: ce620e1d6d6a5c5ef9eb9cf982fadf5d0c6af66c.

Report: /data/documents/YawbleCoreEngineeringTeam/w27510-verification.md
Evidence: /data/documents/YawbleCoreEngineeringTeam/w27510-verification/

These are verification scripts only, not product changes. The browser scripts require
Playwright installed outside the checkout and TEST_ADMIN_EMAIL / TEST_ADMIN_PASSWORD
in the process environment. They use isolated Hosts on 3871 (fixed) and 3872 (baseline),
with private roots under /tmp/lior-w27510. setup.mjs bootstraps an EMPTY private Host;
never use it against an existing or live instance. No credential is recorded in these scripts.

check.mjs checks the real UI and tab behavior, sign-in, next validation, tampering, modifiers.
engines.mjs checks clean Chromium, a separate persistent Chromium profile with privacy flags,
the installed WebKit, and attempts Firefox. PORT selects the baseline/fixed private Host.
repro.mjs is adapted from Soren's supplied kit; it removes cookies by forwarding intercepted
entry requests through node:http, rather than letting the browser reattach its cookies.
DROP=all MODE=globe exercises the reported loop. MODE=open uses a raw entry link.
raw-refusals.mjs checks direct entry refusals without loops.
PrincipalProbe.csproj uses the production issuer to mint two fixture machine principals
in the PRIVATE database, then asserts /open returns 403. Members authenticate as Container.
It holds generated credentials only in memory; anonymous must return 401.

The baseline source was extracted from git archive into .verification-baseline inside this
card's own worktree. Neither the main clone nor another member's worktree was changed.
No full test suite was run. No merge, release, deployment, or request to live 8080 was made.
