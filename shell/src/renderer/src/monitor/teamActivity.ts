import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { MonitorModel, type ActivityRow, type FeedRow } from './model';
import { readStep, type StepRow } from './steps';
import { timeOf } from './timeline';

/**
 * ONE TEAM'S ACTIVITY, for the team board: the same lines the Monitor's activity list shows (MonitorModel's activity,
 * from monitor.messages and monitor.steps), kept for this team only, and handed out per member: a member's lines are
 * the ones the Monitor shows when that member is picked (what it did, and what it was told).
 *
 * The feeds are the Monitor's, which carry every team: the newest messages at the start and what follows, and the
 * steps of the last StepMinutes; other teams' rows are dropped. Nothing is drawn, so nothing is played live.
 */
export const TeamActivityKeep = 400;
export const StepMinutes = 240;
const StartStepPages = 4;

export function useTeamActivity(api: YawbleApi, team: () => string, pollMs: () => number | undefined) {
  let model = new MonitorModel(TeamActivityKeep);
  const rows = shallowRef<ActivityRow[]>([]);
  const loaded = ref(false);
  const now = ref(Date.now());
  let after = 0;
  let stepAfter = 0;
  let stepsOff = false;
  let busy = false;
  let generation = 0;
  let timer: ReturnType<typeof setInterval> | null = null;

  const mine = (name: string | null | undefined) => !!name && name.toLowerCase() === team().toLowerCase();

  async function messages(first: boolean): Promise<FeedRow[]> {
    const page = await api.call<{ end?: number; messages?: FeedRow[] }>('monitor.messages', { after, limit: first ? 500 : 200 });
    if (!page || !Array.isArray(page.messages)) return [];
    after = Math.max(after, page.end ?? 0);
    return page.messages.filter((r) => mine(r.team));
  }

  async function steps(first: boolean): Promise<StepRow[]> {
    if (stepsOff) return [];
    const got: StepRow[] = [];
    try {
      for (let page = 0; page < (first ? StartStepPages : 2); page++) {
        const res = await api.call<{ end?: number; pending?: boolean; steps?: unknown[] }>('monitor.steps', { after: stepAfter, sinceMinutes: StepMinutes, limit: 3000 });
        if (!res || !Array.isArray(res.steps)) break;
        for (const raw of res.steps) { const s = readStep(raw); if (s && mine(s.team)) got.push(s); }
        stepAfter = Math.max(stepAfter, res.end ?? 0);
        if (!res.pending) break;
      }
    } catch {
      stepsOff = true; // a core without the step feed: messages only
    }
    return got;
  }

  async function tick(first = false): Promise<void> {
    if (busy) return;
    busy = true;
    const gen = generation;
    try {
      const rs = await messages(first);
      const ss = await steps(first);
      if (gen !== generation) return; // the board moved to another team meanwhile
      rs.sort((a, b) => timeOf(a) - timeOf(b));
      ss.sort((a, b) => timeOf(a) - timeOf(b));
      // In the order they happened, so file changes gather and lines stack as on the Monitor.
      let i = 0, j = 0;
      while (i < rs.length || j < ss.length) {
        if (j >= ss.length || (i < rs.length && timeOf(rs[i]!) <= timeOf(ss[j]!))) model.apply(rs[i++]!, 0, false);
        else model.applyStep(ss[j++]!, 0, false);
      }
      if (rs.length || ss.length || first) rows.value = [...model.activity];
    } catch {
      // keeps what it shows; the next poll tries again
    } finally {
      if (gen === generation) { loaded.value = true; now.value = Date.now(); }
      busy = false;
    }
  }

  function reset(): void {
    generation++;
    model = new MonitorModel(TeamActivityKeep);
    rows.value = [];
    loaded.value = false;
    after = 0;
    stepAfter = 0;
    busy = false;
    void tick(true);
  }

  /** A member's lines, newest first: what it did and what it was told (the Monitor's picked-member filter). */
  const byMember = computed(() => {
    const map = new Map<string, ActivityRow[]>();
    for (const r of rows.value) {
      if (!r.member) continue;
      const k = r.member.toLowerCase();
      let list = map.get(k);
      if (!list) map.set(k, (list = []));
      list.push(r);
    }
    return map;
  });
  const of = (member: string): ActivityRow[] => byMember.value.get(member.toLowerCase()) ?? [];

  onMounted(() => {
    void tick(true);
    timer = setInterval(() => void tick(), pollMs() ?? 1500);
  });
  watch(team, reset);
  onBeforeUnmount(() => { if (timer) clearInterval(timer); generation++; });

  return { of, loaded, now };
}

/** How long ago, as the Monitor's activity list says it. */
export function ago(at: string, now: number): string {
  const s = Math.max(0, (now - new Date(at).getTime()) / 1000);
  if (s < 60) return 'now';
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  if (s < 86400) return `${Math.floor(s / 3600)}h`;
  return new Date(at).toLocaleDateString();
}
