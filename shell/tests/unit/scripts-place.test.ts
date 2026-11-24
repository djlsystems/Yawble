// @vitest-environment happy-dom
// B134 (SC-4, card 597): the Scripts place. The nav entry, the Scripts dialog's three tabs, the editor's check problems
// and the jump to a step, the outline, Delete only for an unused script (scripts.delete, faked: DeveloperNoah's core
// card adds it), schemas (built-ins read-only, schema_breaks), the test-run form (dry run by default, the first live run
// asks once per script), the run view, and a Visitor's read-only view. Fakes only: no core; placeholder values only.
import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import ScriptsDialog from '../../src/renderer/src/components/scripts/ScriptsDialog.vue';
import ScriptRunView from '../../src/renderer/src/components/scripts/ScriptRunView.vue';
import ScriptTestRun from '../../src/renderer/src/components/scripts/ScriptTestRun.vue';
import { ASK_CONCIERGE_PROMPT, discussRequest } from '../../src/renderer/src/conciergeDiscuss';
import { confirmState } from '../../src/renderer/src/confirm';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { buildNav } from '../../src/renderer/src/embed';
import {
  buildOutline, fakeTriggerFired, INBOX_SMS_ALERT, jsonTokens, lineOf, pretty, settingFromText, stepRange, writtenFiles,
} from '../../src/renderer/src/scripts/api';
import type { RunDetail, ScriptSummary, TestRunResult } from '../../src/renderer/src/scripts/types';

enableAutoUnmount(afterEach);

const at = '2026-11-23T10:00:00Z';
const teams: TeamSummary[] = [{ id: 1, name: 'Support', repoPath: null, members: 2, running: 0, queued: 0 }, { id: 2, name: 'Ops', repoPath: null, members: 1, running: 0, queued: 0 }];
const summary = (key: string, extra: Partial<ScriptSummary> = {}): ScriptSummary => ({
  id: key === 'inbox-sms-alert' ? 1 : 2, key, name: key === 'inbox-sms-alert' ? 'Inbox SMS alert' : 'Daily note', description: `About ${key}`,
  latestVersion: 3, runnableVersion: 2, draft: false, updatedAt: at, usedBy: [], ...extra,
});
/** The template in the alias form (requires names each member binds), as older scripts and the live InboxWatcher are: it keeps working. */
const ALIAS: any = ((d: any) => {
  const map: Record<string, string> = { 'Support inbox': 'mail', Twilio: 'sms' };
  d.requires = d.requires.map((r: any) => ({ ...r, name: map[r.name] ?? r.name }));
  d.steps = d.steps.map((s: any) => (typeof s.use === 'string' ? { ...s, use: map[s.use] ?? s.use } : s));
  return d;
})(JSON.parse(JSON.stringify(INBOX_SMS_ALERT)));
const coded = (code: string, message: string, data?: unknown) => Object.assign(new Error(message), { code, data });

