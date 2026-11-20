// B091 (card 484): which specs are phone specs, chosen by what they import, not by a hand-kept list of names.
//
// A phone spec loads the built renderer into a phone-sized page (tests/e2e/phone/server.ts, buildRenderer, the fake
// core), so it runs in the `phone` project with at most PHONE_WORKERS workers. playwright.config.ts builds that project's
// testMatch from phoneSpecs() and refuses to load (checkPhoneSpecs) when a phone spec would run outside it or does not
// take `test` from phone/fixtures (which closes the contexts a test leaves open). No Playwright import here, so the unit
// test (tests/unit/b091-phone-specs.test.ts) runs it under vitest.
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

/** An import of a module in tests/e2e/phone/ ('./phone/server', '../phone/fake-core'); every spec inside phone/ counts too. */
const PHONE_IMPORT = /\bfrom\s+['"](?:\.\.?\/)+(?:[^'"]*\/)?phone\/[^'"]+['"]/;
/** buildRenderer named anywhere (a spec that builds the renderer some other way still needs the cap). */
const BUILD_RENDERER = /\bbuildRenderer\b/;
/** `test` taken from phone/fixtures: `import { ..., test, ... } from './phone/fixtures'` (or './fixtures' inside phone/). */
const FIXTURES_TEST = /import\s*\{[^}]*\btest\b[^}]*\}\s*from\s+['"](?:\.\.?\/)+(?:[^'"]*\/)?(?:phone\/)?fixtures['"]/;

const inPhoneDir = (rel: string) => rel.split(/[\\/]/)[0] === 'phone';

/** Whether a spec (its path relative to tests/e2e, and its source) is a phone spec. */
export function isPhoneSpec(rel: string, src: string) {
  return inPhoneDir(rel) || PHONE_IMPORT.test(src) || BUILD_RENDERER.test(src);
}

/** Every *.spec.ts under dir, relative to it, with / separators, sorted. */
export function listSpecs(dir: string, sub = ''): string[] {
  const out: string[] = [];
  for (const e of readdirSync(path.join(dir, sub), { withFileTypes: true })) {
    const rel = sub ? `${sub}/${e.name}` : e.name;
    if (e.isDirectory()) { if (e.name !== 'node_modules') out.push(...listSpecs(dir, rel)); }
    else if (e.name.endsWith('.spec.ts')) out.push(rel);
  }
  return out.sort();
}

/** The phone specs under dir (relative paths). */
export function phoneSpecs(dir: string) {
  return listSpecs(dir).filter((rel) => isPhoneSpec(rel, readFileSync(path.join(dir, rel), 'utf8')));
}

const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** A testMatch for exactly these specs (relative to the testDir): matches the absolute path's ending, either separator. */
export function specMatcher(rels: string[]) {
  if (!rels.length) return /$^/;
  return new RegExp(`(?:^|[\\\\/])(?:${rels.map((r) => r.split('/').map(esc).join('[\\\\/]')).join('|')})$`);
}

/**
 * What is wrong with the phone specs under dir, given the phone project's testMatch: a phone spec it does not match
 * (it would run in `default`, uncapped), or one that takes `test` from @playwright/test instead of phone/fixtures.
 * Empty when all is well.
 */
export function checkPhoneSpecs(dir: string, phoneMatch: RegExp | RegExp[]) {
  const matchers = Array.isArray(phoneMatch) ? phoneMatch : [phoneMatch];
  const problems: string[] = [];
  for (const rel of listSpecs(dir)) {
    const src = readFileSync(path.join(dir, rel), 'utf8');
    if (!isPhoneSpec(rel, src)) continue;
    const abs = path.join(dir, rel);
    if (!matchers.some((m) => m.test(abs))) problems.push(`${rel} imports tests/e2e/phone (or calls buildRenderer) but is not in the phone project, so it would run in 'default' with no worker cap`);
    if (!FIXTURES_TEST.test(src)) problems.push(`${rel} is a phone spec but does not take \`test\` from phone/fixtures, so the contexts it leaves open are never closed`);
  }
  return problems;
}
