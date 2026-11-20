// @vitest-environment happy-dom
// B110 (card 492): Member settings > Policy: the Folder access block (add -> confirm -> shown with its source, remove ->
// confirm -> gone), one save model ("Unsaved changes", "Discard?" on close), honest results ("Nothing changed"), the changed
// Effective rows highlighted, the Override table under "Advanced", and the missing / relative path warnings.
// Against a small stateful FAKE core (policy.getMember / previewLoosen / saveLoosen / saveOverride); no real program runs.
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import { confirmState } from '../../src/renderer/src/confirm';
import { describeChange, folderRows, isAbsolutePath, samePath } from '../../src/renderer/src/policyFields';
import type { MemberPolicyView, YawbleApi } from '../../src/shared/api';

const member = { id: 2, teamId: 1, team: 'payments', name: 'Ben', role: 'member', cli: 'grok', instructions: '', model: '', credential: 'default' };
const creds = { implementation: 'fake', storage: 'none', storageWarning: '', claude: { mode: 'subscription' }, grok: { mode: 'login' } };

/** A fake core holding one member's override and loosening, merging them roughly as PolicyMerger does. */
function fakeCore(opts: { existing?: string[]; remote?: boolean; picked?: string | null } = {}) {
  const global = { filesystem: { grantableRoots: ['C:/src'], readonly: ['C:/tools'], readwrite: ['C:/scratch'], denied: ['C:/secrets'] },
    network: { allowHosts: [] }, compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'fake' } };
  const implicit = { readwrite: ['C:/clone'], readonly: [], denied: [] };
  const state = { override: {} as any, loosen: {} as any };
  const merge = (override: any, loosen: any) => {
    const rw = [...implicit.readwrite, ...(override.filesystem?.readwrite ?? global.filesystem.readwrite)];
    const ro = [...(override.filesystem?.readonly ?? global.filesystem.readonly)];
    const denied = [...global.filesystem.denied, ...(override.filesystem?.denied ?? [])];
    const loosened: string[] = [];
    for (const p of loosen.filesystem?.readonly ?? []) { if (![...ro, ...rw].includes(p)) { ro.push(p); loosened.push(`filesystem.readonly + ${p}`); } }
    for (const p of loosen.filesystem?.readwrite ?? []) { if (!rw.includes(p)) { rw.push(p); loosened.push(`filesystem.readwrite + ${p}`); } }
    for (const h of loosen.network?.allowHosts ?? []) loosened.push(`network.allowHosts + ${h}`);
    const wall = override.compute?.wallTimeoutSec ?? global.compute.wallTimeoutSec;
    return { readonly: ro, readwrite: rw, denied, allowHosts: loosen.network?.allowHosts ?? [], loosened,
      compute: { wallTimeoutSec: wall, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null }, clipboard: 'none', notes: [] };
  };
  const view = (): MemberPolicyView => {
    const e = merge(state.override, state.loosen);
    return { memberId: 2, global, override: state.override, loosen: state.loosen, loosenSummary: e.loosened, loosenApprovedAt: null,
      effective: e, implicit, widening: [], violations: [], backend: 'fake', machineMemoryMb: 16000 } as any;
  };
  const clone = (x: unknown) => JSON.parse(JSON.stringify(x ?? {}));
  const calls: { method: string; params: any }[] = [];
  const answers: Record<string, (p: any) => unknown> = {
    'members.get': () => ({ ...member }),
    'credentials.status': () => creds,
    'agentProfiles.list': () => [],
    'agents.models': () => ({ models: [] }),
    'agents.commandLine': () => { throw new Error('none'); },
    'policy.teamHosts': () => ({ teamId: 1, hosts: [] }),
    'policy.checkHosts': (p) => (p.hosts as string[]).map((host) => ({ host, problem: null })),
    'policy.getMember': view,
    'policy.previewLoosen': (p) => ({ loosened: merge(state.override, p.loosen).loosened }),
    'policy.saveLoosen': (p) => {
      if (JSON.stringify(merge(state.override, p.loosen).loosened) !== JSON.stringify(p.confirmed)) throw new Error('the confirmation does not match');
      state.loosen = clone(p.loosen);
      return view();
    },
    'policy.saveOverride': (p) => { state.override = clone(p.override); return view(); },
  };
  const existing = new Set(opts.existing ?? ['C:\\work\\shared', 'C:\\data']);
  const api: YawbleApi = {
    remote: opts.remote,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => opts.picked ?? null,
    folderExists: async (p: string) => existing.has(p),
  };
  return { api, calls, state };
}