interface Call { method: string; params: any }
function fakeApi(over: Record<string, (p: any) => unknown> = {}) {
  const calls: Call[] = [];
  const handlers: Record<string, (p: any) => unknown> = {
    'scripts.list': () => [
      summary('inbox-sms-alert', { usedBy: [{ teamId: 1, team: 'Support', memberId: 9, member: 'InboxWatcher', version: null }] }),
      summary('daily-note', { draft: true, latestVersion: 1, runnableVersion: null }),
    ],
    'scripts.get': (p) => ({ id: 1, key: String(p.script), name: 'Inbox SMS alert', description: '', version: p.version ?? 3, ok: true, document: ALIAS, check: { ok: true, issues: [], schemaReferences: [] }, createdAt: at }),
    'scripts.versions': () => [{ version: 3, ok: true, createdAt: at }, { version: 2, ok: true, createdAt: at }, { version: 1, ok: false, createdAt: at }],
    'scripts.save': () => ({ id: 1, key: 'inbox-sms-alert', version: 4, ok: false, draft: true, check: { ok: false, schemaReferences: [], issues: [
      { stepId: 'text', code: 'unknown_ref', message: 'm.subjekt is not a field of EmailMessage' },
      { stepId: null, code: 'bad_setting', message: 'Setting folder has no type' },
    ] } }),
    'scripts.check': () => ({ ok: true, issues: [], schemaReferences: [] }),
    'scripts.delete': () => ({ deleted: true }),
    'scripts.export': () => ({ fileName: 'x.json', json: '{}' }),
    'scripts.runs': () => [
      { id: 41, teamId: 1, team: 'Support', memberId: 9, member: 'InboxWatcher', scriptId: 1, script: 'inbox-sms-alert', version: 3, trigger: 'trigger:7', status: 'succeeded', dryRun: false, test: false, input: {}, outputs: null, failedStepId: null, reason: null, startedAt: at, endedAt: '2026-11-23T10:00:02Z' },
      { id: 40, teamId: 2, team: 'Ops', memberId: null, member: null, scriptId: 2, script: 'daily-note', version: 1, trigger: 'test', status: 'failed', dryRun: true, test: true, input: {}, outputs: null, failedStepId: 'write', reason: 'no folder', startedAt: at, endedAt: at },
    ],
    'scripts.run': (p) => runDetail(p.id),
    'scripts.testRun': () => testResult(),
    'schemas.list': () => [
      { name: 'TriggerFired', description: 'A time trigger fired.', latestVersion: 1, builtIn: true, updatedAt: at },
      { name: 'Triage', description: 'A triage result.', latestVersion: 2, builtIn: false, updatedAt: at },
    ],
    'schemas.get': (p) => ({ name: p.name, description: 'd', version: p.name === 'Triage' ? 2 : 1, builtIn: p.name === 'TriggerFired', schema: { type: 'object', properties: { category: { type: 'string' } } }, createdAt: at }),
    'schemas.save': () => { throw coded('schema_breaks', 'Breaks inbox-sms-alert v3 step text (m.category)', { breaks: [{ scriptId: 1, script: 'inbox-sms-alert', version: 3, stepId: 'text', fieldPath: 'category' }] }); },
    'integrations.apiList': () => [{ kind: 'http', name: 'Twilio', all: false, teams: [1] }, { kind: 'http', name: 'Elsewhere', all: false, teams: [2] }],
    'integrations.connections': () => [{ id: 5, name: 'Support mail', kind: 'email' }],
    'integrations.emailList': () => [{ id: 3, name: 'Support mail', kind: 'email', enabledAll: false, teamIds: [1] }, { id: 4, name: 'Ops mail', kind: 'email', enabledAll: false, teamIds: [2] }],
    ...over,
  };
  const api = {
    getInfo: async () => ({}), getAvailableBackends: async () => [], onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const h = handlers[method];
      if (!h) throw new Error(`no fake for ${method}`);
      return h(params);
    },
  } as unknown as YawbleApi;
  return { api, calls, of: (m: string) => calls.filter((c) => c.method === m) };
}

function runDetail(id: number): RunDetail {
  return {
    id, teamId: 1, team: 'Support', memberId: 9, member: 'InboxWatcher', scriptId: 1, script: 'inbox-sms-alert', version: 3, trigger: 'trigger:7',
    status: 'succeeded', dryRun: false, test: false, input: {}, outputs: { report: { path: 'reports/today.txt' } }, failedStepId: null, reason: null,
    startedAt: at, endedAt: '2026-11-23T10:00:02Z',
    steps: [
      { seq: 1, stepId: 'list', type: 'integration', input: { folder: 'INBOX' }, output: [{ subject: 'Hello' }], status: 'succeeded', startedAt: at, endedAt: '2026-11-23T10:00:00.400Z' },
      { seq: 2, stepId: 'log', type: 'file.appendLine', input: { path: 'run.log', text: 'one line' }, output: null, status: 'succeeded', startedAt: at, endedAt: '2026-11-23T10:00:01Z' },
    ],
    log: [{ at, line: '1 new, 2 texts' }],
  };
}
function testResult(): TestRunResult {
  return { runId: 77, status: 'succeeded', failedStepId: null, reason: null, outputs: null,
    steps: [{ seq: 1, stepId: 'list', type: 'integration', input: {}, output: [], status: 'succeeded', startedAt: at, endedAt: at }], log: ['would POST /Messages.json with To=+15550000000'] };
}

async function dialog(api: YawbleApi) {
  const w = mount(ScriptsDialog, { props: { api, teams, page: true }, attachTo: document.body });
  await flushPromises();
  return w;
}
const rowOf = (w: VueWrapper, key: string) => w.get(`[data-test=script-row][data-key="${key}"]`);
async function answerConfirm(ok: boolean) {
  await flushPromises();
  expect(confirmState.value, 'a confirm is open').not.toBeNull();
  confirmState.value!.resolve(ok);
  await flushPromises();
}

beforeEach(() => { localStorage.clear(); applyRemoteAccess('full'); });
afterEach(() => { applyRemoteAccess('full'); confirmState.value?.resolve(false); document.body.innerHTML = ''; });

