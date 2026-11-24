// @vitest-environment happy-dom
// Card 493 (B110 verify), TesterKofi: Member settings > Policy against a FAKE core that behaves like the real store and
// merger where B110 depends on it (no real program runs):
//  - the stored row is what PolicyStore writes: override_json / loosen_json are null when the patch is empty, else JSON;
//  - paths are resolved as PolicyPaths does on Windows ("/" -> "\", drive upper case, "." and ".." folded, no trailing "\");
//    a relative path is a violation that previewLoosen / saveLoosen refuse (PolicyException), as the real core does;
//  - saveLoosen refuses a `confirmed` list that is not exactly what the block loosens.
// The original bug (B110 WHY): the screen said "saved" while override_json and loosen_json were both null. Here every
// "... now:" result is checked against the stored row.
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import { confirmState } from '../../src/renderer/src/confirm';
import type { MemberPolicyView, YawbleApi } from '../../src/shared/api';

const member = { id: 7, teamId: 1, team: 'smoke-sessions', name: 'OperatorClifford', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' };
const creds = { implementation: 'fake', storage: 'none', storageWarning: '', claude: { mode: 'subscription' }, grok: { mode: 'login' } };

/** PolicyPaths.Resolve on Windows, lexically (no links followed): null for a relative path. */
function resolve(raw: string): string | null {
  const p = raw.trim().replace(/\//g, '\\');
  const m = /^([a-zA-Z]):\\(.*)$/.exec(p);
  if (!m) return null;
  const out: string[] = [];
  for (const seg of m[2].split('\\')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') out.pop(); else out.push(seg);
  }
  return `${m[1].toUpperCase()}:\\${out.join('\\')}`;
}
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
const within = (p: string, root: string) => same(p, root) || p.toLowerCase().startsWith(root.toLowerCase().replace(/\\?$/, '\\'));
const isEmptyPatch = (d: any) => JSON.stringify(d ?? {}, (_k, v) => (Array.isArray(v) && v.length === 0 ? undefined : v)).replace(/\{\}|,|"[a-zA-Z]+":/g, '').replace(/[{}]/g, '') === '';

function realishCore(opts: { remote?: boolean; picked?: string | null; existing?: string[] } = {}) {
  const global = { filesystem: { grantableRoots: ['C:\\src'], readonly: ['C:\\tools'], readwrite: ['C:\\scratch'], denied: ['C:\\Users\\pat\\.ssh'] },
    network: { allowHosts: ['api.github.com'] }, compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'fake' } };
  const implicit = { readwrite: ['C:\\clone'], readonly: [], denied: [] };
  /** The member_policy row as PolicyStore writes it. */
  const row = { override_json: null as string | null, loosen_json: null as string | null, loosen_summary: null as string | null };
  const parsed = (j: string | null) => (j ? JSON.parse(j) : {});

  const merge = (override: any, loosen: any) => {
    const violations: string[] = [];
    const res = (items: string[] | undefined, field: string) => (items ?? []).flatMap((r) => {
      const x = resolve(r);
      if (x === null) { violations.push(`${field}: '${r}' is not an absolute Windows path (or ~/...)`); return []; }
      return [x];
    });
    const rw = [...implicit.readwrite, ...res(global.filesystem.readwrite, 'global filesystem.readwrite')];
    const ro = [...res(global.filesystem.readonly, 'global filesystem.readonly')];
    const denied = [...global.filesystem.denied, ...res(override.filesystem?.denied, 'override filesystem.denied')];
    const loosened: string[] = [];
    for (const p of res(loosen.filesystem?.readonly, 'loosen filesystem.readonly')) {
      if ([...ro, ...rw].some((g) => within(p, g))) continue;
      ro.push(p); loosened.push(`filesystem.readonly + ${p}`);
    }
    for (const p of res(loosen.filesystem?.readwrite, 'loosen filesystem.readwrite')) {
      if (rw.some((g) => within(p, g))) continue;
      rw.push(p); loosened.push(`filesystem.readwrite + ${p}`);
    }
    const hosts = [...global.network.allowHosts];
    for (const h of loosen.network?.allowHosts ?? []) if (!hosts.includes(h)) { hosts.push(h); loosened.push(`network.allowHosts + ${h}`); }
    const wall = override.compute?.wallTimeoutSec ?? global.compute.wallTimeoutSec;
    return { violations, effective: { readonly: ro, readwrite: rw, denied, allowHosts: hosts, loosened, notes: [],
      compute: { wallTimeoutSec: wall, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null }, clipboard: 'none' } };
  };
  const view = (): MemberPolicyView => {
    const o = parsed(row.override_json), l = parsed(row.loosen_json);
    const m = merge(o, l);
    return { memberId: 7, global, override: o, loosen: l, loosenSummary: row.loosen_summary ? row.loosen_summary.split('\n') : [],
      loosenApprovedAt: null, effective: m.effective, implicit, widening: [], violations: [], backend: 'fake', machineMemoryMb: 16000 } as any;
  };
  const refuse = (errors: string[]) => { if (errors.length) throw new Error(errors.join('; ')); };
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
    'policy.previewLoosen': (p) => { const m = merge(parsed(row.override_json), p.loosen ?? {}); refuse(m.violations); return { loosened: m.effective.loosened }; },
    'policy.saveLoosen': (p) => {
      const m = merge(parsed(row.override_json), p.loosen ?? {});
      const errors = [...m.violations];
      if (JSON.stringify(m.effective.loosened) !== JSON.stringify(p.confirmed)) errors.push('the confirmation does not match what this loosen block loosens; preview it again and confirm the new list');
      refuse(errors);
      const empty = isEmptyPatch(p.loosen);
      row.loosen_json = empty ? null : JSON.stringify(p.loosen);
      row.loosen_summary = empty ? null : (p.confirmed as string[]).join('\n');
      return view();
    },
    'policy.saveOverride': (p) => {
      refuse(merge(p.override ?? {}, parsed(row.loosen_json)).violations);
      row.override_json = isEmptyPatch(p.override) ? null : JSON.stringify(p.override);
      return view();
    },
  };
  const existing = new Set(opts.existing ?? ['C:\\work\\shared', 'Z:\\data']);
  const api: YawbleApi = {
    remote: opts.remote,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params: JSON.parse(JSON.stringify(params ?? null)) });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => opts.picked ?? null,
    folderExists: async (p: string) => existing.has(p),
  } as any;
  return { api, calls, row };
}

