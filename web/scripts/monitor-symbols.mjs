/**
 * THE MONITOR'S ICONS: the FULL Material Symbols Outlined font, and which codepoint draws each name.
 *
 * A plugin's monitor hint names any Material Symbols icon, so the Monitor needs the whole font, not
 * the app's subset (`css/material-symbols.scss`, which stays as it is). This Vite plugin serves one
 * module, `virtual:monitor-symbols`, that only the Monitor imports, and only when it first opens:
 *
 * - `fontUrl` is the font file `@quasar/extras` ships, imported with `?url` so the build emits it as
 *   its own hashed asset the browser caches;
 * - `codepoints` maps each icon NAME to the codepoint its glyph sits at, read at build time from the
 *   font itself - its `post` table names every glyph and its `cmap` places it - so a canvas draws an
 *   icon by its character and never by typing a ligature name into `fillText`.
 *
 * Nothing is committed: the map is made from whatever font the installed package ships.
 */
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import * as hb from 'harfbuzzjs';
import { SourceFontPath, decompress } from './icon-font.mjs';

export const MonitorSymbolsModule = 'virtual:monitor-symbols';
const Resolved = '\0' + MonitorSymbolsModule;

/** The font's own name -> codepoint data: every Private Use codepoint whose glyph has a name. */
export async function readCodepoints(fontPath = SourceFontPath) {
  const face = new hb.Face(new hb.Blob(await decompress(readFileSync(fontPath))));
  const font = new hb.Font(face);
  /** @type {Record<string, number>} */
  const codepoints = {};
  // Copied first: the array is a view of harfbuzz's memory, which a later call may move.
  for (const codepoint of Array.from(face.collectUnicodes())) {
    if (codepoint < 0xe000) continue;
    const glyph = font.glyph(codepoint);
    const name = glyph ? font.glyphName(glyph) : '';
    if (/^[a-z0-9_]+$/.test(name) && codepoints[name] === undefined) codepoints[name] = codepoint;
  }
  return codepoints;
}

/** @returns {import('vite').Plugin} */
export function monitorSymbols() {
  /** @type {Promise<string> | null} */
  let source = null;
  return {
    name: 'monitor-symbols',
    resolveId: (id) => (id === MonitorSymbolsModule ? Resolved : null),
    load(id) {
      if (id !== Resolved) return null;
      source ??= readCodepoints().then(
        (codepoints) =>
          `import fontUrl from '@quasar/extras/material-symbols-outlined/web-font/${basename(SourceFontPath)}?url';\n` +
          `export { fontUrl };\n` +
          `export const codepoints = ${JSON.stringify(codepoints)};\n`,
      );
      return source;
    },
  };
}
