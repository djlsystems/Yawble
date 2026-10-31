import { installPlugin } from '../api/client';
import type { MarketplacePackage, PluginInstallResult, PluginSecretMove } from '../api/types';
import { fetchIntoDocuments } from './marketplaceFetch';

/**
 * UPGRADING AN INSTALLED PLUGIN TO THE MARKETPLACE'S NEWER VERSION (PLG 2), as Admin -> Plugins and
 * the Marketplace's Update both do it: one confirm, then the fetch and the install in one go. A
 * member is hired as `plugin:<id>` with no version, so the new active version moves every member
 * onto it, settings kept by field name; the old version stays on disk, inactive.
 */

/** The confirm's title. */
export function upgradeTitle(name: string, version: string): string {
  return `Upgrade ${name} to ${version}?`;
}

/** The confirm's warning, word for word. */
export const UPGRADE_WARNING =
  'Settings from the old version are not guaranteed to map to the new version. Are you sure you want to replace the installed plugin with the latest version?';

/** The icon's tooltip and aria-label. */
export function upgradeLabel(version: string): string {
  return `Upgrade to ${version}`;
}

/** The line once it is done. */
export function upgradedWords(name: string, version: string): string {
  return `Upgraded ${name} to ${version}`;
}

/** One member's secret move on the upgrade, as a person reads it. Never a value. */
export function secretMoveWords(move: PluginSecretMove, teamName: (team: string) => string = (team) => team): string {
  const who = `${teamName(move.team)} / ${move.member}`;
  const fields = move.fields.map((field) => `\`${field}\``).join(', ');

  switch (move.outcome) {
    case 'moved':
      return `${who}: ${fields} moved into a connection on slot ${move.slot}.`;
    case 'kept':
      return `${who}: slot ${move.slot} was already bound, so ${fields} stayed as they were.`;
    case 'nothing':
      return `${who}: no value for ${fields}, so slot ${move.slot} is left for a person to bind.`;
    default:
      return `${who}: ${fields} could not be moved to slot ${move.slot}: ${(move.reason ?? 'the Host gave no reason').replace(/\.$/, '')}.`;
  }
}

/** What an upgrade came to: the install's answer, or the Host's sentence at either step. */
export type UpgradeAnswer = { ok: true; result: PluginInstallResult } | { ok: false; text: string };

/**
 * FETCH, THEN INSTALL, in one go: `POST /api/marketplace/{id}/fetch` unpacks the package into the
 * documents (a folder already holding this version is used as it is), then `POST /api/plugins/install`
 * installs that folder. Replace is set so a copy of this version kept on disk (after a rollback) is
 * replaced by the catalog's; the active version is a different one. A refusal at either step is the
 * Host's sentence, and the installed version stays active.
 */
export async function upgradePlugin(pkg: Pick<MarketplacePackage, 'id' | 'name' | 'version'>): Promise<UpgradeAnswer> {
  const fetched = await fetchIntoDocuments(pkg);
  if (!fetched.ok) return { ok: false, text: fetched.text };

  try {
    const result = await installPlugin(fetched.folder, true);
    if (!result.installed) {
      return { ok: false, text: `${pkg.name} ${pkg.version} was not installed: ${(result.reason ?? 'the Host gave no reason').replace(/\.$/, '')}.` };
    }
    return { ok: true, result };
  } catch (cause) {
    const message = cause instanceof Error ? cause.message : String(cause);
    return { ok: false, text: `${pkg.name} ${pkg.version} was not installed: ${message.trim().replace(/\.$/, '')}. The installed version is still active.` };
  }
}
