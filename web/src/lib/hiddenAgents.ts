import type { Agent } from '../api/types'

/**
 * The Agents a person may be offered.
 *
 * ONE function rather than a `.filter()` written out in each of four screens, so hiding cannot be
 * half-applied - the same reason `progress.ts` and `reset.ts` exist rather than a `v-if` per card.
 *
 * FOR RENDERING ONLY, and that restriction is the whole risk of this feature rather than a style
 * note. `PUT /api/agents` REPLACES the catalog wholesale, and `AgentsDialog.saveAgent` composes its
 * submission from the loaded list - `[...agents.value, next]` on a create, `agents.value.map(...)`
 * on an edit. So filtering the ref that BACKS that submission, rather than filtering at render,
 * permanently deletes every hidden preset on the next save of anything at all.
 *
 * It is the same hazard as `AgentEditDialog` rebuilding a `launch` object from its own inputs and
 * dropping a field it does not know. A TypeScript interface is compile-time only, so nothing about
 * the type of what is submitted would catch either.
 *
 * An ABSENT flag is visible, which is every catalog written before the field existed - hence
 * `!a.hidden` rather than `a.hidden === false`.
 */
export const visibleAgents = (agents: readonly Agent[]): Agent[] => agents.filter((a) => !a.hidden)
