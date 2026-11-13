// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import DiscoverChecklist from '../../src/renderer/src/components/DiscoverChecklist.vue';
import type { DiscoverChecklistData, DiscoverNode, YawbleApi } from '../../src/shared/api';

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

const suggestedOf = (cls: string) => (({ readonly: 'allow', change: 'ask', unknown: 'ask', destructive: 'off', credential: 'never' }) as Record<string, DiscoverNode['suggested']>)[cls] ?? null;
/** A row someone already decided in the default list (so a first-discover prefill leaves it alone). */
const decided = (d: 'allow' | 'ask' | 'deny', team: 'allow' | 'ask' | 'deny' | null = null): Partial<DiscoverNode> =>
  ({ effective: team ?? d, defaultDecision: d, teamDecision: team, decision: team ?? d });

const node = (path: string, o: Partial<DiscoverNode> = {}): DiscoverNode => {
  const words = path.split(' ');
  const cls = o.class ?? 'change';
  return {
    path, parent: words.slice(0, -1).join(' '), name: words[words.length - 1], depth: words.length, summary: `${path} summary`, hasChildren: false,
    class: 'change', source: 'verb', reason: 'verb matches', state: 'free', ticked: false, tickedFrom: null, defaultTick: false, decision: null,
    gone: false, badge: null, seenVersion: '2.45.0',
    suggested: suggestedOf(cls), defaultDecision: null, teamDecision: null, effective: cls === 'credential' ? 'never' : 'ask', origin: 'discovered', ...o,
  };
};

function checklist(extra: Partial<DiscoverChecklistData> = {}, nodes?: DiscoverNode[]): DiscoverChecklistData {
  return {
    cli: 'gh', teamId: 3, version: '2.45.0', discoveredAt: '2026-11-09', runs: 1, partial: false, partialReason: null, error: null, new: 0, changed: 0, gone: 0, choices: 0,
    kind: 'cli', offeredAll: false, offeredTeams: [3], counts: { allowed: 0, ask: 0, off: 0, undecided: 0 },
    nodes: nodes ?? [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', reason: 'verb "list" matches read-only' }),
      node('pr view', { class: 'readonly' }),
      node('pr status', { class: 'readonly' }),
      node('pr create', { class: 'change' }),
      node('repo', { hasChildren: true, class: 'group' }),
      node('repo delete', { class: 'destructive', state: 'refused', reason: 'verb "delete" is destructive' }),
      node('auth', { hasChildren: true, class: 'group' }),
      node('auth token', { class: 'credential', state: 'locked', source: 'floor', reason: 'floor word "token"' }),
    ],
    otherRules: [], ...extra,
  };
}

const teams = [{ id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 }];
const idle = () => ({ state: 'idle' });

async function open(answers: Record<string, (p: any) => unknown> = {}, data = checklist(), props: Record<string, unknown> = {}) {
  const f = fakeApi({ 'integrations.checklist': () => data, 'integrations.discoverStatus': idle, ...answers });
  const w = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams, teamIds: [3], ...props } });
  await flushPromises();
  return { w, ...f };
}

const box = (w: any, p: string) => w.find(`[data-test=tick-${p}]`).element as HTMLInputElement;
const saveOf = (calls: { method: string; params: any }[]) => calls.find((c) => c.method === 'integrations.saveChecklist')!;
const stateOf = (c: { method: string; params: any }) => Object.fromEntries(c.params.choices.map((x: any) => [x.path, x.state]));

