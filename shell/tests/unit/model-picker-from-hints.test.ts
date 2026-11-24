// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import AgentModelDialog from '../../src/renderer/src/components/AgentModelDialog.vue';
import AgentEditDialog from '../../src/renderer/src/components/AgentEditDialog.vue';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import NewTeamDialog from '../../src/renderer/src/components/NewTeamDialog.vue';
import { modelOptions, modelOptionText } from '../../src/renderer/src/modelOptions';
import { agentPillTitle } from '../../src/renderer/src/memberModel';
import type { AgentModelList, YawbleApi } from '../../src/shared/api';

// Every model picker shows where each model comes from (agents.models' items and their fromLabel), falls back to the
// plain models with no hint for a core that sends no items, and New team takes a typed model (Custom…) like Settings >
// Agents does. A scripted fake core: no agent, no CLI, no network.
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

const withItems = (cli: string): AgentModelList => cli === 'codex'
  ? {
    models: ['gpt-6.1-sol', 'gpt-5.5', 'gpt-5.5-mini'], source: 'app',
    items: [
      { model: 'gpt-6.1-sol', from: 'settings', fromLabel: 'your Codex settings' },
      { model: 'gpt-5.5', from: 'cli', fromLabel: 'the CLI' },
      { model: 'gpt-5.5-mini', from: 'builtin', fromLabel: 'built-in' },
    ],
  }
  : {
    models: ['opus', 'sonnet', 'haiku-run'], source: 'app',
    items: [
      { model: 'opus', from: 'cli', fromLabel: 'the CLI' },
      { model: 'sonnet', from: 'builtin', fromLabel: 'built-in' },
      { model: 'haiku-run', from: 'runs', fromLabel: 'your runs' },
    ],
  };
const noItems = (cli: string): AgentModelList => ({ models: cli === 'codex' ? ['gpt-5.5', 'gpt-5.5-mini'] : ['opus', 'sonnet'], source: 'app' });

const builtin = (name: string, defaultModel = '') => ({ id: 0, name, baseCli: name, custom: false, defaultModel, argsBefore: [], argsAfter: [], removedArgs: [] });
const lines = { cli: 'codex', agent: 'codex', model: { model: '', source: 'cli', label: "The CLI's default" }, headless: { argv: ['codex'], env: [] }, interactive: { argv: ['codex'], env: [] } };

const wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers.splice(0)) w.unmount(); document.body.innerHTML = ''; });
async function mounted(c: any, props: Record<string, unknown>) {
  const w = mount(c, { props, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return w;
}
const q = <T extends Element = HTMLSelectElement>(t: string) => document.querySelector(`[data-test="${t}"]`) as T | null;
const texts = (t: string) => Array.from(q(t)!.options).map((o) => o.text);
const froms = (t: string) => Array.from(q(t)!.options).map((o) => o.getAttribute('data-from'));
async function choose(t: string, value: string) {
  const el = q(t)!; el.value = value; el.dispatchEvent(new Event('change')); await flushPromises();
}
async function type(t: string, value: string) {
  const el = q<HTMLInputElement>(t)!; el.value = value; el.dispatchEvent(new Event('input')); await flushPromises();
}

describe('the options of a model picker', () => {
  it('items give each model its from hint; no items: the models, no hint', () => {
    expect(modelOptions(withItems('codex')).map(modelOptionText)).toEqual(['gpt-6.1-sol · from your Codex settings', 'gpt-5.5 · from the CLI', 'gpt-5.5-mini · built-in']);
    expect(modelOptions(noItems('codex')).map(modelOptionText)).toEqual(['gpt-5.5', 'gpt-5.5-mini']);
    expect(modelOptions({ models: ['a'], source: 'app', items: [] }).map(modelOptionText)).toEqual(['a']);
    expect(modelOptions(null)).toEqual([]);
  });
});

describe('Settings > Agents: the default model', () => {
  it('shows each model with where it comes from; a picked one is saved as the model alone', async () => {
    const { api, of } = fakeApi({ 'agents.models': (p) => withItems(p.cli), 'agentProfiles.setDefaultModel': () => ({}) });
    await mounted(AgentModelDialog, { api, agent: builtin('codex'), title: 'Codex' });
    expect(texts('model-dialog-pick')).toEqual(["The CLI's default", 'gpt-6.1-sol · from your Codex settings', 'gpt-5.5 · from the CLI', 'gpt-5.5-mini · built-in', 'Custom…']);
    expect(froms('model-dialog-pick')).toEqual([null, 'settings', 'cli', 'builtin', null]);
    await choose('model-dialog-pick', 'gpt-6.1-sol');
    q<HTMLButtonElement>('model-dialog-save')!.click();
    await flushPromises();
    expect(of('agentProfiles.setDefaultModel')[0].params).toEqual({ baseCli: 'codex', model: 'gpt-6.1-sol' });
  });

  it('a typed model (Custom…) is saved like a picked one', async () => {
    const { api, of } = fakeApi({ 'agents.models': (p) => withItems(p.cli), 'agentProfiles.setDefaultModel': () => ({}) });
    await mounted(AgentModelDialog, { api, agent: builtin('codex'), title: 'Codex' });
    await choose('model-dialog-pick', '__custom__');
    expect(q<HTMLButtonElement>('model-dialog-save')!.disabled).toBe(true); // no name yet
    await type('model-dialog-pick-text', '  gpt-7-preview ');
    q<HTMLButtonElement>('model-dialog-save')!.click();
    await flushPromises();
    expect(of('agentProfiles.setDefaultModel')[0].params).toEqual({ baseCli: 'codex', model: 'gpt-7-preview' });
  });

  it('an older core with no items: the plain models, no hint', async () => {
    const { api } = fakeApi({ 'agents.models': (p) => noItems(p.cli) });
    await mounted(AgentModelDialog, { api, agent: builtin('codex'), title: 'Codex' });
    expect(texts('model-dialog-pick')).toEqual(["The CLI's default", 'gpt-5.5', 'gpt-5.5-mini', 'Custom…']);
    expect(froms('model-dialog-pick')).toEqual([null, null, null, null]);
  });
});

describe('the agent editor', () => {
  it('its default-model picker shows the from hints, and the plain list without items', async () => {
    for (const [answer, want] of [
      [withItems, ["The CLI's default", 'opus · from the CLI', 'sonnet · built-in', 'haiku-run · from your runs', 'Custom…']],
      [noItems, ["The CLI's default", 'opus', 'sonnet', 'Custom…']],
    ] as const) {
      const { api } = fakeApi({ 'agents.models': (p) => answer(p.cli), 'agentProfiles.check': () => [], 'agents.commandLine': () => lines });
      await mounted(AgentEditDialog, { api, mode: 'new', baseCli: 'claude', baseTitle: 'Claude Code',
        initial: { name: 'mine', defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] } });
      expect(texts('editor-model')).toEqual(want);
      for (const w of wrappers.splice(0)) w.unmount();
      document.body.innerHTML = '';
    }
  });
});

describe('Member settings', () => {
  function core(models: Answer) {
    const member = { id: 3, teamId: 1, team: 'T', name: 'Ada', role: 'member', cli: 'codex', instructions: '', model: '', credential: 'default' };
    return fakeApi({
      'members.get': () => member,
      'policy.getMember': () => ({ backend: 'x', global: {}, override: {}, loosen: {}, widening: [], violations: [], effective: { notes: [] }, implicit: { readwrite: [], readonly: [], denied: [] }, loosenSummary: [] }),
      'credentials.status': () => ({}),
      'agentProfiles.list': () => [builtin('claude'), builtin('codex')],
      'agents.models': models,
      'agents.commandLine': () => lines,
      'members.update': (p) => ({ ...member, ...p }),
    });
  }

  it('the model picker shows the from hints; Custom… still takes a typed model', async () => {
    const { api } = core((p) => withItems(p.cli));
    await mounted(MemberSettingsDialog, { api, memberId: 3 });
    expect(texts('member-model-pick').slice(1)).toEqual(['gpt-6.1-sol · from your Codex settings', 'gpt-5.5 · from the CLI', 'gpt-5.5-mini · built-in', 'Custom…']);
    expect(froms('member-model-pick').slice(1, 4)).toEqual(['settings', 'cli', 'builtin']);
    await choose('member-model-pick', '__custom__');
    expect(q('member-model')).not.toBeNull();
  });

  it('no items: the plain models', async () => {
    const { api } = core((p) => noItems(p.cli));
    await mounted(MemberSettingsDialog, { api, memberId: 3 });
    expect(texts('member-model-pick').slice(1)).toEqual(['gpt-5.5', 'gpt-5.5-mini', 'Custom…']);
  });
});

describe('New team: the Manager and member rows', () => {
  async function dialog(models: Answer) {
    const f = fakeApi({
      'agentProfiles.list': () => [builtin('claude', 'opus'), builtin('codex')],
      'teams.defaults': () => ({ manager: { name: 'Lead', cli: 'codex' }, members: [{ name: 'Developer', cli: 'claude', onNewTeams: true }], memberNames: [], country: 'en-US', countries: [] }),
      'agents.models': models,
      'teams.create': () => ({ teamId: 9 }),
    });
    await mounted(NewTeamDialog, { api: f.api });
    await type('team-name', 'alpha');
    return f;
  }

  it('each row shows its agent\'s models with their from hints', async () => {
    await dialog((p) => withItems(p.cli));
    expect(texts('manager-model')).toEqual(['default', 'gpt-6.1-sol · from your Codex settings', 'gpt-5.5 · from the CLI', 'gpt-5.5-mini · built-in', 'Custom…']);
    expect(texts('member-model-0')).toEqual(['default (opus)', 'opus · from the CLI', 'sonnet · built-in', 'haiku-run · from your runs', 'Custom…']);
  });

  it('no items: the plain models, no hint', async () => {
    await dialog((p) => noItems(p.cli));
    expect(texts('manager-model')).toEqual(['default', 'gpt-5.5', 'gpt-5.5-mini', 'Custom…']);
    expect(texts('member-model-0')).toEqual(['default (opus)', 'opus', 'sonnet', 'Custom…']);
  });

  it('a typed model on the Manager and on a member is sent like a picked one', async () => {
    const { of } = await dialog((p) => withItems(p.cli));
    await choose('manager-model', '__custom__');
    await type('manager-model-text', ' gpt-7-preview ');
    await choose('member-model-0', '__custom__');
    await type('member-model-0-text', 'claude-next');
    q<HTMLButtonElement>('create-team')!.click();
    await flushPromises();
    const sent = of('teams.create')[0].params;
    expect(sent.managerModel).toBe('gpt-7-preview');
    expect(sent.members[0]).toEqual({ name: 'Developer', cli: 'claude', model: 'claude-next' });
  });

  it('a picked model is sent the same way; a new agent drops a typed model back to the default', async () => {
    const { of } = await dialog((p) => withItems(p.cli));
    await choose('manager-model', 'gpt-6.1-sol');
    await choose('member-model-0', '__custom__');
    await type('member-model-0-text', 'claude-next');
    await choose('member-cli-0', 'codex');
    expect(q('member-model-0')!.value).toBe('');
    expect(q('member-model-0-text')).toBeNull();
    q<HTMLButtonElement>('create-team')!.click();
    await flushPromises();
    const sent = of('teams.create')[0].params;
    expect(sent.managerModel).toBe('gpt-6.1-sol');
    expect(sent.members[0]).toEqual({ name: 'Developer', cli: 'codex' });
  });
});

describe('the member pill', () => {
  it('a model from the CLI\'s own settings says so', () => {
    expect(agentPillTitle('codex', { name: 'gpt-6.1-sol', source: 'settings', at: null })).toBe("codex/gpt-6.1-sol\nSet in the CLI's own settings");
  });
});
