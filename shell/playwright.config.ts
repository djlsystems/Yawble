import { defineConfig } from '@playwright/test';

// The heavy phone specs (card 391): each loads the built renderer (out/b037/renderer, or its own build for card320 and
// card332) into a phone-sized page, and several at once ran out of memory. They run in their own project with at most
// PHONE_WORKERS workers (B037_PHONE_WORKERS to change it); every other spec keeps the run's full worker count.
const PHONE_SPECS = [
  /[\\/]phone[\\/][^\\/]+\.spec\.ts$/,
  /[\\/](card320|card332|card347|card367|card372|card375|card430|card444|card477|member-pane-phone|phone-remote|team-settings-phone|tell-icons|workflows-visitor)[^\\/]*\.spec\.ts$/,
];
const PHONE_WORKERS = Number(process.env.B037_PHONE_WORKERS) || 2;

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
