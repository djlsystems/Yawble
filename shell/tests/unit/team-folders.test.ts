// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import TeamFolders from '../../src/renderer/src/components/TeamFolders.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { folderRows, SOURCE_LABEL } from '../../src/renderer/src/policyFields';
import type { MemberPolicyView, TeamFolder, TeamSummary, YawbleApi } from '../../src/shared/api';

/**
 * Team settings > Folders: the team's folder list, "Add a folder" with a confirm step that states what it grants,
 * Remove with a confirm, the core's refusal as plain text, and a read-only view with no controls. A fake API only: no
 * core, no real folder, no picker dialog.
 */
function fakeApi(answers: Record<string, (params: any) => unknown>, opts: { remote?: boolean; pick?: string | null; exists?: boolean } = {}) {
  const calls: { method: string; params: any }[] = [];
  const picks: number[] = [];
  const api = {
    ...(opts.remote ? { remote: true } : {}),
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
    pickFolder: async () => { picks.push(1); return opts.pick ?? null; },
    folderExists: async () => opts.exists ?? true,
  } as unknown as YawbleApi;
  return { api, calls, picks };
}

const folder = (path: string, over: Partial<TeamFolder> = {}): TeamFolder => ({ path, access: 'read', addedBy: 'micro', addedVia: 'app', addedAt: '2026-11-27T10:00:00Z', ...over });
const rpcError = (code: string, message: string) => Object.assign(new Error(message), { code });
const SESSIONS = 'Z:\\smokesessions';

const mounted: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T): T => { mounted.push(w); return w; };
afterEach(() => {
  while (mounted.length) { try { mounted.pop()!.unmount(); } catch { /* already */ } }
  applyRemoteAccess('full');
  document.body.innerHTML = '';
});

async function open(answers: Record<string, (params: any) => unknown>, opts: Parameters<typeof fakeApi>[1] = {}) {
  const fake = fakeApi(answers, opts);
  const w = keep(mount(TeamFolders, { props: { teamId: 4, teamName: 'Alpha', api: fake.api } }));
  await flushPromises();
  return { w, ...fake };
}

const addCalls = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'policy.addTeamFolder').map((c) => c.params);
const removeCalls = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'policy.removeTeamFolder').map((c) => c.params);

describe('TeamFolders: the list', () => {
  it('says what it is, and lists each folder with its access, who added it, how and when', async () => {
    const { w, calls } = await open({
      'policy.teamFolders': (p) => ({ teamId: p.teamId, folders: [folder(SESSIONS), folder('D:\\output', { access: 'readwrite', addedBy: 'ines', addedVia: 'cloud' })] }),
    });
    expect(calls).toEqual([{ method: 'policy.teamFolders', params: { teamId: 4 } }]);
    const about = w.get('[data-test=team-folders-about]').text();
    expect(about).toContain('every member of Alpha may use');
    expect(about).toContain('members hired later too');
    const rows = w.findAll('[data-test=team-folder]');
    expect(rows.map((r) => r.get('[data-test=team-folder-path]').text())).toEqual([SESSIONS, 'D:\\output']);
    expect(rows.map((r) => r.get('[data-test=team-folder-access]').text())).toEqual(['read-only', 'read-write']);
    const added = rows[1].get('[data-test=team-folder-added]').text();
    expect(added).toContain('ines');
    expect(added).toContain('via cloud');
    expect(rows[0].get('[data-test=team-folder-remove]').text()).toBe('Remove');
  });

  it('says so when the team has none', async () => {
    const { w } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) });
    expect(w.get('[data-test=team-folders-empty]').text()).toBe('No folders are shared with the whole team.');
    expect(w.find('[data-test=team-folder]').exists()).toBe(false);
  });

  it('shows a path as plain text, never HTML', async () => {
    const { w } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [folder('Z:\\<img src=x onerror=1>')] }) });
    expect(w.find('img').exists()).toBe(false);
    expect(w.get('[data-test=team-folder-path]').text()).toBe('Z:\\<img src=x onerror=1>');
  });
});

