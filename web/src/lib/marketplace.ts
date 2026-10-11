import type { CatalogNeeds, InstalledSolution, MarketplaceCatalog, MarketplacePackage } from '../api/types';
import { filterWords, matchesWords } from './filterWords';
import { compareVersions } from './solutions';

/**
 * THE WORDS OF MARKETPLACE > BROWSE, kept out of the component so each can be read and tested on
 * its own: what a package is, whether this instance has it, why the catalog was not read, where a
 * fetched package landed, which cards the filter keeps, and which installed team has an update.
 */

/** A package's kind as a person reads it. */
export function kindWords(kind: MarketplacePackage['kind']): string {
  return kind === 'solution' ? 'Solution' : 'Plugin';
}

/** Whether this instance has the package, and when the catalog's version is newer, that too. */
export function installedWords(pkg: MarketplacePackage): string {
  if (!pkg.installed) return 'Not installed';

  const on = pkg.installedOn.length ? ` on ${pkg.installedOn.join(', ')}` : '';
  const version = pkg.installedVersion ? ` ${pkg.installedVersion}` : '';

  return pkg.updateAvailable
    ? `Installed${version}${on} · Update available: ${pkg.version}`
    : `Installed${version}${on}`;
}

/**
 * BROWSE'S FILTER: every word of `text` found, case-insensitively, in the package's name, summary,
 * description, or the words for what it needs - the card's short lines and the Host's full
 * sentences under Details. No words keeps every card.
 */
export function packageMatches(pkg: MarketplacePackage, text: string): boolean {
  return matchesWords(filterWords(text), pkg.name, pkg.summary, pkg.description, ...plainNeeds(pkg.catalogNeeds), ...pkg.needs);
}

/** A filter that kept no card, as a sentence naming what was typed. */
export function noneMatchWords(text: string): string {
  return `No package in the catalog matches “${text.trim()}”.`;
}

/**
 * The catalog's entry for an installed team when it lists a NEWER version than the team's, so
 * Installed can say so and offer Update; null when it lists none, the same or an older one.
 */
export function updateFor(row: Pick<InstalledSolution, 'id' | 'version'>, catalog: MarketplaceCatalog | null): MarketplacePackage | null {
  const listed = catalog?.checked ? catalog.packages.find((pkg) => pkg.kind === 'solution' && pkg.id === row.id) : undefined;
  return listed && compareVersions(listed.version, row.version) > 0 ? listed : null;
}

/** A catalog the Host has not read, as a sentence that says why. Empty when it was read. */
export function notCheckedWords(catalog: MarketplaceCatalog): string {
  if (catalog.checked) return '';

  const reason = (catalog.reason ?? '').trim().replace(/\.$/, '');

  return reason
    ? `The package catalog has not been checked: ${reason}.`
    : 'The package catalog has not been checked, and the Host did not say why.';
}

/** A catalog the console could not ask for at all, as a sentence. */
export function unreachableWords(message: string): string {
  return `Could not reach the package catalog: ${message.trim().replace(/\.$/, '')}.`;
}

/** A Get the Host refused, as a sentence naming the package. Nothing was installed. */
export function refusedWords(pkg: Pick<MarketplacePackage, 'name'>, message: string): string {
  return `${pkg.name} was not fetched: ${message.trim().replace(/\.$/, '')}. Nothing was installed.`;
}

/**
 * The absolute folder the install routes take, from the documents root (`/data/documents`) and the
 * documents-relative folder the fetch answered (`Marketplace/mail-1.2.0`).
 */
export function fetchedFolder(root: string, folder: string): string {
  return `${root.replace(/\/+$/, '')}/${folder.replace(/^\/+/, '')}`;
}

/**
 * THE WORDING TABLE: each connection provider id the Host knows, as a person says it - `name` in "a
 * Google account", and `mailboxes`, the mailboxes a person has that it reaches, in "a mailbox: Gmail,
 * iCloud, ...". The only place a provider id becomes words; an id not here reads as itself.
 */
