import type { ArgGroup } from '../../shared/api';

// Helpers for the agent editor (Settings > Agents). The core's AgentArgsValidator is the authority on what may be added
// or removed (agentProfiles.check / update refuse a clash); the lock reasons come from the core with each group.

/** The model picker's first entry in a member's settings: "Agent default (<model>)" or the CLI's own default. */
export function agentDefaultLabel(defaultModel: string | undefined | null): string {
  return defaultModel ? `Agent default (${defaultModel})` : "Agent default (the CLI's default)";
}

/** What the agent editor starts from: an existing custom agent (with its id) or a new one's prefill. */
export interface AgentDraft { id?: number; name: string; defaultModel: string; argsBefore: string[]; argsAfter: string[]; removedArgs: string[] }

/** A name no agent has yet: "<base> - copy", then "<base> - copy 2", "<base> - copy 3", … (compared without case). */
export function freeCopyName(base: string, taken: readonly string[]): string {
  const names = new Set(taken.map((n) => n.trim().toLowerCase()));
  const first = `${base} - copy`;
  if (!names.has(first.toLowerCase())) return first;
  for (let i = 2; ; i++) {
    const next = `${first} ${i}`;
    if (!names.has(next.toLowerCase())) return next;
  }
}

/** The removedArgs entries that remove this group (the core's IsRemoved rule: its flag, or "flag value"). */
const matches = (g: ArgGroup, r: string) => r === g.tokens[0] || r === g.tokens.join(' ');

/** Whether `removed` removes this built-in group (only an unlocked, non-tail group can be removed). */
export function groupRemoved(g: ArgGroup, removed: readonly string[]): boolean {
  return !g.locked && !g.tail && removed.some((r) => matches(g, r));
}

/** removedArgs with this group removed ("flag value" as one entry) or restored (every entry naming it taken out). */
export function toggleRemoved(g: ArgGroup, removed: readonly string[]): string[] {
  if (groupRemoved(g, removed)) return removed.filter((r) => !matches(g, r));
  return [...removed, g.tokens.join(' ')];
}
