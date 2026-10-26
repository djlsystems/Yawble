import type { RibbonSpec } from './ribbon';

/**
 * THE ONE HEADER ROW GIVES WAY IN ONE ORDER, AND ONLY THE CLUSTER GIVES WAY.
 *
 * The ribbon must show its commands from New Team through Backlog beside the right cluster. When it
 * cannot, the cluster yields, one step at a time and each only while the steps before it still leave
 * too few: the version string leaves the row (it is under the logo's tooltip and in the account
 * menu); the ribbon's buttons drop from 8px to 4px padding a side and the active team's title is
 * capped at 3rem with an ellipsis (`tight`, the commands measured again; the full name its tooltip
 * and aria-label, and whole on the strip's tab); last, the logo shrinks to the head mark alone, the
 * product name in its tooltip. The ribbon's labels never give way; what still does not fit spills
 * into the overflow menu.
 *
 * THE TOOLS ON THE RIGHT TAKE NO STEP: live, Needs you, the activity gauge and the account are each a
 * glyph at every width, their words in their tooltips and aria-labels, and the update notice is an
 * icon beside the logo. Nothing of them is left to give way.
 */
export const GiveWaySteps = ['version', 'tight', 'mark'] as const;

export interface HeaderFit {
  /** The version string on the row. */
  version: boolean;
  /** The ribbon's buttons at 4px padding a side rather than 8px, the active team's title capped at 3rem. */
  tight: boolean;
  /** The logo is the head mark alone, the product name in its tooltip. */
  mark: boolean;
}

/** What the cluster carries after `level` steps. A phone row runs no steps and never carries the version. */
export function headerFit(level: number, phone: boolean): HeaderFit {
  return {
    version: !phone && level < 1,
    tight: level >= 2,
    mark: level >= 3,
  };
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
