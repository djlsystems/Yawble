/**
 * THE ICON FONT IS A SUBSET, AND THIS FILE IS THE ONE ANSWER TO "WHICH ICONS DOES THE APP USE".
 *
 * The full Material Symbols Outlined font is 3,980,372 bytes of woff2 (@quasar/extras 2.1.0) and
 * can take several seconds over a slow tunnel, with every icon hidden until it arrives
 * (`lib/iconsReady.ts`). The app uses a couple of hundred of its ~3,900 glyphs, so it ships only
 * those. `subset-icons.mjs` writes the subset from this list; `icon-names.spec.ts` fails when a used
 * name does not form its glyph in the subset that is committed.
 *
 * A NAME IS "USED" WHEN IT CAN REACH THE FONT:
 *  - any quoted word in the app's own source that is a Symbols name. Deliberately broad: an icon
 *    name returned from a function (`rungIcon`), held in a map (`ACTION_ICONS`) or an object
 *    (`ribbon.ts`) is a literal somewhere, and every dynamic `:name` / `:icon` in the app resolves to
 *    one. A word that is merely a Symbols name by coincidence (`label`, `title`) costs one glyph.
 *  - every glyph QUASAR asks for at runtime from its icon set (select arrows, expansion chevrons,
 *    table pagination). Those names are never written in this app's source; they are enumerated
 *    from the icon set `quasar.config.ts` installs.
 *
 * A name BUILT at runtime (`\`arrow_${direction}\``) is neither, and `icon-names.spec.ts` refuses one
 * in an icon position: spell each name out so it stays enumerable.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as hb from 'harfbuzzjs';
import wawoff2 from 'wawoff2';
import quasarIconSet from 'quasar/icon-set/material-symbols-outlined.js';

const web = join(dirname(fileURLToPath(import.meta.url)), '..');
const extras = join(web, 'node_modules/@quasar/extras/exports/material-symbols-outlined');

/** The full font, as `@quasar/extras` ships it. The subset is cut from this. */
export const SourceFontPath = join(extras, 'web-font/kJEhBvYX7BgnkSrUwT8OhrdQw4oELdPIeeII9v6oFsLjBuVY.woff2');
/** The font the app serves; `css/material-symbols.scss` points at it. */
export const SubsetFontPath = join(web, 'src/assets/fonts/material-symbols-outlined-subset.woff2');

/** Every character an icon name is spelled with. Kept as plain glyphs, so an unknown name shows as its letters. */
const NameAlphabet = 'abcdefghijklmnopqrstuvwxyz0123456789_';

const symbolExports = new Set(JSON.parse(readFileSync(join(extras, 'icons.json'), 'utf8')));

/** `18_up_rating` is exported as `symOutlined18UpRating`. */
export const isSymbolsName = (/** @type {string} */ name) =>
  symbolExports.has(
    'symOutlined' + name.split('_').map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join(''),
  );

/**
 * The app's own `.vue` / `.ts` files, tests excluded, with comments blanked: comments name icons
 * that are deliberately NOT used (`error_outline` was replaced by ...).
 * @returns {{ path: string, text: string }[]}
 */
export function appSources() {
  const src = join(web, 'src');
  /** @param {string} dir @returns {string[]} */
  const walk = (dir) =>
    readdirSync(dir).flatMap((entry) => {
      const path = join(dir, entry);
      if (statSync(path).isDirectory()) return entry === '__tests__' ? [] : walk(path);
      return /\.(vue|ts)$/.test(entry) ? [path] : [];
    });

  return walk(src).map((path) => ({
    path: relative(src, path),
    text: readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/<!--[\s\S]*?-->/g, ' '),
  }));
}

/** Every Symbols name Quasar's own components ask for: the `sym_o_` values of its icon set. */
export function quasarIconNames() {
  /** @param {unknown} value @returns {string[]} */
  const values = (value) =>
    typeof value === 'string' ? [value] : value && typeof value === 'object' ? Object.values(value).flatMap(values) : [];

  return values(quasarIconSet)
    .filter((value) => value.startsWith('sym_o_'))
    .map((value) => value.slice('sym_o_'.length));
}