describe('the Scripts place in the nav', () => {
  it('the sidebar has Scripts after Monitor, wide and folded, and it goes to the scripts view', async () => {
    const props = { view: 'concierge', conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false };
    const w = mount(Sidebar, { props: props as any });
    const ids = w.findAll('nav.places button').map((b) => b.attributes('data-test'));
    expect(ids.slice(ids.indexOf('nav-monitor'), ids.indexOf('nav-monitor') + 3)).toEqual(['nav-monitor', 'nav-scripts', 'nav-logs']);
    await w.get('[data-test=nav-scripts]').trigger('click');
    expect(w.emitted('go')).toEqual([['scripts']]);
    const rail = mount(Sidebar, { props: { ...props, rail: true, view: 'scripts' } as any });
    expect(rail.get('[data-test=rail-scripts]').classes()).toContain('on');
    expect(rail.get('[data-test=rail-scripts] svg').exists()).toBe(true);
  });
  it('buildNav has the Scripts place (icon scripts), active on the scripts view, and enabled for a Visitor too', () => {
    const s = { view: 'scripts', conversations: [], teams: [], conversationId: null, teamId: null, slots: null, readOnly: true };
    const p = buildNav(s).places.find((x) => x.id === 'scripts')!;
    expect(p).toEqual({ id: 'scripts', label: 'Scripts', icon: 'scripts', badge: null, active: true, disabled: false });
  });
});

