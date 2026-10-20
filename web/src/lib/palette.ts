import { itemLabel } from './backlog';
import { needsActiveWorkTeam, ribbonEntries, type RibbonSpec } from './ribbon';

/**
 * THE COMMAND PALETTE'S LIST AND ITS MATCHING, as pure functions of what the app already holds: the
 * overview's teams and their members, the backlog list, the outcomes and the ribbon spec. Nothing
 * here reads a store or a route, so the matching is tested without a mount; `CommandPalette.vue`
 * gathers the inputs and does what a row says.
 *
 * A row only ever OPENS something - a board, an item, a member's card, a view, a ribbon command, an
 * outcome. None types into the Concierge or sends an instruction.
 */

/** The shortcut, written once: the account menu and the first-run guide show it. */
export const PaletteShortcut = 'Ctrl+K';

export type PaletteView = 'teams' | 'kanban' | 'monitor';

export type PaletteTarget =
  | { kind: 'team'; team: string }
  | { kind: 'item'; id: number }
  | { kind: 'member'; team: string; member: string }
  | { kind: 'view'; view: PaletteView }
  | { kind: 'action'; action: string }
  | { kind: 'outcome'; id: string };

export type PaletteKind = PaletteTarget['kind'];

export interface PaletteEntry {
  /** Unique across the list. */
  key: string;
  target: PaletteTarget;
  /** What the row reads: the team, `team / member`, the item's title, the view, the command, the outcome. */
  label: string;
  /** The muted words after it: what kind of thing it is, and for a command, its group. */
  detail: string;
  icon: string;
  /** An item's citation, built by `itemLabel` from its number. */
  citation?: string;
  /** The state chip: a team's tab chip, an item's state, a proposed outcome. */
  state?: { text: string; token: string } | null;
}

export interface PaletteTeam {
  id: string;
  name: string;
  state?: { text: string; token: string } | null;
  members: ReadonlyArray<{ id: string; name: string }>;
}

export interface PaletteSource {
  teams: readonly PaletteTeam[];
  items: ReadonlyArray<{ id: number; title: string; state: string }>;
  outcomes: ReadonlyArray<{ id: string; name: string; status: string }>;
  ribbon: RibbonSpec;
  /** The team in scope: `team-` commands are offered only with one, as the ribbon gates them. */
  activeWorkTeamId: string | undefined;
  /** The active team's name, said after its commands' group. */
  activeTeamName?: string | undefined;
}

const Views: ReadonlyArray<{ view: PaletteView; label: string; icon: string }> = [
  { view: 'teams', label: 'Teams', icon: 'groups' },
  { view: 'kanban', label: 'Kanban', icon: 'view_kanban' },
  { view: 'monitor', label: 'Monitor', icon: 'hub' },
];

/** The rows, in the order an empty query lists them: views, commands, teams, members, items, outcomes. */
export function paletteEntries(source: PaletteSource): PaletteEntry[] {
  const entries: PaletteEntry[] = [];

  for (const view of Views) {
    entries.push({ key: `view:${view.view}`, target: { kind: 'view', view: view.view }, label: view.label, detail: 'View', icon: view.icon });
  }

  for (const entry of ribbonEntries(source.ribbon)) {
    const { action, label, icon } = entry.item;
    if (entry.item.kind !== 'button' || !action || !label) continue;
    // THE RIBBON'S OWN GATE: a `team-` command with no team in scope is not offered at all.
    if (needsActiveWorkTeam(action, source.activeWorkTeamId)) continue;

    const group = entry.tab.titleFrom === 'active-team' && source.activeTeamName
      ? `${entry.tab.label} · ${source.activeTeamName}`
      : entry.tab.label;
    entries.push({ key: `action:${action}`, target: { kind: 'action', action }, label, detail: group, icon: icon ?? 'search' });
  }

  for (const team of source.teams) {
    entries.push({ key: `team:${team.id}`, target: { kind: 'team', team: team.id }, label: team.name, detail: 'Team', icon: 'groups', state: team.state ?? null });
  }

  for (const team of source.teams) {
    for (const member of team.members) {
      entries.push({
        key: `member:${team.id}/${member.id}`,
        target: { kind: 'member', team: team.id, member: member.id },
        label: `${team.name} / ${member.name}`,
        detail: 'Member',
        icon: 'person',
      });
    }
  }

  for (const item of source.items) {
    entries.push({
      key: `item:${item.id}`,
      target: { kind: 'item', id: item.id },
      label: item.title,
      detail: 'Backlog item',
      icon: 'checklist',
      citation: itemLabel(item.id),
      state: { text: item.state, token: item.state },
    });
  }

  for (const outcome of source.outcomes) {
    entries.push({
      key: `outcome:${outcome.id}`,
      target: { kind: 'outcome', id: outcome.id },
      label: outcome.name,
      detail: 'Outcome',
      icon: 'flag',
      state: outcome.status === 'proposed' ? { text: 'proposed', token: 'proposed' } : null,
    });
  }

  return entries;
}

