import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * THE ENTRY CHUNK CARRIES NO LIBRARY NOBODY HAS OPENED A VIEW FOR. The Monitor (d3-force) is
 * loaded by `defineAsyncComponent` when it first shows, so signing in paints one chunk without
 * it. The statistics tile (echarts) is on the board's own strip, the first screen after sign-in,
 * so deferring it would defer nothing; the dialogs are small and stay static so a spec can find
 * them by their definition.
 */
const lazy: Record<string, string[]> = {
  'pages/IndexPage.vue': ['MonitorView'],
};

function source(file: string): string {
  return readFileSync(fileURLToPath(new URL(`../${file}`, import.meta.url)), 'utf8');
}

describe('the entry chunk', () => {
  for (const [file, names] of Object.entries(lazy)) {
    for (const name of names) {
      it(`${file} loads ${name} when it first shows, not at sign-in`, () => {
        const text = source(file);

        expect(text).not.toMatch(new RegExp(`^import ${name} from`, 'm'));
        expect(text).toMatch(new RegExp(`const ${name} = defineAsyncComponent\\(\\(\\) => import\\('[^']*/${name}\\.vue'\\)\\)`));
      });
    }
  }
});