async function open(api: YawbleApi) {
  const w = mount(MemberSettingsDialog, { props: { memberId: 2, api, initialTab: 'policy' } });
  await flushPromises();
  await flushPromises();
  return w;
}
const rowsOf = (w: any, list: string) => w.findAll(`[data-test=folders-${list}] [data-test=folder-row]`)
  .map((r: any) => `${r.get('[data-test=folder-path]').text()} | ${r.get('[data-test=folder-source]').text()}`);

afterEach(() => { confirmState.value?.resolve(false); });

describe('B110 Folder access', () => {
  it('add -> confirm -> shows in Read/write as this member\'s; reopen -> still there; remove -> confirm -> gone', async () => {
    const core = fakeCore();
    let w = await open(core.api);
    expect(rowsOf(w, 'readwrite')).toEqual(['C:/clone | implicit', 'C:/scratch | global']);
    expect(rowsOf(w, 'readonly')).toEqual(['C:/tools | global']);
    expect(rowsOf(w, 'denied')).toEqual(['C:/secrets | global']);
    expect(w.findAll('[data-test=folder-remove]')).toHaveLength(0); // only the member's own entries can be removed

    await w.get('[data-test=folder-add-path]').setValue('C:\\work\\shared');
    expect(w.get('[data-test=folder-unsaved]').text()).toBe('Unsaved changes');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe('This gives Ben read-write on:');
    expect(w.findAll('[data-test=folder-confirm-item]').map((x) => x.text())).toEqual(['C:\\work\\shared']);
    expect(w.find('[data-test=folder-warning]').exists()).toBe(false);
    expect(core.calls.some((c) => c.method === 'policy.saveLoosen')).toBe(false); // nothing saved before Confirm
    await w.get('[data-test=folder-confirm]').trigger('click');
    await flushPromises();
    const save = core.calls.find((c) => c.method === 'policy.saveLoosen')!.params;
    expect(save.loosen).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] } });
    expect(save.confirmed).toEqual(['filesystem.readwrite + C:\\work\\shared']);
    expect(rowsOf(w, 'readwrite')).toContain('C:\\work\\shared | this member (loosened)');
    expect(w.get('[data-test=settings-saved]').text()).toBe('Read/write now: C:/clone, C:/scratch, C:\\work\\shared. It applies from the next run; a running sandbox keeps its policy.');
    // The change is highlighted: the new folder, and the Effective row.
    const fresh = w.findAll('[data-test=folder-row][data-changed=true]');
    expect(fresh.map((r) => r.attributes('data-path'))).toEqual(['C:\\work\\shared']);
    expect(w.get('[data-test=row-filesystem-readwrite]').attributes('data-changed')).toBe('true');
    expect(w.get('[data-test=row-filesystem-readonly]').attributes('data-changed')).toBeUndefined();
    expect(w.get('[data-test=effective-filesystem-readwrite]').text()).toContain('C:\\work\\shared');
    expect(w.find('[data-test=folder-unsaved]').exists()).toBe(false);
    expect(w.emitted('changed')).toHaveLength(1);
    w.unmount();

    w = await open(core.api);
    expect(rowsOf(w, 'readwrite')).toContain('C:\\work\\shared | this member (loosened)');
    const row = w.findAll('[data-test=folders-readwrite] [data-test=folder-row]').find((r) => r.attributes('data-path') === 'C:\\work\\shared')!;
    await row.get('[data-test=folder-remove]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe("This takes away Ben's read-write access to:");
    expect(core.state.loosen).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] } }); // not yet
    await w.get('[data-test=folder-confirm]').trigger('click');
    await flushPromises();
    expect(core.calls.filter((c) => c.method === 'policy.saveLoosen').at(-1)!.params).toEqual({ memberId: 2, loosen: {}, confirmed: [] });
    expect(rowsOf(w, 'readwrite')).toEqual(['C:/clone | implicit', 'C:/scratch | global']);
    expect(w.get('[data-test=settings-saved]').text()).toContain('Read/write now: C:/clone, C:/scratch.');
  });

  it('Cancel leaves everything as it was; a folder already covered says "Nothing changed" and saves nothing', async () => {
    const core = fakeCore();
    const w = await open(core.api);
    await w.get('[data-test=folder-add-path]').setValue('C:\\data');
    await w.get('[data-test=folder-add-mode]').setValue('readonly');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe('This gives Ben read-only on:');
    await w.get('[data-test=folder-cancel]').trigger('click');
    expect(w.find('[data-test=folder-confirm-box]').exists()).toBe(false);
    expect(core.calls.some((c) => c.method === 'policy.saveLoosen')).toBe(false);

    await w.get('[data-test=folder-add-path]').setValue('C:/scratch');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=folder-confirm-box]').exists()).toBe(false);
    expect(w.get('[data-test=settings-saved]').text()).toBe("Nothing changed: Ben's current access already covers read-only on C:/scratch.");
    expect(core.calls.some((c) => c.method === 'policy.saveLoosen')).toBe(false);
  });

  it('warns before confirm on a relative or missing path, and still saves when the person insists', async () => {
    const core = fakeCore();
    const w = await open(core.api);
    await w.get('[data-test=folder-add-path]').setValue('shared');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-warning]').text()).toContain('"shared" is not a full path');
    w.findAll('[data-test=folder-cancel]')[0].trigger('click');
    await flushPromises();

    await w.get('[data-test=folder-add-path]').setValue('D:\\not\\there');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-warning]').text()).toBe('⚠ D:\\not\\there does not exist on this computer. You can still save it; it applies from the next run, once the folder is there.');
    expect(w.get('[data-test=folder-confirm]').text()).toBe('Confirm anyway');
    await w.get('[data-test=folder-confirm]').trigger('click');
    await flushPromises();
    expect(core.state.loosen).toEqual({ filesystem: { readwrite: ['D:\\not\\there'] } });
    expect(rowsOf(w, 'readwrite')).toContain('D:\\not\\there | this member (loosened)');
  });

  it('Browse… fills the path in the desktop window; a remote view has no Browse (text only)', async () => {
    const desk = await open(fakeCore({ picked: 'C:\\work\\shared' }).api);
    await desk.get('[data-test=folder-browse]').trigger('click');
    await flushPromises();
    expect((desk.get('[data-test=folder-add-path]').element as HTMLInputElement).value).toBe('C:\\work\\shared');
    const remote = await open(fakeCore({ remote: true }).api);
    expect(remote.find('[data-test=folder-browse]').exists()).toBe(false);
    expect(remote.find('[data-test=folder-add-path]').exists()).toBe(true);
  });

  it('an override\'s own denied folder shows as this member\'s and its removal confirms first', async () => {
    const core = fakeCore();
    core.state.override = { filesystem: { denied: ['C:/scratch/keys'] } };
    const w = await open(core.api);
    expect(rowsOf(w, 'denied')).toEqual(['C:/secrets | global', 'C:/scratch/keys | this member (override)']);
    expect((w.get('[data-test=policy-advanced]').element as HTMLDetailsElement).open).toBe(true); // it has an override
    await w.get('[data-test=folders-denied] [data-test=folder-remove]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-confirm-title]').text()).toContain("out of Ben's override");
    await w.get('[data-test=folder-confirm]').trigger('click');
    await flushPromises();
    expect(core.calls.find((c) => c.method === 'policy.saveOverride')!.params.override).toEqual({ filesystem: { denied: [] } });
    expect(rowsOf(w, 'denied')).toEqual(['C:/secrets | global']);
  });
});

