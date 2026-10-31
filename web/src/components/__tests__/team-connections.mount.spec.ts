// @vitest-environment happy-dom
//
// TEAM CONNECTIONS, the platform's own member on every team (TeamConnectionsMember in the Host). Its
// card's settings icon opens Team settings -> Connections, never Member settings - it has no settings of
// its own; and its built-in plugin is offered nowhere a person hires (Add member), nowhere in the
// Marketplace, and in Admin -> Plugins as a locked built-in with no Remove.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { listContainerTriggers, listCatalog, listPlugins } = vi.hoisted(() => ({
  listContainerTriggers: vi.fn(),
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
  listCatalog,
  listPlugins,
}));

import ContainerCard from '../ContainerCard.vue';
import AddMemberDialog from '../AddMemberDialog.vue';
import { asMemberId, asTeamId, type ContainerSnapshot, type TeamId } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin } from '../../test/pluginFixtures';
import { pluginRows } from '../../lib/plugins';
import { isTeamConnections, TEAM_CONNECTIONS_AGENT } from '../../lib/teamConnections';

const teamConnections = (): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('TeamConnections'),
  name: 'Team Connections',
  agent: TEAM_CONNECTIONS_AGENT,
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
});

const builtIn = hostPlugin({ id: 'team-connections', name: 'Team Connections', version: '1.0.0', builtIn: true, locked: true });
const echo = hostPlugin({ id: 'sample-echo', name: 'Sample Echo', version: '0.1.0' });

beforeEach(() => {
  setActivePinia(createPinia());
  listContainerTriggers.mockResolvedValue([]);
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([builtIn, echo]));
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('Team Connections', () => {
  it('is known by what it runs', () => {
    expect(isTeamConnections(teamConnections())).toBe(true);
    expect(isTeamConnections({ agent: 'plugin:sample-echo' })).toBe(false);
  });

  it("opens Team settings on Connections from its card's settings icon, and never Member settings", async () => {
    const wrapper = mount(ContainerCard, {
      attachTo: document.body,
      props: { snapshot: teamConnections(), feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, TeamSettingsDialog: true } },
    });
    await flushPromises();

    expect(wrapper.find('[aria-label="Member settings"]').exists()).toBe(false);
    await wrapper.find('[aria-label="Team connections"]').trigger('click');
    await flushPromises();

    const team = wrapper.findComponent({ name: 'TeamSettingsDialog' });
    expect(team.exists()).toBe(true);
    expect(team.props('initialTab')).toBe('connections');
    expect(team.props('teamId')).toBe('alpha');
    expect(wrapper.findComponent({ name: 'MemberSettingsDialog' }).exists()).toBe(false);
    wrapper.unmount();
  });

  it('is not offered in Add member', async () => {
    const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });

    const agent = (wrapper.findAllComponents({ name: 'QSelect' }) as { props: (n: string) => unknown }[])
      .find((s) => s.props('label') === 'Agent')!;
    expect(agent.props('options')).toEqual(['claude', 'plugin:sample-echo']);
    wrapper.unmount();
  });

  it('is a row of Admin -> Plugins though it has no version folder on the volume', () => {
    const rows = pluginRows(hostList([builtIn, echo], {
      versions: [{ id: 'sample-echo', version: '0.1.0', name: 'Sample Echo', active: true, verdict: 'installed', reason: null }],
    }));

    expect(rows.map((row) => row.id)).toEqual(['sample-echo', 'team-connections']);
    const row = rows.find((candidate) => candidate.id === 'team-connections')!;
    expect(row.verdict === 'installed' && row.plugin.builtIn).toBe(true);
  });
});