async function open(api: YawbleApi) {
  const w = mount(MemberSettingsDialog, { props: { memberId: 7, api, initialTab: 'policy' } });
  await flushPromises(); await flushPromises();
  return w;
}
const rowsOf = (w: any, list: string) => w.findAll(`[data-test=folders-${list}] [data-test=folder-row]`)
  .map((r: any) => `${r.get('[data-test=folder-path]').text()} | ${r.get('[data-test=folder-source]').text()}`);
const savedText = (w: any) => (w.find('[data-test=settings-saved]').exists() ? w.get('[data-test=settings-saved]').text() : '');
const errorText = (w: any) => w.findAll('.error').map((e: any) => e.text()).join(' ');
async function addFolder(w: any, path: string, mode: 'readwrite' | 'readonly' = 'readwrite') {
  await w.get('[data-test=folder-add-path]').setValue(path);
  await w.get('[data-test=folder-add-mode]').setValue(mode);
  await w.get('[data-test=folder-add]').trigger('click');
  await flushPromises();
}
async function confirmPending(w: any) {
  await w.get('[data-test=folder-confirm]').trigger('click');
  await flushPromises();
}
/** Clicks the dialog's close and says what the window asked (null: it closed without asking). */
async function closeAsks(w: any): Promise<string | null> {
  await w.get('[data-test=modal-close]').trigger('click');
  await flushPromises();
  const asked = confirmState.value?.message ?? null;
  return asked;
}

afterEach(() => { confirmState.value?.resolve(false); });