/** Lower-cased words, split on anything that is not a letter or a digit. */
function wordsOf(text: string): string[] {
  return text.toLowerCase().split(/[^\p{L}\p{N}]+/u).filter((word) => word !== '');
}

/**
 * How well a row answers the query, lower is better, or null for no match.
 *
 * 0 the exact citation; 1 a citation it begins; 2 the exact label; 3 a label it begins; 4 every
 * query word begins a word of the row; 5 every query word is somewhere in the row.
 */
export function paletteScore(entry: PaletteEntry, query: string): number | null {
  const q = query.trim().toLowerCase();
  if (q === '') return 5;

  const citation = entry.citation?.toLowerCase();
  if (citation !== undefined) {
    if (citation === q) return 0;
    if (citation.startsWith(q)) return 1;
  }

  const label = entry.label.toLowerCase();
  if (label === q) return 2;
  if (label.startsWith(q)) return 3;

  const queryWords = wordsOf(q);
  if (queryWords.length === 0) return null;

  const haystack = [entry.label, entry.citation ?? ''].join(' ');
  const rowWords = wordsOf(haystack);
  if (queryWords.every((word) => rowWords.some((rowWord) => rowWord.startsWith(word)))) return 4;

  const flat = haystack.toLowerCase();
  if (queryWords.every((word) => flat.includes(word))) return 5;

  return null;
}

/** The rows a query matches, best first; ties keep the list's own order. At most `limit`. */
export function matchPalette(entries: readonly PaletteEntry[], query: string, limit = 50): PaletteEntry[] {
  return entries
    .map((entry, index) => ({ entry, index, score: paletteScore(entry, query) }))
    .filter((row): row is { entry: PaletteEntry; index: number; score: number } => row.score !== null)
    .sort((left, right) => left.score - right.score || left.index - right.index)
    .slice(0, limit)
    .map((row) => row.entry);
}

/**
 * Whether a key press came from somewhere a person types: a text-like input, a textarea (the
 * Concierge's terminal is one), a select or anything contenteditable. The shortcut never fires there.
 */
export function inTextField(target: EventTarget | null): boolean {
  if (typeof Element === 'undefined' || !(target instanceof Element)) return false;

  const field = target.closest('input, textarea, select, [contenteditable]');
  if (!field) return false;

  if (field instanceof HTMLInputElement) {
    return !['button', 'checkbox', 'radio', 'range', 'reset', 'submit', 'color', 'file', 'image'].includes(field.type);
  }

  if (field.hasAttribute('contenteditable')) return field.getAttribute('contenteditable') !== 'false';

  return true;
}

/** Whether a key press asks for the palette: Ctrl+K or Cmd+K, or a bare `/`, outside a text field. */
export interface PaletteKeyPress {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  target: EventTarget | null;
}

export function opensPalette(event: PaletteKeyPress): boolean {
  if (inTextField(event.target)) return false;

  if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') return true;

  return event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey;
}
