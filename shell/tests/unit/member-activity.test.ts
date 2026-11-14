// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import { MonitorModel } from '../../src/renderer/src/monitor/model';
import { ACTIVITY_DEFAULT, ACTIVITY_MIN, activityWidth, setActivityWidth } from '../../src/renderer/src/memberActivityWidth';
import type { AgentCard, YawbleApi, TeamSummary } from '../../src/shared/api';

// The team board: beside each member's card, that member's activity, the same lines the Monitor's activity list shows
// for it (what it did and what it was told), without the face, other teams' and other members' lines left out.
// A fake api only: no core, program, network or port.
const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as TeamSummary;
const mk = (id: number, name: string): AgentCard => ({
  member: { id, teamId: 1, team: 'payments', name, role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'hello', runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
}) as AgentCard;
const at = (msAgo: number) => new Date(Date.now() - msAgo).toISOString();
const row = (seq: number, kind: string, teamName: string, member: string | null, from: string, text: string | null, ago: number) =>
  ({ seq, kind, team: teamName, member, from, text, workflow: 1, at: at(ago) });

function fakeApi(opts: { messages?: unknown; steps?: unknown } = {}) {
  const calls: string[] = [];
  const api = {
    getInfo: async () => ({ hostChecks: [] }), getAvailableBackends: async () => [],
    call: async (method: string, params?: { after?: number }) => {
      calls.push(method);
      if (method === 'teams.agents') return [mk(7, 'DeveloperMarlo'), mk(8, 'TesterWren')];
      if (method === 'monitor.messages') {
        if ((params?.after ?? 0) > 0) return { end: 9, messages: [] };
        return opts.messages ?? {
          end: 9, messages: [
            row(1, 'instruction', 'payments', 'DeveloperMarlo', 'Manager', 'Build the parser', 300_000),
            row(2, 'progress', 'payments', 'DeveloperMarlo', 'DeveloperMarlo', 'parser half done', 200_000),
            row(3, 'completed', 'payments', 'TesterWren', 'TesterWren', 'tests pass', 100_000),
            row(4, 'progress', 'other team', 'DeveloperMarlo', 'DeveloperMarlo', 'not this team', 50_000),
            row(5, 'done', 'payments', null, 'Manager', 'all done', 10_000),
          ],
        };
      }
      if (method === 'monitor.steps') return opts.steps ?? {
        end: 2, pending: false, steps: [
          { id: 1, at: at(150_000), team: 'payments', member: 'DeveloperMarlo', run: 1, kind: 'edit', tool: 'Edit', target: 'src/parser.ts' },
          { id: 2, at: at(140_000), team: 'other team', member: 'DeveloperMarlo', run: 2, kind: 'edit', tool: 'Edit', target: 'src/other.ts' },
        ],
      };
      return [];
    },
    onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls };
}

async function board(api: YawbleApi) {
  const w = mount(TeamView, { props: { team, api, pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  await flushPromises();
  return w;
}
const tile = (w: Awaited<ReturnType<typeof board>>, member: string) =>
  w.find(`[data-test=agent-${member}]`).element.parentElement!.querySelector('[data-test=member-activity]')!;
const lines = (el: Element) => [...el.querySelectorAll('[data-test=member-activity-row]')].map((r) => r.textContent!.replace(/\s+/g, ' ').trim());

describe('team board: each member\'s activity beside its card', () => {
  beforeEach(() => { localStorage.clear(); document.body.innerHTML = ''; });

  it('puts an activity tile to the right of every member card, in the same row', async () => {
    const w = await board(fakeApi().api);
    const rows = w.findAll('.member-row');
    expect(rows).toHaveLength(2);
    for (const r of rows) {
      const kids = [...r.element.children];
      expect(kids[0]!.getAttribute('data-test')).toMatch(/^agent-/);
      expect(kids[1]!.classList.contains('member-side')).toBe(true);
      expect(kids[1]!.querySelector('[data-test=member-activity]')).not.toBeNull();
      expect(kids[1]!.querySelector('[data-test=member-activity-handle-w]')).not.toBeNull();
    }
    w.unmount();
  });

  it('a bar between card and activity resizes every member\'s activity together: right is narrower, kept on release, double-click resets', async () => {
    setActivityWidth(ACTIVITY_DEFAULT);
    localStorage.clear();
    const w = await board(fakeApi().api);
    const widths = () => w.findAll('.member-side').map((x) => (x.element as HTMLElement).style.flexBasis);
    expect(widths()).toEqual(['360px', '360px']);
    const bar = w.findAll('[data-test=member-activity-handle-w]')[1]!;
    await bar.trigger('pointerdown', { clientX: 500, clientY: 0, pointerId: 1, button: 0 });
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: 440, clientY: 0, pointerId: 1 }));
    await w.vm.$nextTick();
    expect(widths()).toEqual(['420px', '420px']); // dragged left: wider, every member at once
    expect(localStorage.getItem('yawble.memberActivityWidth')).toBeNull();
    window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 }));
    expect(activityWidth.value).toBe(420);
    expect(localStorage.getItem('yawble.memberActivityWidth')).toBe('420');
    await bar.trigger('keydown', { key: 'ArrowRight' });
    expect(activityWidth.value).toBe(404); // the bar moves right: narrower
    await bar.trigger('pointerdown', { clientX: 0, clientY: 0, pointerId: 2, button: 0 });
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: 5000, clientY: 0, pointerId: 2 }));
    window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 2 }));
    expect(activityWidth.value).toBe(ACTIVITY_MIN); // clamped
    await bar.trigger('dblclick');
    expect(activityWidth.value).toBe(ACTIVITY_DEFAULT);
    w.unmount();
  });

  it('shows the member\'s own lines newest first: told, progress and its file changes; not other teams\' or members\'', async () => {
    const w = await board(fakeApi().api);
    const marlo = lines(tile(w, 'DeveloperMarlo'));
    expect(marlo).toHaveLength(3);
    expect(marlo[0]).toMatch(/^edited files · Developer Marlo · \d+m ?parser\.ts$/);
    expect(marlo[1]).toMatch(/^progress · Developer Marlo · \d+m ?parser half done$/);
    expect(marlo[2]).toMatch(/^Manager told Developer Marlo · \d+m ?Build the parser$/);
    expect(marlo.join('\n')).not.toContain('not this team');
    expect(marlo.join('\n')).not.toContain('other.ts');
    const wren = lines(tile(w, 'TesterWren'));
    expect(wren).toEqual([expect.stringMatching(/^finished a run · Tester Wren · \d+m ?tests pass$/)]);
    w.unmount();
  });

  it('draws no face on its lines, in the light look', async () => {
    const w = await board(fakeApi().api);
    const el = tile(w, 'DeveloperMarlo');
    expect(el.querySelector('.avatar, [data-test=member-avatar], canvas, svg')).toBeNull();
    w.unmount();
  });

  it('says Nothing yet for a member with no lines, and copes with a core that has no step feed', async () => {
    const { api } = fakeApi({ messages: { end: 0, messages: [] }, steps: null });
    const w = await board(api);
    expect(tile(w, 'TesterWren').querySelector('[data-test=member-activity-empty]')?.textContent).toBe('Nothing yet.');
    w.unmount();
  });
});

describe('MonitorModel keep', () => {
  it('holds as many activity lines as it is given (the Monitor\'s 80 by default)', () => {
    const big = new MonitorModel(400);
    const small = new MonitorModel();
    for (let i = 1; i <= 120; i++) {
      const r = row(i, 'progress', 'payments', 'DeveloperMarlo', 'DeveloperMarlo', `n${i}`, 0) as never;
      big.apply(r, 0, false);
      small.apply(r, 0, false);
    }
    expect(big.activity).toHaveLength(120);
    expect(small.activity).toHaveLength(80);
  });
});
