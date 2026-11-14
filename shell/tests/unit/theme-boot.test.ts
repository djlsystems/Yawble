// B029 (card 144): no white first frame in the dark theme. Before the page's stylesheet paints, a classic script file
// (public/theme-boot.js, from 'self' as the CSP allows) sets data-theme on <html>; main keeps the last theme a page said
// (theme.json) so the first window of the next launch starts with the person's choice, not only the OS's.
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { REMOTE_THEME_KEY, THEME_KEY } from '../../src/renderer/src/theme';
import { ThemeFile, TitleBars } from '../../src/main/title-bar';

const renderer = resolve(__dirname, '../../src/renderer');
const bootSource = readFileSync(join(renderer, 'public/theme-boot.js'), 'utf8');

/** Runs theme-boot.js against fakes: the page's script tag (its data-key), localStorage and prefers-color-scheme. */
function boot(opts: { key?: string | null; stored?: Record<string, string>; dark?: boolean; storage?: 'blocked'; matchMedia?: 'none' | 'throws' }) {
  const attrs = new Map<string, string>();
  const root = { setAttribute: (n: string, v: string) => void attrs.set(n, v), style: { colorScheme: '' } };
  const document = {
    documentElement: root,
    currentScript: { getAttribute: (n: string) => (n === 'data-key' ? opts.key ?? null : null) },
  };
  const stored = opts.stored ?? {};
  const window: Record<string, unknown> = {
    matchMedia: opts.matchMedia === 'none' ? undefined
      : (q: string) => { if (opts.matchMedia === 'throws') throw new Error('no'); expect(q).toBe('(prefers-color-scheme: dark)'); return { matches: !!opts.dark }; },
  };
  Object.defineProperty(window, 'localStorage', {
    get: () => { if (opts.storage === 'blocked') throw new DOMException('blocked', 'SecurityError'); return { getItem: (k: string) => stored[k] ?? null }; },
  });
  new Function('document', 'window', bootSource)(document, window);
  return { theme: attrs.get('data-theme'), colorScheme: root.style.colorScheme };
}

describe('theme-boot.js (before the stylesheet paints)', () => {
  it('uses the kept choice of the desktop window, over the OS', () => {
    expect(boot({ stored: { [THEME_KEY]: 'dark' }, dark: false })).toEqual({ theme: 'dark', colorScheme: 'dark' });
    expect(boot({ stored: { [THEME_KEY]: 'light' }, dark: true })).toEqual({ theme: 'light', colorScheme: 'light' });
  });

  it('follows the OS when nothing (or nothing valid) is kept', () => {
    expect(boot({ dark: true }).theme).toBe('dark');
    expect(boot({ dark: false }).theme).toBe('light');
    expect(boot({ stored: { [THEME_KEY]: 'purple' }, dark: true }).theme).toBe('dark');
  });

  it("a remote view reads its own key (the script tag's data-key), not the desktop's", () => {
    const stored = { [THEME_KEY]: 'dark', [REMOTE_THEME_KEY]: 'light' };
    expect(boot({ key: REMOTE_THEME_KEY, stored, dark: true }).theme).toBe('light');
    expect(boot({ key: REMOTE_THEME_KEY, stored: { [THEME_KEY]: 'dark' }, dark: false }).theme).toBe('light');
  });

  it('never throws: blocked storage follows the OS; no or a failing matchMedia is light', () => {
    expect(boot({ storage: 'blocked', dark: true }).theme).toBe('dark');
    expect(boot({ matchMedia: 'none' }).theme).toBe('light');
    expect(boot({ matchMedia: 'throws' }).theme).toBe('light');
  });

  it('is a classic script (no import/export) with the same keys as theme.ts', () => {
    expect(bootSource).not.toMatch(/^\s*(import|export)\b/m);
    expect(bootSource).toContain(`'${THEME_KEY}'`);
  });
});

describe('the pages load theme-boot.js first', () => {
  for (const [page, key] of [['index.html', null], ['remote.html', REMOTE_THEME_KEY]] as const) {
    it(`${page}: a classic script from 'self' in <head>, before any stylesheet or module script`, () => {
      const html = readFileSync(join(renderer, page), 'utf8');
      const head = html.slice(0, html.indexOf('</head>'));
      const tag = /<script\b[^>]*\bsrc="\.\/theme-boot\.js"[^>]*><\/script>/.exec(head);
      expect(tag, 'theme-boot.js in <head>').toBeTruthy();
      expect(tag![0]).not.toMatch(/type="module"|\basync\b|\bdefer\b/);
      expect(tag![0].includes(`data-key="${REMOTE_THEME_KEY}"`)).toBe(key !== null);
      const first = head.search(/<script\b|<link\b[^>]*stylesheet/);
      expect(first).toBe(tag!.index); // Vite adds the stylesheet and module script after it, before </head>
      expect(head).toMatch(/script-src 'self'/);
    });
  }
});

describe('the theme kept for the next launch (main, theme.json)', () => {
  let dir = '';
  afterEach(() => { if (dir) rmSync(dir, { recursive: true, force: true }); dir = ''; });

  it('writes the last theme a page said, and the next launch starts with it', () => {
    dir = mkdtempSync(join(tmpdir(), 'yawble-theme-'));
    const file = join(dir, 'data', 'theme.json');
    const first = new TitleBars(() => 'light', 'win32', new ThemeFile(file));
    expect(first.startTheme()).toBe('light');
    first.setTheme({ setTitleBarOverlay: () => {} }, 'dark');
    expect(JSON.parse(readFileSync(file, 'utf8'))).toEqual({ theme: 'dark' });
    expect(new TitleBars(() => 'light', 'win32', new ThemeFile(file)).startTheme()).toBe('dark');
  });

  it('a missing or damaged file means the OS theme', () => {
    dir = mkdtempSync(join(tmpdir(), 'yawble-theme-'));
    const file = join(dir, 'theme.json');
    expect(new ThemeFile(file).read()).toBeNull();
    writeFileSync(file, '{not json');
    expect(new ThemeFile(file).read()).toBeNull();
    writeFileSync(file, JSON.stringify({ theme: 'purple' }));
    expect(new TitleBars(() => 'dark', 'win32', new ThemeFile(file)).startTheme()).toBe('dark');
  });
});
