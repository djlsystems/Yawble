import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * EVERY TEXT SIZE IS IN REM, so a person's text size in Themes reaches it. `lib/appearance.ts` sets
 * the size on <html>; a `font-size` in px, or an ECharts `fontSize` written as a number, stays where
 * it is whatever the person chose. A chart's size goes through `textPx` instead.
 *
 * The Concierge panel is the one exception, on purpose: its terminal and frame keep their own look,
 * set in its own display settings (`lib/terminalDisplay.ts`), and Themes does not change them.
 */
const src = join(import.meta.dirname, '..', '..');

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === '__tests__' ? [] : files(path);
    return /\.(vue|ts|scss)$/.test(name) ? [path] : [];
  });
}

const ownLook = new Set(['components/ConciergePanel.vue', 'lib/terminalDisplay.ts']);

const sources = files(src)
  .map((path) => ({ name: relative(src, path).replace(/\\/g, '/'), text: readFileSync(path, 'utf8') }))
  .filter((s) => !ownLook.has(s.name));

describe('text sizes follow the person', () => {
  for (const { name, text } of sources) {
    it(`${name} sizes no text in px`, () => {
      expect(text.match(/font-size:\s*[\d.]+px/g) ?? []).toEqual([]);
      expect(text.match(/fontSize:\s*\d/g) ?? []).toEqual([]);
    });
  }

  it('finds a px size where one is written', () => {
    expect('a { font-size: 13px }'.match(/font-size:\s*[\d.]+px/g)).toHaveLength(1);
    expect('{ fontSize: 11 }'.match(/fontSize:\s*\d/g)).toHaveLength(1);
  });
});
