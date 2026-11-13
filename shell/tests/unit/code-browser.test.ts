// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import AgentCardView from '../../src/renderer/src/components/AgentCard.vue';
import CodeDialog from '../../src/renderer/src/components/CodeDialog.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import LinkDialog from '../../src/renderer/src/components/LinkDialog.vue';
import { closeCode, codeTarget, openCode } from '../../src/renderer/src/codeDialog';
import { isUiMethod, type AgentCard, type CoreEvent, type LinkStatus, type YawbleApi } from '../../src/shared/api';

// The shell side of the Code browser: the card's Code icon, the one dialog, the Cloud Link setting.
// The core is a fake answering the code.* calls.

const NOW = Date.now();
const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperAda', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, ...over,
});

function fakeCore(answers: Record<string, (params: any) => unknown> = {}) {
  const calls: { method: string; params: any }[] = [];
  const listeners = new Set<(e: CoreEvent) => void>();
  let n = 0;
  const all: Record<string, (params: any) => unknown> = {
    'code.open': (p) => ({ session: `s${++n}-${p.member}`, root: 'C:/clones/x', latest: { path: 'README.md', line: 1 }, clonePresent: true }),
    'code.list': (p) => ({ path: p.path, entries: [{ name: 'README.md', path: 'README.md', kind: 'file', size: 60, mtime: new Date(NOW - 5000).toISOString(), marker: 'live', hidden: false }] }),
    'code.read': (p) => ({ path: p.path, type: 'markdown', size: 60, content: '# Notes\n\n<script>alert(1)</script>\n<img src=x onerror="alert(1)">\n[x](javascript:alert(1))',
      lines: [{ from: 1, to: 1, kind: 'added', at: new Date(NOW - 5000).toISOString() }], removed: [], linesUnknown: false }),
    'code.download': (p) => ({ name: 'README.md', base64: btoa('# Notes'), size: 7 }),
    'code.close': () => ({}),
    ...answers,
  };
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = all[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: (l) => { listeners.add(l); return () => listeners.delete(l); },
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls, called: (m: string) => calls.filter((c) => c.method === m) };
}

const wrappers: { unmount: () => void }[] = [];
afterEach(() => { closeCode(); while (wrappers.length) wrappers.pop()!.unmount(); });
const keep = <T extends { unmount: () => void }>(w: T) => { wrappers.push(w); return w; };

