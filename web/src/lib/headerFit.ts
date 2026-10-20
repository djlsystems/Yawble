import type { RibbonSpec } from './ribbon';

/**
 * THE ONE HEADER ROW GIVES WAY IN ONE ORDER, AND ONLY THE CLUSTER GIVES WAY.
 *
 * The ribbon must show its commands from New Team through Backlog beside the right cluster. When it
 * cannot, the cluster yields, one step at a time and each only while the steps before it still leave
 * too few: "Update available" shows its icon alone (the words its tooltip and aria-label, and it
 * still opens Updates); the version string leaves the row (it is under the logo's tooltip and in the
 * account menu); the live chip becomes a dot with its word in the tooltip; the account menu shows
 * only its icon (the name is the menu's first line); Needs you shows only its count pill (the words
 * stay its tooltip and aria-label, as on a phone); the ribbon's buttons drop from 8px to 4px padding
 * a side and the active team's title is capped at 3rem with an ellipsis (`tight`, the commands
 * measured again; the full name its tooltip and aria-label, and whole on the strip's tab); last, the
 * logo shrinks to the head mark alone, the product name in its tooltip. The gauge-text step the item
 * first named has nothing to take: the activity gauge is a glyph whose words are already its
 * tooltip. The ribbon's labels never give way; what still does not fit spills into the overflow menu.
 *
 * A step with nothing to take - the update notice when no update is pending - is still a step: the
 * layout fits the ribbon again after each one (`RibbonBar`'s `refit`), so the next is taken at once.
 */
export const GiveWaySteps = ['update-word', 'version', 'live-word', 'account-name', 'needs-you-word', 'tight', 'mark'] as const;

export interface HeaderFit {
  /** "Update available" says its words beside its icon, when an update is pending. */
  updateWord: boolean;
  /** The version string on the row. */
  version: boolean;
  /** The live chip says its word; otherwise it is a dot with the word in its tooltip. */
  liveWord: boolean;
  /** The account menu's button says the name beside its icon. */
  accountName: boolean;
  /** Needs you says its words beside the count pill. */
  needsYouWord: boolean;
  /** The ribbon's buttons at 4px padding a side rather than 8px, the active team's title capped at 3rem. */
  tight: boolean;
  /** The logo is the head mark alone, the product name in its tooltip. */
  mark: boolean;
}

/**
 * What the cluster carries after `level` steps. A phone row runs no steps (it has no commands to fit)
 * and never carries the version, the account name, the Needs you words or the update notice's words.
 * With an update pending it also shows the live chip as its dot - the next step in the same order -
 * since the update icon beside the full chip would push the account menu off a 390px row.
 */
export function headerFit(level: number, phone: boolean, updatePending = false): HeaderFit {
  return {
    updateWord: !phone && level < 1,
    version: !phone && level < 2,
    liveWord: !(phone && updatePending) && level < 3,
    accountName: !phone && level < 4,
    needsYouWord: !phone && level < 5,
    tight: level >= 6,
    mark: level >= 7,
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
