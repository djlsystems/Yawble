// @vitest-environment happy-dom
//
// ADMIN → AGENTS, AN UPDATE IN THE GATE. The update route answers at once, and the row reads the
// Host's gate: "Waiting for N <command> runs to finish" naming each run's team and member, then
// "Updating…", then its outcome. The update lives in the gate, not in the dialog, so closing and
// opening again shows the same state; a waiting update can be cancelled, a running one cannot. A
// launch held behind it shows on its card as "waiting for the <command> update".
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { listCatalog, getAgentAuth, getAgentTools, getTenantSettings, updateAgentCli, listAgentUpdates, cancelAgentUpdate, notify } =
  vi.hoisted(() => ({
    notify: vi.fn(),
    listCatalog: vi.fn(),
    getAgentAuth: vi.fn(),
    getAgentTools: vi.fn(),
    getTenantSettings: vi.fn(),
    updateAgentCli: vi.fn(),
    listAgentUpdates: vi.fn(),
    cancelAgentUpdate: vi.fn(),
  }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
  updateAgentCli,
  listAgentUpdates,
  cancelAgentUpdate,
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

import AgentsDialog from '../AgentsDialog.vue';
import KanbanCard from '../KanbanCard.vue';
import type { Agent, AgentUpdateState } from '../../api/types';
import type { KanbanCard as Card } from '../../api/kanban';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { stamp } from '../../lib/agentVersions';

const preset = (name: string, fileName: string): Agent => ({
  name,
  mode: 'Headless',
  builtIn: true,
  launch: { fileName, arguments: [] },
  updates: { update: [fileName, 'update'] },
  tags: [],
  tagsFromOperator: false,
  buildTags: [],
});

const state = (over: Partial<AgentUpdateState>): AgentUpdateState => ({
  command: 'claude',
  phase: 'waiting',
  agent: 'claude-headless',
  requestedBy: 'quinn@example.test',
  requestedAt: '2026-09-30T09:37:00Z',
  running: 0,
  inFlight: [],
  held: [],
  startedAt: null,
  finishedAt: null,
  result: null,
  error: null,
  cancelledBy: null,
  ...over,
});

const waiting = state({
  running: 2,
  inFlight: [
    { team: 'b001m-runs-and-wakes', member: 'DeveloperAda' },
    { team: 'alpha', member: 'Worker' },
  ],
  held: [{ team: 'beta', member: 'Builder' }],
});

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [preset('claude-headless', 'claude'), preset('grok-headless', 'grok')],
    cliVersions: [],
  });
  getAgentAuth.mockReset();
  getAgentAuth.mockResolvedValue([]);
  getAgentTools.mockReset();
  getAgentTools.mockResolvedValue({ at: null, running: false, presets: [] });
  getTenantSettings.mockReset();
  getTenantSettings.mockResolvedValue({ settings: [], roots: [] });
  updateAgentCli.mockReset();
  listAgentUpdates.mockReset();
  listAgentUpdates.mockResolvedValue([]);
  cancelAgentUpdate.mockReset();
  notify.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

function row(name: string): HTMLElement {
  const found = [...document.body.querySelectorAll<HTMLElement>('.agent-tile')]
    .find((item) => item.querySelector('.mono')?.textContent?.trim().split(/\s+/)[0] === name);
  if (!found) throw new Error(`no row for ${name}`);
  return found;
}

const updateLine = (name: string) => row(name).querySelector<HTMLElement>('.agent-version-outcome');
const cancelButton = (name: string) => row(name).querySelector<HTMLElement>('.agent-update-cancel');
const updateButton = (name: string) =>
  row(name).querySelector<HTMLButtonElement>(`[aria-label="Update the CLI ${name} runs"]`)!;

