/**
 * WRITES THE NAMED OPTION SETS A PLUGIN SETTING MAY DRAW ITS CHOICES FROM (`"options": "locale"`,
 * `"options": "timeZone"`): src/Harness.Host/PluginOptionSets.json, embedded in the Host, which
 * checks a value against it and answers each field's options, label and value, to the settings form.
 *
 *   node web/scripts/plugin-option-sets.mjs <chromium_language_matcher.cc> <zone.tab>
 *
 * locale    every language Chrome accepts for its languages (Accept-Language and navigator.languages):
 *           kAcceptLanguageList in Chromium's ui/base/l10n/chromium_language_matcher.cc, without its
 *           pseudolocales (ar-XB, en-XA). Each is labelled in English by Intl.DisplayNames
 *           ("English (United States)"), sorted by label.
 * timeZone  UTC, then every IANA time zone in the tz database's zone.tab (Europe/London,
 *           Asia/Kolkata), sorted. Not Intl.supportedValuesOf('timeZone'): it omits UTC and, in
 *           Node and older browsers, answers CLDR's old names (Asia/Calcutta).
 *
 * Nothing in the file is typed by hand. `PluginOptionSetsTests` pins what it must hold.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const [chromium, zoneTab] = process.argv.slice(2);
if (!chromium || !zoneTab) {
  console.error('usage: node web/scripts/plugin-option-sets.mjs <chromium_language_matcher.cc> <zone.tab>');
  process.exit(2);
}

const target = join(dirname(fileURLToPath(import.meta.url)), '../../src/Harness.Host/PluginOptionSets.json');

// The accept list only: the platform list before it is not quoted, and pseudolocales sit in #if blocks.
const source = readFileSync(chromium, 'utf8');
const start = source.indexOf('kAcceptLanguageList');
if (start < 0) throw new Error('no kAcceptLanguageList in ' + chromium);
const block = source.slice(start, source.indexOf('});', start));
const Pseudo = new Set(['ar-XB', 'en-XA']);
const codes = [...block.matchAll(/GetKnownLanguageTag\("([^"]+)"\)/g)].map((m) => m[1]).filter((c) => !Pseudo.has(c));
if (codes.length < 100) throw new Error(`only ${codes.length} languages read from ${chromium}`);

const names = new Intl.DisplayNames('en', { type: 'language', languageDisplay: 'standard', fallback: 'none' });
const locale = codes
  .map((value) => ({ value, label: names.of(value) ?? value }))
  .sort((a, b) => a.label.localeCompare(b.label, 'en') || a.value.localeCompare(b.value));

const tab = readFileSync(zoneTab, 'utf8');
const header = /^#\s*version\s+(\S+)/m.exec(readFileSync(join(dirname(zoneTab), 'tzdata.zi'), 'utf8'))?.[1] ?? null;
const zones = tab
  .split('\n')
  .filter((line) => line && !line.startsWith('#'))
  .map((line) => line.split('\t')[2])
  .filter(Boolean);
if (zones.length < 300) throw new Error(`only ${zones.length} zones read from ${zoneTab}`);
const timeZone = ['UTC', ...[...new Set(zones)].sort()].map((value) => ({ value, label: value }));

const out = {
  source: {
    locale: 'Chromium ui/base/l10n/chromium_language_matcher.cc kAcceptLanguageList, labelled by Intl.DisplayNames(en)',
    timeZone: `tz database zone.tab${header ? ` ${header}` : ''}, with UTC`,
  },
  locale,
  timeZone,
};
// One option per line, so a regenerated file's diff reads as what was added and removed.
const list = (items) => '[\n' + items.map((o) => '    ' + JSON.stringify(o)).join(',\n') + '\n  ]';
writeFileSync(
  target,
  `{\n  "source": ${JSON.stringify(out.source)},\n  "locale": ${list(locale)},\n  "timeZone": ${list(timeZone)}\n}\n`,
);
console.log(`${locale.length} languages, ${timeZone.length} time zones -> ${target}`);