describe('the Scripts dialog', () => {
  it('three tabs: Scripts (scripts.list), Schemas (schemas.list) and Runs (scripts.runs with no teamId)', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    expect(w.findAll('[role=tab]').map((t) => t.text())).toEqual(['Scripts', 'Schemas', 'Runs']);
    expect(w.find('[data-test=scripts-library]').exists()).toBe(true);
    expect(f.of('scripts.list')).toHaveLength(1);
    await w.get('[data-test=scripts-tab-schemas]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schemas-tab]').exists()).toBe(true);
    expect(f.of('schemas.list')).toHaveLength(1);
    await w.get('[data-test=scripts-tab-runs]').trigger('click');
    await flushPromises();
    expect(f.of('scripts.runs')[0]!.params).toEqual({});
    const rows = w.findAll('[data-test=run-row]');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain('Support');
    expect(rows[0]!.text()).toContain('InboxWatcher');
    expect(rows[0]!.text()).toContain('inbox-sms-alert@3');
    expect(rows[0]!.text()).toContain('trigger:7');
    expect(rows[0]!.text()).toContain('succeeded');
    expect(rows[1]!.text()).toContain('daily-note@1');
    expect(rows[1]!.text()).toContain('failed');
  });

  it('the library rows: name, description, version and draft, the teams that use it', async () => {
    const w = await dialog(fakeApi().api);
    const used = rowOf(w, 'inbox-sms-alert');
    expect(used.text()).toContain('Inbox SMS alert');
    expect(used.text()).toContain('About inbox-sms-alert');
    expect(used.get('[data-test=script-version]').text()).toBe('v3');
    expect(used.get('[data-test=script-used-by]').text()).toBe('Support / InboxWatcher');
    const draft = rowOf(w, 'daily-note');
    expect(draft.find('[data-test=script-draft]').exists()).toBe(true);
    expect(draft.get('[data-test=script-used-by]').text()).toBe('nobody');
  });

  it('Delete only for a script nobody uses; it asks, then calls scripts.delete with the key', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    expect(rowOf(w, 'inbox-sms-alert').find('[data-test=script-delete]').exists()).toBe(false);
    await rowOf(w, 'daily-note').get('[data-test=script-delete]').trigger('click');
    await answerConfirm(true);
    expect(f.of('scripts.delete').map((c) => c.params)).toEqual([{ script: 'daily-note' }]);
    expect(w.get('[data-test=scripts-note]').text()).toBe('Deleted daily-note.');
    expect(f.of('scripts.list')).toHaveLength(2);
  });

  it('Delete refused with in_use (a member started using it meanwhile) says so; Cancel deletes nothing', async () => {
    const f = fakeApi({ 'scripts.delete': () => { throw coded('in_use', 'used by Support / InboxWatcher'); } });
    const w = await dialog(f.api);
    await rowOf(w, 'daily-note').get('[data-test=script-delete]').trigger('click');
    await answerConfirm(false);
    expect(f.of('scripts.delete')).toHaveLength(0);
    await rowOf(w, 'daily-note').get('[data-test=script-delete]').trigger('click');
    await answerConfirm(true);
    expect(w.get('[data-test=scripts-error]').text()).toContain('daily-note is in use by a member, so it was not deleted: used by Support / InboxWatcher');
  });

  it('New offers Blank and the Inbox SMS alert template; Save lists the check problems by step and a click jumps to the step', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await w.get('[data-test=scripts-new]').trigger('click');
    expect(w.findAll('[data-test=scripts-new-menu] button').map((b) => b.text())).toEqual(['Blank', 'Inbox SMS alert', 'Ask the Concierge']);
    await w.get('[data-test=scripts-new-inbox-sms-alert]').trigger('click');
    await flushPromises();
    // The editor opens on the Flowchart, its first tab; JSON comes next.
    expect(w.findAll('[data-test^=editor-tab-]').map((t) => t.text())).toEqual(['Flowchart', 'JSON', 'Test run']);
    expect(w.get('[data-test=editor-tab-flowchart]').attributes('aria-selected')).toBe('true');
    await w.get('[data-test=editor-tab-json]').trigger('click');
    const area = w.get('[data-test=json-text]').element as HTMLTextAreaElement;
    expect(JSON.parse(area.value)).toEqual(INBOX_SMS_ALERT);
    // Highlighted: the keys and strings get their own classes.
    expect(w.findAll('.t-key').length).toBeGreaterThan(10);
    await w.get('[data-test=editor-save]').trigger('click');
    await flushPromises();
    expect(f.of('scripts.save')[0]!.params).toEqual({ document: area.value });
    expect(w.get('[data-test=editor-saved]').text()).toContain('version 4, a draft');
    const issues = w.findAll('[data-test=check-issue]');
    expect(issues.map((i) => i.attributes('data-step'))).toEqual(['', 'text']); // the document's own first
    expect(issues[1]!.text()).toContain('m.subjekt is not a field of EmailMessage');
    expect(w.get('[data-test=outline-step][data-step=text]').classes()).toContain('bad');
    await w.get('[data-test=check-step][data-step=text]').trigger('click');
    await flushPromises();
    const r = stepRange(area.value, 'text')!;
    expect(area.selectionStart).toBe(r.start);
    expect(area.selectionEnd).toBe(r.end);
    expect(area.value.slice(area.selectionStart, area.selectionEnd)).toBe('"id": "text"');
    expect(area.getAttribute('data-marked-line')).toBe(String(lineOf(area.value, r.start)));
    // The outline's step names jump too.
    await w.get('[data-test=outline-step][data-step=mark] button').trigger('click');
    await flushPromises();
    expect(area.value.slice(area.selectionStart, area.selectionEnd)).toBe('"id": "mark"');
  });

  it('the outline: settings, requires, variables and steps with next and branches', async () => {
    const w = await dialog(fakeApi().api);
    await rowOf(w, 'inbox-sms-alert').get('[data-test=script-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=outline-settings]').text()).toContain('recipients list<string>');
    expect(w.get('[data-test=outline-settings]').text()).toContain('default "INBOX"');
    expect(w.get('[data-test=outline-requires]').text()).toContain('mail email · list');
    expect(w.get('[data-test=outline-variables]').text()).toContain('lastId string · saved');
    const links = (id: string) => w.findAll(`[data-test=outline-step][data-step="${id}"] [data-test=outline-link]`).map((l) => l.text());
    expect(links('any')).toEqual(['then → each', 'else → done']);
    expect(links('each')).toEqual(['body → note', 'then → keep']);
    expect(links('phones')).toEqual(['body → text', 'then → end of body']);
    expect(links('done')).toEqual([]);
    expect(w.get('[data-test=outline-step][data-step=list]').text()).toContain('start');
    // Versions come from scripts.versions; picking one loads it.
    expect(w.findAll('[data-test=editor-version] option').map((o) => o.text())).toEqual(['3', '2', '1 (draft)']);
    // The flowchart has its own tab (script-flowchart.test.ts covers it).
    await w.get('[data-test=editor-tab-flowchart]').trigger('click');
    expect(w.find('[data-test=editor-flowchart]').exists()).toBe(true);
  });

  it('New > Ask the Concierge opens a new Concierge chat (the discuss path) with a first message ready; no editor', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    const before = discussRequest.value?.seq ?? 0;
    await w.get('[data-test=scripts-new]').trigger('click');
    await w.get('[data-test=scripts-new-concierge]').trigger('click');
    expect(discussRequest.value).toMatchObject({ teamId: null, text: ASK_CONCIERGE_PROMPT });
    expect(discussRequest.value!.seq).toBeGreaterThan(before);
    expect(ASK_CONCIERGE_PROMPT).toMatch(/^Help me build a script that/);
    expect(w.find('[data-test=scripts-new-menu]').exists()).toBe(false);
    expect(w.find('[data-test=script-editor]').exists()).toBe(false);
    expect(f.of('scripts.save')).toHaveLength(0);
  });

  it('Test run with unsaved edits uses the saved version\'s document: its settings and bindings in the form and in the RPC', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await rowOf(w, 'inbox-sms-alert').get('[data-test=script-open]').trigger('click');
    await flushPromises();
    // Unsaved: fromNumber renamed to sender, a new setting and a new requires entry.
    const edited = JSON.parse(JSON.stringify(ALIAS));
    edited.settings = edited.settings.map((s: any) => (s.name === 'fromNumber' ? { ...s, name: 'sender' } : s));
    edited.settings.push({ name: 'extra', type: 'string', label: 'Unsaved setting' });
    edited.requires.push({ name: 'files', kind: 'api', actions: ['Get'] });
    await w.get('[data-test=json-text]').setValue(pretty(edited));
    expect(w.find('[data-test=editor-dirty]').exists()).toBe(true);
    await w.get('[data-test=editor-tab-test]').trigger('click');
    await flushPromises();
    expect(w.get('.test .warn').text()).toContain('version 3 as saved');
    const names = (sel: string) => w.findAll(`[data-test^=${sel}-]`).map((x) => x.attributes('data-test'));
    expect(names('test-setting')).toEqual(['test-setting-recipients', 'test-setting-fromNumber', 'test-setting-folder', 'test-setting-since']);
    expect(names('test-binding')).toEqual(['test-binding-mail', 'test-binding-sms']);
    await w.get('[data-test=test-setting-recipients] textarea').setValue('+15550000001');
    await w.get('[data-test=test-setting-fromNumber] input').setValue('+15550000000');
    await w.get('[data-test=script-test-run]').trigger('submit');
    await flushPromises();
    const p = f.of('scripts.testRun')[0]!.params;
    expect(p.version).toBe(3);
    expect(Object.keys(p.settings).sort()).toEqual(['folder', 'fromNumber', 'recipients', 'since']);
    expect(p.settings).toMatchObject({ recipients: ['+15550000001'], fromNumber: '+15550000000' });
    expect(p.bindings).toEqual({ mail: 'Support mail', sms: 'Twilio' });
  });

  it('after a Save, Test run follows the newly saved document (its new setting and requires entry)', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await rowOf(w, 'inbox-sms-alert').get('[data-test=script-open]').trigger('click');
    await flushPromises();
    await w.get('[data-test=editor-tab-test]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=test-setting-extra]').exists()).toBe(false);
    const edited = JSON.parse(JSON.stringify(ALIAS));
    edited.settings.push({ name: 'extra', type: 'string', label: 'Saved setting', default: 'x' });
    edited.requires.push({ name: 'files', kind: 'api', actions: ['Get'] });
    await w.get('[data-test=editor-tab-json]').trigger('click');
    await w.get('[data-test=json-text]').setValue(pretty(edited));
    await w.get('[data-test=editor-save]').trigger('click');
    await flushPromises();
    await w.get('[data-test=editor-tab-test]').trigger('click');
    await flushPromises();
    expect(w.find('.test .warn').exists()).toBe(false);
    expect(w.find('[data-test=test-setting-extra]').exists()).toBe(true);
    expect(w.find('[data-test=test-binding-files]').exists()).toBe(true);
    await w.get('[data-test=script-test-run]').trigger('submit');
    await flushPromises();
    const p = f.of('scripts.testRun')[0]!.params;
    expect(p.version).toBe(4);
    expect(p.bindings).toMatchObject({ mail: 'Support mail', sms: 'Twilio' });
  });

  it('a JSON that does not parse is not saved and says why', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await w.get('[data-test=scripts-new]').trigger('click');
    await w.get('[data-test=scripts-new-blank]').trigger('click');
    await w.get('[data-test=json-text]').setValue('{ "id": ');
    expect(w.get('[data-test=editor-parse]').text()).toContain('Not valid JSON');
    expect(w.get('[data-test=editor-save]').attributes('disabled')).toBeDefined();
  });

  it('Schemas: a built-in is read-only; a save that breaks scripts shows schema_breaks and its list', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await w.get('[data-test=scripts-tab-schemas]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=schema-row][data-name=TriggerFired]').find('[data-test=schema-builtin]').exists()).toBe(true);
    await w.get('[data-test=schema-row][data-name=TriggerFired]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schema-readonly]').exists()).toBe(true);
    expect(w.find('[data-test=schema-save]').exists()).toBe(false);
    await w.get('[data-test=schema-view-json]').trigger('click');
    expect(w.get('[data-test=json-text]').attributes('readonly')).toBeDefined();
    await w.get('[data-test=schema-row][data-name=Triage]').trigger('click');
    await flushPromises();
    await w.get('[data-test=schema-view-json]').trigger('click');
    expect(w.get('[data-test=json-text]').attributes('readonly')).toBeUndefined();
    await w.get('[data-test=schema-save]').trigger('click');
    await flushPromises();
    expect(f.of('schemas.save')[0]!.params.name).toBe('Triage');
    expect(w.get('[data-test=schema-error]').text()).toContain('would break saved scripts');
    expect(w.findAll('[data-test=schema-break]').map((b) => b.text())).toEqual(['inbox-sms-alert v3, step text: category']);
  });

  it('a Runs row opens the run view (scripts.run)', async () => {
    const f = fakeApi();
    const w = await dialog(f.api);
    await w.get('[data-test=scripts-tab-runs]').trigger('click');
    await flushPromises();
    await w.get('[data-test=run-row][data-id="41"]').trigger('click');
    await flushPromises();
    expect(f.of('scripts.run')[0]!.params).toEqual({ id: 41 });
    expect(w.find('[data-test=script-run-view]').exists()).toBe(true);
    await w.get('[data-test=run-back]').trigger('click');
    expect(w.findAll('[data-test=run-row]')).toHaveLength(2);
  });
});

