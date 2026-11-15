// B029 (card 143): the colours main paints before and around the page (title-bar.ts: the window controls' overlay,
// dimmed and not; a new window's backgroundColor) must equal the palette the page itself paints with. This test
// reads styles.css and terminal-theme.css, and the rules that say which variable the bar and the page use, so the
// two cannot drift apart again.
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { TITLE_BAR, TITLE_BAR_DIMMED, WINDOW_BACKGROUND, windowBackground, type Theme } from '../../src/main/title-bar';

const src = resolve(__dirname, '../../src');
const read = (rel: string) => readFileSync(join(src, rel), 'utf8');
const stylesCss = read('renderer/src/styles.css');
const terminalCss = read('renderer/src/terminal-theme.css');

/** The declarations of every top-level rule whose selector is exactly `selector`, later ones winning (comments dropped). */
function block(css: string, selector: string): Record<string, string> {
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const re = /([^{}]+)\{([^{}]*)\}/g;
  const vars: Record<string, string> = {};
  let found = false;
  for (let m = re.exec(clean); m; m = re.exec(clean)) {
    if (m[1].trim().replace(/\s+/g, ' ') !== selector) continue;
    found = true;
    for (const d of m[2].split(';')) {
      const i = d.indexOf(':');
      if (i > 0) vars[d.slice(0, i).trim()] = d.slice(i + 1).trim();
    }
  }
  if (!found) throw new Error(`no rule "${selector}"`);
  return vars;
}

const palette: Record<Theme, Record<string, string>> = {
  light: block(stylesCss, ':root, :root[data-theme="dark"] .monitor'),
  dark: block(stylesCss, ':root[data-theme="dark"]'),
};
const terminal: Record<Theme, Record<string, string>> = {
  light: block(terminalCss, ':root'),
  dark: block(terminalCss, ':root[data-theme="dark"]'),
};

type Rgba = [number, number, number, number];
function parse(c: string): Rgba {
  const hex = /^#([0-9a-f]{6})$/i.exec(c);
  if (hex) return [0, 2, 4].map(i => parseInt(hex[1].slice(i, i + 2), 16)).concat(1) as Rgba;
  const rgba = /^rgba\(\s*(\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\s*\)$/.exec(c);
  if (rgba) return [Number(rgba[1]), Number(rgba[2]), Number(rgba[3]), Number(rgba[4])];
  throw new Error(`not a colour: ${c}`);
}
const hex = (rgb: number[]) => '#' + rgb.map(v => v.toString(16).padStart(2, '0')).join('');
/** `under` seen through `over` (an rgba scrim), as the screen shows it. */
function composite(under: string, over: string): string {
  const [r, g, b] = parse(under);
  const [sr, sg, sb, a] = parse(over);
  return hex([[r, sr], [g, sg], [b, sb]].map(([u, s]) => Math.round(s * a + u * (1 - a))));
}

/** The declaration `prop` in the rule `selector` of a component's <style>. */
function styleOf(file: string, selector: string, prop: string): string | undefined {
  const re = new RegExp(`(?:^|\\n)\\s*${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{([^}]*)\\}`);
  const body = re.exec(read(file))?.[1];
  return body && new RegExp(`(?:^|[;\\s])${prop}\\s*:\\s*([^;]+)`).exec(body)?.[1].trim();
}

describe('title-bar overlay and window background follow the palette (styles.css)', () => {
  it('the page paints the bar with --surface, its menu text with --ink, dialogs dim it with --backdrop', () => {
    expect(styleOf('renderer/src/App.vue', '.app', 'background')).toBe('var(--surface)');
    expect(styleOf('renderer/src/components/MenuBar.vue', '.top', 'color')).toBe('var(--ink)');
    expect(styleOf('renderer/src/components/Modal.vue', '.backdrop', 'background')).toBe('var(--backdrop)');
    expect(styleOf('renderer/src/styles.css', 'body', 'background')).toBe('var(--surface)');
    expect(styleOf('renderer/src/TerminalApp.vue', '.term-window', 'background')).toBe('var(--term-bg)');
  });

  for (const theme of ['light', 'dark'] as const) {
    it(`${theme}: the overlay is --surface / --ink, and dimmed is both under --backdrop`, () => {
      const p = palette[theme];
      expect(TITLE_BAR[theme]).toEqual({ color: p['--surface'], symbolColor: p['--ink'] });
      expect(TITLE_BAR_DIMMED[theme]).toEqual({
        color: composite(p['--surface'], p['--backdrop']),
        symbolColor: composite(p['--ink'], p['--backdrop']),
      });
    });

    it(`${theme}: a new window's background is the page's --surface, a terminal window's --term-bg`, () => {
      expect(WINDOW_BACKGROUND[theme]).toEqual({ main: palette[theme]['--surface'], terminal: terminal[theme]['--term-bg'] });
      expect(windowBackground(theme, false)).toBe(palette[theme]['--surface']);
      expect(windowBackground(theme, true)).toBe(terminal[theme]['--term-bg']);
    });
  }

  it('the values themselves (so a palette change is a visible diff here too)', () => {
    expect(TITLE_BAR).toEqual({
      light: { color: '#ffffff', symbolColor: '#1d2330' },
      dark: { color: '#1b1e24', symbolColor: '#e4e7ec' },
    });
    expect(TITLE_BAR_DIMMED).toEqual({
      light: { color: '#adafb2', symbolColor: '#1a202c' },
      dark: { color: '#0c0d10', symbolColor: '#67686a' },
    });
    expect(WINDOW_BACKGROUND).toEqual({ light: { main: '#ffffff', terminal: '#fbfbfc' }, dark: { main: '#1b1e24', terminal: '#101418' } });
  });

  it('main has no window colour of its own: index.ts takes the background from title-bar.ts', () => {
    const index = read('main/index.ts');
    expect(index).toMatch(/backgroundColor:\s*windowBackground\(/);
    expect(index).not.toMatch(/backgroundColor:[^\n]*#[0-9a-f]{3,8}/i);
  });
});