describe('AgentsDialog, an update waiting in the gate', () => {
  it('shows a waiting update with the team and member of each run, and the same after closing and opening', async () => {
    listAgentUpdates.mockResolvedValue([waiting]);

    const wrapper = await mountDialog(AgentsDialog);

    const line = updateLine('claude-headless')!;
    expect(line.dataset.phase).toBe('waiting');
    expect(line.textContent).toContain(
      'Waiting for 2 claude runs to finish: b001m-runs-and-wakes / DeveloperAda, alpha / Worker.',
    );
    expect(row('claude-headless').querySelector('.agent-update-held')?.textContent).toContain(
      '1 launch held until it is done: beta / Builder',
    );
    expect(cancelButton('claude-headless')).not.toBeNull();
    // It is already asked for: the button cannot ask again.
    expect(updateButton('claude-headless').disabled).toBe(true);
    // Another CLI's row says nothing.
    expect(updateLine('grok-headless')).toBeNull();

    // Closed and opened again: read from the Host, the same state.
    await wrapper.setProps({ modelValue: false });
    await flushPromises();
    await wrapper.setProps({ modelValue: true });
    await flushPromises();

    expect(listAgentUpdates).toHaveBeenCalledTimes(2);
    expect(updateLine('claude-headless')!.textContent).toContain(
      'Waiting for 2 claude runs to finish: b001m-runs-and-wakes / DeveloperAda, alpha / Worker.',
    );

    wrapper.unmount();
  });

  it('asks at once, then polls the gate through updating to the outcome', async () => {
    const wrapper = await mountDialog(AgentsDialog);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });

    updateAgentCli.mockResolvedValue(state({ running: 1, inFlight: [{ team: 'alpha', member: 'Worker' }] }));
    updateButton('claude-headless').click();
    await flushPromises();

    expect(updateAgentCli).toHaveBeenCalledWith('claude-headless');
    expect(updateLine('claude-headless')!.textContent).toContain('Waiting for 1 claude run to finish: alpha / Worker.');
    expect(notify).not.toHaveBeenCalled();

    // The run ends: the gate says updating, and a running update offers no cancel.
    listAgentUpdates.mockResolvedValue([state({ phase: 'updating', startedAt: '2026-09-30T09:52:00Z' })]);
    await vi.advanceTimersByTimeAsync(2000);
    await flushPromises();

    expect(updateLine('claude-headless')!.dataset.phase).toBe('updating');
    expect(updateLine('claude-headless')!.textContent).toContain('Updating…');
    expect(cancelButton('claude-headless')).toBeNull();

    // Done: the outcome says what was measured, and the server's sentence is announced verbatim.
    const result = {
      agent: 'claude-headless',
      command: 'claude',
      updated: true,
      exitCode: 0,
      versionBefore: '2.1.285 (Claude Code)',
      versionAfter: '2.1.290 (Claude Code)',
      at: '2026-09-30T09:53:00Z',
      detail: '`claude update` updated claude from 2.1.285 to 2.1.290.',
      cliVersion: null,
    };
    listAgentUpdates.mockResolvedValue([state({ phase: 'done', finishedAt: result.at, result })]);
    await vi.advanceTimersByTimeAsync(2000);
    await flushPromises();

    expect(updateLine('claude-headless')!.textContent).toContain(
      `Updated at ${stamp(result.at)}: 2.1.285 (Claude Code) → 2.1.290 (Claude Code).`,
    );
    expect(notify).toHaveBeenCalledWith({ type: 'positive', message: result.detail });
    expect(updateButton('claude-headless').disabled).toBe(false);

    // Out of the gate: polling stops.
    const reads = listAgentUpdates.mock.calls.length;
    await vi.advanceTimersByTimeAsync(6000);
    expect(listAgentUpdates.mock.calls.length).toBe(reads);

    wrapper.unmount();
  });

  it('cancels a waiting update and says nothing was run', async () => {
    listAgentUpdates.mockResolvedValue([waiting]);
    cancelAgentUpdate.mockResolvedValue(
      state({
        phase: 'cancelled',
        running: 2,
        inFlight: waiting.inFlight,
        held: [],
        finishedAt: '2026-09-30T09:40:00Z',
        cancelledBy: 'quinn@example.test',
      }),
    );

    const wrapper = await mountDialog(AgentsDialog);

    cancelButton('claude-headless')!.click();
    await flushPromises();

    expect(cancelAgentUpdate).toHaveBeenCalledWith('claude-headless');
    const line = updateLine('claude-headless')!;
    expect(line.dataset.phase).toBe('cancelled');
    expect(line.textContent).toContain(
      `The update was cancelled at ${stamp('2026-09-30T09:40:00Z')} by quinn@example.test while it waited; nothing was run.`,
    );
    expect(cancelButton('claude-headless')).toBeNull();
    expect(row('claude-headless').querySelector('.agent-update-held')).toBeNull();
    expect(updateButton('claude-headless').disabled).toBe(false);

    wrapper.unmount();
  });

  it('says the Host\'s sentence when a cancel comes too late, and shows the update running', async () => {
    listAgentUpdates.mockResolvedValue([waiting]);
    cancelAgentUpdate.mockRejectedValue(
      new Error('The claude update is already running, so it cannot be cancelled; it finishes on its own.'),
    );

    const wrapper = await mountDialog(AgentsDialog);
    listAgentUpdates.mockResolvedValue([state({ phase: 'updating' })]);

    cancelButton('claude-headless')!.click();
    await flushPromises();

    expect(notify).toHaveBeenCalledWith({
      type: 'negative',
      message: 'The claude update is already running, so it cannot be cancelled; it finishes on its own.',
    });
    expect(updateLine('claude-headless')!.textContent).toContain('Updating…');

    wrapper.unmount();
  });
});

describe('a card held behind an update', () => {
  const card: Card = {
    id: '3512',
    workflowSeq: 3509,
    team: 'beta',
    member: 'Builder',
    item: null,
    title: 'Build it',
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-30T09:00:00Z',
    updatedAt: '2026-09-30T09:00:00Z',
    awaitingManager: false,
    paused: false,
  } as Card;

  it('says it is waiting for the update, and not that anything failed', () => {
    const wrapper = mount(KanbanCard, { props: { card, updateWait: 'claude' } });
    const badge = wrapper.element.querySelector('.k-card-update-wait');

    expect(badge?.textContent).toContain('waiting for the claude update');
    expect((wrapper.element as HTMLElement).dataset.updateWait).toBe('claude');
    expect(wrapper.element.textContent).not.toMatch(/fail/i);

    wrapper.unmount();
  });

  it('carries no mark when nothing holds it', () => {
    const wrapper = mount(KanbanCard, { props: { card, updateWait: null } });

    expect(wrapper.element.querySelector('.k-card-update-wait')).toBeNull();

    wrapper.unmount();
  });
});
