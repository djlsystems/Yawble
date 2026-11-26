import {
  GOALS_ASSIGN, GOALS_DELETE, GOALS_LIST, GOALS_SAVE, GoalNameMax,
  type Goal, type GoalAssignAnswer, type GoalDeleteAnswer, type GoalsListAnswer, type GoalTime, type YawbleApi,
} from '../../../shared/api';

// The Goals page's calls and pure parts. The core (goals.list / goals.save / goals.delete / goals.assign) keeps the goals
// and computes each goal's time; nothing here works a time out, it only counts an open one on from the answer.

type Caller = Pick<YawbleApi, 'call'>;

export const goalsApi = (api: Caller) => ({
  list: () => api.call<GoalsListAnswer>(GOALS_LIST, {}),
  save: (g: { id?: number | null; name: string; description?: string }) =>
    api.call<Goal>(GOALS_SAVE, { ...(g.id != null ? { id: g.id } : {}), name: g.name.trim(), description: (g.description ?? '').trim() }),
  remove: (id: number) => api.call<GoalDeleteAnswer>(GOALS_DELETE, { id }),
  assign: (deliverable: number, goal: number | null) => api.call<GoalAssignAnswer>(GOALS_ASSIGN, { deliverable, goal }),
});

/** What is wrong with a name before it is sent (the core says the same and more, e.g. a name already taken); null when fine. */
export function nameProblem(name: string, others: readonly Pick<Goal, 'id' | 'name'>[] = [], id: number | null = null): string | null {
  const n = name.trim();
  if (!n) return 'Give the goal a name.';
  if (n.length > GoalNameMax) return `A goal's name is at most ${GoalNameMax} characters.`;
  // The same name ignoring case for every letter (é and É too): the rule the core checks with (GoalRules.SameName).
  if (others.some((g) => g.id !== id && g.name.trim().toLowerCase() === n.toLowerCase())) return `There is already a goal named "${n}".`;
  return null;
}

/** The elapsed time to show now: the core's elapsedMs, plus (while a deliverable is open: endAt null) the time since its answer's now. */
export function liveElapsedMs(t: GoalTime | null | undefined, answerNow: string | null | undefined, now: number): number {
  const base = Math.max(0, t?.elapsedMs ?? 0);
  if (!t || t.endAt !== null || !t.firstAt) return base;
  const at = answerNow ? Date.parse(answerNow) : NaN;
  return Number.isFinite(at) ? base + Math.max(0, now - at) : base;
}

/** "3 deliverables, 1 open" / "1 deliverable, none open" / "No deliverables". */
export function countsLine(total: number, open: number): string {
  if (!total) return 'No deliverables';
  return `${total} deliverable${total === 1 ? '' : 's'}, ${open ? `${open} open` : 'none open'}`;
}