export const PROVIDER_WORDS: Readonly<Record<string, { name: string; mailboxes: readonly string[] }>> = {
  google: { name: 'Google', mailboxes: ['Gmail'] },
  microsoft: { name: 'Microsoft', mailboxes: ['Outlook'] },
  imap: { name: 'mailbox by app password', mailboxes: ['Gmail', 'iCloud', 'Yahoo'] },
  custom: { name: 'custom sign-in', mailboxes: [] },
};

/** The order mailboxes are named in, as the website names them. */
const MAILBOX_ORDER = ['Gmail', 'iCloud', 'Yahoo', 'Outlook'];

/** Each runtime the catalog names (`needs.runtimes`), as a person says it; one not here reads as itself. */
export const RUNTIME_WORDS: Readonly<Record<string, string>> = {
  dotnet: '.NET',
  node: 'Node.js',
  python3: 'Python 3',
};

/** The table's words for a provider id, only its own entries (never `toString` and the like). */
function wordsOf(id: string) {
  return Object.hasOwn(PROVIDER_WORDS, id) ? PROVIDER_WORDS[id] : undefined;
}

/** A provider id as a person says it: its words, or the id itself, never blank. */
export function providerWords(id: string): string {
  return wordsOf(id)?.name ?? (id.trim() || 'an unnamed provider');
}

function joined(items: string[], last: 'or' | 'and'): string {
  return items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} ${last} ${items[items.length - 1]}`;
}

function article(word: string): string {
  return /^[aeiou]/i.test(word) ? 'an' : 'a';
}

/** One connection by what a person has: "Needs a Google account", "Needs a mailbox: Gmail, ... or another IMAP mailbox". */
export function connectionWords(connection: CatalogNeeds['connections'][number]): string {
  const lead = connection.required ? 'Needs' : 'Can use';
  const providers = connection.providers;

  if (providers.includes('imap')) {
    const has = new Set(providers.flatMap((id) => wordsOf(id)?.mailboxes ?? []));
    const others = providers.filter((id) => !wordsOf(id)?.mailboxes.length).map(providerWords);
    return `${lead} a mailbox: ${joined([...MAILBOX_ORDER.filter((m) => has.has(m)), ...others, 'another IMAP mailbox'], 'or')}`;
  }

  if (providers.length === 0) return `${lead} an account connected`;

  const names = providers.map(providerWords);
  return `${lead} ${article(names[0] ?? '')} ${joined(names, 'or')} account`;
}

/** The settings the catalog's `when` texts name ("when the sources setting includes adzuna"), each once. */
function settingsNamed(whens: string[]): string[] {
  const named = whens.map((when) => /\bthe (\S+) setting\b/.exec(when)?.[1]).filter((name): name is string => !!name);
  return [...new Set(named)];
}

/**
 * A CARD'S NEEDS, a few short lines in the website's words: each connection by what a person has, each
 * folder a file goes in, a setting only when it is required (optional ones are asked in the install
 * wizard), every secret as ONE line naming the settings that call for keys, and the runtimes. The full
 * lines are the Host's sentences, under Details.
 */
export function plainNeeds(needs: CatalogNeeds | undefined): string[] {
  if (!needs) return [];
  const lines = needs.connections.map(connectionWords);

  for (const input of needs.inputs) {
    if (input.kind === 'documents') lines.push(`${input.required ? 'Needs' : 'Can take'} a file in ${input.name}`);
    else if (input.required) lines.push(`Asks for its ${input.name} setting at install`);
  }

  if (needs.secrets.length) {
    const always = needs.secrets.some((secret) => !secret.when);
    const settings = settingsNamed(needs.secrets.map((secret) => secret.when ?? ''));
    lines.push(
      always ? 'Needs keys set on the Host'
      : settings.length ? `Needs keys set on the Host for some of its settings: ${joined(settings, 'and')}`
      : 'Needs keys set on the Host for some of its settings',
    );
  }

  if (needs.runtimes.length) {
    lines.push(`Runs on ${joined(needs.runtimes.map((id) => (Object.hasOwn(RUNTIME_WORDS, id) ? RUNTIME_WORDS[id] : undefined) ?? id), 'and')}`);
  }

  return lines;
}