describe('the test run', () => {
  const mountForm = (api: YawbleApi) => mount(ScriptTestRun, { props: { api, script: { id: 1, key: 'inbox-sms-alert' }, version: 3, doc: ALIAS, teams }, attachTo: document.body });

  it('the form: team, settings from the script (defaults filled), a binding per requires entry over the team\'s integrations, dry run on', async () => {
    const f = fakeApi();
    const w = mountForm(f.api);
    await flushPromises();
    expect((w.get('[data-test=test-dry-run]').element as HTMLInputElement).checked).toBe(true);
    expect(w.get('[data-test=test-run-go]').text()).toBe('Test run');
    expect((w.get('[data-test=test-setting-folder] input').element as HTMLInputElement).value).toBe('INBOX');
    expect(w.find('[data-test=test-setting-recipients] textarea').exists()).toBe(true);
    // mail (email) gets the email account; sms (api) the API offered to team 1, not team 2's.
    expect(w.findAll('[data-test=test-binding-mail] option').map((o) => o.text())).toEqual(['(not bound)', 'Support mail']);
    expect(w.findAll('[data-test=test-binding-sms] option').map((o) => o.text())).toEqual(['(not bound)', 'Twilio']);
    expect((w.get('[data-test=test-input-fake]').element as HTMLInputElement).checked).toBe(true);
  });

  it('a dry run sends dryRun true, the settings, the bindings and a fake TriggerFired; no confirm', async () => {
    const f = fakeApi();
    const w = mountForm(f.api);
    await flushPromises();
    await w.get('[data-test=test-setting-recipients] textarea').setValue('+15550000001\n+15550000002\n');
    await w.get('[data-test=test-setting-fromNumber] input').setValue('+15550000000');
    await w.get('form').trigger('submit');
    await flushPromises();
    expect(confirmState.value).toBeNull();
    const p = f.of('scripts.testRun')[0]!.params;
    expect(p).toMatchObject({ script: 1, version: 3, teamId: 1, dryRun: true,
      settings: { recipients: ['+15550000001', '+15550000002'], fromNumber: '+15550000000', folder: 'INBOX' },
      bindings: { mail: 'Support mail', sms: 'Twilio' } });
    expect(p.input).toMatchObject({ triggerId: 'test', kind: 'every' });
    expect(typeof p.input.firedAt).toBe('string');
    expect(w.find('[data-test=script-run-view]').exists()).toBe(true);
    expect(w.get('[data-test=run-log]').text()).toContain('would POST /Messages.json');
  });

  it('the first live run asks; No runs nothing; Yes runs and is remembered for that script key only', async () => {
    const f = fakeApi();
    const w = mountForm(f.api);
    await flushPromises();
    await w.get('[data-test=test-dry-run]').setValue(false);
    expect(w.get('[data-test=test-run-go]').text()).toBe('Run live');
    await w.get('form').trigger('submit');
    await answerConfirm(false);
    expect(f.of('scripts.testRun')).toHaveLength(0);
    await w.get('form').trigger('submit');
    await answerConfirm(true);
    expect(f.of('scripts.testRun').map((c) => c.params.dryRun)).toEqual([false]);
    await w.get('form').trigger('submit');
    await flushPromises();
    expect(confirmState.value).toBeNull();
    expect(f.of('scripts.testRun').map((c) => c.params.dryRun)).toEqual([false, false]);
    // Another script still asks.
    const other = mount(ScriptTestRun, { props: { api: f.api, script: { id: 2, key: 'daily-note' }, doc: INBOX_SMS_ALERT, teams }, attachTo: document.body });
    await flushPromises();
    await other.get('[data-test=test-dry-run]').setValue(false);
    await other.get('form').trigger('submit');
    await answerConfirm(false);
    expect(f.of('scripts.testRun')).toHaveLength(2);
  });

  it('a sample input is sent as given; one that does not parse blocks the run', async () => {
    const f = fakeApi();
    const w = mountForm(f.api);
    await flushPromises();
    await w.get('[data-test=test-input-sample]').setValue(true);
    await w.get('[data-test=test-sample]').setValue('{ "text": ');
    expect(w.get('[data-test=test-run-go]').attributes('disabled')).toBeDefined();
    await w.get('[data-test=test-sample]').setValue('{ "text": "hello" }');
    await w.get('form').trigger('submit');
    await flushPromises();
    expect(f.of('scripts.testRun')[0]!.params.input).toEqual({ text: 'hello' });
  });
});

