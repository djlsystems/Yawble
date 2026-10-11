import type { InstalledPlugin, PluginEvent, PluginList, PluginVersion, RefusedPlugin } from '../api/types';

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
 */
export function pluginRows(list: PluginList): PluginRow[] {
  const rows = list.versions.map((entry: PluginVersion, index): PluginRow => {
    const plugin = entry.verdict === 'installed'
      ? list.plugins.find((candidate) => candidate.id === entry.id && candidate.version === entry.version)
      : undefined;

    if (plugin) return installedRow(plugin);

    return {
      key: `${entry.verdict}:${entry.id}@${entry.version ?? ''}#${index}`,
      verdict: entry.verdict === 'inactive' ? 'inactive' : 'refused',
      id: entry.id,
      name: entry.name ?? entry.id,
      version: entry.version,
      active: entry.active,
      reason: entry.reason,
    };
  });

  return [...rows, ...unshownRefusals(list.refused, rows)].sort(byId);
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
