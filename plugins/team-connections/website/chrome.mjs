// GOOGLE CHROME, THE PLATFORM'S OWN COPY. A website connection's browser is the sign-in page's: the
// stable build scripts/install-google-chrome.sh keeps on the volume, <root>/<version>/chrome, where root
// is $HARNESS_SIGN_IN_CHROME or google-chrome in the data root (the worker hands it over). Nothing is
// fetched here: a worker without it says so, as the sign-in page does. Moved from the Web Credentials
// plugin (CON 6), less its own download.
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

export function platformRoot() {
  return process.env.HARNESS_SIGN_IN_CHROME || join(process.env.HARNESS_DATA_ROOT || '/data', 'google-chrome');
}

// Compares dotted versions numerically.
export function compareVersions(a, b) {
  const x = String(a).split('.').map(Number);
  const y = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] || 0) - (y[i] || 0);
    if (d !== 0) return d;
  }
  return 0;
}

const major = (v) => String(v || '').split('.')[0];

// Every installed Chrome under the root: [{ version, path }], newest first.
export function installed(root) {
  let names = [];
  try {
    names = readdirSync(root);
  } catch {
    return [];
  }
  return names
    .filter((n) => /^\d+(\.\d+)+$/.test(n))
    .map((version) => ({ version, path: join(root, version, 'chrome') }))
    .filter((c) => existsSync(c.path))
    .sort((a, b) => compareVersions(b.version, a.version));
}

// The copy to run: the version a saved session's identity names, else one of its major, else the
// newest. Answers null when there is none.
export function choose(copies, wanted) {
  if (copies.length === 0) return null;
  if (wanted) {
    const exact = copies.find((c) => c.version === wanted);
    if (exact) return exact;
    const same = copies.find((c) => major(c.version) === major(wanted));
    if (same) return same;
  }
  return copies[0];
}

// The Chrome a run uses: { version, path }, or throws with the sign-in page's sentence.
export function chromeFor({ root, wanted }) {
  const pick = choose(installed(root || platformRoot()), wanted);
  if (!pick) throw new Error('this worker has no Google Chrome installed for signing in (scripts/install-google-chrome.sh puts it in google-chrome on the volume)');
  return pick;
}
