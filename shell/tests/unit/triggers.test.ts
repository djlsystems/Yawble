// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TriggersDialog from '../../src/renderer/src/components/TriggersDialog.vue';
import type { AgentCard, YawbleApi, Trigger, WatchFireOn } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const card = (id: number, name: string, role: 'manager' | 'member'): AgentCard => ({
  member: { id, teamId: 1, team: 'ops', name, role, cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null, runStartedAt: null,
  lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
} as AgentCard);

const team = { id: 1, name: 'ops', repoPath: null, running: 0, queued: 0 } as any;
const nightly: Trigger = {
  id: 7, teamId: 1, memberId: 2, name: 'nightly', instruction: 'Check the build.', onlyWhenIdle: true, enabled: true,
  schedule: { kind: 'every', intervalSec: 86400, cron: null, timezone: null, fireAt: null },
  nextDueAt: '2026-11-07T12:00:00Z', lastFiredAt: null, lastOutcome: 'missed', lastReason: '2 fire(s) passed while the app was not running',
  lastSeq: null, missedCount: 2, wakeManager: 'onHandbackOrFailure',
};

describe('triggers dialog', () => {
  it('lists the team\'s triggers with their schedule, agent and missed fires', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly] });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), card(2, 'Ada', 'member')] } });
    await flushPromises();
    const row = w.find('[data-test=trigger-nightly]').text();
    expect(row).toContain('Ada');
    expect(row).toContain('every 1 day(s)');
    expect(row).toContain('2 missed');
    expect(row).toContain('Manager: only if it hands back or fails');
  });

  it('saves the Manager wake choice the person picks', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [nightly],
      'triggers.preview': () => ({ error: null, next: [] }),
      'triggers.save': (p) => ({ ...nightly, wakeManager: p.wakeManager }),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-edit]').trigger('click');
    expect((w.find('[data-test=trigger-wake-manager]').element as HTMLSelectElement).value).toBe('onHandbackOrFailure');
    await w.find('[data-test=trigger-wake-manager]').setValue('never');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'triggers.save')!.params).toMatchObject({ id: 7, wakeManager: 'never' });
  });

  it('the instruction box spans the form, with its label above it', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [], 'triggers.preview': () => ({ error: null, next: [] }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    const box = w.find('[data-test=trigger-instruction]');
    expect(box.classes()).toContain('wide');
    const label = box.element.parentElement!;
    expect(label.tagName).toBe('LABEL');
    expect(label.parentElement!.classList.contains('form')).toBe(true); // a direct field of the form: stacked, full width
    expect(label.firstChild!.textContent!.trim()).toBe('Instruction'); // the label text comes before the box
  });

  it('a script member needs no instruction and wakes no Manager: the box and the wake choice are hidden', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [],
      'triggers.preview': () => ({ error: null, next: [] }),
      'triggers.save': (p) => ({ ...nightly, id: 9, name: p.name, memberId: p.memberId }),
    });
    const bot = { ...card(5, 'InboxWatcher', 'member'), kind: 'script' } as AgentCard;
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), bot] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    expect(w.find('[data-test=trigger-instruction]').exists()).toBe(true);
    expect(w.find('[data-test=trigger-member]').findAll('option').map((o) => o.text())).toEqual(['Manager (manager)', 'InboxWatcher (script)']);
    await w.find('[data-test=trigger-member]').findAll('option')[1].setSelected();
    expect(w.find('[data-test=trigger-instruction]').exists()).toBe(false);
    expect(w.find('[data-test=trigger-wake-manager]').exists()).toBe(false);
    expect(w.find('[data-test=trigger-script-note]').text()).toContain('InboxWatcher runs its script on each fire');
    await w.find('[data-test=trigger-name]').setValue('inbox poll');
    expect((w.find('[data-test=trigger-save]').element as HTMLButtonElement).disabled).toBe(false);
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'triggers.save')!.params).toMatchObject({ name: 'inbox poll', memberId: 5, instruction: '' });
  });

  it('opened with editId, it goes straight to that trigger form', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly], 'triggers.preview': () => ({ error: null, next: [] }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), card(2, 'Ada', 'member')], editId: nightly.id } });
    await flushPromises();
    expect((w.find('[data-test=trigger-name]').element as HTMLInputElement).value).toBe(nightly.name);
  });

  it('saves a new every-N trigger in seconds, for the chosen agent', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [],
      'triggers.preview': () => ({ error: null, next: ['2026-11-07T13:00:00Z'] }),
      'triggers.save': (p) => ({ ...nightly, id: 8, name: p.name, memberId: p.memberId }),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    await w.find('[data-test=trigger-name]').setValue('hourly sweep');
    await w.find('[data-test=trigger-member]').findAll('option')[1].setSelected(); // Ada
    await w.find('[data-test=trigger-every]').setValue(2);
    await w.find('[data-test=trigger-instruction]').setValue('Sweep the queue.');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'triggers.save')!.params;
    expect(save).toMatchObject({ name: 'hourly sweep', memberId: 2, kind: 'every', intervalSec: 7200, instruction: 'Sweep the queue.', onlyWhenIdle: true, wakeManager: 'onHandbackOrFailure' });
    expect(w.find('[data-test=triggers-note]').text()).toContain('Saved hourly sweep');
  });

  // Folder > Fire on.
  const folder = (fireOn?: WatchFireOn): Trigger => ({
    ...nightly, id: 9, name: 'inbox',
    schedule: { kind: 'folder', intervalSec: 30, cron: null, timezone: null, fireAt: null },
    watch: { root: 'documents', path: 'inbox', glob: '*.pdf', quietSec: 10, ...(fireOn === undefined ? {} : { fireOn }) },
  });
  const fireOnSelect = (w: ReturnType<typeof mount>) => w.find('[data-test=trigger-watch-fire-on]').element as HTMLSelectElement;

  it('a new folder trigger fires on Added files by default and sends fireOn on save', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [],
      'triggers.preview': () => ({ error: null, next: [] }),
      'triggers.save': (p) => ({ ...folder(p.fireOn), name: p.name }),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager')] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    await w.find('[data-test=trigger-kind-folder]').setValue(true);
    expect(fireOnSelect(w).value).toBe('added');
    expect(Array.from(fireOnSelect(w).options).map((o) => [o.value, o.text])).toEqual([
      ['added', 'Added files'], ['added-or-changed', 'Added or changed'], ['any', 'Any change']]);
    await w.find('[data-test=trigger-name]').setValue('inbox');
    await w.find('[data-test=trigger-watch-path]').setValue('inbox');
    await w.find('[data-test=trigger-instruction]').setValue('File the scans.');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'triggers.save')!.params).toMatchObject({ kind: 'folder', watchPath: 'inbox', fireOn: 'added' });
  });

  it('a new trigger is back to Added files after editing one with another value', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [folder('any')], 'triggers.preview': () => ({ error: null, next: [] }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager')] } });
    await flushPromises();
    await w.find('[data-test=trigger-edit]').trigger('click');
    expect(fireOnSelect(w).value).toBe('any');
    await w.find('[data-test=trigger-cancel]').trigger('click');
    await w.find('[data-test=trigger-new]').trigger('click');
    await w.find('[data-test=trigger-kind-folder]').setValue(true);
    expect(fireOnSelect(w).value).toBe('added');
  });

  it('a saved Fire on loads on edit, and the changed value is what is saved', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [folder('added-or-changed')],
      'triggers.preview': () => ({ error: null, next: [] }),
      'triggers.save': (p) => folder(p.fireOn),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-edit]').trigger('click');
    expect(fireOnSelect(w).value).toBe('added-or-changed');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'triggers.save')[0].params).toMatchObject({ id: 9, fireOn: 'added-or-changed' });
    await w.find('[data-test=trigger-edit]').trigger('click');
    await w.find('[data-test=trigger-watch-fire-on]').setValue('any');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'triggers.save')[1].params).toMatchObject({ id: 9, fireOn: 'any' });
  });

  it('an older folder trigger without fireOn shows and edits as Any change', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [folder()], 'triggers.preview': () => ({ error: null, next: [] }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    expect(w.find('[data-test=trigger-inbox] [data-test=trigger-fire-on]').text()).toBe('Fire on: Any change');
    await w.find('[data-test=trigger-edit]').trigger('click');
    expect(fireOnSelect(w).value).toBe('any');
  });

  it('the list shows Fire on for folder triggers only, read-only in a remote (phone/cloud) view too', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly, folder('added'), { ...folder('added-or-changed'), id: 10, name: 'scans' }] });
    (api as any).remote = true;
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    expect(w.find('[data-test=trigger-nightly] [data-test=trigger-fire-on]').exists()).toBe(false);
    expect(w.find('[data-test=trigger-inbox] [data-test=trigger-fire-on]').text()).toBe('Fire on: Added files');
    expect(w.find('[data-test=trigger-scans] [data-test=trigger-fire-on]').text()).toBe('Fire on: Added or changed');
    expect(w.find('[data-test=trigger-scans] select').exists()).toBe(false);
  });

  it('Run now says what happened', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly], 'triggers.runNow': () => ({ outcome: 'skipped', seq: null, reason: 'Ada was busy (only when idle)' }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-run-now]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=triggers-note]').text()).toContain('skipped (Ada was busy');
  });
});
