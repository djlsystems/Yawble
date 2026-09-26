import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';

/**
 * AN OVER-LIMIT LANE HEADER MUST BE READABLE IN BOTH THEMES.
 *
 * The header is 12px text, so WCAG AA wants 4.5:1. `--q-warning` (#c98a12) would be only 2.95:1 on
 * the light theme's white. jsdom paints nothing, so this
 * does the browser's sum by hand: take the colour the `.k-lane-head--over` rule asks for, resolve it
 * through the compiled theme tokens, and measure it against the grounds a header sits on - the
 * lane's `--os-chrome` on the board, the sticky header's `--os-surface` in swimlanes.
 */
const srcDirectory = join(import.meta.dirname, '..', '..');
const cssDirectory = join(srcDirectory, 'css');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

const quasarWarning = readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8').match(
  /^\$warning\s*:\s*(#[0-9a-f]{6});/m,
)![1]!;

function tokens(selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`))!;

  return Object.fromEntries(
    [...match[1]!.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const light = tokens(':root');
const themes = { light, dark: { ...light, ...tokens('body.body--dark') } };

/** A `var(--x)` chain down to a hex, the way the browser would for that theme. */
function resolve(value: string, theme: Record<string, string>): string {
  const reference = value.match(/^var\((--[\w-]+)\)$/);
  if (!reference) return value;
  if (reference[1] === '--q-warning') return quasarWarning;

  const next = theme[reference[1]!];
  if (!next) throw new Error(`${reference[1]} is not set`);

  return resolve(next, theme);
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

const board = readFileSync(join(srcDirectory, 'components', 'KanbanBoard.vue'), 'utf8');
const overColour = board.match(/\n\.k-lane-head--over \{[^}]*?\bcolor:\s*([^;]+);/)![1]!.trim();

describe('the over-limit lane header', () => {
  it('does not paint plain --q-warning text', () => {
    expect(overColour).not.toBe('var(--q-warning)');
    expect(resolve(overColour, themes.light)).not.toBe(quasarWarning);
  });

  it('gives the count and title no colour of their own, so they take the header ink', () => {
    expect(board).not.toMatch(/\n\.k-lane-(count|title) \{[^}]*\bcolor:/);
  });

  for (const [name, theme] of Object.entries(themes)) {
    for (const ground of ['--os-surface', '--os-chrome']) {
      it(`reaches 4.5:1 on ${ground} in ${name}`, () => {
        const ink = resolve(overColour, theme);
        const paper = resolve(`var(${ground})`, theme);

        expect(contrast(ink, paper), `${ink} on ${paper}`).toBeGreaterThanOrEqual(4.5);
      });
    }
  }

  it('still reads as amber in light: red leads, blue trails', () => {
    const ink = resolve(overColour, themes.light);
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(ink.slice(i, i + 2), 16));

    expect(r!).toBeGreaterThan(g!);
    expect(g!).toBeGreaterThan(b!);
  });
});
