// @vitest-environment happy-dom
//
// ADMIN → MCP SERVERS AND TEAM SETTINGS → MCP SERVERS (MCP 1): the list shows each entry's health, its
// tools and the teams it is given to; a save shows the check's sentence and the tools' names and count;
// an entry the platform runs says it is checked when MCP 3 is in place, and a refusal shows the Host's
// sentence; removing asks first, naming the teams. Team settings lists only what the team was given.
//
// THE MOCK IS OF `api/mcpServers`; the Host's own rules (the check, the marks, the version rule, the
// rows, the scan for values) are pinned server-side in McpServersTests.cs.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listMcpServers, addMcpServer, testMcpServer, removeMcpServer, listTeamMcpServers, assignTeamMcpServer, removeTeamMcpServer, listConnections } =
  vi.hoisted(() => ({
    listMcpServers: vi.fn(),
    addMcpServer: vi.fn(),
    testMcpServer: vi.fn(),
    removeMcpServer: vi.fn(),
    listTeamMcpServers: vi.fn(),
    assignTeamMcpServer: vi.fn(),
    removeTeamMcpServer: vi.fn(),
    listConnections: vi.fn(),
  }));

vi.mock('../../api/mcpServers', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listMcpServers,
  addMcpServer,
  testMcpServer,
  removeMcpServer,
  listTeamMcpServers,
  assignTeamMcpServer,
  removeTeamMcpServer,
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
}));

import McpServersDialog from '../McpServersDialog.vue';
import TeamMcpServersPanel from '../TeamMcpServersPanel.vue';
import type { McpServer } from '../../api/mcpServers';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';

function server(overrides: Partial<McpServer>): McpServer {
  return {
    id: 'mcp-1',
    name: 'github',
    kind: 'url',
    url: 'https://mcp.example.test/mcp',
    connection: { id: 'conn-key', name: 'GitHub key' },
    field: 'token',
    header: 'Authorization',
    template: 'Bearer {value}',
    command: null,
    package: null,
    version: null,
    args: [],
    env: [],
    health: 'ok',
    healthReason: null,
    tools: ['search_issues', 'create_issue'],
    toolCount: 2,
    checkedAt: '2026-10-31T09:00:00Z',
    teams: ['Alpha'],
    ...overrides,
  };
}

const github = server({});
const refused = server({
  id: 'mcp-2',
  name: 'jira',
  health: 'needs-you',
  healthReason: 'mcp.example.test refused the credential (HTTP 401). Fix the credential in its connection, then test it again.',
  tools: null,
  toolCount: null,
  teams: ['Alpha', 'Beta'],
});
const local = server({
  id: 'mcp-3',
  name: 'fetch',
  kind: 'runs',
  url: null,
  connection: null,
  command: 'uvx',
  package: 'mcp-server-fetch',
  version: '0.6.2',
  health: 'unchecked',
  healthReason: 'checked when MCP 3 is in place',
  tools: null,
  toolCount: null,
  teams: [],
});

beforeEach(() => {
  for (const mock of [listMcpServers, addMcpServer, testMcpServer, removeMcpServer, listTeamMcpServers, assignTeamMcpServer, removeTeamMcpServer, listConnections]) {
    mock.mockReset();
  }
  listMcpServers.mockResolvedValue([github, refused, local]);
  listConnections.mockResolvedValue([]);
  listTeamMcpServers.mockImplementation(async (team: string) =>
    [github, refused, local].filter((s) => s.teams.includes(team)).map((s) => ({ assignedAt: '2026-10-31T09:00:00Z', server: s })),
  );
});

afterEach(resetBody);

