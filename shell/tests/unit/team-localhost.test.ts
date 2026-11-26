// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import { TEAM_LOCALHOST, TEAM_LOCALHOST_LABEL, TEAM_LOCALHOST_WARNING } from '../../src/shared/api';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';

// Team settings > Network has "Allow localhost (macOS)" (teams.localhost, off by default) with the
// warning word for word. A Visitor sees it read-only (the core refuses a Visitor's set anyway). Fakes only.

const WARNING = "Agents on this team can open local ports and connect to any service listening on this Mac's localhost (your dev servers, local databases). Needed for classic `dotnet test` (VSTest) and some other test runners. While it is on, a server an agent starts on `0.0.0.0` can be reached from other machines on your network unless the macOS firewall blocks it. Keep the macOS firewall on. Outbound traffic still goes only through the gateway.";
const team = { id: 4, name: 'payments', members: 1, running: 0, queued: 0, repoPath: null } as unknown as TeamSummary;

function fakeApi(localhost: { on: boolean } | null) {
  const calls: { method: string; params: any }[] = [];
  const answers: Record<string, (p: any) => unknown> = {
    'teams.quiet': () => ({ quiet: true, working: [], openDeliverables: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
    'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind: null }),
    'teams.repos': () => ({ main: null, extras: [] }), 'teams.deliverableExpiry': () => ({ days: 3 }),
    'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
  };
  if (localhost) answers[TEAM_LOCALHOST] = (p: any) => { if (p.on !== undefined) localhost.on = p.on; return { on: localhost.on }; };
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

async function openNetwork(api: YawbleApi) {
  const w = mount(TeamManageDialog, { props: { team, api },
    global: { stubs: { IntegrationDialog: true, TeamNetworkAllows: true, Modal: { template: '<div><slot /></div>' } } } });
  await flushPromises();
  await w.get('[data-test=manage-tab-network]').trigger('click');
  await flushPromises();
  return w;
}

const toggle = (w: Awaited<ReturnType<typeof openNetwork>>) => w.get('[data-test=team-localhost-toggle]').element as HTMLInputElement;

describe('Team settings: Allow localhost (macOS)', () => {
  afterEach(() => applyRemoteAccess('full'));

  it('keeps the warning word for word', () => {
    expect(TEAM_LOCALHOST).toBe('teams.localhost');
    expect(TEAM_LOCALHOST_LABEL).toBe('Allow localhost (macOS)');
    expect(TEAM_LOCALHOST_WARNING).toBe(WARNING);
  });

  it('is off by default, shows the label and the warning, and turning it on sends teams.localhost { teamId, on: true }', async () => {
    const state = { on: false };
    const { api, calls } = fakeApi(state);
    const w = await openNetwork(api);
    const block = w.get('[data-test=team-localhost]');
    expect(block.text()).toContain('Allow localhost (macOS)');
    expect(toggle(w).checked).toBe(false);
    const warning = w.get('[data-test=team-localhost-warning]');
    expect(warning.text()).toBe(WARNING.replaceAll('`', ''));
    expect(warning.findAll('code').map((c) => c.text())).toEqual(['dotnet test', '0.0.0.0']);
    // Question 5497: the network exposure, the firewall and the gateway are said plainly.
    expect(warning.text()).toContain('a server an agent starts on 0.0.0.0 can be reached from other machines on your network unless the macOS firewall blocks it');
    expect(warning.text()).toContain('Keep the macOS firewall on.');
    expect(warning.text()).toContain('Outbound traffic still goes only through the gateway.');
    expect(calls.find((c) => c.method === 'teams.localhost')!.params).toEqual({ teamId: 4 });

    toggle(w).checked = true;
    await w.get('[data-test=team-localhost-toggle]').trigger('change');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.localhost').at(-1)!.params).toEqual({ teamId: 4, on: true });
    expect(state.on).toBe(true);
    expect(toggle(w).checked).toBe(true);

    toggle(w).checked = false;
    await w.get('[data-test=team-localhost-toggle]').trigger('change');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.localhost').at(-1)!.params).toEqual({ teamId: 4, on: false });
    expect(state.on).toBe(false);
    w.unmount();
  });

  it('shows a team that has it on as on', async () => {
    const w = await openNetwork(fakeApi({ on: true }).api);
    expect(toggle(w).checked).toBe(true);
    w.unmount();
  });

  it('is read-only for a Visitor', async () => {
    applyRemoteAccess('read');
    const w = await openNetwork(fakeApi({ on: false }).api);
    expect(toggle(w).disabled).toBe(true);
    expect(toggle(w).title).toBe(READ_ONLY_TITLE);
    expect(w.get('[data-test=team-localhost-warning]').text()).toBe(WARNING.replaceAll('`', ''));
    w.unmount();
  });

  it('an older core without the setting: the block is not shown', async () => {
    const w = await openNetwork(fakeApi(null).api);
    expect(w.find('[data-test=team-localhost]').exists()).toBe(false);
    w.unmount();
  });
});
