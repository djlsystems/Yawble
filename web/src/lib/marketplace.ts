import type { CatalogContents, CatalogNeeds, InstalledSolution, MarketplaceCatalog, MarketplacePackage } from '../api/types';
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
 * BROWSE'S BUTTON FOR A PACKAGE: Get, Update, or none. A plugin is installed once for the whole
 * instance, so one already installed at the catalog's version (or newer) has nothing to get - its
 * card says Installed and offers no button - and one the catalog lists newer offers Update. A
 * solution is installed per team, so Get always stays: it installs another team.
 */
export function getLabel(pkg: Pick<MarketplacePackage, 'kind' | 'installed' | 'updateAvailable'>): 'Get' | 'Update' | null {
  if (pkg.kind !== 'plugin' || !pkg.installed) return 'Get';
  return pkg.updateAvailable ? 'Update' : null;
}

/** The catalog's entry for an installed plugin when it lists a NEWER version than the one installed. */
export function pluginUpdateFor(id: string, catalog: MarketplaceCatalog | null): MarketplacePackage | null {
  const pkg = catalog?.packages.find((candidate) => candidate.kind === 'plugin' && candidate.id === id);
  return pkg && pkg.updateAvailable ? pkg : null;
}

/** Who runs an installed plugin, as a person reads it: "Used by Team / member, ..." or not yet. */
export function pluginUsedByWords(members: { team: string; member: string }[]): string {
  if (members.length === 0) return 'No member uses it yet';
  return `Used by ${members.map((m) => `${m.team} / ${m.member}`).join(', ')}`;
}

/**
 * An installed solution team's need of a plugin, as a person reads it on the Installed tab:
 * "Inbox (Fixture Mail 1.0.0) needs 2.3.0 or newer". An older version is refused while it is installed.
 */
export function pluginNeedWords(need: { teamName: string; package: string; version: string; minimum: string }): string {
  return `${need.teamName} (${need.package} ${need.version}) needs ${need.minimum} or newer`;
}

/**
 * BROWSE'S FILTER: every word of `text` found, case-insensitively, in the package's name, summary,
 * description, or the words for what it needs - the card's short lines and the Host's full
 * sentences under Details. No words keeps every card.
 */
export function packageMatches(pkg: MarketplacePackage, text: string): boolean {
  return matchesWords(
    filterWords(text), pkg.name, pkg.summary, pkg.description, ...plainNeeds(pkg.catalogNeeds), ...pkg.needs,
    ...contentsLines(pkg));
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

/** Each thing a person sets, by its label, or its name when the catalog gives none. */
function labelled(item: { name: string; label: string; required: boolean }): string {
  return `${item.label.trim() || item.name}${item.required ? ' (required)' : ''}`;
}

/**
 * A CARD'S CONTENTS, the bullets it opens with, from the catalog's `contents`; empty when the
 * catalog has none, and the card then shows what it showed before. A SOLUTION says what it holds:
 * each plugin with its version, each member with its role (or Agent), its skills, its triggers in
 * words and its sites. A PLUGIN says first what a person sets up - each setting and key by its label,
 * each sign-in by its slot and the accounts that fill it - then the events it announces and its
 * skills. A list the catalog leaves empty says nothing.
 */
export function contentsLines(pkg: Pick<MarketplacePackage, 'kind' | 'contents'>): string[] {
  const c: Partial<CatalogContents> | null | undefined = pkg.contents;
  if (!c) return [];

  const lines: string[] = [];
  const skills = c.skills ?? [];

  if (pkg.kind === 'solution') {
    for (const plugin of c.plugins ?? []) lines.push(`Plugin ${plugin.id} ${plugin.version}`.trim());
    for (const member of c.members ?? []) {
      const as = member.agent ? 'Agent' : member.role?.trim();
      lines.push(as ? `Member ${member.name}: ${as}` : `Member ${member.name}`);
    }
    if (skills.length) lines.push(`${skills.length === 1 ? 'Skill' : 'Skills'}: ${skills.join(', ')}`);
    for (const trigger of c.triggers ?? []) lines.push(`Runs ${trigger.words.trim() || trigger.name}`);
    for (const site of c.sites ?? []) lines.push(`Site ${site}`);
    return lines;
  }

  for (const setting of c.settings ?? []) lines.push(`Setting: ${labelled(setting)}`);
  for (const secret of c.secrets ?? []) lines.push(`Key: ${labelled(secret)}`);
  for (const connection of c.connections ?? []) {
    const through = connection.providers.length ? ` (${joined(connection.providers.map(providerWords), 'or')})` : '';
    lines.push(`Sign-in: ${connection.slot.trim() || 'an account'}${through}${connection.required ? ' (required)' : ''}`);
  }
  for (const event of c.events ?? []) lines.push(`Announces: ${event.summary.trim() || event.type}`);
  if (skills.length) lines.push(`${skills.length === 1 ? 'Skill' : 'Skills'}: ${skills.join(', ')}`);
  return lines;
}