describe('the Code browser in the shell', () => {
  it('the core methods are allowed to the window', () => {
    for (const m of ['code.open', 'code.list', 'code.read', 'code.download', 'code.close']) expect(isUiMethod(m)).toBe(true);
  });

  it("the member card's Code icon shows only when the member has a clone, and asks to open it", async () => {
    const without = mount(AgentCardView, { props: { card: card() } });
    expect(without.find('[data-test=open-code]').exists()).toBe(false);
    const noClone = mount(AgentCardView, { props: { card: card({ hasClone: false }) } });
    expect(noClone.find('[data-test=open-code]').exists()).toBe(false);
    const w = mount(AgentCardView, { props: { card: card({ hasClone: true }) } });
    const icon = w.get('[data-test=agent-title] [data-test=open-code]');
    expect(icon.attributes('title')).toBe('Code');
    await icon.trigger('click');
    expect(w.emitted('code')).toHaveLength(1);
  });

  it('the team page opens the dialog for the member whose icon was clicked', async () => {
    const core = fakeCore({
      'teams.agents': () => [card({ hasClone: true })], 'bus.decisions': () => [], 'integrations.asks': () => [],
    });
    const w = keep(mount(TeamView, { props: { team: { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }, api: core.api, pollMs: 100000 },
      global: { stubs: { TeamSyncBanner: true } }, attachTo: document.body }));
    await flushPromises();
    await w.get('[data-test=open-code]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=code-dialog]').attributes('data-member')).toBe('DeveloperAda');
    expect(core.called('code.open')[0].params).toEqual({ team: 'payments', member: 'DeveloperAda' });
  });

  it('one dialog at a time: opening another member replaces it (and closes its session); Escape closes it', async () => {
    const core = fakeCore();
    const w = keep(mount(CodeDialog, { props: { api: core.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    expect(w.findAll('[data-test=code-dialog]')).toHaveLength(1);
    openCode('payments', 'TesterBo');
    await flushPromises();
    expect(w.findAll('[data-test=code-dialog]')).toHaveLength(1);
    expect(w.get('[data-test=code-dialog]').attributes('data-member')).toBe('TesterBo');
    expect(core.called('code.close').map((c) => c.params.session)).toEqual(['s1-DeveloperAda']);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await flushPromises();
    expect(codeTarget.value).toBeNull();
    expect(w.find('[data-test=code-dialog]').exists()).toBe(false);
    expect(core.called('code.close').map((c) => c.params.session)).toEqual(['s1-DeveloperAda', 's2-TesterBo']);
  });

  it("opens on the latest change, and renders markdown with the app's markdown-it: no raw HTML, no script links", async () => {
    const core = fakeCore();
    const w = keep(mount(CodeDialog, { props: { api: core.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    expect(core.called('code.read')[0].params).toEqual({ session: 's1-DeveloperAda', path: 'README.md' });
    // Opened on a change: the source lines with the change highlighted; Formatted shows the safe render.
    expect(w.get('[data-test=code-line-1]').classes()).toContain('recent');
    await w.get('[data-test=code-source]').trigger('click');
    const view = w.get('[data-test=code-markdown]');
    expect(view.find('h1').text()).toBe('Notes');
    expect(view.find('script').exists()).toBe(false);
    expect(view.find('img').exists()).toBe(false);
    expect(view.find('a[href^="javascript:"]').exists()).toBe(false);
    expect(view.text()).toContain('<script>alert(1)</script>');
  });

  it("agent markdown's images (a local file, a UNC share, the web) are never loaded: each shows as its alt text", async () => {
    const content = ['# Notes', '![key](C:/Users/me/key.png)', String.raw`![share](\\attacker\share\x.png)`, '![pixel](https://t.example.com/p.gif)'].join('\n\n');
    const core = fakeCore({ 'code.read': (p) => ({ path: p.path, type: 'markdown', size: content.length, content, lines: [], removed: [], linesUnknown: false }) });
    const w = keep(mount(CodeDialog, { props: { api: core.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    await w.get('[data-test=code-source]').trigger('click'); // opened on a change (its lines): to the formatted view
    const view = w.get('[data-test=code-markdown]');
    expect(view.find('img').exists()).toBe(false);
    expect(view.html()).not.toMatch(/\bsrc=/);
    expect(view.findAll('[data-test=code-md-image]').map((n) => n.text())).toEqual(['[image: key]', '[image: share]', '[image: pixel]']);
  });

  it('a burst of code.changed for the open file reads it again once in flight and once more, not once per event', async () => {
    const releases: (() => void)[] = [];
    let reads = 0;
    const file = (p: any) => ({ path: p.path, type: 'text', size: 3, content: 'a\nb\n', lines: [], removed: [], linesUnknown: false });
    const listeners = new Set<(e: CoreEvent) => void>();
    const core = fakeCore({
      'code.open': () => ({ session: 's1', root: 'C:/clones/x', latest: { path: 'a.ts', line: 1 }, clonePresent: true }),
      'code.read': (p) => (++reads === 1 ? file(p) : new Promise((resolve) => releases.push(() => resolve(file(p))))),
    });
    const onEvent = core.api.onEvent;
    core.api.onEvent = (l) => { listeners.add(l); const off = onEvent(l); return () => { listeners.delete(l); off(); }; };
    keep(mount(CodeDialog, { props: { api: core.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    for (let i = 0; i < 8; i++) {
      for (const l of listeners) l({ event: 'code.changed', data: { session: 's1', path: 'a.ts', ranges: [], at: new Date().toISOString() } } as CoreEvent);
      await flushPromises();
    }
    expect(core.called('code.read')).toHaveLength(2);
    releases.shift()!();
    await flushPromises();
    releases.shift()?.();
    await flushPromises();
    expect(core.called('code.read')).toHaveLength(3);
  });

  it("the listing's markers use the theme's state colours, and Download calls the core", async () => {
    const created = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:x');
    const core = fakeCore();
    const w = keep(mount(CodeDialog, { props: { api: core.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    expect(w.get('[data-test="code-entry-README.md"] .cb-dot').attributes('data-marker')).toBe('live');
    await w.get('[data-test=code-download]').trigger('click');
    await flushPromises();
    expect(core.called('code.download')[0].params).toEqual({ session: 's1-DeveloperAda', path: 'README.md', offset: 0 });
    expect(created).toHaveBeenCalled();
    created.mockRestore();
  });

  it('says so when the member has no clone, and shows the not-shown state of a binary file', async () => {
    const none = fakeCore({ 'code.open': () => ({ session: 's1', root: '', latest: null, clonePresent: false }) });
    const w = keep(mount(CodeDialog, { props: { api: none.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    expect(w.get('[data-test=code-no-clone]').text()).toContain('has no clone');
    w.unmount(); wrappers.pop(); closeCode();

    const bin = fakeCore({ 'code.read': (p) => ({ path: p.path, type: 'notShown', notShownReason: 'binary', size: 3.4 * 1024 * 1024, lines: [], removed: [], linesUnknown: false }) });
    const w2 = keep(mount(CodeDialog, { props: { api: bin.api }, attachTo: document.body }));
    openCode('payments', 'DeveloperAda');
    await flushPromises();
    expect(w2.get('[data-test=code-not-shown]').text()).toContain("This file isn't shown (binary / 3.4 MB)");
    expect(w2.find('[data-test=code-not-shown-download]').exists()).toBe(true);
  });
});

describe('Settings > Cloud Link: "Show members\' code to cloud viewers"', () => {
  const base: LinkStatus = {
    state: 'connected', url: 'https://cloud.yawble.ai/', code: null, codeExpiresAt: null, instanceId: 'i1', instanceName: 'DESK', org: null, workgroup: null,
    allowHostTerminals: false, viewers: [], error: null,
  };
  function fake(status: LinkStatus) {
    const calls: { method: string; params: any }[] = [];
    let s = status;
    const api = {
      call: async (method: string, params?: any) => {
        calls.push({ method, params });
        if (method === 'link.setShowCode') s = { ...s, showCode: params.show };
        return s as any;
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    return { api, calls };
  }

  it('is on by default and turning it off calls link.setShowCode', async () => {
    const f = fake({ ...base });
    const w = mount(LinkDialog, { props: { api: f.api, inline: true } });
    await flushPromises();
    const box = w.get('[data-test=link-show-code]');
    expect((box.element as HTMLInputElement).checked).toBe(true);
    expect(w.text()).toContain("Show members' code to cloud viewers");
    await box.setValue(false);
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'link.setShowCode')!.params).toEqual({ show: false });
    expect((w.get('[data-test=link-show-code]').element as HTMLInputElement).checked).toBe(false);
  });

  it('shows the setting as the core says it', async () => {
    const w = mount(LinkDialog, { props: { api: fake({ ...base, showCode: false }).api, inline: true } });
    await flushPromises();
    expect((w.get('[data-test=link-show-code]').element as HTMLInputElement).checked).toBe(false);
  });
});
