import type { InstalledPlugin, PluginEvent, PluginList, PluginVersion, RefusedPlugin } from '../api/types';

/**
 * THE PLUGINS SCREEN'S ROWS: one per plugin version under the plugins directory, installed or
 * refused, from `GET /api/plugins`. Kept out of the component so the reading of the list - and the
 * older Host's names for the same facts - is one place and testable without a mount.
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
    // An older Host loaded only the active version, so everything it listed was active.
    active: plugin.active ?? true,
    plugin,
  };
}

/**
 * One row per version folder, by id then version. From `versions` when the Host sends it - every
 * folder, with the Host's verdict - joined to `plugins` for an installed version's details; from
 * `plugins` and `refused` alone on an older Host.
 */
export function pluginRows(list: PluginList): PluginRow[] {
  const plugins = list.plugins ?? [];

  if (list.versions && list.versions.length > 0) {
    const rows = list.versions
      .map((entry: PluginVersion, index): PluginRow => {
        const plugin = entry.verdict === 'installed'
          ? plugins.find((candidate) => candidate.id === entry.id && candidate.version === entry.version)
          : undefined;

        if (plugin) return installedRow(plugin);

        return {
          key: `${entry.verdict}:${entry.id}@${entry.version ?? ''}#${index}`,
          verdict: entry.verdict === 'inactive' ? 'inactive' : 'refused',
          id: entry.id,
          name: entry.name ?? entry.id,
          version: entry.version ?? null,
          active: entry.active,
          reason: entry.reason ?? null,
        };
      });

    return [...rows, ...unshownRefusals(list.refused ?? [], rows)].sort(byId);
  }

  const refused = (list.refused ?? []).map(
    (entry: RefusedPlugin, index): PluginRow => ({
      key: `refused:${entry.id}@${entry.version ?? ''}#${index}`,
      verdict: 'refused',
      id: entry.id,
      name: entry.name ?? entry.id,
      version: entry.version ?? null,
      active: entry.active ?? false,
      reason: entry.reason,
    }),
  );

  return [...plugins.map(installedRow).sort(byId), ...refused.sort(byId)];
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

/** The events a plugin publishes, as full type names, under either name the Host has used. */
export function pluginEvents(plugin: InstalledPlugin): PluginEvent[] {
  return (plugin.events ?? plugin.publishes ?? []).map((event) =>
    typeof event === 'string' ? { type: event } : event,
  );
}

/** Its skill's name, or null. An older Host listed `skills`. */
export function pluginSkill(plugin: InstalledPlugin): string | null {
  if (plugin.skill !== undefined) return plugin.skill;

  const file = plugin.skills?.[0];
  return file ? file.replace(/^.*\//, '').replace(/\.md$/, '') : null;
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
