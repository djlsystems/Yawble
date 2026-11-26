// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import SettingsView from '../../src/renderer/src/components/SettingsView.vue';
import NewTeamDialog from '../../src/renderer/src/components/NewTeamDialog.vue';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';
import { newChatAgent, savedConciergeAgent } from '../../src/renderer/src/conciergeDefault';
import type { TeamDefaults, YawbleApi } from '../../src/shared/api';

// Settings > Concierge (the default agent) and Settings > Teams (New Team defaults, the member-name list and
// Generate), and the New Team dialog starting from those defaults. A scripted fake core: no agent, no network.
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

const defaults = (over: Partial<TeamDefaults> = {}): TeamDefaults => ({
  manager: { name: 'Lead', cli: 'codex' },
  members: [
    { name: 'Developer', cli: 'claude', onNewTeams: true },
    { name: 'Tester Ada', cli: 'codex', onNewTeams: false },
    { name: 'Researcher', cli: 'claude', onNewTeams: false },
  ],
  memberNames: ['Amara', 'Tomas', 'Priya'],
  country: 'de-DE',
  countries: [{ code: 'en-US', label: 'United States (US English)' }, { code: 'de-DE', label: 'Germany (German)' }],
  usedCount: 1,
  ...over,
});

let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; applyRemoteAccess('full'); });
async function settings(api: YawbleApi, tab: 'concierge' | 'teams') {
  const w = mount(SettingsView, { props: { api, tab, teams: [] } });
  wrappers.push(w);
  await flushPromises();
  return w;
}
const el = <T extends Element = HTMLInputElement>(w: VueWrapper, t: string) => w.get(`[data-test="${t}"]`).element as unknown as T;

describe('Settings tabs', () => {
  it('Concierge and Teams come right after Agents', async () => {
    const f = fakeApi({ 'concierge.defaults': () => ({ defaultAgent: null }), 'agentProfiles.list': () => [] });
    const w = await settings(f.api, 'concierge');
    const tabs = w.findAll('[role=tab]').map((b) => b.text());
    expect(tabs.slice(0, 4)).toEqual(['General', 'Agents', 'Concierge', 'Teams']);
  });
});

describe('Settings > Concierge', () => {
  it('shows "First available on this computer" for no saved agent, then saves the picked one and back to null', async () => {
    const f = fakeApi({
      'concierge.defaults': () => ({ defaultAgent: null }),
      'concierge.setDefaults': () => ({}),
      'agentProfiles.list': () => [{ name: 'my-claude', custom: true }],
    });
    const w = await settings(f.api, 'concierge');
    const select = w.get('[data-test=concierge-default-agent]');
    const options = select.findAll('option');
    expect(options[0].text()).toBe('First available on this computer');
    expect(options.map((o) => o.text())).toContain('my-claude (my agent)');
    expect((select.element as HTMLSelectElement).value).toBe('');

    await select.setValue('codex');
    await flushPromises();
    expect(f.of('concierge.setDefaults').at(-1)!.params).toEqual({ defaultAgent: 'codex' });
    expect(w.find('[data-test=concierge-defaults-saved]').exists()).toBe(true);
    // The window's next new chat starts on it without reading concierge.defaults again.
    expect(savedConciergeAgent.value).toBe('codex');
    expect(newChatAgent([{ id: 'claude', ready: true }, { id: 'codex', ready: true }], savedConciergeAgent.value)).toBe('codex');
    await select.setValue('');
    await flushPromises();
    expect(f.of('concierge.setDefaults').at(-1)!.params).toEqual({ defaultAgent: null });
    expect(savedConciergeAgent.value).toBeNull();
  });

  it('a new chat starts on a saved custom agent when its base CLI is ready, else on the first ready built-in', () => {
    const agents = [{ id: 'claude', ready: true }, { id: 'codex', ready: false }];
    const customs = [{ name: 'Reviewer', baseCli: 'claude' }, { name: 'Coder', baseCli: 'codex' }];
    expect(newChatAgent(agents, 'Reviewer', customs)).toBe('Reviewer');
    expect(newChatAgent(agents, 'Coder', customs)).toBe('claude');
    expect(newChatAgent(agents, 'Gone', customs)).toBe('claude');
  });

  it('shows the saved agent, and a refused save puts it back with the error', async () => {
    const f = fakeApi({
      'concierge.defaults': () => ({ defaultAgent: 'codex' }),
      'concierge.setDefaults': () => { throw new Error('unknown agent'); },
      'agentProfiles.list': () => [],
    });
    const w = await settings(f.api, 'concierge');
    expect(el<HTMLSelectElement>(w, 'concierge-default-agent').value).toBe('codex');
    await w.get('[data-test=concierge-default-agent]').setValue('claude');
    await flushPromises();
    expect(w.get('[data-test=concierge-defaults-error]').text()).toContain('unknown agent');
    expect(el<HTMLSelectElement>(w, 'concierge-default-agent').value).toBe('codex');
  });

  it('a read-only view sees the agent but cannot change it', async () => {
    applyRemoteAccess('read');
    const f = fakeApi({ 'concierge.defaults': () => ({ defaultAgent: 'codex' }), 'agentProfiles.list': () => [] });
    const w = await settings(f.api, 'concierge');
    expect(el<HTMLSelectElement>(w, 'concierge-default-agent').value).toBe('codex');
    expect(el<HTMLSelectElement>(w, 'concierge-default-agent').disabled).toBe(true);
  });
});

