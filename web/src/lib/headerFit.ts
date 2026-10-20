import type { RibbonSpec } from './ribbon';

/**
 * THE ONE HEADER ROW GIVES WAY IN ONE ORDER, AND ONLY THE CLUSTER GIVES WAY.
 *
 * The ribbon must show its commands from New Team through Backlog beside the right cluster. When it
 * cannot, the cluster yields, one step at a time and each only while the steps before it still leave
 * too few: the version string leaves the row (it is under the logo's tooltip and in the account
 * menu); the live chip becomes a dot with its word in the tooltip; the account menu shows only its
 * icon (the name is the menu's first line); Needs you shows only its count pill (the words stay its
 * tooltip and aria-label, as on a phone); the ribbon's buttons drop from 8px to 4px padding a side
 * (`tight`, the commands measured again); last, the logo shrinks to the head mark alone, the product
 * name in its tooltip. The gauge-text step the item named has nothing to take: the activity gauge is
 * a glyph whose words are already its tooltip. The ribbon's labels never give way; what still does
 * not fit spills into the overflow menu as before.
 */
export const GiveWaySteps = ['version', 'live-word', 'account-name', 'needs-you-word', 'tight', 'mark'] as const;

export interface HeaderFit {
  /** The version string on the row. */
  version: boolean;
  /** The live chip says its word; otherwise it is a dot with the word in its tooltip. */
  liveWord: boolean;
  /** The account menu's button says the name beside its icon. */
  accountName: boolean;
  /** Needs you says its words beside the count pill. */
  needsYouWord: boolean;
  /** The ribbon's buttons at 4px padding a side rather than 8px. */
  tight: boolean;
  /** The logo is the head mark alone, the product name in its tooltip. */
  mark: boolean;
}

/** What the cluster carries after `level` steps; a phone row never carries the version. */
export function headerFit(level: number, phone: boolean): HeaderFit {
  return { version: !phone && level < 1, liveWord: level < 2, accountName: !phone && level < 3, needsYouWord: !phone && level < 4, tight: level >= 5, mark: level >= 6 };
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
