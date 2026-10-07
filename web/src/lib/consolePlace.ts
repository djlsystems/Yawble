import type { LocationQuery, LocationQueryRaw } from 'vue-router';

/**
 * WHERE A PERSON IS IN THE CONSOLE, as the address says it: the Teams table, one team's board, or
 * the Kanban. The address carries it so the browser's Back and Forward move between them, and a
 * copied address opens the same view. Nothing else about the Console is in it.
 */
export type ConsolePlace =
  | { kind: 'teams' }
  | { kind: 'kanban' }
  | { kind: 'team'; team: string };

/** The two keys this module owns. Every other key in the query (a connection's callback) is left. */
const keys = ['view', 'team'] as const;

/** The place the store is in, or null when it is in none worth an address (a board with no team). */
export function placeOf(view: string, activeTeamId: string): ConsolePlace | null {
  if (view === 'teams') return { kind: 'teams' };
  if (view === 'kanban') return { kind: 'kanban' };
  return activeTeamId ? { kind: 'team', team: activeTeamId } : null;
}

/** The place an address names, or null when it names none. */
export function placeFromQuery(query: LocationQuery): ConsolePlace | null {
  const one = (value: LocationQuery[string] | undefined) => (Array.isArray(value) ? value[0] : value) ?? null;
  const team = one(query.team);
  if (team) return { kind: 'team', team };
  const view = one(query.view);
  if (view === 'teams') return { kind: 'teams' };
  if (view === 'kanban') return { kind: 'kanban' };
  return null;
}

/** The query for a place, keeping every key this module does not own. */
export function queryFor(place: ConsolePlace, current: LocationQuery): LocationQueryRaw {
  const rest: LocationQueryRaw = { ...current };
  for (const key of keys) delete rest[key];
  return place.kind === 'team' ? { ...rest, team: place.team } : { ...rest, view: place.kind };
}

export function samePlace(a: ConsolePlace | null, b: ConsolePlace | null): boolean {
  if (a === null || b === null) return a === b;
  if (a.kind !== b.kind) return false;
  return a.kind !== 'team' || a.team === (b as { team: string }).team;
}