describe('the run view', () => {
  it('each step with status and time, its input and output on demand, the log and the files it wrote', async () => {
    const w = mount(ScriptRunView, { props: { run: runDetail(41) } });
    const steps = w.findAll('[data-test=run-step]');
    expect(steps.map((s) => s.attributes('data-step'))).toEqual(['list', 'log']);
    expect(steps[0]!.text()).toContain('integration');
    expect(steps[0]!.text()).toContain('succeeded');
    expect(steps[0]!.text()).toContain('400 ms');
    expect(w.find('[data-test=run-step-io]').exists()).toBe(false);
    await steps[0]!.get('[data-test=run-step-toggle]').trigger('click');
    expect(w.get('[data-test=run-step-input]').text()).toContain('"folder": "INBOX"');
    expect(w.get('[data-test=run-step-output]').text()).toContain('"subject": "Hello"');
    expect(w.get('[data-test=run-log]').text()).toContain('1 new, 2 texts');
    expect(w.findAll('[data-test=run-file]').map((f) => f.text())).toEqual(['run.log', 'reports/today.txt']);
    expect(w.get('[data-test=run-status]').text()).toBe('succeeded');
    expect(w.text()).toContain('inbox-sms-alert@3');
  });
  it('a failed run names the step and the reason', () => {
    const w = mount(ScriptRunView, { props: { run: { ...runDetail(40), status: 'failed', failedStepId: 'log', reason: 'disk full' } } });
    expect(w.get('[data-test=run-reason]').text()).toBe('Step log: disk full');
  });
});

