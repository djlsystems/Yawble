// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import SettingsView from '../../src/renderer/src/components/SettingsView.vue';
import NewTeamDialog from '../../src/renderer/src/components/NewTeamDialog.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import TeamMembers from '../../src/renderer/src/components/TeamMembers.vue';
import { hireRoleOf } from '../../src/renderer/src/agentModels';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import type { HireRow, RoleRow, TeamDefaults, TeamHireRow, TeamSummary, YawbleApi } from '../../src/shared/api';

// B147: the role catalog (Settings > Teams > Roles), the default hire list (Settings > Teams), a team's hire list (Team
// settings > Hiring), the role picker and "Who the Manager may hire" on New team, and the role on a member's tile.
// A scripted fake core: no agent, no network.
type Answer = (params: any) => unknown;
function fakeApi(answers: Record<string, Answer>) {
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
  return { api, calls, of: (m: string) => calls.filter((c) => c.method === m) };
}

const ROLES: RoleRow[] = [
  { name: 'Developer', job: 'Writes the code and its unit tests on its own branch.', agent: 'claude', model: 'opus' },
  { name: 'Tester', job: 'Builds the branches and checks them end to end.', agent: 'codex', model: null },
  { name: 'Reviewer', job: 'Reviews a branch for correctness.', agent: 'claude', model: 'sonnet' },
];
const models: Answer = (p) => ({ models: p.cli === 'claude' ? ['opus', 'sonnet', 'haiku'] : ['gpt-5.5', 'gpt-5.5-mini'], source: 'app' });

const defaults = (over: Partial<TeamDefaults> = {}): TeamDefaults => ({
  manager: { name: 'Manager', cli: 'claude' }, members: [], memberNames: ['Amara'], country: 'en-US',
  countries: [{ code: 'en-US', label: 'United States (US English)' }], usedCount: 0,
  hireList: [{ role: 'Developer', agent: 'claude', model: 'opus' }],
  ...over,
});

let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; applyRemoteAccess('full'); document.body.innerHTML = ''; });
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
const val = (w: VueWrapper, t: string) => (w.get(`[data-test="${t}"]`).element as HTMLInputElement).value;