describe('TeamFolders: Add a folder', () => {
  it('states exactly what it grants, and adds nothing until Confirm', async () => {
    let list: TeamFolder[] = [];
    const { w, calls } = await open({
      'policy.teamFolders': () => ({ teamId: 4, folders: list }),
      'policy.addTeamFolder': (p) => { list = [folder(p.path, { access: p.access })]; return { teamId: 4, folders: list }; },
    });
    expect((w.get('[data-test=team-folder-add]').element as HTMLButtonElement).disabled).toBe(true); // nothing typed
    await w.get('[data-test=team-folder-path-input]').setValue(`  ${SESSIONS}  `);
    await w.get('[data-test=team-folder-access-readwrite]').setValue(true);
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(addCalls(calls)).toEqual([]);
    expect(w.get('[data-test=team-folder-confirm-text]').text()).toBe(
      `This gives every member of Alpha read-write access to ${SESSIONS}: they can read and change the files in it, from their next runs. Members hired later get it too.`);
    expect(w.get('[data-test=team-folder-confirm]').text()).toBe('Give the team this folder');

    await w.get('[data-test=team-folder-confirm]').trigger('click');
    await flushPromises();
    expect(addCalls(calls)).toEqual([{ teamId: 4, path: SESSIONS, access: 'readwrite' }]);
    expect(w.get('[data-test=team-folder]').attributes('data-access')).toBe('readwrite');
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);
    expect((w.get('[data-test=team-folder-path-input]').element as HTMLInputElement).value).toBe('');
    expect(w.get('[data-test=team-folders-note]').text()).toContain(`Added ${SESSIONS}, read-write`);
    expect(w.emitted('changed')).toHaveLength(1);
  });

  it('read-only is the first choice, and its confirm says read, not change', async () => {
    const { w, calls } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }), 'policy.addTeamFolder': (p) => ({ teamId: 4, folders: [folder(p.path)] }) });
    expect((w.get('[data-test=team-folder-access-read]').element as HTMLInputElement).checked).toBe(true);
    await w.get('[data-test=team-folder-path-input]').setValue(SESSIONS);
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folder-confirm-text]').text()).toContain(`read-only access to ${SESSIONS}: they can read, and not change, the files in it`);
    await w.get('[data-test=team-folder-confirm]').trigger('click');
    await flushPromises();
    expect(addCalls(calls)).toEqual([{ teamId: 4, path: SESSIONS, access: 'read' }]);
  });

  it('Cancel, or a change to the path or the access, drops the step waiting for Confirm', async () => {
    const { w, calls } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) });
    await w.get('[data-test=team-folder-path-input]').setValue(SESSIONS);
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    await w.get('[data-test=team-folder-cancel]').trigger('click');
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);

    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(true);
    await w.get('[data-test=team-folder-access-readwrite]').setValue(true);
    await flushPromises();
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false); // what was to be confirmed is not what is picked now
    expect(addCalls(calls)).toEqual([]);
  });

  it('a path that is not a full path is refused here, with no confirm step', async () => {
    const { w, calls } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) });
    await w.get('[data-test=team-folder-path-input]').setValue('data\\sessions');
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folders-error]').text()).toContain('is not a full path');
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);
    expect(addCalls(calls)).toEqual([]);
  });

  it('the core\'s refusal is shown as it says it, and nothing is added', async () => {
    const refusal = "'C:\\Users\\pat\\.ssh' is a folder that keeps the person's secrets or keys (C:\\Users\\pat\\.ssh); it is never granted";
    const { w, calls } = await open({
      'policy.teamFolders': () => ({ teamId: 4, folders: [] }),
      'policy.addTeamFolder': () => { throw rpcError('policy_refused', refusal); },
    });
    await w.get('[data-test=team-folder-path-input]').setValue('C:\\Users\\pat\\.ssh');
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    await w.get('[data-test=team-folder-confirm]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folders-error]').text()).toBe(refusal);
    expect(w.find('[data-test=team-folder]').exists()).toBe(false);
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);
    expect(calls.filter((c) => c.method === 'policy.teamFolders')).toHaveLength(2); // read again
    expect(w.emitted('changed')).toBeUndefined();
    expect((w.get('[data-test=team-folder-path-input]').element as HTMLInputElement).value).toBe('C:\\Users\\pat\\.ssh'); // kept, to correct
  });

  it('a folder on the list with the other access: the confirm says it changes; with the same access nothing is asked', async () => {
    const { w, calls } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [folder(SESSIONS)] }), 'policy.addTeamFolder': (p) => ({ teamId: 4, folders: [folder(SESSIONS, { access: p.access })] }) });
    await w.get('[data-test=team-folder-path-input]').setValue('z:\\SmokeSessions\\');
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folders-note]').text()).toContain('is on the list already, read-only');
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);

    await w.get('[data-test=team-folder-access-readwrite]').setValue(true);
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folder-confirm-text]').text()).toContain(`This changes ${SESSIONS} from read-only to read-write for every member of Alpha`);
    expect(addCalls(calls)).toEqual([]);
  });

  it('a folder that is not on this computer yet is said before Confirm', async () => {
    const { w } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) }, { exists: false });
    await w.get('[data-test=team-folder-path-input]').setValue(SESSIONS);
    await w.get('[data-test=team-folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folder-warning]').text()).toContain(`${SESSIONS} does not exist on this computer`);
    expect(w.find('[data-test=team-folder-confirm]').exists()).toBe(true);
  });

  it('Browse opens the desktop window\'s folder picker and fills the path; a remote view types it', async () => {
    const desktop = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) }, { pick: 'D:\\picked' });
    await desktop.w.get('[data-test=team-folder-browse]').trigger('click');
    await flushPromises();
    expect(desktop.picks).toHaveLength(1);
    expect((desktop.w.get('[data-test=team-folder-path-input]').element as HTMLInputElement).value).toBe('D:\\picked');
    expect(desktop.w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false); // picking adds nothing

    const remote = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) }, { remote: true });
    expect(remote.w.find('[data-test=team-folder-browse]').exists()).toBe(false);
    expect(remote.w.find('[data-test=team-folder-path-input]').exists()).toBe(true);
  });
});