describe('Settings > Teams', () => {
  function teamsApi(over: Record<string, Answer> = {}, d = defaults()) {
    let saved = d;
    return fakeApi({
      'teams.defaults': () => saved,
      'teams.setDefaults': (p) => {
        const lines = String(p.memberNamesText).split('\n').map((l) => l.trim()).filter(Boolean);
        const ok = lines.filter((l) => /^[A-Za-z-]{2,20}$/.test(l));
        saved = { ...saved, manager: p.manager, members: p.members, memberNames: ok, country: p.country };
        return { saved: true, rejected: lines.filter((l) => !ok.includes(l)).map((line) => ({ line, reason: 'not one name in plain letters' })) };
      },
      'agentProfiles.list': () => [],
      ...over,
    });
  }

  it('shows the saved Manager, member rows, names, count and country', async () => {
    const w = await settings(teamsApi().api, 'teams');
    expect(el(w, 'default-manager-name').value).toBe('Lead');
    expect(el<HTMLSelectElement>(w, 'default-manager-cli').value).toBe('codex');
    expect(el(w, 'default-member-name-0').value).toBe('Developer');
    expect(el(w, 'default-member-on-0').checked).toBe(true);
    expect(el(w, 'default-member-on-1').checked).toBe(false);
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Amara\nTomas\nPriya');
    expect(w.get('[data-test=member-names-count]').text()).toBe('3 names, 1 used by a member');
    expect(el<HTMLSelectElement>(w, 'names-country').value).toBe('de-DE');
    expect(w.findAll('[data-test=names-country] option').map((o) => o.text())).toEqual(['United States (US English)', 'Germany (German)']);
  });

  it('Save sends the Manager, the named member rows, the names text and the country; rejected lines are listed', async () => {
    const f = teamsApi();
    const w = await settings(f.api, 'teams');
    await w.get('[data-test=default-manager-name]').setValue('Boss');
    await w.get('[data-test=default-member-remove-2]').trigger('click');
    await w.get('[data-test=default-member-add]').trigger('click');
    await w.get('[data-test=default-member-name-2]').setValue('Writer');
    await w.get('[data-test=default-member-on-2]').setValue(false);
    await w.get('[data-test=default-member-add]').trigger('click'); // left blank: not saved
    await w.get('[data-test=member-names]').setValue('Amara\n\n  Zoe \nJosé\namara\nTwo words');
    expect(w.get('[data-test=member-names-count]').text()).toBe('4 names, 1 used by a member');
    await w.get('[data-test=names-country]').setValue('en-US');
    await w.get('[data-test=team-defaults-save]').trigger('click');
    await flushPromises();
    expect(f.of('teams.setDefaults')).toHaveLength(1);
    expect(f.of('teams.setDefaults')[0].params).toEqual({
      manager: { name: 'Boss', cli: 'codex' },
      members: [
        { name: 'Developer', cli: 'claude', onNewTeams: true },
        { name: 'Tester Ada', cli: 'codex', onNewTeams: false },
        { name: 'Writer', cli: 'codex', onNewTeams: false },
      ],
      memberNamesText: 'Amara\n\n  Zoe \nJosé\namara\nTwo words',
      country: 'en-US',
    });
    expect(w.get('[data-test=team-defaults-saved]').text()).toBe('Saved');
    const rejected = w.findAll('[data-test=member-names-rejected-line]').map((l) => l.text());
    expect(rejected).toEqual(['José not one name in plain letters', 'Two words not one name in plain letters']);
    // The box shows the list as the core kept it.
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Amara\nZoe\namara');
  });

  it('a refused save shows its error and keeps what was typed', async () => {
    const f = teamsApi({ 'teams.setDefaults': () => { throw new Error('manager: a name is required'); } });
    const w = await settings(f.api, 'teams');
    await w.get('[data-test=member-names]').setValue('Kofi');
    await w.get('[data-test=team-defaults-save]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-defaults-save-error]').text()).toContain('manager: a name is required');
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Kofi');
    expect(w.find('[data-test=team-defaults-saved]').exists()).toBe(false);
  });

  it('Generate is busy and disabled while it runs, then REPLACES the box without saving', async () => {
    let finish!: (v: unknown) => void;
    const f = teamsApi({ 'teams.generateNames': () => new Promise((r) => { finish = r; }) });
    const w = await settings(f.api, 'teams');
    await w.get('[data-test=names-country]').setValue('en-US');
    await w.get('[data-test=names-generate]').trigger('click');
    const btn = el<HTMLButtonElement>(w, 'names-generate');
    expect(btn.disabled).toBe(true);
    expect(btn.getAttribute('aria-busy')).toBe('true');
    expect(btn.textContent).toContain('Generating');
    await w.get('[data-test=names-generate]').trigger('click'); // ignored while running
    expect(f.of('teams.generateNames')).toEqual([{ method: 'teams.generateNames', params: { country: 'en-US' } }]);
    finish({ names: ['James', 'Mary', 'Olivia'] });
    await flushPromises();
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('James\nMary\nOlivia');
    expect(w.get('[data-test=member-names-count]').text()).toBe('3 names, 1 used by a member');
    expect(btn.disabled).toBe(false);
    expect(f.of('teams.setDefaults')).toHaveLength(0);
  });

  it('a failed Generate, or one with no names, shows an error line and leaves the box alone', async () => {
    let answer: () => unknown = () => { throw new Error('the agent did not answer'); };
    const f = teamsApi({ 'teams.generateNames': () => answer() });
    const w = await settings(f.api, 'teams');
    await w.get('[data-test=names-generate]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=names-generate-error]').text()).toContain('the agent did not answer');
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Amara\nTomas\nPriya');
    answer = () => ({ names: [] });
    await w.get('[data-test=names-generate]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=names-generate-error]').text()).toBe('The agent gave no names. Try again.');
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Amara\nTomas\nPriya');
    expect(el<HTMLButtonElement>(w, 'names-generate').disabled).toBe(false);
  });

  it('a read-only view sees everything but cannot change, add, remove, Generate or Save', async () => {
    applyRemoteAccess('read');
    const f = teamsApi({ 'teams.generateNames': () => ({ names: ['X'] }) });
    const w = await settings(f.api, 'teams');
    expect(el(w, 'default-manager-name').value).toBe('Lead');
    expect(el<HTMLTextAreaElement>(w, 'member-names').value).toBe('Amara\nTomas\nPriya');
    for (const t of ['default-manager-name', 'default-manager-cli', 'default-member-name-0', 'default-member-cli-0', 'default-member-on-0', 'names-country'])
      expect(el<HTMLInputElement>(w, t).disabled, t).toBe(true);
    expect(el<HTMLTextAreaElement>(w, 'member-names').readOnly).toBe(true);
    for (const t of ['names-generate', 'team-defaults-save', 'default-member-add', 'default-member-remove-0']) {
      const b = el<HTMLButtonElement>(w, t);
      expect(b.disabled, t).toBe(true);
      expect(b.title, t).toBe(READ_ONLY_TITLE);
    }
    await w.get('[data-test=names-generate]').trigger('click');
    await w.get('[data-test=team-defaults-save]').trigger('click');
    await flushPromises();
    expect(f.of('teams.generateNames')).toHaveLength(0);
    expect(f.of('teams.setDefaults')).toHaveLength(0);
  });
});

