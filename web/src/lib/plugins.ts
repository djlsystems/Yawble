import type { InstalledPlugin, MarketplaceCatalog, MarketplacePackage, PluginEvent, PluginList, PluginVersion, RefusedPlugin } from '../api/types';
import { isBuiltInPlugin } from './teamConnections';

/**
 * THE PLUGINS SCREEN'S ROWS: one per plugin version under the plugins directory, installed or
 * refused, from `GET /api/plugins`. Kept out of the component so the reading of the list is one
 * place and testable without a mount.
 */

export type PluginRow =
  | {
      key: string;
      verdict: 'installed';
      id: string;
      name: string;
      version: string;
      active: boolean;
      plugin: InstalledPlugin;
    }
  | {
      key: string;
      verdict: 'refused' | 'inactive';
      id: string;
      name: string;
      version: string | null;
      active: boolean;
      reason: string | null;
    };

const byId = (a: PluginRow, b: PluginRow) =>
  a.id.localeCompare(b.id) || (a.version ?? '').localeCompare(b.version ?? '', undefined, { numeric: true });

function installedRow(plugin: InstalledPlugin): PluginRow {
  return {
    key: `installed:${plugin.id}@${plugin.version}`,
    verdict: 'installed',
    id: plugin.id,
    name: plugin.name,
    version: plugin.version,
    active: plugin.active,
    plugin,
  };
}

/**
 * One row per version folder, by id then version: each of `versions`, with the Host's verdict,
 * joined to `plugins` for an installed version's details - and a row for every refusal no version
 * folder carries.
 *
 * NO BUILT-IN PLUGIN IS A ROW (`isBuiltInPlugin`, as Add member and the Solutions launcher read
 * it): it ships with the platform and a release keeps it, so there is nothing here for a person to
 * do with it. A FOLDER a person put under a built-in's id is theirs to fix, so its refusal stays.
 */
export function pluginRows(list: PluginList): PluginRow[] {
  const rows = list.versions.flatMap((entry: PluginVersion, index): PluginRow[] => {
    const plugin = entry.verdict === 'installed'
      ? list.plugins.find((candidate) => candidate.id === entry.id && candidate.version === entry.version)
      : undefined;

    if (plugin) return isBuiltInPlugin(plugin) ? [] : [installedRow(plugin)];

    // An `installed` verdict whose version `plugins` does not hold is not a second installed tile:
    // the installed plugin of that id gets its own below, from `plugins`.
    if (entry.verdict === 'installed' && list.plugins.some((candidate) => candidate.id === entry.id)) return [];

    return [{
      key: `${entry.verdict}:${entry.id}@${entry.version ?? ''}#${index}`,
      verdict: entry.verdict === 'inactive' ? 'inactive' : 'refused',
      id: entry.id,
      name: entry.name ?? entry.id,
      version: entry.version,
      active: entry.active,
      reason: entry.reason,
    }];
  });

  return [...rows, ...unlistedInstalled(list.plugins, rows), ...unshownRefusals(list.refused, rows)].sort(byId);
}

/**
 * EVERY INSTALLED PLUGIN IS A ROW, whatever `versions` says. Add member and the Marketplace list
 * `plugins`; this screen joined `plugins` only onto `versions`, so an installed plugin with no installed
 * version row of its own - two plugins sharing a name, one of them read from a `versions` that does not
 * carry it - was in Add member and missing here, where it is managed. Each installed id no row already
 * shows becomes its own installed row, so this screen never lists fewer plugins than Add member offers.
 */
function unlistedInstalled(plugins: InstalledPlugin[], rows: PluginRow[]): PluginRow[] {
  const shown = new Set(rows.flatMap((row) => (row.verdict === 'installed' ? [`${row.id}@${row.version}`] : [])));
  return plugins
    .filter((plugin) => !isBuiltInPlugin(plugin) && !shown.has(`${plugin.id}@${plugin.version}`))
    .map(installedRow);
}

/**
 * NO TWO PLUGIN CHOICES READ ALIKE, wherever a person picks a plugin (Add member, Member settings'
 * Agent, the solution wizard; the Host's hiring list says the same, `PluginChoices.Label`): a name no
 * other installed plugin has reads `Name (plugin)`, as it always did; a name another one shares (trimmed,
 * case-insensitive) reads `Name (plugin, <id> <version>)`.
 */
export function pluginChoiceLabel(
  plugin: Pick<InstalledPlugin, 'id' | 'name' | 'version'>,
  installed: readonly Pick<InstalledPlugin, 'id' | 'name' | 'builtIn'>[],
): string {
  const name = plugin.name.trim();
  return sharesName(plugin, installed) ? `${name} (plugin, ${plugin.id} ${plugin.version})` : `${name} (plugin)`;
}