describe('Settings > Teams > Roles', () => {
  function rolesApi(over: Record<string, Answer> = {}) {
    let roles = ROLES.map((r) => ({ ...r }));
    return fakeApi({
      'teams.defaults': () => defaults(), 'agentProfiles.list': () => [], 'agents.models': models,
      'roles.list': () => roles.map((r) => ({ ...r })),
      'roles.save': (p) => {
        const row = { name: p.name, job: p.job, agent: p.agent, model: p.model };
        const at = roles.findIndex((r) => r.name === (p.originalName ?? p.name));
        if (at >= 0) roles[at] = row; else roles.push(row);
        return row;
      },
      'roles.remove': (p) => {
        if (p.name === 'Developer') throw new Error('Developer is on the hire list of payments and web; take it off there first.');
        roles = roles.filter((r) => r.name !== p.name);
        return {};
      },
      ...over,
    });
  }
  async function open(f: ReturnType<typeof rolesApi>) {
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'teams', teams: [] } }));
    await flushPromises();
    return w;
  }

  it('lists the roles with their job, agent and model', async () => {
    const w = await open(rolesApi());
    const dev = w.get('[data-test="role-Developer"]').text();
    expect(dev).toContain('Developer');
    expect(dev).toContain('Writes the code and its unit tests');
    expect(dev).toContain('Claude Code · opus');
    expect(w.get('[data-test="role-Tester"]').text()).toContain('Codex (OpenAI) · agent default');
  });

  it('adds a role: one word, its job, agent and model, saved at once with roles.save', async () => {
    const f = rolesApi();
    const w = await open(f);
    await w.get('[data-test=role-add]').trigger('click');
    await w.get('[data-test=role-name]').setValue('Two words');
    expect(w.find('[data-test=role-name-warn]').exists()).toBe(true);
    expect((w.get('[data-test=role-save]').element as HTMLButtonElement).disabled).toBe(true);
    await w.get('[data-test=role-name]').setValue('Writer');
    await w.get('[data-test=role-job]').setValue('Writes the docs.');
    await w.get('[data-test=role-agent]').setValue('codex');
    await flushPromises();
    await w.get('[data-test=role-model]').setValue('gpt-5.5-mini');
    await w.get('[data-test=role-save]').trigger('click');
    await flushPromises();
    expect(f.of('roles.save')[0].params).toEqual({ name: 'Writer', job: 'Writes the docs.', agent: 'codex', model: 'gpt-5.5-mini' });
    expect(w.find('[data-test=role-form]').exists()).toBe(false);
    expect(w.get('[data-test="role-Writer"]').text()).toContain('Writes the docs.');
  });

  it('edits a role: the form starts from it and roles.save names the original', async () => {
    const f = rolesApi();
    const w = await open(f);
    await w.get('[data-test="role-edit-Tester"]').trigger('click');
    expect(val(w, 'role-name')).toBe('Tester');
    expect(val(w, 'role-agent')).toBe('codex');
    expect(val(w, 'role-model')).toBe('');
    await w.get('[data-test=role-name]').setValue('QA');
    await w.get('[data-test=role-save]').trigger('click');
    await flushPromises();
    expect(f.of('roles.save')[0].params).toEqual({ name: 'QA', job: ROLES[1].job, agent: 'codex', model: null, originalName: 'Tester' });
    expect(w.find('[data-test="role-QA"]').exists()).toBe(true);
    expect(w.find('[data-test="role-Tester"]').exists()).toBe(false);
  });

  it('removes a role, and shows a refusal naming the teams as it is', async () => {
    const f = rolesApi();
    const w = await open(f);
    await w.get('[data-test="role-remove-Reviewer"]').trigger('click');
    await flushPromises();
    expect(f.of('roles.remove')[0].params).toEqual({ name: 'Reviewer' });
    expect(w.find('[data-test="role-Reviewer"]').exists()).toBe(false);
    await w.get('[data-test="role-remove-Developer"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=roles-error]').text()).toContain('Developer is on the hire list of payments and web; take it off there first.');
    expect(w.find('[data-test="role-Developer"]').exists()).toBe(true);
  });

  it('an older core without roles shows no Roles section', async () => {
    const f = rolesApi({ 'roles.list': () => { throw new Error('unknown method roles.list'); } });
    const w = await open(f);
    expect(w.find('[data-test=roles]').exists()).toBe(false);
  });
});

describe('Settings > Teams: the default hire list', () => {
  function api(d = defaults()) {
    let saved = d;
    return fakeApi({
      'teams.defaults': () => saved, 'agentProfiles.list': () => [], 'agents.models': models, 'roles.list': () => ROLES,
      'teams.setDefaults': (p) => { saved = { ...saved, hireList: p.hireList }; return { saved: true, rejected: [] }; },
    });
  }

  it('shows the saved rows, adds one from a role, and Save sends hireList', async () => {
    const f = api();
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'teams', teams: [] } }));
    await flushPromises();
    expect(val(w, 'default-hire-role-0')).toBe('Developer');
    expect(val(w, 'default-hire-model-0')).toBe('opus');
    expect(w.find('[data-test=default-hire-max-0]').exists()).toBe(false); // no count per role
    await w.get('[data-test=default-hire-add]').trigger('click');
    await flushPromises();
    // The first role not on the list yet, with its suggested agent and model.
    expect(val(w, 'default-hire-role-1')).toBe('Tester');
    expect(val(w, 'default-hire-agent-1')).toBe('codex');
    await w.get('[data-test=team-defaults-save]').trigger('click');
    await flushPromises();
    expect(f.of('teams.setDefaults')[0].params.hireList).toEqual([
      { role: 'Developer', agent: 'claude', model: 'opus' },
      { role: 'Tester', agent: 'codex', model: null },
    ]);
  });

  it('an empty list says the Manager may hire any agent', async () => {
    const w = keep(mount(SettingsView, { props: { api: api(defaults({ hireList: [] })).api, tab: 'teams', teams: [] } }));
    await flushPromises();
    expect(w.get('[data-test=default-hire-empty]').text()).toContain('the Manager may hire any agent');
  });

  it('an older core without a default hire list: no section, and Save sends no hireList', async () => {
    const f = api(defaults({ hireList: undefined }));
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'teams', teams: [] } }));
    await flushPromises();
    expect(w.find('[data-test=default-hire]').exists()).toBe(false);
    await w.get('[data-test=team-defaults-save]').trigger('click');
    await flushPromises();
    expect('hireList' in f.of('teams.setDefaults')[0].params).toBe(false);
  });
});

