import type { RibbonSpec } from './ribbon';

/**
 * THE ONE HEADER ROW GIVES WAY IN ONE ORDER, AND ONLY THE CLUSTER GIVES WAY.
 *
 * The ribbon must show its commands from New Team through Backlog beside the right cluster. When it
 * cannot, the cluster yields first, one step at a time: the version string leaves the row (it is
 * under the logo's tooltip and in the account menu), then the live chip becomes a dot with its word
 * in the tooltip. The third step the item names - the gauge losing its text - has nothing to take:
 * the activity gauge is a glyph whose words are already its tooltip. The ribbon's labels and the logo
 * never give way; what still does not fit spills into the overflow menu as before.
 */
export const GiveWaySteps = ['version', 'live-word'] as const;

export interface HeaderFit {
  /** The version string on the row. */
  version: boolean;
  /** The live chip says its word; otherwise it is a dot with the word in its tooltip. */
  liveWord: boolean;
}

/** What the cluster carries after `level` steps; a phone row never carries the version. */
export function headerFit(level: number, phone: boolean): HeaderFit {
  return { version: !phone && level < 1, liveWord: level < 2 };
}

/** How many commands, from the left, the row must keep: New Team through Backlog. */
export function keepThrough(spec: RibbonSpec): number {
  const entries = spec.tabs.flatMap((tab) => tab.items);
  return entries.findIndex((item) => item.action === 'backlog') + 1;
}

/** One more step while the ribbon shows fewer than it must keep and a step is left. */
export function nextGiveWay(level: number, shown: number, keep: number): number {
  return shown < keep && level < GiveWaySteps.length ? level + 1 : level;
}
