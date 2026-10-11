import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { symbolsIconMap } from '../icons';
import {
  SubsetFontPath,
  decompress,
  namesWithoutGlyph,
  quasarIconNames,
  usedIconNames,
} from '../../../scripts/icon-font.mjs';

/**
 * EVERY ICON NAME IS A MATERIAL SYMBOLS OUTLINED NAME.
 *
 * The app loads Symbols Outlined and nothing else. An Icons-era name that Symbols dropped or
 * relabelled (`error_outline`, `help_outline`, `report_problem`, `get_app`, `panorama_fish_eye`, for
 * example) renders as its own letters at the wrong width, with
 * nothing thrown and nothing logged. The Symbols list is the one `@quasar/extras` ships, so this
 * checks against the font actually served.
 */
const extras = join(import.meta.dirname, '../../../node_modules/@quasar/extras/exports');
const src = join(import.meta.dirname, '../..');

const symbols = new Set<string>(
  JSON.parse(readFileSync(join(extras, 'material-symbols-outlined/icons.json'), 'utf8')),
);
const materialIcons = new Set<string>(
  JSON.parse(readFileSync(join(extras, 'material-icons/icons.json'), 'utf8')),
);

/** `18_up_rating` is exported as `symOutlined18UpRating`. */
const exportName = (prefix: string, name: string) =>
  prefix + name.split('_').map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join('');

const inSymbols = (name: string) => symbols.has(exportName('symOutlined', name));
const inMaterialIcons = (name: string) => materialIcons.has(exportName('mat', name));

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) return entry === '__tests__' ? [] : sources(path);
    return /\.(vue|ts)$/.test(entry) ? [path] : [];
  });
}

const files = sources(src).map((path) => ({
  path: relative(src, path),
  // Comments name icons that are deliberately NOT used (`error_outline` was replaced by ...).
  text: readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/<!--[\s\S]*?-->/g, ' '),
}));

const Ligature = /^[a-z0-9_]+$/;
/** Literals a bound expression can evaluate to - not the operand of a comparison (`tone === 'ok'`). */
const quoted = (expression: string) =>
  [...expression.matchAll(/(?<!==\s*)'([a-z0-9_]+)'/g)].map((m) => m[1]!);

/** Names written where an icon is expected: the attribute, the bound expression, the `icon:` key. */
function iconContextNames(text: string): string[] {
  return [
    ...[...text.matchAll(/<q-icon\b[^>]*?\sname="([^"]*)"/g)].map((m) => m[1]!),
    ...[...text.matchAll(/<q-icon\b[^>]*?\s:name="([^"]*)"/g)].flatMap((m) => quoted(m[1]!)),
    ...[...text.matchAll(/\s(?:[a-z-]+-)?icon="([^"]*)"/g)].map((m) => m[1]!),
    ...[...text.matchAll(/\s:(?:[a-z-]+-)?icon="([^"]*)"/g)].flatMap((m) => quoted(m[1]!)),
    ...[...text.matchAll(/\bicon:\s*'([^']*)'/g)].map((m) => m[1]!),
    ...[...text.matchAll(/\b\w*Icon\w*\s*=\s*'([^']*)'/g)].map((m) => m[1]!),
  ].filter((name) => Ligature.test(name));
}

/**
 * Icons-vocabulary words this source uses as ordinary strings, never as icons: a mode called
 * `create`, a status called `done`, a field called `email`. Symbols gives each of them another name, which is
 * why the broad scan below sees them at all.
 */
const NotIcons = new Set([
  'class', 'clear', 'create', 'done', 'email', 'launch', 'message', 'mode', 'note', 'source',
]);

describe('icon names', () => {
  it('every name in an icon position exists in Material Symbols Outlined', () => {
    const missing = files.flatMap(({ path, text }) =>
      iconContextNames(text).filter((name) => !inSymbols(name)).map((name) => `${path}: ${name}`),
    );

    expect(missing).toEqual([]);
  });

  it('no Icons-era name survives anywhere, including maps and functions that return one', () => {
    const missing = files.flatMap(({ path, text }) =>
      [...text.matchAll(/['"`]([a-z][a-z0-9_]*)['"`]/g)]
        .map((m) => m[1]!)
        .filter((name) => !NotIcons.has(name) && inMaterialIcons(name) && !inSymbols(name))
        .map((name) => `${path}: ${name}`),
    );

    expect(missing).toEqual([]);
  });

  it('the scan finds the icons it is meant to guard', () => {
    // A regex that silently matches nothing passes the two cases above with an empty list.
    const all = new Set(files.flatMap(({ text }) => iconContextNames(text)));

    for (const name of ['settings', 'close', 'groups', 'check_circle', 'error']) {
      expect(all.has(name)).toBe(true);
    }
  });
});

/**
 * THE APP SERVES A SUBSET OF THE FONT, so a name that is valid Symbols can still render as its own
 * letters: it was added after the subset was cut. `usedIconNames` is the list the subset was cut
 * from; the fix for a failure here is `npm run icons:subset` and committing the font it writes.
 */
describe('icon font subset', () => {
  const used = usedIconNames();

  it('every icon the app uses forms its glyph in the committed subset', async () => {
    const subset = await decompress(readFileSync(SubsetFontPath));

    expect(namesWithoutGlyph(subset, used)).toEqual([]);
  });

  it('the used list takes in the names the scans and Quasar ask for', () => {
    // An empty list would pass the case above with nothing checked.
    const all = new Set(used);
    const reached = [
      ...files.flatMap(({ text }) => iconContextNames(text)),
      ...quasarIconNames(),
      // Returned from functions and held in maps, never written in an icon position.
      'circle', 'delete_sweep', 'no_accounts', 'cloud_done', 'brightness_auto',
    ].filter(inSymbols);

    expect(reached.filter((name) => !all.has(name))).toEqual([]);
    expect(quasarIconNames()).toContain('arrow_drop_down');
  });

  it('no icon name is built at runtime, where the subset cannot see it', () => {
    // `:name="`arrow_${dir}`"` or `icon: 'sync' + suffix` names a glyph no scan can enumerate.
    // `lib/icons.ts` is the map: it prefixes `sym_o_` to a name that is itself spelled out elsewhere.
    const built = files.filter(({ path }) => path !== join('lib', 'icons.ts')).flatMap(({ path, text }) =>
      [
        ...[...text.matchAll(/<q-icon\b[^>]*?\s:name="([^"]*)"/g)].map((m) => m[1]!),
        ...[...text.matchAll(/\s:(?:[a-z-]+-)?icon="([^"]*)"/g)].map((m) => m[1]!),
        ...[...text.matchAll(/\bicon:\s*([^,}\n]*)/g)].map((m) => m[1]!),
      ]
        .filter((expression) => /`|\+/.test(expression))
        .map((expression) => `${path}: ${expression}`),
    );

    expect(built).toEqual([]);
  });
});

describe('symbolsIconMap', () => {
  it('maps a bare ligature name to Symbols Outlined', () => {
    expect(symbolsIconMap('settings')).toEqual({ icon: 'sym_o_settings' });
    expect(symbolsIconMap('18_up_rating')).toEqual({ icon: 'sym_o_18_up_rating' });
  });

  it('leaves an explicit name alone', () => {
    for (const name of ['sym_r_settings', 'sym_o_close', 'mdi-account', 'img:logo.png', 'svguse:#x']) {
      expect(symbolsIconMap(name)).toBeUndefined();
    }
  });
});