describe('New team: role picker and who the Manager may hire', () => {
  async function dialog(over: Record<string, Answer> = {}) {
    const f = fakeApi({
      'agentProfiles.list': () => [], 'agents.models': models, 'roles.list': () => ROLES, 'teams.defaults': () => defaults(),
      'teams.create': () => ({ teamId: 9 }), ...over,
    });
    const w = keep(mount(NewTeamDialog, { props: { api: f.api }, attachTo: document.body }));
    await flushPromises();
    return { w, ...f };
  }

  it('picking a role fills the row\'s agent and model (and a blank name); both stay editable; create sends the role', async () => {
    const { w, of } = await dialog();
    await w.get('[data-test=add-member-row]').trigger('click');
    await flushPromises();
    const roleOptions = w.findAll('[data-test=member-role-0] option').map((o) => o.text());
    expect(roleOptions).toEqual(['no role', 'Developer', 'Tester', 'Reviewer']);
    await w.get('[data-test=member-role-0]').setValue('Reviewer');
    await flushPromises();
    expect(val(w, 'member-cli-0')).toBe('claude');
    expect(val(w, 'member-model-0')).toBe('sonnet');
    expect(val(w, 'member-name-0')).toBe('Reviewer');
    await w.get('[data-test=member-role-0]').setValue('Tester');
    await flushPromises();
    expect(val(w, 'member-cli-0')).toBe('codex');
    expect(val(w, 'member-model-0')).toBe('');
    expect(val(w, 'member-name-0')).toBe('Reviewer'); // a typed name is kept
    // Still editable after the role filled them.
    await w.get('[data-test=member-name-0]').setValue('Tester');
    await w.get('[data-test=member-cli-0]').setValue('claude');
    await flushPromises();
    await w.get('[data-test=member-model-0]').setValue('haiku');
    await w.get('[data-test=team-name]').setValue('alpha');
    await w.get('[data-test=create-team]').trigger('click');
    await flushPromises();
    expect(of('teams.create')[0].params.members).toEqual([{ name: 'Tester', cli: 'claude', model: 'haiku', role: 'Tester' }]);
  });

  it('"Who the Manager may hire" starts from the defaults, can change, and is sent with teams.create', async () => {
    const { w, of } = await dialog();
    expect(w.text()).toContain('Who the Manager may hire');
    expect(val(w, 'new-hire-role-0')).toBe('Developer');
    expect(w.find('[data-test=new-hire-max-0]').exists()).toBe(false);
    await w.get('[data-test=new-hire-add]').trigger('click');
    await flushPromises();
    await w.get('[data-test=new-hire-role-1]').setValue('Reviewer');
    await flushPromises();
    expect(val(w, 'new-hire-agent-1')).toBe('claude');
    expect(val(w, 'new-hire-model-1')).toBe('sonnet');
    await w.get('[data-test=team-name]').setValue('alpha');
    await w.get('[data-test=create-team]').trigger('click');
    await flushPromises();
    expect(of('teams.create')[0].params.hireList).toEqual([
      { role: 'Developer', agent: 'claude', model: 'opus' },
      { role: 'Reviewer', agent: 'claude', model: 'sonnet' },
    ]);
    expect(of('teams.create')[0].params.members).toEqual([]);
  });

  it('with no default list the section explains the Manager may hire any agent, and sends an empty list', async () => {
    const { w, of } = await dialog({ 'teams.defaults': () => defaults({ hireList: [] }) });
    expect(w.get('[data-test=new-hire-empty]').text()).toContain('the Manager may hire any agent');
    await w.get('[data-test=team-name]').setValue('alpha');
    await w.get('[data-test=create-team]').trigger('click');
    await flushPromises();
    expect(of('teams.create')[0].params.hireList).toEqual([]);
  });

  it('an older core without roles: no role picker, members sent as before', async () => {
    const { w, of } = await dialog({ 'roles.list': () => { throw new Error('unknown method'); } });
    await w.get('[data-test=add-member-row]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=member-role-0]').exists()).toBe(false);
    await w.get('[data-test=member-name-0]').setValue('Developer');
    await w.get('[data-test=team-name]').setValue('alpha');
    await w.get('[data-test=create-team]').trigger('click');
    await flushPromises();
    expect(of('teams.create')[0].params.members).toEqual([{ name: 'Developer', cli: 'claude' }]);
  });
});