describe('TeamFolders: Remove', () => {
  it('asks first, saying what the team loses; Cancel keeps it; Confirm calls policy.removeTeamFolder', async () => {
    let list = [folder(SESSIONS), folder('D:\\output', { access: 'readwrite' })];
    const { w, calls } = await open({
      'policy.teamFolders': () => ({ teamId: 4, folders: list }),
      'policy.removeTeamFolder': (p) => { list = list.filter((f) => f.path !== p.path); return { teamId: 4, folders: list }; },
    });
    await w.findAll('[data-test=team-folder-remove]')[0].trigger('click');
    expect(removeCalls(calls)).toEqual([]);
    expect(w.get('[data-test=team-folder-confirm-text]').text()).toBe(
      `This takes ${SESSIONS} away from every member of Alpha, from their next runs. A member that has the folder on its own Policy tab keeps it.`);
    expect(w.get('[data-test=team-folder-confirm]').text()).toBe('Remove for the team');
    await w.get('[data-test=team-folder-cancel]').trigger('click');
    expect(w.findAll('[data-test=team-folder]')).toHaveLength(2);

    await w.findAll('[data-test=team-folder-remove]')[0].trigger('click');
    await w.get('[data-test=team-folder-confirm]').trigger('click');
    await flushPromises();
    expect(removeCalls(calls)).toEqual([{ teamId: 4, path: SESSIONS }]);
    expect(w.findAll('[data-test=team-folder]').map((r) => r.attributes('data-path'))).toEqual(['D:\\output']);
    expect(w.emitted('changed')).toHaveLength(1);
  });

  it('removed meanwhile in another view: the core\'s words, and the list is read again', async () => {
    let list = [folder(SESSIONS)];
    const { w } = await open({
      'policy.teamFolders': () => ({ teamId: 4, folders: list }),
      'policy.removeTeamFolder': () => { list = []; throw rpcError('not_found', `'${SESSIONS}' is not on this team's folder list`); },
    });
    await w.get('[data-test=team-folder-remove]').trigger('click');
    await w.get('[data-test=team-folder-confirm]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-folders-error]').text()).toBe(`'${SESSIONS}' is not on this team's folder list`);
    expect(w.find('[data-test=team-folders-empty]').exists()).toBe(true);
    expect(w.emitted('changed')).toBeUndefined();
  });
});

