import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';

/**
 * THE STATISTICS TILE'S STATE COLOURS HOLD 3:1 AGAINST THE TILE, IN BOTH THEMES.
 *
 * A span is a graphical mark, so WCAG asks 3:1 of it against what it sits on: the tile's
 * `--os-chrome`. The colours are theme tokens in `app.scss`; this resolves each through the compiled
 * stylesheet for light and dark and measures it, as `kanban-over-limit-contrast.spec.ts` does for
 * the lane header. Idle is left out on purpose: it is a pale empty track, not a mark.
 */
const cssDirectory = join(import.meta.dirname, '..', '..', 'css');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

function tokens(selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`))!;

  return Object.fromEntries(
    [...match[1]!.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const light = tokens(':root');
const themes = { light, dark: { ...light, ...tokens('body.body--dark') } };

/** A token down to its hex, through any `var(--x)` chain, the way the browser would. */
function resolve(token: string, theme: Record<string, string>): string {
  const value = theme[token];
  if (!value) throw new Error(`${token} is not set`);

  const reference = value.match(/^var\((--[\w-]+)\)$/);

  return reference ? resolve(reference[1]!, theme) : value;
}

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi! + 0.05) / (lo! + 0.05);
}

describe('the Statistics tile state colours', () => {
  for (const [name, theme] of Object.entries(themes)) {
    for (const state of ['running', 'waiting', 'blocked', 'failed']) {
      it(`${state} reaches 3:1 on --os-chrome in ${name}`, () => {
        const mark = resolve(`--os-stat-${state}`, theme);
        const ground = resolve('--os-chrome', theme);

        expect(contrast(mark, ground), `${mark} on ${ground}`).toBeGreaterThanOrEqual(3);
      });
    }

    it(`has a pale idle track in ${name}`, () => {
      expect(resolve('--os-stat-idle', theme)).toMatch(/^#[0-9a-f]{6}$/);
    });
  }
});