describe('Team settings > Hiring', () => {
  const team = { id: 4, name: 'payments', members: 3, running: 0, queued: 0, repoPath: null } as unknown as TeamSummary;
  function api(list: TeamHireRow[] | null) {
    let rows = list;
    return fakeApi({
      'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null }),
      'teams.repos': () => ({ main: null, extras: [] }), 'teams.workflowExpiry': () => ({ days: 3 }),
      'integrations.list': () => ({ integrations: [] }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
      'agentProfiles.list': () => [], 'agents.models': models, 'roles.list': () => ROLES,
      'teams.hireList': () => { if (!rows) throw new Error('unknown method teams.hireList'); return rows; },
      'teams.setHireList': (p) => {
        rows = p.rows.map((r: HireRow) => ({ ...r, job: ROLES.find((x) => x.name === r.role)?.job ?? '' }));
        return {};
      },
    });
  }
  async function openHiring(a: YawbleApi) {
    const w = keep(mount(TeamManageDialog, { props: { team, api: a },
      global: { stubs: { IntegrationDialog: true, TeamNetworkAllows: true, Modal: { template: '<div><slot /></div>' } } } }));
    await flushPromises();
    await w.get('[data-test=manage-tab-hiring]').trigger('click');
    await flushPromises();
    return w;
  }

  it('shows the rows (no count per role), changes them, and Save sends teams.setHireList', async () => {
    const f = api([{ role: 'Developer', job: ROLES[0].job, agent: 'claude', model: 'opus' }]);
    const w = await openHiring(f.api);
    expect(w.get('[data-test=manage-tab-hiring]').text()).toBe('Hiring');
    expect(f.of('teams.hireList')[0].params).toMatchObject({ teamId: 4, team: 4 });
    expect(val(w, 'team-hire-role-0')).toBe('Developer');
    expect(val(w, 'team-hire-agent-0')).toBe('claude');
    expect(val(w, 'team-hire-model-0')).toBe('opus');
    expect(w.find('[data-test=team-hire-max-0]').exists()).toBe(false);
    expect(w.find('[data-test=team-hire-used-0]').exists()).toBe(false);
    expect(w.get('[data-test=team-hiring]').text()).not.toContain('hired');
    await w.get('[data-test=team-hire-model-0]').setValue(''); // back to the agent's default
    await w.get('[data-test=team-hire-add]').trigger('click');
    await flushPromises();
    expect(val(w, 'team-hire-role-1')).toBe('Tester');
    await w.get('[data-test=team-hire-save]').trigger('click');
    await flushPromises();
    expect(f.of('teams.setHireList')[0].params).toEqual({
      teamId: 4, team: 4,
      rows: [{ role: 'Developer', agent: 'claude', model: null }, { role: 'Tester', agent: 'codex', model: null }],
    });
    expect(w.find('[data-test=team-hire-saved]').exists()).toBe(true);
    expect(JSON.stringify(f.of('teams.setHireList')[0].params)).not.toContain('max');
    expect(w.findAll('[data-test=team-hire] input[type=number]')).toHaveLength(0);
    expect(w.get('[data-test=team-hiring]').text()).not.toContain('at most');
  });

  it('shows how many hold a role as "N hired", a count and not a limit', async () => {
    const f = api([
      { role: 'Developer', job: ROLES[0].job, agent: 'claude', model: 'opus', used: 2, holders: [5, 6] },
      { role: 'Tester', job: '', agent: 'codex', model: null, used: 0, holders: [] },
    ]);
    const w = await openHiring(f.api);
    expect(w.get('[data-test=team-hire-used-0]').text()).toBe('2 hired');
    expect(w.find('[data-test=team-hire-used-1]').exists()).toBe(false);
    expect(w.get('[data-test=team-hiring]').text()).not.toContain('of max');
    await w.get('[data-test=team-hire-save]').trigger('click');
    await flushPromises();
    // used and holders are not sent back.
    expect(f.of('teams.setHireList')[0].params.rows).toEqual([
      { role: 'Developer', agent: 'claude', model: 'opus' }, { role: 'Tester', agent: 'codex', model: null },
    ]);
  });

  it('offers custom agents in a row\'s agent picker', async () => {
    const f = api([]);
    (f as any).api.call = ((orig) => async (m: string, p?: any) => m === 'agentProfiles.list'
      ? [{ id: 9, name: 'my-agent', baseCli: 'claude', custom: true, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] }]
      : orig(m, p))(f.api.call);
    const w = await openHiring(f.api);
    await w.get('[data-test=team-hire-add]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=team-hire-agent-0] option').map((o) => o.text())).toContain('my-agent (my agent)');
  });

  it('an empty list explains that the Manager may hire any agent; removing the last row saves an empty list', async () => {
    const f = api([{ role: 'Tester', job: '', agent: 'codex', model: null }]);
    const w = await openHiring(f.api);
    await w.get('[data-test=team-hire-remove-0]').trigger('click');
    expect(w.get('[data-test=team-hire-empty]').text()).toContain('the Manager may hire any agent');
    await w.get('[data-test=team-hire-save]').trigger('click');
    await flushPromises();
    expect(f.of('teams.setHireList')[0].params.rows).toEqual([]);
  });

  it('an older core without hire lists says so', async () => {
    const w = await openHiring(api(null).api);
    expect(w.get('[data-test=team-hiring-missing]').text()).toContain('the Manager may hire any agent');
    expect(w.find('[data-test=team-hire]').exists()).toBe(false);
  });
});