/** Every icon name the app can render, sorted and unique. */
export function usedIconNames() {
  const literals = appSources().flatMap(({ text }) =>
    [...text.matchAll(/['"`]([a-z0-9][a-z0-9_]*)['"`]/g)].map((m) => /** @type {string} */ (m[1])),
  );

  return [...new Set([...literals, ...quasarIconNames()])].filter(isSymbolsName).sort();
}

/**
 * Shapes each name in a font and returns the ones that do NOT become a single glyph - the names
 * that would render as their own letters.
 * @param {Uint8Array} ttf
 * @param {readonly string[]} names
 */
export function namesWithoutGlyph(ttf, names) {
  const font = new hb.Font(new hb.Face(new hb.Blob(ttf)));

  return names.filter((name) => ligatureGlyph(font, name) === null);
}

/** @param {hb.Font} font @param {string} name @returns {number | null} */
function ligatureGlyph(font, name) {
  const buffer = new hb.Buffer();
  buffer.addText(name);
  buffer.guessSegmentProperties();
  hb.shape(font, buffer);
  const glyphs = buffer.getGlyphInfos();

  return glyphs.length === 1 && glyphs[0] ? glyphs[0].codepoint : null;
}

/** @param {Uint8Array} woff2 @returns {Promise<Uint8Array>} */
export const decompress = (woff2) => wawoff2.decompress(woff2);

/** HB_SUBSET_FLAGS_NO_LAYOUT_CLOSURE: keep only the ligatures whose result glyph was asked for. */
const NoLayoutClosure = 0x200;

/**
 * Cuts the font down to the given names' glyphs plus the alphabet they are spelled in, keeping
 * every variation axis (`RepoCard.vue` sets `FILL`).
 *
 * WITHOUT NoLayoutClosure THE SUBSET IS THE WHOLE FONT AGAIN: every ligature is made of letters
 * that are kept, so the closure over GSUB pulls every icon back in.
 * @param {Uint8Array} ttf
 * @param {readonly string[]} names
 * @returns {Promise<Uint8Array>} woff2
 */
export async function subsetFont(ttf, names) {
  const font = new hb.Font(new hb.Face(new hb.Blob(ttf)));
  const glyphs = names.map((name) => {
    const glyph = ligatureGlyph(font, name);
    if (glyph === null) throw new Error(`"${name}" is not a ligature in the source font`);
    return glyph;
  });

  const wasm = await WebAssembly.instantiate(
    readFileSync(join(web, 'node_modules/harfbuzzjs/dist/harfbuzz-subset.wasm')),
  );
  const x = /** @type {any} */ (wasm.instance.exports);
  const heap = () => new Uint8Array(x.memory.buffer);

  const data = x.malloc(ttf.length);
  heap().set(ttf, data);
  const face = x.hb_face_create(x.hb_blob_create(data, ttf.length, 2 /* WRITABLE */, 0, 0), 0);

  const input = x.hb_subset_input_create_or_fail();
  const unicodes = x.hb_subset_input_unicode_set(input);
  for (const c of NameAlphabet) x.hb_set_add(unicodes, c.charCodeAt(0));
  const glyphSet = x.hb_subset_input_glyph_set(input);
  for (const glyph of glyphs) x.hb_set_add(glyphSet, glyph);
  x.hb_subset_input_set_flags(input, x.hb_subset_input_get_flags(input) | NoLayoutClosure);

  const subset = x.hb_subset_or_fail(face, input);
  if (!subset) throw new Error('harfbuzz could not subset the font');
  const blob = x.hb_face_reference_blob(subset);
  const start = x.hb_blob_get_data(blob, 0);
  const out = heap().slice(start, start + x.hb_blob_get_length(blob));

  return wawoff2.compress(out);
}
