import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';
import vue from '@vitejs/plugin-vue';
import { quasar, transformAssetUrls } from '@quasar/vite-plugin';

/**
 * THE WEB SUITE'S TRANSFORM PIPELINE, AND THE ONE DECISION IN IT IS `environment`.
 *
 * On its defaults vitest has no Vue plugin, so a `.vue` file could not be IMPORTED, let alone
 * mounted. The plugins below are what make a component importable at all; specs that read a
 * component as TEXT do so by choice, not because mounting is unavailable.
 *
 * THE DOM IS OPTED INTO PER FILE, by a `// @vitest-environment happy-dom` docblock on a spec's
 * first line, and deliberately NOT set globally here. The node-environment cases run in ~1.4s;
 * making happy-dom global would charge every one of them for a DOM that a handful need. A spec
 * that needs one says so in its own first line, where the next reader sees it rather than having to
 * find this file.
 *
 * `include` is stated rather than left to the default so that the second runner - the UI smoke
 * suite is an xUnit project, deliberately, so `test.ps1` and the corpus can see it - can never be
 * picked up by this one.
 */
export default defineConfig({
  plugins: [
    vue({ template: { transformAssetUrls } }),
    quasar({
      sassVariables: fileURLToPath(new URL('./src/css/quasar.variables.scss', import.meta.url)),
    }),
  ],
  test: {
    environment: 'node',
    include: ['src/**/*.spec.ts'],
  },
});