describe('the role on a member\'s tile', () => {
  it('hireRoleOf reads only hireRole, never member.role', () => {
    expect(hireRoleOf({ hireRole: 'Developer' })).toBe('Developer');
    expect(hireRoleOf({ hireRole: ' Tester ' })).toBe('Tester');
    expect(hireRoleOf({ hireRole: null })).toBeNull();
    expect(hireRoleOf({ hireRole: '' })).toBeNull();
    expect(hireRoleOf({ role: 'Tester', hireRole: null } as any)).toBeNull(); // no fallback to role
  });

  it('the Teams list tile names the member\'s role', async () => {
    const card = (id: number, name: string, role: 'manager' | 'member', hireRole: string | null) => ({
      member: { id, teamId: 1, team: 'payments', name, role, cli: 'claude', instructions: '', model: '', credential: '', hireRole },
      state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null, runStartedAt: null, lastRunState: null, lastRunReason: null,
    });
    const f = fakeApi({ 'teams.agents': () => [card(1, 'Manager', 'manager', null), card(2, 'DeveloperKofi', 'member', 'Developer')] });
    const w = keep(mount(TeamMembers, { props: { teamId: 1, api: f.api, fallback: '' } }));
    await flushPromises();
    const faces = w.findAll('[data-test=team-member]');
    expect(faces[0].attributes('title')).toMatch(/^Manager · manager\n/);
    expect(faces[0].attributes('data-role')).toBeUndefined();
    expect(faces[1].attributes('title')).toMatch(/ · member · Developer\n/);
    expect(faces[1].attributes('data-role')).toBe('Developer');
  });
});
