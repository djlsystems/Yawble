// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { ENV_HIDDEN, ENV_HIDDEN_HINT, ENV_UNREADABLE, ENV_UNREADABLE_HINT, envEntries, envRows } from '../../src/renderer/src/teamEnv';

// Team settings > Environment over the Cloud Link: the computer sends each name with the hidden marker in place of
// its value. The view shows "set on the computer", and a save sends the marker back for a value it never saw, so
// the computer keeps the real one. A value the computer cannot read (saved on another computer) arrives with the
// unreadable marker: the view asks for it again, and a save leaves it as it is until a new value is typed. Fakes only.

const team = { id: 4, name: 'payments', members: 1, running: 0, queued: 0, repoPath: null } as unknown as TeamSummary;

function fakeApi(env: Record<string, string>) {
  const calls: { method: string; params: any }[] = [];
  const answers: Record<string, (p: any) => unknown> = {
    'teams.quiet': () => ({ quiet: true, working: [], openDeliverables: [] }), 'teams.env': () => env, 'teams.setEnv': () => env, 'teams.agents': () => [],
    'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind: null }),
    'teams.repos': () => ({ main: null, extras: [] }), 'teams.deliverableExpiry': () => ({ days: 3 }), 'teams.localhost': () => ({ on: false }),
    'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
  };
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}

async function openEnv(api: YawbleApi) {
  const w = mount(TeamManageDialog, { props: { team, api },
    global: { stubs: { IntegrationDialog: true, TeamNetworkAllows: true, Modal: { template: '<div><slot /></div>' } } } });
  await flushPromises();
  await w.get('[data-test=manage-tab-env]').trigger('click');
  await flushPromises();
  return w;
}

const input = (w: Awaited<ReturnType<typeof openEnv>>, test: string) => w.get(`[data-test=${test}]`).element as HTMLInputElement;

describe('team environment values hidden from a Cloud Link viewer', () => {
  it('a hidden value is an empty box marked hidden; a real value is itself', () => {
    expect(envRows({ API_BASE: ENV_HIDDEN, PLAIN: 'x' })).toEqual([
      { name: 'API_BASE', value: '', hidden: true },
      { name: 'PLAIN', value: 'x', hidden: false },
    ]);
  });

  it('an untouched hidden row is saved as the marker, a typed value replaces it, and rows with no name are left out', () => {
    expect(envEntries([
      { name: 'KEPT', value: '', hidden: true },
      { name: 'REPLACED', value: 'new', hidden: true },
      { name: 'EMPTY', value: '', hidden: false },
      { name: '  ', value: 'x', hidden: false },
    ])).toEqual([
      { name: 'KEPT', value: ENV_HIDDEN },
      { name: 'REPLACED', value: 'new' },
      { name: 'EMPTY', value: '' },
    ]);
  });

  it('the dialog shows "set on the computer" instead of a value, and Save cannot overwrite the value with the marker text as typed', async () => {
    const { api, calls } = fakeApi({ REGISTRY_KEY: ENV_HIDDEN, API_BASE: ENV_HIDDEN });
    const w = await openEnv(api);
    expect(input(w, 'env-name-0').value).toBe('REGISTRY_KEY');
    expect(input(w, 'env-value-0').value).toBe('');
    expect(input(w, 'env-value-0').placeholder).toBe(ENV_HIDDEN_HINT);
    expect(w.get('[data-test=env-rows]').html()).not.toContain(ENV_HIDDEN);

    await w.get('[data-test=env-value-1]').setValue('http://localhost:5000');
    await w.get('[data-test=env-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'teams.setEnv')!;
    expect(save.params.entries).toEqual([
      { name: 'REGISTRY_KEY', value: ENV_HIDDEN }, // the computer keeps the value it has
      { name: 'API_BASE', value: 'http://localhost:5000' },
    ]);
  });

  it('a value the computer cannot read is an empty box that asks for it again, with a note; saved untouched it stays as it is', async () => {
    expect(envRows({ LOST: ENV_UNREADABLE })).toEqual([{ name: 'LOST', value: '', hidden: false, unreadable: true }]);
    const { api, calls } = fakeApi({ LOST: ENV_UNREADABLE, ALSO_LOST: ENV_UNREADABLE, API_BASE: 'http://localhost:5000' });
    const w = await openEnv(api);
    expect(input(w, 'env-value-0').value).toBe('');
    expect(input(w, 'env-value-0').placeholder).toBe(ENV_UNREADABLE_HINT);
    expect(w.get('[data-test=env-rows]').html()).not.toContain(ENV_UNREADABLE);
    expect(w.get('[data-test=env-unreadable]').text()).toContain('Enter them again');

    await w.get('[data-test=env-value-0]').setValue('entered-again');
    expect(w.find('[data-test=env-unreadable]').exists()).toBe(true); // one is still missing
    await w.get('[data-test=env-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'teams.setEnv')!.params.entries).toEqual([
      { name: 'LOST', value: 'entered-again' },
      { name: 'ALSO_LOST', value: ENV_UNREADABLE }, // the computer leaves it as it is
      { name: 'API_BASE', value: 'http://localhost:5000' },
    ]);
  });

  it('with every value readable there is no note', async () => {
    const w = await openEnv(fakeApi({ API_BASE: 'http://localhost:5000' }).api);
    expect(w.find('[data-test=env-unreadable]').exists()).toBe(false);
  });

  it('in the window on the computer the values are shown and saved as they are', async () => {
    const { api, calls } = fakeApi({ API_BASE: 'http://localhost:5000' });
    const w = await openEnv(api);
    expect(input(w, 'env-value-0').value).toBe('http://localhost:5000');
    expect(input(w, 'env-value-0').placeholder).toBe('value');
    await w.get('[data-test=env-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'teams.setEnv')!.params.entries).toEqual([{ name: 'API_BASE', value: 'http://localhost:5000' }]);
  });
});