describe('B110 one save model and honest results', () => {
  it('Save override with no change says "Nothing changed" (never "Override saved"); a real change says what changed', async () => {
    const core = fakeCore();
    const w = await open(core.api);
    expect((w.get('[data-test=policy-advanced]').element as HTMLDetailsElement).open).toBe(false); // collapsed: no override
    expect(w.get('[data-test=policy-advanced-toggle]').text()).toContain('Advanced: tighten this member (override)');
    expect(w.get('[data-test=policy-advanced]').text()).toContain('can only narrow');
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings-saved]').text()).toBe('Nothing changed.');
    expect(w.emitted('changed')).toBeUndefined();
    expect(w.findAll('tr[data-changed=true]')).toHaveLength(0);

    await w.get('[data-test=inherit-compute-wallTimeoutSec]').setValue(false);
    await w.get('[data-test=override-compute-wallTimeoutSec]').setValue('900');
    expect(w.get('[data-test=override-unsaved-save]').text()).toBe('Unsaved changes');
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings-saved]').text()).toBe('Wall-clock timeout now: 900 (was 7200). It applies from the next run; a running sandbox keeps its policy.');
    expect(w.get('[data-test=row-compute-wallTimeoutSec]').attributes('data-changed')).toBe('true');
    expect(w.find('[data-test=override-unsaved-save]').exists()).toBe(false);
  });

  it('typing then closing asks "Discard?"; Cancel keeps the dialog, Discard closes it; nothing typed closes at once', async () => {
    const core = fakeCore();
    const w = await open(core.api);
    await w.get('[data-test=modal-close]').trigger('click');
    await flushPromises();
    expect(confirmState.value).toBeNull();
    expect(w.emitted('close')).toHaveLength(1);

    await w.get('[data-test=loosen-network-allowHosts]').setValue('registry.npmjs.org');
    expect(w.get('[data-test=loosen-unsaved]').text()).toBe('Unsaved changes');
    await w.get('[data-test=modal-close]').trigger('click');
    await flushPromises();
    expect(confirmState.value!.message).toBe('Policy has unsaved changes. Discard?');
    expect(confirmState.value!.okLabel).toBe('Discard');
    confirmState.value!.resolve(false);
    await flushPromises();
    expect(w.emitted('close')).toHaveLength(1);

    await w.get('[data-test=loosen-network-allowHosts]').setValue('');
    await w.get('[data-test=folder-add-path]').setValue('C:\\data');
    await w.get('[data-test=modal-close]').trigger('click');
    await flushPromises();
    expect(confirmState.value!.message).toContain('Discard?');
    confirmState.value!.resolve(true);
    await flushPromises();
    expect(w.emitted('close')).toHaveLength(2);
  });

  it('a folder save keeps the override and Loosen… text typed and not saved (no edit is silently dropped)', async () => {
    const core = fakeCore();
    const w = await open(core.api);
    await w.get('[data-test=inherit-compute-wallTimeoutSec]').setValue(false);
    await w.get('[data-test=override-compute-wallTimeoutSec]').setValue('600');
    await w.get('[data-test=loosen-network-allowHosts]').setValue('registry.npmjs.org');
    await w.get('[data-test=folder-add-path]').setValue('C:\\data');
    await w.get('[data-test=folder-add]').trigger('click');
    await flushPromises();
    await w.get('[data-test=folder-confirm]').trigger('click');
    await flushPromises();
    expect(core.state.loosen).toEqual({ filesystem: { readwrite: ['C:\\data'] } });
    expect((w.get('[data-test=override-compute-wallTimeoutSec]').element as HTMLInputElement).value).toBe('600');
    expect((w.get('[data-test=loosen-network-allowHosts]').element as HTMLTextAreaElement).value).toBe('registry.npmjs.org');
    expect(w.find('[data-test=override-unsaved-save]').exists()).toBe(true);
    expect(w.find('[data-test=loosen-unsaved]').exists()).toBe(true);
    // The Loosen… block keeps the saved folder when it saves the host.
    await w.get('[data-test=preview-loosen]').trigger('click');
    await flushPromises();
    await w.get('[data-test=confirm-loosen]').trigger('click');
    await flushPromises();
    expect(core.state.loosen).toEqual({ filesystem: { readwrite: ['C:\\data'] }, network: { allowHosts: ['registry.npmjs.org'] } });
    expect(w.get('[data-test=settings-saved]').text()).toContain('Allowed hosts now: registry.npmjs.org.');
    expect(w.find('[data-test=loosen-unsaved]').exists()).toBe(false);
    // The Loosen… block has no folder boxes any more: folders have one place.
    expect(w.find('[data-test=loosen-filesystem-readwrite]').exists()).toBe(false);
  });
});