describe('discover checklist', () => {
  it('first discover ticks the suggested rows, leaves a destructive one off and locks credentials with the reason on hover', async () => {
    const { w } = await open({}, checklist({ teamId: null }), { initialScope: null });
    expect(box(w, 'pr_list').checked).toBe(true);
    expect(box(w, 'pr_create').checked).toBe(true); // suggested: ask first
    expect(box(w, 'pr_create').disabled).toBe(false);
    expect(box(w, 'repo_delete').checked).toBe(false);
    expect(box(w, 'repo_delete').disabled).toBe(false);
    expect(w.find('[data-test=pill-repo_delete]').text()).toBe('off');
    const locked = w.find('[data-test=tick-auth_token]');
    expect((locked.element as HTMLInputElement).disabled).toBe(true);
    expect(locked.attributes('title')).toContain('floor word "token"');
    expect(locked.attributes('title')).toContain('can never be allowed');
    expect(w.find('[data-test=pill-auth_token]').text()).toBe('never');
    expect(w.find('[data-test=badge-auth_token]').text()).toContain('Locked');
    expect(w.find('[data-test=discover-save]').text()).toBe('Save (5 changes)');
  });

  it('a locked row cannot be ticked, even by a click, and is never sent', async () => {
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 0, asked: 0, denied: 1, removed: 0 }) }, checklist({ teamId: null }), { initialScope: null });
    await w.find('[data-test=tick-auth_token]').setValue(true);
    expect(w.find('[data-test=state-auth_token]').exists()).toBe(false);
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    expect(Object.keys(stateOf(saveOf(calls)))).not.toContain('auth token');
  });

  it('a ticked row offers allowed and ask first, and unticking makes it off', async () => {
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 0, asked: 0, denied: 0, removed: 0 }) }, checklist({ teamId: null }), { initialScope: null });
    const select = w.find('[data-test=state-pr_list]');
    expect(select.findAll('option').map((o) => o.text())).toEqual(['allowed', 'ask first']);
    expect((select.element as HTMLSelectElement).value).toBe('allow');
    await select.setValue('ask');
    await w.find('[data-test=tick-pr_view]').setValue(false);
    expect(w.find('[data-test=state-pr_view]').exists()).toBe(false);
    expect(w.find('[data-test=pill-pr_view]').text()).toBe('off');
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    const sent = stateOf(saveOf(calls));
    expect(sent['pr list']).toBe('ask');
    expect(sent['pr view']).toBe('off');
  });

  it('first discover pre-fills the suggested states and Save writes them as choices', async () => {
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 3, asked: 1, denied: 1, removed: 0 }) }, checklist({ teamId: null }), { initialScope: null });
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    const save = saveOf(calls);
    expect(save.params).toMatchObject({ kind: 'cli', name: 'gh', teamId: null });
    const sent = stateOf(save);
    expect(sent['pr list']).toBe('allow');
    expect(sent['pr create']).toBe('ask');
    expect(sent['repo delete']).toBe('off');
    expect(w.find('[data-test=discover-saved]').text()).toBe('5 rules saved for every team');
  });

  it('new rows on a re-discover stay not decided until touched', async () => {
    const data = checklist({ runs: 2, new: 1, choices: 2 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('allow') }),
      node('pr status', { class: 'readonly', badge: 'new' }),
    ]);
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 1, asked: 0, denied: 0, removed: 0 }) }, data);
    await w.find('[data-test=discover-only-fresh]').setValue(false);
    expect(box(w, 'pr_status').checked).toBe(false);
    expect(w.find('[data-test=pill-pr_status]').text()).toBe('not decided (asks you)');
    expect(w.find('[data-test=discover-save]').attributes('disabled')).toBeDefined();
    expect(calls.some((c) => c.method === 'integrations.saveChecklist')).toBe(false);
    await w.find('[data-test=tick-pr_status]').setValue(true);
    expect(w.find('[data-test=discover-save]').text()).toBe('Save (1 change)');
  });

  it('team scope shows an override with reset to default, which sends default', async () => {
    const data = checklist({ choices: 2 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('allow', 'deny') }),
      node('pr view', { class: 'readonly', ...decided('allow') }),
    ]);
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 0, asked: 0, denied: 0, removed: 1 }) }, data);
    expect(w.find('[data-test=pill-pr_list]').text()).toBe('off');
    expect(w.find('[data-test=reset-pr_view]').exists()).toBe(false);
    expect(w.find('[data-test=node-pr_view]').text()).toContain('from every team');
    await w.find('[data-test=reset-pr_list]').trigger('click');
    expect(w.find('[data-test=reset-pr_list]').exists()).toBe(false);
    expect(box(w, 'pr_list').checked).toBe(true); // back to the default (allowed)
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    expect(saveOf(calls).params.choices).toEqual([{ path: 'pr list', state: 'default' }]);
  });

  it('found in use and built-in rows are tagged', async () => {
    const data = checklist({ runs: 2 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('allow') }),
      node('pr sneaky', { origin: 'found', ...decided('ask') }),
      node('pr_open', { origin: 'builtin', ...decided('ask') }),
    ]);
    const { w } = await open({}, data);
    expect(w.find('[data-test=node-pr_sneaky]').text()).toContain('found in use');
    expect(w.find('[data-test=node-pr_open]').text()).toContain('built-in');
    expect(w.find('[data-test=node-pr_list]').text()).not.toContain('found in use');
  });

  it('a destructive row starts off and can be set to allowed', async () => {
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 1, asked: 0, denied: 0, removed: 0 }) }, checklist({ teamId: null }), { initialScope: null });
    expect(w.find('[data-test=pill-repo_delete]').text()).toBe('off');
    await w.find('[data-test=tick-repo_delete]').setValue(true);
    const select = w.find('[data-test=state-repo_delete]');
    expect(select.findAll('option').map((o) => o.text())).toEqual(['allowed', 'ask first']);
    await select.setValue('allow');
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    expect(stateOf(saveOf(calls))['repo delete']).toBe('allow');
  });

  it('the scope lists only offered teams and opens on initialScope', async () => {
    const two = [...teams, { id: 4, name: 'dev', repoPath: null, members: 1, running: 0, queued: 0 }, { id: 5, name: 'qa', repoPath: null, members: 1, running: 0, queued: 0 }];
    const f = fakeApi({ 'integrations.checklist': () => checklist({ offeredTeams: [3, 4] }), 'integrations.discoverStatus': idle });
    const w = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams: two, teamIds: [3], initialScope: null } });
    await flushPromises();
    const select = w.find('[data-test=discover-scope]');
    expect(select.findAll('option').map((o) => o.text())).toEqual(['Every team (default)', 'Team: ops', 'Team: dev']);
    expect((select.element as HTMLSelectElement).value).toBe('');
    expect(f.calls.find((c) => c.method === 'integrations.checklist')!.params).toMatchObject({ teamId: null, kind: 'cli' });
    const f2 = fakeApi({ 'integrations.checklist': (p) => checklist({ offeredTeams: [3, 4], teamId: p.teamId }), 'integrations.discoverStatus': idle });
    const w2 = mount(DiscoverChecklist, { props: { api: f2.api, cli: 'gh', teams: two, teamIds: [3], initialScope: 4 } });
    await flushPromises();
    expect(f2.calls.find((c) => c.method === 'integrations.checklist')!.params.teamId).toBe(4);
    const f3 = fakeApi({ 'integrations.checklist': () => checklist({ offeredAll: true }), 'integrations.discoverStatus': idle });
    const w3 = mount(DiscoverChecklist, { props: { api: f3.api, cli: 'gh', teams: two, all: true } });
    await flushPromises();
    expect(w3.find('[data-test=discover-scope]').findAll('option').length).toBe(4);
    w.unmount(); w2.unmount(); w3.unmount();
  });

  it('footer counts the offered rows', async () => {
    const { w } = await open({}, checklist({ teamId: null }), { initialScope: null });
    // first discover: pr list/view/status allowed, pr create ask first, repo delete off
    expect(w.find('[data-test=discover-footer]').text()).toBe('4 offered: 3 allowed, 1 ask first');
    await w.find('[data-test=state-pr_list]').setValue('ask');
    expect(w.find('[data-test=discover-footer]').text()).toBe('4 offered: 2 allowed, 2 ask first');
  });

  it('untick all turns a group off, and an MCP server sends kind mcp', async () => {
    const data = checklist({ kind: 'mcp', runs: 2 }, [
      node('search', { class: 'readonly', ...decided('allow') }),
      node('write', { class: 'change', ...decided('ask') }),
    ]);
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 0, asked: 0, denied: 2, removed: 0 }) }, data, { kind: 'mcp', cli: 'files' });
    expect(calls.find((c) => c.method === 'integrations.checklist')!.params.kind).toBe('mcp');
    expect(w.find('[data-test=discover-start-files]').exists()).toBe(true);
    await w.find('[data-test=untick-all]').trigger('click');
    expect(box(w, 'search').checked).toBe(false);
    expect(box(w, 'write').checked).toBe(false);
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    const save = saveOf(calls);
    expect(save.params.kind).toBe('mcp');
    expect(stateOf(save)).toEqual({ search: 'off', write: 'off' });
  });

  it('a group ticks only its read-only leaves, and Save sends them as allowed for the team scope', async () => {
    const data = checklist({ runs: 2, new: 1, choices: 3 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('allow') }),
      node('pr view', { class: 'readonly', ...decided('allow') }),
      node('pr status', { class: 'readonly', badge: 'new' }),
      node('pr create', { class: 'change', ...decided('ask') }),
      node('repo', { hasChildren: true, class: 'group' }),
      node('repo delete', { class: 'destructive', ...decided('deny') }),
    ]);
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 3, asked: 0, denied: 1, removed: 0 }) }, data);
    await w.find('[data-test=discover-only-fresh]').setValue(false);
    await w.find('[data-test=tick-reads-pr]').trigger('click');
    expect(box(w, 'pr_status').checked).toBe(true);
    expect(box(w, 'pr_create').checked).toBe(true); // already ask first, untouched
    expect(box(w, 'repo_delete').checked).toBe(false);
    expect(w.find('[data-test=discover-save]').text()).toBe('Save (1 change)');
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    const save = saveOf(calls);
    expect(save.params.teamId).toBe(3); // the default scope is this team
    expect(save.params.choices).toEqual([{ path: 'pr status', state: 'allow' }]);
  });

  it('tick all read-only leaves a read-only row that is ask first as it is, and a changed row hints it will be set for the team', async () => {
    const data = checklist({ runs: 2, new: 1, choices: 3 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('ask') }),
      node('pr view', { class: 'readonly', ...decided('deny') }),
      node('pr status', { class: 'readonly', badge: 'new' }),
    ]);
    const { w, calls } = await open({ 'integrations.saveChecklist': () => ({ allowed: 2, asked: 0, denied: 0, removed: 0 }) }, data);
    await w.find('[data-test=discover-only-fresh]').setValue(false);
    await w.find('[data-test=tick-reads-pr]').trigger('click');
    expect((w.find('[data-test=state-pr_list]').element as HTMLSelectElement).value).toBe('ask');
    expect(w.find('[data-test=pending-pr_view]').text()).toBe('will be set for ops');
    await w.find('[data-test=discover-save]').trigger('click');
    await flushPromises();
    expect(saveOf(calls).params.choices).toEqual([{ path: 'pr view', state: 'allow' }, { path: 'pr status', state: 'allow' }]);
  });

  it('collapses a group and switches scope to all teams', async () => {
    const { w, calls } = await open();
    await w.find('[aria-label="Close pr"]').trigger('click');
    expect(w.find('[data-test=node-pr_list]').exists()).toBe(false);
    await w.find('[data-test=discover-scope]').setValue('');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.checklist').pop()!.params.teamId).toBeNull();
  });

  it('marks new and changed commands on a re-discover, opens on them, and offers to tick the new reads', async () => {
    const data = checklist({ runs: 2, new: 1, changed: 1, gone: 1, choices: 4 }, [
      node('pr', { hasChildren: true, class: 'group' }),
      node('pr list', { class: 'readonly', ...decided('allow') }),
      node('pr status', { class: 'readonly', badge: 'new' }),
      node('pr create', { class: 'change', badge: 'changed', ...decided('deny') }),
      node('pr old', { class: 'change', gone: true }),
    ]);
    const { w } = await open({}, data);
    expect(w.find('[data-test=discover-banner]').text()).toContain('1 new, 1 changed, 1 removed. Your 4 choices are kept.');
    expect(w.find('[data-test=node-pr_list]').exists()).toBe(false); // filtered to new / changed
    expect(w.findAll('[data-test=mark]').map((m) => m.text())).toEqual(['New', 'Changed']);
    await w.find('[data-test=discover-tick-new]').trigger('click');
    expect((w.find('[data-test=tick-pr_status]').element as HTMLInputElement).checked).toBe(true);
    expect((w.find('[data-test=tick-pr_create]').element as HTMLInputElement).checked).toBe(false);
    expect(w.find('[data-test=discover-gone]').text()).toContain('pr old');
  });

  it('runs a walk with progress and Cancel, then loads the checklist', async () => {
    let state = 'running';
    let loaded = false;
    const f = fakeApi({
      'integrations.checklist': () => { if (!loaded) throw new Error('gh is not set up yet: detect or enable it first'); return checklist(); },
      'integrations.discoverStatus': () => (state === 'running' ? { state, found: 12, seconds: 4 } : { state }),
      'integrations.discover': () => ({ state: 'running', started: true, found: 0 }),
      'integrations.discoverCancel': () => { state = 'cancelled'; return { ok: true }; },
    });
    const w = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams, teamIds: [3] } });
    await flushPromises();
    expect(w.find('[data-test=discover-progress]').text()).toContain('12 commands found');
    await w.find('[data-test=discover-cancel]').trigger('click');
    await flushPromises();
    expect(f.calls.some((c) => c.method === 'integrations.discoverCancel')).toBe(true);
    expect(w.find('[data-test=discover-progress]').exists()).toBe(false);
    expect(w.find('[data-test=discover-start-gh]').text()).toBe('Discover');
    w.unmount();
  });

  it('says so when the help could not be read, and keeps the earlier list', async () => {
    const { w } = await open({}, checklist({ error: 'no help flag worked' }));
    expect(w.find('[data-test=discover-unreadable]').text()).toContain('no help flag worked');
    expect(w.find('[data-test=node-pr_list]').exists()).toBe(true);
  });

  it('never pre-fills a first discover at a team scope: the rows stay not decided', async () => {
    const { w } = await open();
    expect(w.find('[data-test=pill-pr_list]').text()).toBe('not decided (asks you)');
    expect(box(w, 'pr_list').checked).toBe(false);
    expect(w.find('[data-test=discover-save]').attributes('disabled')).toBeDefined();
  });

  it('lists every other rule with a Remove button that deletes it and reloads', async () => {
    let rules = [{ id: 7, teamId: 3, pattern: 'pr*', decision: 'allow' }, { id: 8, teamId: null, pattern: '*--force*', decision: 'deny' }];
    const f = fakeApi({ 'integrations.checklist': () => checklist({ otherRules: rules }), 'integrations.discoverStatus': idle,
      'integrations.deleteRule': (p) => { rules = rules.filter((r) => r.id !== p.id); return { ok: true }; } });
    const w2 = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams, teamIds: [3] } });
    await flushPromises();
    expect(w2.find('[data-test=discover-other]').text()).toContain('pr*');
    expect(w2.find('[data-test=other-remove-8]').exists()).toBe(true);
    await w2.find('[data-test=other-remove-7]').trigger('click');
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'integrations.deleteRule')!.params).toEqual({ id: 7 });
    expect(f.calls.filter((c) => c.method === 'integrations.checklist').length).toBe(2);
    expect(w2.find('[data-test=discover-other]').text()).not.toContain('pr*');
    w2.unmount();
  });

  it('an MCP server removes an other rule with mcpDeleteRule', async () => {
    const data = checklist({ kind: 'mcp', runs: 2, otherRules: [{ id: 4, teamId: null, pattern: 'create_*', decision: 'allow' }] }, [node('search', { class: 'readonly', ...decided('allow') })]);
    const { w, calls } = await open({ 'integrations.mcpDeleteRule': () => ({ ok: true }) }, data, { kind: 'mcp', cli: 'files' });
    await w.find('[data-test=other-remove-4]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.mcpDeleteRule')!.params).toEqual({ id: 4 });
    expect(calls.some((c) => c.method === 'integrations.deleteRule')).toBe(false);
  });

  it('says when a walk it watched has finished, so the dialog re-reads the sign-in', async () => {
    let state = 'running';
    const f = fakeApi({
      'integrations.checklist': () => checklist(),
      'integrations.discoverStatus': () => ({ state }),
      'integrations.discover': () => ({ state: 'running', started: true, found: 0 }),
    });
    const w = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams, teamIds: [3] } });
    await flushPromises();
    expect(w.emitted('discovered')).toBeUndefined();
    state = 'done';
    await new Promise((r) => setTimeout(r, 800));
    await flushPromises();
    expect(w.emitted('discovered')).toHaveLength(1);
    w.unmount();
  });

  it('says a walk it started has finished even when it ends before the first poll', async () => {
    const f = fakeApi({
      'integrations.checklist': () => checklist(),
      'integrations.discoverStatus': () => ({ state: 'done' }),
      'integrations.discover': () => ({ state: 'running', started: true, found: 0 }),
    });
    const w = mount(DiscoverChecklist, { props: { api: f.api, cli: 'gh', teams, teamIds: [3] } });
    await flushPromises();
    expect(w.emitted('discovered')).toBeUndefined();
    await w.find('[data-test=discover-start-gh]').trigger('click');
    await flushPromises();
    expect(w.emitted('discovered')).toHaveLength(1);
    w.unmount();
  });
});
