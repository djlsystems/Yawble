import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { checkPhoneSpecs, phoneSpecs, specMatcher } from './tests/e2e/phone/specs';

// The heavy phone specs: each loads the built renderer (out/e2e/renderer, or its own build for teams-buttons and
// team-more-menu) into a phone-sized page, and several at once ran out of memory. They run in their own project with at most
// PHONE_WORKERS workers (E2E_PHONE_WORKERS to change it); every other spec keeps the run's full worker count.
//
// Which specs those are is read from the specs themselves (tests/e2e/phone/specs.ts): every spec in
// tests/e2e/phone/, and every spec that imports a module from it (server, fake-core, fixtures) or calls buildRenderer. A
// new phone spec joins the project by what it imports, without a list to keep. The config refuses to load when a phone
// spec would still fall outside the project, or takes `test` from @playwright/test instead of phone/fixtures (whose fixture
// closes the contexts a test leaves open, which is what held the memory).
const E2E_DIR = path.join(__dirname, 'tests', 'e2e');
const PHONE_SPECS = specMatcher(phoneSpecs(E2E_DIR));
const problems = checkPhoneSpecs(E2E_DIR, PHONE_SPECS);
if (problems.length) throw new Error(`playwright.config.ts: phone specs:\n  ${problems.join('\n  ')}`);
const PHONE_WORKERS = Number(process.env.E2E_PHONE_WORKERS) || 2;

export default defineConfig({
  testDir: 'tests/e2e',
  timeout: 90_000,
  reporter: [['list']],
  outputDir: 'test-results',
  globalSetup: './tests/e2e/global-setup.ts',
  projects: [
    { name: 'phone', testMatch: PHONE_SPECS, workers: PHONE_WORKERS },
    { name: 'default', testIgnore: PHONE_SPECS },
  ],
});
