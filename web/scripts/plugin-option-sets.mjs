/**
 * WRITES THE NAMED OPTION SETS A PLUGIN SETTING MAY DRAW ITS CHOICES FROM (`"options": "locale"`,
 * `"options": "timeZone"`): src/Harness.Host/PluginOptionSets.json, embedded in the Host, which
 * checks a value against it and answers each field's options, label and value, to the settings form.
 *
 *   node web/scripts/plugin-option-sets.mjs <chromium_language_matcher.cc> <tzdata directory>
 *
 * locale    every language Chrome accepts for its languages (Accept-Language and navigator.languages):
 *           kAcceptLanguageList in Chromium's ui/base/l10n/chromium_language_matcher.cc, without its
 *           pseudolocales (ar-XB, en-XA). Each is labelled in English by Intl.DisplayNames
 *           ("English (United States)"), sorted by label. Its `keywords` are its other English name
 *           when it has one ("British English", "Flemish"), so typing either finds it.
 * timeZone  UTC, then every IANA time zone in the tz database's zone.tab (Europe/London,
 *           Asia/Kolkata), sorted. Not Intl.supportedValuesOf('timeZone'): it omits UTC and, in
 *           Node and older browsers, answers CLDR's old names (Asia/Calcutta).
 * aliases   for timeZone, each older name the tz database still links (its `backward` and `etcetera`
 *           Link lines: Asia/Calcutta, Europe/Kiev, US/Eastern) to the entry above it stands for
 *           (Asia/Kolkata); Etc/UTC, GMT and their kin to UTC. A value saved under an alias is
 *           stored and run as its entry; the dropdown lists only the entries, each with its aliases
 *           as its `keywords`, so typing "Calcutta" finds Asia/Kolkata.
 *
 * The tzdata directory is an unpacked https://data.iana.org/time-zones/releases/tzdataNNNNx.tar.gz
 * (zone.tab, backward, etcetera, version).
 *
 * Nothing in the file is typed by hand. `PluginOptionSetsTests` pins what it must hold.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const [chromium, tzdata] = process.argv.slice(2);
if (!chromium || !tzdata) {
  console.error('usage: node web/scripts/plugin-option-sets.mjs <chromium_language_matcher.cc> <tzdata directory>');
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
const dialects = new Intl.DisplayNames('en', { type: 'language', languageDisplay: 'dialect', fallback: 'none' });
const withKeywords = (option, keywords) => (keywords ? { ...option, keywords } : option);
const locale = codes
  .map((value) => {
    const label = names.of(value) ?? value;
    const dialect = dialects.of(value);
    return withKeywords({ value, label }, dialect && dialect !== label ? dialect : null);
  })
  .sort((a, b) => a.label.localeCompare(b.label, 'en') || a.value.localeCompare(b.value));

const zoneTab = join(tzdata, 'zone.tab');
const tab = readFileSync(zoneTab, 'utf8');
const header = readFileSync(join(tzdata, 'version'), 'utf8').trim() || null;
const zones = tab
  .split('\n')
  .filter((line) => line && !line.startsWith('#'))
  .map((line) => line.split('\t')[2])
  .filter(Boolean);
if (zones.length < 300) throw new Error(`only ${zones.length} zones read from ${zoneTab}`);
const timeZone = ['UTC', ...[...new Set(zones)].sort()].map((value) => ({ value, label: value }));

// ALIASES: Link TARGET NAME, followed to an entry above (a link may point at another link).
const listed = new Set(timeZone.map((o) => o.value));
const links = new Map();
for (const file of ['backward', 'etcetera']) {
  for (const line of readFileSync(join(tzdata, file), 'utf8').split('\n')) {
    const [kind, target, name] = line.replace(/#.*/, '').trim().split(/\s+/);
    if (kind === 'Link' && target && name) links.set(name, target);
  }
}
const Universal = new Set(['Etc/UTC', 'Etc/GMT']);
const entryFor = (name) => {
  for (let at = name, hops = 0; at && hops < 10; at = links.get(at), hops++) {
    if (Universal.has(at)) return 'UTC';
    if (listed.has(at)) return at;
  }
  return null;
};
const timeZoneAliases = Object.fromEntries(
  [...Universal, ...links.keys()]
    .filter((name) => !listed.has(name))
    .map((name) => [name, entryFor(name)])
    .filter(([, entry]) => entry !== null)
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)),
);
if (timeZoneAliases['Asia/Calcutta'] !== 'Asia/Kolkata') throw new Error('Asia/Calcutta is not linked to Asia/Kolkata in ' + tzdata);
for (const option of timeZone) {
  const older = Object.keys(timeZoneAliases).filter((alias) => timeZoneAliases[alias] === option.value);
  if (older.length > 0) option.keywords = older.join(' ');
}

const out = {
  source: {
    locale: 'Chromium ui/base/l10n/chromium_language_matcher.cc kAcceptLanguageList, labelled by Intl.DisplayNames(en)',
    timeZone: `tz database zone.tab${header ? ` ${header}` : ''}, with UTC; aliases from its backward and etcetera links`,
  },
  locale,
  timeZone,
  aliases: { timeZone: timeZoneAliases },
};
// One option per line, so a regenerated file's diff reads as what was added and removed.
const list = (items) => '[\n' + items.map((o) => '    ' + JSON.stringify(o)).join(',\n') + '\n  ]';
const map = (pairs) =>
  '{\n' + Object.entries(pairs).map(([k, v]) => '      ' + JSON.stringify(k) + ': ' + JSON.stringify(v)).join(',\n') + '\n    }';
writeFileSync(
  target,
  `{\n  "source": ${JSON.stringify(out.source)},\n  "locale": ${list(locale)},\n  "timeZone": ${list(timeZone)},\n`
    + `  "aliases": {\n    "timeZone": ${map(timeZoneAliases)}\n  }\n}\n`,
);
console.log(`${locale.length} languages, ${timeZone.length} time zones, ${Object.keys(timeZoneAliases).length} time zone aliases -> ${target}`);
