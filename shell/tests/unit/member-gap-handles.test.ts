// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import { outputHeight, setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import type { AgentCard, YawbleApi, TeamSummary } from '../../src/shared/api';

// The team board: the 14px gap below each member card is a height handle for every member's output box, the same
// interaction as the Teams list tiles (teams-list-tiles.test.ts). One column, so there are no width handles.
// A fake api only: no core, program, network or port.
const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as TeamSummary;
const mk = (id: number, name: string): AgentCard => ({
  member: { id, teamId: 1, team: 'payments', name, role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'hello', runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
}) as AgentCard;
const cardsData = [mk(7, 'DeveloperMarlo'), mk(8, 'TesterWren')];
const api = {
  getInfo: async () => ({ hostChecks: [] }), getAvailableBackends: async () => [],
  call: async (method: string) => (method === 'teams.agents' ? cardsData : []),
  onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
} as unknown as YawbleApi;

async function board() {
  const w = mount(TeamView, { props: { team, api, pollMs: 0 }, attachTo: document.body });
  await flushPromises();
  return w;
}
const handles = (w: Awaited<ReturnType<typeof board>>) => w.findAll('[data-test=member-handle-h]');
const outs = (w: Awaited<ReturnType<typeof board>>) => w.findAll('[data-test=agent-output]').map((o) => (o.element as HTMLElement).style.height);
const down = (h: any, y = 0) => h.trigger('pointerdown', { clientX: 0, clientY: y, pointerId: 1, button: 0 });
const move = (y: number) => window.dispatchEvent(new PointerEvent('pointermove', { clientX: 0, clientY: y, pointerId: 1 }));

describe('team board: gap handles between member cards', () => {
  beforeEach(() => { setOutputHeight(120); localStorage.clear(); document.body.innerHTML = ''; });

  it('gives every card a height handle outside the card, labelled, and no width handle or old grip', async () => {
    const w = await board();
    expect(handles(w)).toHaveLength(2);
    expect(handles(w)[0]!.attributes('aria-label')).toBe('Resize all member cards: height');
    expect(w.find('[data-test=member-handle-w]').exists()).toBe(false);
    expect(w.find('[data-test=agent-resize]').exists()).toBe(false);
    for (const c of w.findAll('[data-test=agent-DeveloperMarlo]')) expect(c.find('[data-test=member-handle-h]').exists()).toBe(false);
    w.unmount();
  });

  it('dragging a handle resizes every member output live, keeps it on release, and clamps', async () => {
    const w = await board();
    await down(handles(w)[0]!, 300);
    move(360);
    await w.vm.$nextTick();
    expect(outs(w)).toEqual(['180px', '180px']);
    expect(localStorage.getItem('yawble.agentOutput')).toBeNull();
    window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 }));
    expect(outputHeight.value).toBe(180);
    expect(localStorage.getItem('yawble.agentOutput')).toBe('180');
    await down(handles(w)[1]!, 300);
    move(-900);
    window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 }));
    await w.vm.$nextTick();
    expect(outs(w)).toEqual(['48px', '48px']);
    w.unmount();
  });

  it('keyboard: up/down only, 16 px steps, 64 with Shift; left/right do nothing', async () => {
    const w = await board();
    const h = handles(w)[0]!;
    await h.trigger('keydown', { key: 'ArrowDown' });
    expect(outputHeight.value).toBe(136);
    await h.trigger('keydown', { key: 'ArrowUp', shiftKey: true });
    expect(outputHeight.value).toBe(72);
    await h.trigger('keydown', { key: 'ArrowRight' });
    expect(outputHeight.value).toBe(72);
    expect(outs(w)).toEqual(['72px', '72px']);
    w.unmount();
  });

  it('double-click resets to the default and keeps the default', async () => {
    setOutputHeight(300);
    const w = await board();
    await handles(w)[1]!.trigger('dblclick');
    expect(outs(w)).toEqual(['120px', '120px']);
    w.unmount();
  });

  it('pointercancel restores the height and nothing is saved', async () => {
    const w = await board();
    await down(handles(w)[0]!, 300);
    move(380);
    expect(outputHeight.value).toBe(200);
    expect(document.body.classList.contains('resizing-tiles-h')).toBe(true);
    window.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1 }));
    expect(outputHeight.value).toBe(120);
    expect(localStorage.getItem('yawble.agentOutput')).toBeNull();
    move(500);
    expect(outputHeight.value).toBe(120);
    expect(document.body.className).not.toContain('resizing-tiles');
    w.unmount();
  });

  it('the card buttons still work and a handle click does nothing', async () => {
    const w = await board();
    await handles(w)[0]!.trigger('click');
    expect(w.emitted('settings')).toBeUndefined();
    await w.get('[data-test=agent-TesterWren] [data-test=open-settings]').trigger('click');
    expect(w.emitted('settings')![0]).toEqual([8]);
    w.unmount();
  });
});
