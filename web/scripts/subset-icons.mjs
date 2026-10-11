/**
 * REGENERATES THE ICON FONT SUBSET: `npm run icons:subset`, from `web/`.
 *
 * Run it after adding an icon, or after upgrading `@quasar/extras` / `quasar`, and commit the
 * rewritten `src/assets/fonts/material-symbols-outlined-subset.woff2`. `icon-names.spec.ts` fails
 * until you do. Which names go in, and why, is in `icon-font.mjs`.
 */
import { mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { SourceFontPath, SubsetFontPath, decompress, subsetFont, usedIconNames } from './icon-font.mjs';

const names = usedIconNames();
const source = readFileSync(SourceFontPath);
const woff2 = await subsetFont(await decompress(source), names);

mkdirSync(dirname(SubsetFontPath), { recursive: true });
writeFileSync(SubsetFontPath, woff2);

console.log(`${names.length} icons: ${names.join(' ')}`);
console.log(`${statSync(SourceFontPath).size} bytes -> ${woff2.length} bytes (${SubsetFontPath})`);
