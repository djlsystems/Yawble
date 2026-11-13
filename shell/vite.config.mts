import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

// The build's version (scripts/build-version.mjs, the same rule as the core's), for the About section to fall back on
// when the core does not say. A build from shell/ and code-browser/ alone (no scripts/ folder) has no script:
// YAWBLE_BUILD_VERSION, else 0.0.0+dev.unknown. Never fails the build.
type BuildVersion = { version: string; commit: string; dirty: boolean; released: boolean };
const unknownBuild: BuildVersion = { version: '0.0.0+dev.unknown', commit: 'unknown', dirty: false, released: false };
let buildVersion: BuildVersion = unknownBuild;
try {
  // @ts-ignore: a plain .mjs next to the repository's scripts, without types
  const script = (await import('../scripts/build-version.mjs')) as { readBuildVersion: () => BuildVersion };
  buildVersion = script.readBuildVersion();
} catch {
  const v = process.env.YAWBLE_BUILD_VERSION?.trim();
  const shaped = !!v && v.length <= 40 && /^((\d{4}\.\d{2}\.\d{2}\.\d+)|(\d{4}\.\d{2}\.\d{2}\.\d+|0\.0\.0)\+dev\.([0-9a-f]{7}|unknown)(\.dirty)?)$/.test(v);
  if (v && !shaped) console.error(`vite.config: ignoring YAWBLE_BUILD_VERSION=${JSON.stringify(v.slice(0, 60))}: not a release or dev version`);
  if (v && shaped) buildVersion = { version: v, commit: process.env.YAWBLE_BUILD_COMMIT?.trim() || 'unknown', dirty: v.endsWith('.dirty'), released: /^\d{4}\.\d{2}\.\d{2}\.\d+$/.test(v) };
}

export default defineConfig({
  root: 'src/renderer',
  base: './',
  plugins: [vue()],
  // The Code browser is its own package at the repository's root, used from its source. Its imports of
  // vue and highlight.js resolve here, from shell/node_modules (dedupe), never from a code-browser/node_modules its own
  // tests may have installed: one Vue in the page.
  resolve: {
    alias: { '@yawble/code-browser': resolve(__dirname, '../code-browser/src/index.ts') },
    dedupe: ['vue', 'highlight.js'],
  },
  define: { __YAWBLE_BUILD__: JSON.stringify(buildVersion) },
  // Two pages, one set of screens: index.html (the desktop window, Electron's preload bridge) and remote.html (a
  // remote view in a browser, the same API over a WebSocket; see src/remoteApi.ts).
  build: {
    outDir: '../../out/renderer',
    emptyOutDir: true,
    rollupOptions: { input: { index: resolve(__dirname, 'src/renderer/index.html'), remote: resolve(__dirname, 'src/renderer/remote.html') } },
  },
  test: {
    root: '.',
    include: ['tests/unit/**/*.test.ts'],
    environment: 'node',
  },
});