describe('card 493: B110 verify against a core-like fake', () => {
  it('the original bug: two read/write folders, each "now:" result is really in the stored loosen_json; reopen shows them', async () => {
    const core = realishCore();
    let w = await open(core.api);
    expect(core.row).toEqual({ override_json: null, loosen_json: null, loosen_summary: null });

    await addFolder(w, 'C:\\work\\shared');
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe('This gives OperatorClifford read-write on:');
    expect(w.findAll('[data-test=folder-confirm-item]').map((x) => x.text())).toEqual(['C:\\work\\shared']);
    expect(core.row.loosen_json).toBeNull(); // nothing stored before Confirm
    await confirmPending(w);
    expect(savedText(w)).toContain('Read/write now:');
    expect(savedText(w)).toContain('C:\\work\\shared');
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] } });

    await addFolder(w, 'Z:\\data');
    await confirmPending(w);
    expect(savedText(w)).toContain('Z:\\data');
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\shared', 'Z:\\data'] } });
    expect(core.row.loosen_summary).toBe('filesystem.readwrite + C:\\work\\shared\nfilesystem.readwrite + Z:\\data');
    expect(core.row.override_json).toBeNull();
    w.unmount();

    w = await open(core.api);
    expect(rowsOf(w, 'readwrite')).toEqual(['C:\\clone | implicit', 'C:\\scratch | global',
      'C:\\work\\shared | this member (loosened)', 'Z:\\data | this member (loosened)']);
    expect(w.get('[data-test=effective-filesystem-readwrite]').text()).toContain('Z:\\data');
  });

  it('read-only add is stored under readonly; Remove -> confirm -> stored loosen back to null and gone after reopen', async () => {
    const core = realishCore();
    let w = await open(core.api);
    await addFolder(w, 'C:\\work\\shared', 'readonly');
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe('This gives OperatorClifford read-only on:');
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readonly: ['C:\\work\\shared'] } });
    expect(rowsOf(w, 'readonly')).toContain('C:\\work\\shared | this member (loosened)');
    w.unmount();

    w = await open(core.api);
    const r = w.findAll('[data-test=folders-readonly] [data-test=folder-row]').find((x) => x.attributes('data-path') === 'C:\\work\\shared')!;
    await r.get('[data-test=folder-remove]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe("This takes away OperatorClifford's read-only access to:");
    expect(core.row.loosen_json).not.toBeNull(); // not before Confirm
    await confirmPending(w);
    expect(core.row.loosen_json).toBeNull();
    w.unmount();
    w = await open(core.api);
    expect(rowsOf(w, 'readonly')).toEqual(['C:\\tools | global']);
  });

  it('a folder typed with "/" and a trailing slash: the core resolves it, the row is still this member\'s, Remove takes it out', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'c:/work/shared/');
    expect(w.findAll('[data-test=folder-confirm-item]').map((x) => x.text())).toEqual(['C:\\work\\shared']);
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['c:/work/shared/'] } });
    expect(rowsOf(w, 'readwrite')).toContain('C:\\work\\shared | this member (loosened)');
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('already covered'))).toEqual([]);
    const r = w.findAll('[data-test=folders-readwrite] [data-test=folder-row]').find((x) => x.attributes('data-path') === 'C:\\work\\shared')!;
    await r.get('[data-test=folder-remove]').trigger('click');
    await flushPromises();
    await confirmPending(w);
    expect(core.row.loosen_json).toBeNull();
  });

  // FAULT (reported in B110\verify.md): samePath does not fold "." / ".." as the core does, so a folder typed with ".." is
  // listed twice (resolved, and again as "already covered, not added") and Remove on the resolved row saves the same block
  // back ("Nothing changed.") instead of taking it out. Fixed in card 492's fix round: now a plain it().
  it('a folder typed with ".." is listed once and Remove on it takes it out', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'C:\\work\\x\\..\\shared');
    await confirmPending(w);
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual(['C:\\work\\shared | this member (loosened)']);
    const r = w.findAll('[data-test=folders-readwrite] [data-test=folder-row]').find((x) => x.attributes('data-path') === 'C:\\work\\shared')!;
    await r.get('[data-test=folder-remove]').trigger('click');
    await flushPromises();
    await confirmPending(w);
    expect(core.row.loosen_json).toBeNull();
  });

  it('Save override with no change: "Nothing changed.", never "saved"; the stored row is as before', async () => {
    const core = realishCore();
    const w = await open(core.api);
    expect(w.get('[data-test=policy-advanced]').attributes('open')).toBeUndefined(); // collapsed with no override
    expect(w.get('[data-test=policy-advanced]').text()).toContain('An override can only narrow the global policy for this member; it never gives more.');
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(savedText(w)).toBe('Nothing changed.');
    expect(savedText(w).toLowerCase()).not.toContain('saved');
    expect(core.row).toEqual({ override_json: null, loosen_json: null, loosen_summary: null });
  });

  it('a real override change says what changed, is stored, and opens Advanced on reopen; the Effective row is highlighted', async () => {
    const core = realishCore();
    let w = await open(core.api);
    await w.get('[data-test=inherit-compute-wallTimeoutSec]').setValue(false);
    await w.get('[data-test=override-compute-wallTimeoutSec]').setValue('900');
    expect(w.find('[data-test=override-unsaved-save]').exists()).toBe(true);
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(savedText(w)).toContain('900 (was 7200)');
    expect(JSON.parse(core.row.override_json!)).toEqual({ compute: { wallTimeoutSec: 900 } });
    expect(w.get('[data-test=row-compute-wallTimeoutSec]').attributes('data-changed')).toBe('true');
    expect(w.get('[data-test=row-filesystem-readwrite]').attributes('data-changed')).toBeUndefined();
    w.unmount();
    w = await open(core.api);
    expect(w.get('[data-test=policy-advanced]').attributes('open')).toBeDefined();
  });

  it('closing asks "Discard?" for text in the folder field, a pending confirm, the Override table and the Loosen box', async () => {
    const core = realishCore();
    const cases: [string, (w: any) => Promise<void>][] = [
      ['folder field', async (w) => { await w.get('[data-test=folder-add-path]').setValue('C:\\work\\shared'); }],
      ['pending confirm', async (w) => { await addFolder(w, 'C:\\work\\shared'); expect(w.find('[data-test=folder-confirm-box]').exists()).toBe(true); }],
      ['override box', async (w) => { await w.get('[data-test=inherit-filesystem-denied]').setValue(false); await w.get('[data-test=override-filesystem-denied]').setValue('C:\\private'); }],
      ['override inherit only', async (w) => { await w.get('[data-test=inherit-compute-wallTimeoutSec]').setValue(false); }],
      ['loosen hosts', async (w) => { await w.get('[data-test=loosen-network-allowHosts]').setValue('pypi.org'); }],
    ];
    for (const [name, type] of cases) {
      const w = await open(core.api);
      expect(await closeAsks(w), `${name}: nothing typed`).toBeNull();
      expect(w.emitted('close'), name).toHaveLength(1);
      w.unmount();
      const v = await open(core.api);
      await type(v);
      expect(await closeAsks(v), name).toBe('Policy has unsaved changes. Discard?');
      confirmState.value!.resolve(false);
      await flushPromises();
      expect(v.emitted('close'), `${name}: Cancel keeps the dialog`).toBeUndefined();
      await v.get('[data-test=modal-close]').trigger('click');
      await flushPromises();
      confirmState.value!.resolve(true);
      await flushPromises();
      expect(v.emitted('close'), `${name}: Discard closes`).toHaveLength(1);
      v.unmount();
    }
    expect(core.row).toEqual({ override_json: null, loosen_json: null, loosen_summary: null });
  });

  it('Escape with an unsaved folder path asks "Discard?" too', async () => {
    const core = realishCore();
    const w = mount(MemberSettingsDialog, { props: { memberId: 7, api: core.api, initialTab: 'policy' }, attachTo: document.body });
    await flushPromises(); await flushPromises();
    await w.get('[data-test=folder-add-path]').setValue('C:\\work\\shared');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flushPromises();
    expect(confirmState.value?.message).toBe('Policy has unsaved changes. Discard?');
    expect(w.emitted('close')).toBeUndefined();
    w.unmount();
  });

  // FAULT (reported in B110\verify.md): the Policy tab is v-if, so FolderAccess is unmounted by a tab switch: the typed path
  // (and a pending confirm) is gone with no word, and closing then closes without "Discard?". Fixed in card 492's fix round: now a plain it().
  it('a folder path typed then the General tab and back: the path is kept and closing still asks (nothing dropped silently)', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await w.get('[data-test=folder-add-path]').setValue('C:\\work\\shared');
    await w.get('[data-test=tab-general]').trigger('click');
    await flushPromises();
    await w.get('[data-test=tab-policy]').trigger('click');
    await flushPromises();
    const kept = (w.get('[data-test=folder-add-path]').element as HTMLInputElement).value;
    const asks = await closeAsks(w);
    // Either the typed path is still there (and closing asks), or it is gone and closing does not pretend otherwise.
    expect({ kept, asks }).toEqual({ kept: 'C:\\work\\shared', asks: 'Policy has unsaved changes. Discard?' });
  });

  it('saving one section keeps what is typed in the others; a Loosen… host save keeps the saved folders', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await w.get('[data-test=inherit-filesystem-denied]').setValue(false);
    await w.get('[data-test=override-filesystem-denied]').setValue('C:\\private');
    await w.get('[data-test=loosen-network-allowHosts]').setValue('pypi.org');
    await addFolder(w, 'C:\\work\\shared');
    await confirmPending(w);
    expect((w.get('[data-test=override-filesystem-denied]').element as HTMLTextAreaElement).value).toBe('C:\\private');
    expect((w.get('[data-test=loosen-network-allowHosts]').element as HTMLTextAreaElement).value).toBe('pypi.org');
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] } });

    await w.get('[data-test=preview-loosen]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=loosen-preview]').text()).toContain('network.allowHosts + pypi.org');
    await w.get('[data-test=confirm-loosen]').trigger('click');
    await flushPromises();
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] }, network: { allowHosts: ['pypi.org'] } });
    expect(rowsOf(w, 'readwrite')).toContain('C:\\work\\shared | this member (loosened)');
    expect((w.get('[data-test=override-filesystem-denied]').element as HTMLTextAreaElement).value).toBe('C:\\private');

    // A folder added after the host keeps the host in the stored block.
    await addFolder(w, 'Z:\\data', 'readonly');
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'], readonly: ['Z:\\data'] }, network: { allowHosts: ['pypi.org'] } });
  });

  it('a missing absolute folder: warning before confirm, "Confirm anyway" stores it', async () => {
    const core = realishCore({ existing: [] });
    const w = await open(core.api);
    await addFolder(w, 'D:\\not-there');
    expect(w.get('[data-test=folder-warning]').text()).toContain('D:\\not-there does not exist on this computer. You can still save it; it applies from the next run');
    expect(w.get('[data-test=folder-confirm]').text()).toBe('Confirm anyway');
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['D:\\not-there'] } });
  });

  it('a relative folder against a core that refuses it (as PolicyMerger does): the core\'s words, nothing stored, never "saved"', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'shared');
    expect(w.find('[data-test=folder-confirm-box]').exists()).toBe(false);
    expect(errorText(w)).toContain("'shared' is not an absolute Windows path");
    expect(savedText(w)).toBe('');
    expect(core.row.loosen_json).toBeNull();
  });

  it('Browse… only in the desktop window: it fills the path; the remote view has none', async () => {
    const desk = realishCore({ picked: 'C:\\work\\shared' });
    const w = await open(desk.api);
    await w.get('[data-test=folder-browse]').trigger('click');
    await flushPromises();
    expect((w.get('[data-test=folder-add-path]').element as HTMLInputElement).value).toBe('C:\\work\\shared');
    w.unmount();
    const remote = realishCore({ remote: true });
    const r = await open(remote.api);
    expect(r.find('[data-test=folder-browse]').exists()).toBe(false);
    expect(r.find('[data-test=folder-add-path]').exists()).toBe(true);
  });
});