describe('a Visitor (read-only)', () => {
  it('sees the scripts, schemas and runs; New, Import, Delete, Save, schema Save and Test run are not offered', async () => {
    applyRemoteAccess('read');
    const f = fakeApi();
    const w = await dialog(f.api);
    expect(w.findAll('[data-test=script-row]')).toHaveLength(2);
    for (const t of ['scripts-new', 'scripts-import', 'script-delete']) expect(w.find(`[data-test=${t}]`).exists(), t).toBe(false);
    expect(w.find('[data-test=script-export]').exists()).toBe(true);
    await rowOf(w, 'inbox-sms-alert').get('[data-test=script-open]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=editor-save]').exists()).toBe(false);
    expect(w.get('[data-test=json-text]').attributes('readonly')).toBeDefined();
    await w.get('[data-test=editor-tab-test]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=script-test-run]').exists()).toBe(true);
    expect(w.find('[data-test=test-run-go]').exists()).toBe(false);
    await w.get('[data-test=editor-back]').trigger('click');
    await flushPromises();
    await w.get('[data-test=scripts-tab-schemas]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schema-new]').exists()).toBe(false);
    await w.get('[data-test=schema-row][data-name=Triage]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schema-save]').exists()).toBe(false);
    await w.get('[data-test=schema-view-json]').trigger('click');
    expect(w.get('[data-test=json-text]').attributes('readonly')).toBeDefined();
    await w.get('[data-test=scripts-tab-runs]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=run-row]')).toHaveLength(2);
    const changes = ['scripts.save', 'scripts.import', 'scripts.delete', 'schemas.save', 'scripts.testRun'];
    expect(f.calls.filter((c) => changes.includes(c.method))).toEqual([]);
  });
});