describe('TeamFolders: a read-only view (Visitor)', () => {
  it('sees the list and no controls', async () => {
    applyRemoteAccess('read');
    const { w, calls } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [folder(SESSIONS)] }) }, { remote: true });
    expect(w.get('[data-test=team-folder-path]').text()).toBe(SESSIONS);
    expect(w.get('[data-test=team-folder-access]').text()).toBe('read-only');
    expect(w.find('[data-test=team-folder-remove]').exists()).toBe(false);
    expect(w.find('[data-test=team-folder-add-row]').exists()).toBe(false);
    expect(w.find('[data-test=team-folder-confirm-box]').exists()).toBe(false);
    expect(calls.map((c) => c.method)).toEqual(['policy.teamFolders']);
  });
});

describe('TeamFolders: on a phone', () => {
  it('is one column with the phone class its 44 px tap targets hang on', async () => {
    const before = Object.getOwnPropertyDescriptor(window, 'matchMedia');
    Object.defineProperty(window, 'matchMedia', { configurable: true, value: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }) });
    try {
      const { w } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [folder(SESSIONS)] }) });
      expect(w.get('[data-test=team-folders]').classes()).toContain('phone');
    } finally {
      if (before) Object.defineProperty(window, 'matchMedia', before); else delete (window as any).matchMedia;
    }
    const { w } = await open({ 'policy.teamFolders': () => ({ teamId: 4, folders: [] }) });
    expect(w.get('[data-test=team-folders]').classes()).not.toContain('phone');
  });
});

describe('Team settings', () => {
  it('has a Folders tab right after Network, with the list and where one member\'s own folders are', async () => {
    const { api } = fakeApi({
      'policy.teamFolders': () => ({ teamId: 4, folders: [folder(SESSIONS)] }),
      'policy.teamHosts': () => ({ teamId: 4, hosts: [] }),
      'teams.quiet': () => ({ quiet: true, working: [], openDeliverables: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.syncToBase': () => ({ on: true, note: null }),
      'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [],
      'integrations.list': () => ({ integrations: [] }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
    });
    const team = { id: 4, name: 'Alpha', paused: false, archived: false } as TeamSummary;
    const w = keep(mount(TeamManageDialog, { props: { team, api } }));
    await flushPromises();
    const tabs = w.findAll('[data-test=manage-tabs] button').map((b) => b.text());
    expect(tabs[tabs.indexOf('Network') + 1]).toBe('Folders');
    await w.get('[data-test=manage-tab-folders]').trigger('click');
    await flushPromises();
    const pane = w.get('[data-test=team-folders-tab]');
    expect(pane.get('[data-test=team-folder-path]').text()).toBe(SESSIONS);
    expect(pane.text()).toContain("A folder for one member only is in that member's settings, on its Policy tab.");
    expect(pane.text()).toContain('you approve it in Needs you, for the member, this team or all teams');
  });
});

describe('Member settings > Policy: a folder from the team\'s list', () => {
  it('is shown as the team\'s, with no Remove of its own', () => {
    const view = {
      memberId: 7, global: {}, override: {}, loosen: { filesystem: { readonly: ['Z:\\own'] } }, loosenSummary: [], loosenApprovedAt: null,
      effective: {
        readonly: ['Z:\\own', SESSIONS], readwrite: ['D:\\output'], denied: [], allowHosts: [], compute: {}, clipboard: 'none', notes: [],
        loosened: ['filesystem.readonly + Z:\\own', `filesystem.readonly + ${SESSIONS} (team)`, 'filesystem.readwrite + D:\\output (team)'],
      },
      implicit: { readwrite: [], readonly: [], denied: [] }, widening: [], violations: [],
    } as unknown as MemberPolicyView;
    const readonly = folderRows(view, 'readonly');
    expect(readonly).toEqual([
      { path: 'Z:\\own', source: 'loosen', own: 'Z:\\own' },
      { path: SESSIONS, source: 'team', own: null },
    ]);
    expect(folderRows(view, 'readwrite')).toEqual([{ path: 'D:\\output', source: 'team', own: null }]);
    expect(SOURCE_LABEL.team).toBe('the team (its Settings > Folders)');
  });
});