// Card 493 re-check of 9d7d0af (Ines' fix round): the two faults in the real dialog, the fallback and the root rule.
const toGeneralAndBack = async (w: any) => {
  await w.get('[data-test=tab-general]').trigger('click');
  await flushPromises();
  expect(w.find('[data-test=folder-access]').exists()).toBe(false);
  await w.get('[data-test=tab-policy]').trigger('click');
  await flushPromises();
};
const pathValue = (w: any) => (w.get('[data-test=folder-add-path]').element as HTMLInputElement).value;
const modeValue = (w: any) => (w.get('[data-test=folder-add-mode]').element as HTMLSelectElement).value;
const removeRow = async (w: any, list: string, path: string) => {
  const r = w.findAll(`[data-test=folders-${list}] [data-test=folder-row]`).find((x: any) => x.attributes('data-path') === path);
  expect(r, `row ${path}`).toBeTruthy();
  await r.get('[data-test=folder-remove]').trigger('click');
  await flushPromises();
};

describe('card 493 re-check: 9d7d0af', () => {
  it('tab round trip with a typed path, read-only mode and a pending add: all kept; Close asks; Confirm saves what it showed', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'C:\\work\\shared', 'readonly');
    expect(w.findAll('[data-test=folder-confirm-item]').map((x) => x.text())).toEqual(['C:\\work\\shared']);
    await toGeneralAndBack(w);
    await toGeneralAndBack(w); // twice: nothing drifts
    expect(pathValue(w)).toBe('C:\\work\\shared');
    expect(modeValue(w)).toBe('readonly');
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe('This gives OperatorClifford read-only on:');
    expect(w.findAll('[data-test=folder-confirm-item]').map((x) => x.text())).toEqual(['C:\\work\\shared']);
    // Close on General asks too.
    await w.get('[data-test=tab-general]').trigger('click');
    await flushPromises();
    expect(await closeAsks(w)).toBe('Policy has unsaved changes. Discard?');
    confirmState.value!.resolve(false);
    await flushPromises();
    expect(w.emitted('close')).toBeUndefined();
    await w.get('[data-test=tab-policy]').trigger('click');
    await flushPromises();
    expect(core.row.loosen_json).toBeNull();
    await confirmPending(w);
    expect(errorText(w)).toBe('');
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readonly: ['C:\\work\\shared'] } });
    expect(core.row.loosen_summary).toBe('filesystem.readonly + C:\\work\\shared');
    expect(savedText(w)).toContain('C:\\work\\shared');
    expect(rowsOf(w, 'readonly')).toContain('C:\\work\\shared | this member (loosened)');
    expect(pathValue(w)).toBe('');
    expect(w.find('[data-test=folder-confirm-box]').exists()).toBe(false);
    // Saved: nothing left, so Close closes straight away.
    expect(await closeAsks(w)).toBeNull();
    expect(w.emitted('close')).toHaveLength(1);
  });

  it('tab round trip with a pending Remove: kept, Confirm removes it (stored row back to null)', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'C:\\work\\shared');
    await confirmPending(w);
    await removeRow(w, 'readwrite', 'C:\\work\\shared');
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe("This takes away OperatorClifford's read-write access to:");
    await toGeneralAndBack(w);
    expect(w.get('[data-test=folder-confirm-title]').text()).toBe("This takes away OperatorClifford's read-write access to:");
    expect(await closeAsks(w)).toBe('Policy has unsaved changes. Discard?');
    confirmState.value!.resolve(false);
    await flushPromises();
    await confirmPending(w);
    expect(core.row.loosen_json).toBeNull();
    expect(rowsOf(w, 'readwrite')).toEqual(['C:\\clone | implicit', 'C:\\scratch | global']);
  });

  it('Discard after a round trip really drops the draft: a new dialog starts empty', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'C:\\work\\shared');
    await toGeneralAndBack(w);
    expect(await closeAsks(w)).toBe('Policy has unsaved changes. Discard?');
    confirmState.value!.resolve(true);
    await flushPromises();
    expect(w.emitted('close')).toHaveLength(1);
    w.unmount();
    const v = await open(core.api);
    expect(pathValue(v)).toBe('');
    expect(v.find('[data-test=folder-confirm-box]').exists()).toBe(false);
    expect(await closeAsks(v)).toBeNull();
    expect(core.row.loosen_json).toBeNull();
  });

  it('a ".." entry stored before the dialog opens: listed once after reopen; Remove takes the stored spelling out', async () => {
    const core = realishCore();
    let w = await open(core.api);
    await addFolder(w, 'C:\\work\\x\\..\\shared');
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\x\\..\\shared'] } });
    w.unmount();
    w = await open(core.api);
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual(['C:\\work\\shared | this member (loosened)']);
    await removeRow(w, 'readwrite', 'C:\\work\\shared');
    await confirmPending(w);
    expect(savedText(w)).not.toBe('Nothing changed.');
    expect(core.row.loosen_json).toBeNull();
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual([]);
  });

  it('".." next to a plain entry: each listed once, Remove on either takes out only its own spelling', async () => {
    const core = realishCore();
    const w = await open(core.api);
    await addFolder(w, 'C:\\work\\x\\..\\shared');
    await confirmPending(w);
    await addFolder(w, 'Z:\\data\\.\\sub\\..');
    await confirmPending(w);
    expect(rowsOf(w, 'readwrite')).toEqual(['C:\\clone | implicit', 'C:\\scratch | global',
      'C:\\work\\shared | this member (loosened)', 'Z:\\data | this member (loosened)']);
    await removeRow(w, 'readwrite', 'Z:\\data');
    await confirmPending(w);
    expect(JSON.parse(core.row.loosen_json!)).toEqual({ filesystem: { readwrite: ['C:\\work\\x\\..\\shared'] } });
  });

  it('fallback: previewLoosen of single entries failing still gives one row per folder, and Remove still works', async () => {
    const core = realishCore();
    let w = await open(core.api);
    await addFolder(w, 'C:\\work\\x\\..\\shared');
    await confirmPending(w);
    await addFolder(w, 'C:\\scratch\\inside'); // covered by the global C:\scratch
    w.unmount();
    // Every previewLoosen now fails (the core is down for it) while the dialog opens.
    let fail = true;
    const call = core.api.call;
    (core.api as any).call = async (m: string, p?: any) => {
      if (fail && m === 'policy.previewLoosen') throw new Error('preview down');
      return call(m, p);
    };
    w = await open(core.api);
    expect(errorText(w)).toBe(''); // the background previews do not shout
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual(['C:\\work\\shared | this member (loosened)']);
    fail = false;
    await removeRow(w, 'readwrite', 'C:\\work\\shared');
    await confirmPending(w);
    expect(core.row.loosen_json).toBeNull();
  });

  it('fallback while previews have not returned yet: rows are sane from the first paint', async () => {
    const core = realishCore();
    let w = await open(core.api);
    await addFolder(w, 'C:\\work\\x\\..\\shared');
    await confirmPending(w);
    w.unmount();
    const call = core.api.call;
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    (core.api as any).call = async (m: string, p?: any) => { if (m === 'policy.previewLoosen') await gate; return call(m, p); };
    w = await open(core.api);
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual(['C:\\work\\shared | this member (loosened)']);
    release();
    await flushPromises(); await flushPromises();
    expect(rowsOf(w, 'readwrite').filter((x: string) => x.includes('shared'))).toEqual(['C:\\work\\shared | this member (loosened)']);
  });

  it('samePath: ".." never climbs above a root (C:, \\\\srv\\share, ~, /), relative ".." is kept', async () => {
    const { samePath } = await import('../../src/renderer/src/policyFields');
    expect(samePath('C:\\..\\..\\work', 'C:\\work')).toBe(true);
    expect(samePath('C:\\..', 'C:\\')).toBe(true);
    expect(samePath('\\\\srv\\share\\..\\..\\x', '\\\\srv\\share\\x')).toBe(true);
    expect(samePath('\\\\srv\\share\\..\\x', '\\\\srv\\x')).toBe(false);
    expect(samePath('\\\\srv\\share\\..\\x', '\\\\srv\\x\\share')).toBe(false);
    expect(samePath('~/../x', '~/x')).toBe(true);
    expect(samePath('/../../etc', '/etc')).toBe(true);
    expect(samePath('/..', '/')).toBe(true);
    expect(samePath('..\\x', 'x')).toBe(false);
    expect(samePath('a\\..\\..\\x', '..\\x')).toBe(true);
    expect(samePath('C:\\work\\..\\..\\..\\Windows', 'C:\\Windows')).toBe(true);
    expect(samePath('C:\\work\\shared\\..', 'C:\\work\\shared')).toBe(false);
  });
});