describe('Admin → MCP servers', () => {
  it("lists each entry's health, tools and teams, and an entry the platform runs says when it is checked", async () => {
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });

    expect(bodyFind('[data-mcp-server="github"] [data-mcp-health]')?.textContent).toContain('Working');
    expect(bodyFind('[data-mcp-server="github"] [data-mcp-health]')?.textContent).toContain('2 tools');
    expect(bodyFind('[data-mcp-server="github"]')?.textContent).toContain('Given to Alpha');

    const jira = bodyFind('[data-mcp-server="jira"] [data-mcp-health]')?.textContent ?? '';
    expect(jira).toContain('Needs you');
    expect(jira).toContain('refused the credential (HTTP 401)');

    expect(bodyFind('[data-mcp-server="fetch"]')?.textContent).toContain('uvx mcp-server-fetch@0.6.2');
    expect(bodyFind('[data-mcp-server="fetch"] [data-mcp-health]')?.textContent).toContain('Checked when MCP 3 is in place');
    expect(bodyFind('[data-mcp-server="fetch"] [data-mcp-test]')).toBeNull();

    wrapper.unmount();
  });

  it("adds a web address and shows its check: the tools' names and how many", async () => {
    addMcpServer.mockResolvedValue({
      server: server({ id: 'mcp-9', name: 'linear', tools: ['a', 'b', 'c'], toolCount: 3 }),
      check: { outcome: 'ok', sentence: 'Connected: linear listed 3 tools.', tools: ['list_issues', 'create_issue', 'search'], toolCount: 3 },
    });
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });

    button('Add MCP server').click();
    await settle();
    await type('Name', 'linear');
    await type('Address', 'https://mcp.linear.test/mcp');
    button('Save').click();
    await settle();

    expect(addMcpServer).toHaveBeenCalledWith({ name: 'linear', kind: 'url', url: 'https://mcp.linear.test/mcp', connection: null });
    expect(bodyFind('[data-mcp-check]')?.textContent).toContain('linear: Connected: linear listed 3 tools.');
    expect(bodyFind('[data-mcp-check-tools]')?.textContent).toContain('3 tools');
    expect(bodyFind('[data-mcp-check-tools]')?.textContent).toContain('list_issues, create_issue, search');

    wrapper.unmount();
  });

  it("shows the Host's sentence when an entry the platform runs has no pinned version", async () => {
    addMcpServer.mockRejectedValue(new Error('A version must be pinned: give mcp-server-fetch one exact version, such as 1.4.2 - not `latest`, a range, or nothing.'));
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });

    button('Add MCP server').click();
    await settle();
    await type('Name', 'fetch2');
    const toggle = wrapper.findAllComponents({ name: 'QBtnToggle' })[0]!;
    toggle.vm.$emit('update:modelValue', 'runs');
    await settle();
    await type('Package', 'mcp-server-fetch');
    button('Save').click();
    await settle();

    expect(addMcpServer).toHaveBeenCalledWith(expect.objectContaining({ name: 'fetch2', kind: 'runs', command: 'npx', package: 'mcp-server-fetch', version: '' }));
    expect(bodyFind('[data-mcp-form-error]')?.textContent).toContain('A version must be pinned');

    wrapper.unmount();
  });

  it("tests an entry and shows the check's sentence", async () => {
    testMcpServer.mockResolvedValue({ check: { outcome: 'failed', sentence: 'Could not reach mcp.example.test.', tools: null, toolCount: null }, server: github });
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });

    (bodyFind('[data-mcp-server="github"] [data-mcp-test]') as HTMLButtonElement).click();
    await settle();

    expect(testMcpServer).toHaveBeenCalledWith('mcp-1');
    expect(bodyFind('[data-mcp-check]')?.textContent).toContain('Could not reach mcp.example.test.');

    wrapper.unmount();
  });

  it('asks before removing, naming the teams that lose it', async () => {
    removeMcpServer.mockResolvedValue({ removed: 'mcp-2', name: 'jira', teams: ['Alpha', 'Beta'] });
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });

    (bodyFind('[data-mcp-server="jira"] [data-mcp-remove]') as HTMLButtonElement).click();
    await settle();

    expect(removeMcpServer).not.toHaveBeenCalled();
    expect(bodyFind('[data-mcp-confirm]')?.textContent).toContain('It is taken from Alpha, Beta too.');

    (bodyFind('[data-mcp-confirm-remove]') as HTMLButtonElement).click();
    await settle();
    expect(removeMcpServer).toHaveBeenCalledWith('mcp-2');

    wrapper.unmount();
  });
});

describe('Team settings → MCP servers', () => {
  type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

  const select = (wrapper: { findAllComponents: (s: { name: string }) => unknown[] }) =>
    (wrapper.findAllComponents({ name: 'QSelect' }) as Select[])[0]!;

  it("lists only what the team was given, and offers the rest", async () => {
    const wrapper = await mountDialog(TeamMcpServersPanel, { team: 'Beta' }, { attachTo: document.body });

    expect(listTeamMcpServers).toHaveBeenCalledWith('Beta');
    expect(bodyFind('[data-team-mcp-server="jira"]')?.textContent).toContain('Needs you');
    expect(bodyFind('[data-team-mcp-server="github"]')).toBeNull();
    expect((select(wrapper).props('options') as { value: string }[]).map((o) => o.value)).toEqual(['mcp-1', 'mcp-3']);

    wrapper.unmount();
  });

  it('gives one to the team from the pick list and takes one off', async () => {
    assignTeamMcpServer.mockResolvedValue({});
    removeTeamMcpServer.mockResolvedValue(undefined);
    const wrapper = await mountDialog(TeamMcpServersPanel, { team: 'Beta' }, { attachTo: document.body });

    select(wrapper).vm.$emit('update:modelValue', 'mcp-3');
    await settle();
    expect(assignTeamMcpServer).toHaveBeenCalledWith('Beta', 'mcp-3');

    (bodyFind('[data-team-mcp-server="jira"] [data-team-mcp-remove]') as HTMLButtonElement).click();
    await settle();
    expect(removeTeamMcpServer).toHaveBeenCalledWith('Beta', 'mcp-2');

    wrapper.unmount();
  });

  it('a team with none says it works as it always has', async () => {
    const wrapper = await mountDialog(TeamMcpServersPanel, { team: 'Gamma' }, { attachTo: document.body });

    expect(bodyFind('[data-team-mcp-empty]')?.textContent).toContain('works as it always has');
    expect(bodyText()).not.toContain('Reading the MCP servers');

    wrapper.unmount();
  });
});