describe('B110 helpers', () => {
  it('samePath / isAbsolutePath', () => {
    expect(samePath('C:\\Work\\', 'c:/work')).toBe(true);
    expect(samePath('~/src', '/home/pat/src')).toBe(true);
    expect(samePath('/a/b', '/a/bc')).toBe(false);
    for (const p of ['C:\\x', 'C:/x', '/home/x', '~/x', '\\\\srv\\share']) expect(isAbsolutePath(p)).toBe(true);
    for (const p of ['x', '.\\x', 'C:x', '..\\x']) expect(isAbsolutePath(p)).toBe(false);
  });

  it('folderRows lists a loosened entry the core skipped as covered, still removable', () => {
    const v: any = { global: { filesystem: { readwrite: ['C:/w'] } }, implicit: { readwrite: [], readonly: [], denied: [] }, override: {},
      loosen: { filesystem: { readonly: ['C:/w/sub'] } }, effective: { readwrite: ['C:/w'], readonly: [], denied: [], loosened: [] } };
    expect(folderRows(v, 'readonly')).toEqual([{ path: 'C:/w/sub', source: 'loosen', own: 'C:/w/sub', covered: true }]);
  });

  it('describeChange: the same stored policy and effective policy is "Nothing changed"', () => {
    const v: any = { override: { compute: {} }, loosen: {}, effective: { readwrite: ['a'], readonly: [], denied: [], allowHosts: [], compute: {}, clipboard: 'none' } };
    expect(describeChange(v, JSON.parse(JSON.stringify({ ...v, override: {} })))).toEqual({ changed: false, text: 'Nothing changed.' });
    expect(describeChange(v, { ...v, override: { compute: { wallTimeoutSec: 7200 } } }).text).toBe('Saved. The effective policy is the same as before.');
  });
});