/** Whether another installed plugin (not a built-in) has `plugin`'s name, trimmed and case-insensitively. */
export function sharesName(
  plugin: Pick<InstalledPlugin, 'id' | 'name'>,
  installed: readonly Pick<InstalledPlugin, 'id' | 'name' | 'builtIn'>[],
): boolean {
  const name = plugin.name.trim().toLowerCase();
  return installed.some((other) => other.builtIn !== true && other.id !== plugin.id && other.name.trim().toLowerCase() === name);
}

/** A plugin as a person tells it apart in a sentence: `Fixture Cloud 1.1.4 (fixture-old)`. */
export function pluginWords(plugin: { id: string; name: string; version: string }): string {
  return `${plugin.name} ${plugin.version} (${plugin.id})`;
}

/** "Alpha / Builder, Beta / Deployer", or that none is. */
export function membersOnWords(members: readonly { teamName: string; memberName: string }[]): string {
  return members.length === 0 ? 'No member is on it.' : members.map((m) => `${m.teamName} / ${m.memberName}`).join(', ');
}

/**
 * THE MARKETPLACE'S REPLACEMENT FOR AN INSTALLED PLUGIN, said only by the newer one's own manifest: an
 * installed plugin whose `replaces` names it (shown with the catalog's newer version of that one when it
 * lists one), else a catalog plugin package whose `replaces` names it and that is not installed, or is
 * newer than what is installed. Null when nothing says it replaces `plugin`. Never a guess from a name.
 */
export interface PluginReplacement {
  id: string
  name: string
  version: string
  /** The catalog's package to fetch, when there is something to get or update; null when installed and current. */
  pkg: MarketplacePackage | null
  /** Whether the replacement is installed already. */
  installed: boolean
}

export function replacementFor(
  plugin: Pick<InstalledPlugin, 'id' | 'replacedBy'>,
  installed: readonly InstalledPlugin[],
  catalog: MarketplaceCatalog | null,
): PluginReplacement | null {
  const listed = (id: string) =>
    catalog?.checked ? catalog.packages.find((candidate) => candidate.kind === 'plugin' && candidate.id === id) ?? null : null;

  const local = (plugin.replacedBy ?? [])[0] ?? installed.find((other) => other.id !== plugin.id && (other.replaces ?? []).some((r) => r.id === plugin.id));
  if (local) {
    const pkg = listed(local.id);
    return pkg && pkg.updateAvailable
      ? { id: local.id, name: pkg.name, version: pkg.version, pkg, installed: true }
      : { id: local.id, name: local.name, version: local.version, pkg: null, installed: true };
  }

  const offered = catalog?.checked
    ? catalog.packages.find((candidate) => candidate.kind === 'plugin' && candidate.id !== plugin.id && (candidate.replaces ?? []).includes(plugin.id))
    : undefined;
  if (!offered) return null;

  return { id: offered.id, name: offered.name, version: offered.version, pkg: offered.installed && !offered.updateAvailable ? null : offered, installed: offered.installed };
}

/** "Fixture Cloud 1.1.4 is replaced by Fixture Cloud 3.0.0 (fixture-new)". */
export function replacedByWords(plugin: { name: string; version: string }, replacement: { id: string; name: string; version: string }): string {
  return `${plugin.name} ${plugin.version} is replaced by ${replacement.name} ${replacement.version} (${replacement.id})`;
}

/**
 * EVERY REFUSAL IS SHOWN. `versions` carries a refused reason only on the version folder it
 * belongs to, so a refusal with no such folder is in `refused[]` alone: an `active` naming a folder
 * that is not there (its folders all read inactive), or a refused folder whose name starts with `.`
 * (left out of `versions` altogether). Each one no version row already says becomes its own row.
 */
function unshownRefusals(refused: RefusedPlugin[], rows: PluginRow[]): PluginRow[] {
  const said = new Set(
    rows.flatMap((row) => (row.verdict === 'refused' ? [`${row.id}\n${row.reason ?? ''}`] : [])),
  );

  return refused
    .filter((entry) => !said.has(`${entry.id}\n${entry.reason}`))
    .map((entry, index): PluginRow => ({
      key: `refused:${entry.id}#only-${index}`,
      verdict: 'refused',
      id: entry.id,
      name: entry.id,
      version: null,
      active: false,
      reason: entry.reason,
    }));
}

/** The events a plugin publishes, as full type names with the manifest's line on each. */
export function pluginEvents(plugin: InstalledPlugin): PluginEvent[] {
  return plugin.publishes;
}

/** Its skill's name - `plugin-<id>` for its main one - or null when it has none. */
export function pluginSkill(plugin: InstalledPlugin): string | null {
  return plugin.skill;
}

/**
 * A `plugin.json` for reading: re-indented when it parses, as the Host holds it when it does not -
 * a manifest the Host refused may well be the one that does not parse, and it should still be
 * readable.
 */
export function formatManifest(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