describe('the helpers', () => {
  it('jsonTokens joins back to the text exactly, valid or not', () => {
    for (const t of [pretty(INBOX_SMS_ALERT), '{ "a": tru, "b": -1.5e3, "c": [null, false] ', '"open string\n{}']) {
      expect(jsonTokens(t).map((x) => x.text).join('')).toBe(t);
    }
    const kinds = jsonTokens('{"k": "v", "n": 2, "b": true}').filter((x) => x.kind !== 'space').map((x) => x.kind);
    expect(kinds).toEqual(['punct', 'key', 'punct', 'string', 'punct', 'key', 'punct', 'number', 'punct', 'key', 'punct', 'literal', 'punct']);
  });
  it('stepRange finds the step in steps, not a same-named id elsewhere', () => {
    const text = pretty({ id: 'done', steps: [{ id: 'a', type: 'end' }, { id: 'done', type: 'end' }] });
    const r = stepRange(text, 'done')!;
    expect(r.start).toBeGreaterThan(text.indexOf('"steps"'));
    expect(stepRange(text, 'missing')).toBeNull();
  });
  it('buildOutline copes with a partial document', () => {
    expect(buildOutline(null)).toEqual({ settings: [], requires: [], variables: [], steps: [] });
    expect(buildOutline({ steps: [{ id: 's', type: 'switch', cases: { a: 'x' }, default: null } as any] }).steps[0]!.links)
      .toEqual([{ label: 'case a', target: 'x' }, { label: 'default', target: null }]);
  });
  it('fakeTriggerFired is a TriggerFired (triggerId a string); settingFromText reads lists and numbers', () => {
    expect(fakeTriggerFired({ trigger: { accepts: ['cron'] } }, new Date('2026-11-23T10:00:00.123Z'))).toEqual({ triggerId: 'test', kind: 'cron', firedAt: '2026-11-23T10:00:00Z' });
    expect(fakeTriggerFired(null).kind).toBe('once');
    expect(settingFromText('list<int>', '1\n 2\n\n')).toEqual([1, 2]);
    expect(settingFromText('bool', 'true')).toBe(true);
  });
  it('writtenFiles: file steps\' paths and path fields in outputs, once each', () => {
    expect(writtenFiles([{ seq: 1, stepId: 'w', type: 'file.writeAllText', input: { path: 'a.txt' }, output: { path: 'C:/data/a.txt' }, status: 'succeeded', startedAt: at, endedAt: at }],
      { files: ['C:/data/a.txt', 'b.txt'] })).toEqual(['C:/data/a.txt', 'b.txt']);
  });
});
it('card 605 independent saved version reproduction', async () => {
 const f = fakeApi(); const w = await dialog(f.api);
 await rowOf(w, 'inbox-sms-alert').get('[data-test=script-open]').trigger('click'); await flushPromises();
 const d: any = JSON.parse(JSON.stringify(ALIAS));
 d.settings = d.settings.filter((s: any) => s.name !== 'folder');
 d.settings.push({ name: 'fresh', type: 'string', default: 'new-value' });
 d.requires.push({ name: 'extraMail', kind: 'email', actions: ['list'] }); d.trigger.accepts = ['cron'];
 await w.get('[data-test=json-text]').setValue(pretty(d)); await w.get('[data-test=editor-tab-test]').trigger('click'); await flushPromises();
 expect(w.find('[data-test=test-setting-folder]').exists()).toBe(true); expect(w.find('[data-test=test-setting-fresh]').exists()).toBe(false); expect(w.find('[data-test=test-binding-extraMail]').exists()).toBe(false);
 await w.get('[data-test=script-test-run]').trigger('submit'); await flushPromises();
 const old = f.of('scripts.testRun')[0]!.params; expect(old.version).toBe(3); expect(old.input.kind).toBe('every'); expect(old.settings).toHaveProperty('folder'); expect(old.settings).not.toHaveProperty('fresh'); expect(old.bindings).not.toHaveProperty('extraMail');
 await w.get('[data-test=editor-tab-json]').trigger('click'); await w.get('[data-test=editor-save]').trigger('click'); await flushPromises(); await w.get('[data-test=editor-tab-test]').trigger('click'); await flushPromises();
 expect(w.find('[data-test=test-setting-folder]').exists()).toBe(false); expect(w.find('[data-test=test-setting-fresh]').exists()).toBe(true); expect((w.get('[data-test=test-binding-extraMail] select').element as HTMLSelectElement).value).toBe('Support mail');
 await w.get('[data-test=script-test-run]').trigger('submit'); await flushPromises();
 const now = f.of('scripts.testRun')[1]!.params; expect(now.version).toBe(4); expect(now.input.kind).toBe('cron'); expect(now.settings).toHaveProperty('fresh', 'new-value'); expect(now.settings).not.toHaveProperty('folder'); expect(now.bindings).toHaveProperty('extraMail', 'Support mail');
});