describe('New Team dialog defaults', () => {
  async function dialog(answers: Record<string, Answer>) {
    const f = fakeApi({ 'agentProfiles.list': () => [], 'teams.create': () => ({ teamId: 9 }), ...answers });
    const w = mount(NewTeamDialog, { props: { api: f.api }, attachTo: document.body });
    wrappers.push(w);
    await flushPromises();
    return { w, ...f };
  }
  const q = <T extends Element = HTMLInputElement>(t: string) => document.querySelector(`[data-test="${t}"]`) as T | null;

  it('starts from teams.defaults: Manager, ticked rows in the list, unticked rows as "+ <name>" buttons', async () => {
    const { w, of } = await dialog({ 'teams.defaults': () => defaults() });
    expect(q('manager-name')!.value).toBe('Lead');
    expect(q<HTMLSelectElement>('manager-cli')!.value).toBe('codex');
    expect(q('member-name-0')!.value).toBe('Developer');
    expect(q<HTMLSelectElement>('member-cli-0')!.value).toBe('claude');
    expect(q('member-name-1')).toBeNull();
    const offers = [...document.querySelectorAll('[data-test=member-offers] button')].map((b) => b.textContent!.trim());
    expect(offers).toEqual(['+ Tester Ada', '+ Researcher']);

    (q<HTMLButtonElement>('member-offer-0')!).click();
    await flushPromises();
    expect(q('member-name-1')!.value).toBe('Tester Ada');
    expect(q<HTMLSelectElement>('member-cli-1')!.value).toBe('codex');
    expect([...document.querySelectorAll('[data-test=member-offers] button')].map((b) => b.textContent!.trim())).toEqual(['+ Researcher']);

    // + Add member starts on the Manager's saved agent; everything stays editable.
    (q<HTMLButtonElement>('add-member-row')!).click();
    await flushPromises();
    expect(q<HTMLSelectElement>('member-cli-2')!.value).toBe('codex');
    const name0 = w.find('[data-test=member-name-0]');
    await name0.setValue('Developer Kofi');
    await w.find('[data-test=member-name-2]').setValue('Writer');
    await w.find('[data-test=team-name]').setValue('alpha');
    await w.find('[data-test=manager-name]').setValue('Chief');
    await w.find('[data-test=create-team]').trigger('click');
    await flushPromises();
    expect(of('teams.create')[0].params).toMatchObject({
      name: 'alpha', managerName: 'Chief', managerCli: 'codex',
      members: [{ name: 'Developer Kofi', cli: 'claude' }, { name: 'Tester Ada', cli: 'codex' }, { name: 'Writer', cli: 'codex' }],
    });
  });

  it('each row picks a model of its agent, defaulting to its agent default; a new agent resets it; only chosen models are sent', async () => {
    const { w, of } = await dialog({
      'teams.defaults': () => defaults(),
      'agentProfiles.list': () => [
        { id: 1, name: 'claude', baseCli: 'claude', custom: false, defaultModel: 'opus', argsBefore: [], argsAfter: [], removedArgs: [] },
        { id: 2, name: 'codex', baseCli: 'codex', custom: false, defaultModel: 'gpt-5.5', argsBefore: [], argsAfter: [], removedArgs: [] },
      ],
      'agents.models': (p: any) => ({ models: p.cli === 'claude' ? ['opus', 'sonnet', 'haiku'] : ['gpt-5.5', 'gpt-5.5-mini'], source: 'builtin' }),
    });
    await w.find('[data-test=team-name]').setValue('alpha');
    const opts = (t: string) => Array.from(q<HTMLSelectElement>(t)!.options).map((o) => o.text);
    expect(opts('manager-model')).toEqual(['default (gpt-5.5)', 'gpt-5.5', 'gpt-5.5-mini', 'Custom…']); // the Manager is on Codex
    expect(opts('member-model-0')).toEqual(['default (opus)', 'opus', 'sonnet', 'haiku', 'Custom…']); // Developer on Claude
    await w.find('[data-test=add-member-row]').trigger('click'); // a new row starts on the Manager's agent, Codex
    await flushPromises();
    await w.find('[data-test=member-name-1]').setValue('Tester Ada');
    expect(opts('member-model-1')).toEqual(['default (gpt-5.5)', 'gpt-5.5', 'gpt-5.5-mini', 'Custom…']);
    await w.find('[data-test=member-model-0]').setValue('sonnet');
    await w.find('[data-test=manager-model]').setValue('gpt-5.5-mini');
    await w.find('[data-test=member-model-1]').setValue('gpt-5.5-mini');
    await w.find('[data-test=member-cli-1]').setValue('claude'); // a new agent: back to its default
    await flushPromises();
    expect(q<HTMLSelectElement>('member-model-1')!.value).toBe('');
    await w.find('[data-test=create-team]').trigger('click');
    await flushPromises();
    const sent = of('teams.create')[0].params;
    expect(sent.managerModel).toBe('gpt-5.5-mini');
    expect(sent.members[0]).toEqual({ name: 'Developer', cli: 'claude', model: 'sonnet' });
    expect(sent.members[1]).toEqual({ name: 'Tester Ada', cli: 'claude' });
  });

  it('with nothing saved (or an older core) it starts with Manager on Claude, no members, no offers', async () => {
    for (const answers of [{ 'teams.defaults': () => defaults({ manager: { name: 'Manager', cli: 'claude' }, members: [] }) }, {}] as Record<string, Answer>[]) {
      await dialog(answers);
      expect(q('manager-name')!.value).toBe('Manager');
      expect(q<HTMLSelectElement>('manager-cli')!.value).toBe('claude');
      expect(q('member-name-0')).toBeNull();
      expect(q('member-offers')).toBeNull();
      expect(q('hire-hint')).not.toBeNull();
      for (const w of wrappers) w.unmount();
      wrappers = [];
    }
  });
});
