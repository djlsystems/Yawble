import { flushPromises, mount } from '@vue/test-utils';
import MarkdownIt from 'markdown-it';
import { afterEach, describe, expect, it } from 'vitest';
import CodeBrowser from '../src/CodeBrowser.vue';
import type { CodeFile, CodeList } from '../src/types';
import { NOW, ago, entry, fakeCore, numbered, textFile } from './fakes';

const md = new MarkdownIt({ html: false });
const mounted: { unmount: () => void }[] = [];
afterEach(() => { while (mounted.length) mounted.pop()!.unmount(); });

/** A clone with src/ (app.ts edited just now, util.ts on the branch), README.md, logo.svg, and a hidden .env. */
function clone(over: Record<string, (p: any) => unknown> = {}) {
  const files: Record<string, CodeFile> = {
    'src/app.ts': textFile('src/app.ts', numbered(80), { lines: [{ from: 30, to: 31, kind: 'added', at: ago(5_000) }, { from: 4, to: 4, kind: 'branch' }],
      removed: [{ afterLine: 40, text: 'const removed = 1;', at: ago(20_000) }] }),
    'src/util.ts': textFile('src/util.ts', 'export const x = 1;\n'),
    'README.md': { ...textFile('README.md', '# Title\n\n<script>alert(1)</script>\n<img src=x onerror="alert(1)">\n\nText.'), type: 'markdown' },
    'logo.svg': { path: 'logo.svg', type: 'image', size: 40, base64: btoa('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'), lines: [], removed: [], linesUnknown: false },
    'tool.exe': { path: 'tool.exe', type: 'notShown', notShownReason: 'binary', size: 3.4 * 1024 * 1024, lines: [], removed: [], linesUnknown: false },
  };
  const lists: Record<string, CodeList> = {
    '': { path: '', entries: [
      entry('tool.exe', { size: 3.4 * 1024 * 1024 }), entry('README.md', { marker: 'branch' }), entry('logo.svg'),
      entry('.env', { hidden: true, hiddenReason: 'secrets' }), entry('src', { kind: 'dir', marker: 'live' }), entry('docs', { kind: 'dir' }),
      entry('out', { kind: 'link' }),
    ] },
    src: { path: 'src', entries: [entry('src/util.ts', { marker: 'branch' }), entry('src/app.ts', { marker: 'live', mtime: ago(5_000) })] },
    docs: { path: 'docs', entries: [] },
  };
  return fakeCore({
    'code.open': () => ({ session: 's1', root: 'C:/clones/ada', latest: { path: 'src/app.ts', line: 30 }, clonePresent: true }),
    'code.list': (p) => lists[p.path] ?? { path: p.path, entries: [] },
    'code.read': (p) => { const f = files[p.path]; if (!f) throw new Error('code.refused: not found'); return f; },
    'code.download': (p) => ({ name: p.path.split('/').pop(), base64: btoa('raw bytes\r\n'), size: 11 }),
    'code.close': () => ({}),
    ...over,
  });
}

function open(core: ReturnType<typeof fakeCore>, props: Record<string, unknown> = {}) {
  const w = mount(CodeBrowser, { props: { api: core.api, team: 'payments', member: 'DeveloperAda', renderMarkdown: (t: string) => md.render(t), now: () => NOW, tickMs: 0, ...props }, attachTo: document.body });
  mounted.push(w);
  return w;
}

/** Both checkboxes start on; these tests look at the whole listing and the open file staying put, so they turn them off. */
async function manual(w: ReturnType<typeof open>) {
  await w.get('[data-test=code-only-changed]').setValue(false);
  await w.get('[data-test=code-follow]').setValue(false);
  await flushPromises();
}

/** Gives the line view a height (happy-dom does no layout), so "in view" can be told. */
function sized(height: number) {
  const proto = HTMLElement.prototype as any;
  const before = Object.getOwnPropertyDescriptor(proto, 'clientHeight');
  Object.defineProperty(proto, 'clientHeight', { configurable: true, get() { return this.classList?.contains('cb-lines') ? height : 0; } });
  return () => { if (before) Object.defineProperty(proto, 'clientHeight', before); else delete proto.clientHeight; };
}

describe('the Code browser', () => {
  it('starts with Only changed files and Follow edits on', async () => {
    const w = open(clone());
    await flushPromises();
    expect((w.get('[data-test=code-only-changed]').element as HTMLInputElement).checked).toBe(true);
    expect((w.get('[data-test=code-follow]').element as HTMLInputElement).checked).toBe(true);
  });

  it('opens on the latest change: its folder listed, the file previewed, scrolled to the changed line', async () => {
    const restore = sized(300);
    try {
      const core = clone();
      const w = open(core);
      await flushPromises();
      expect(core.called('code.open')[0].params).toEqual({ team: 'payments', member: 'DeveloperAda' });
      expect(core.called('code.list')[0].params).toEqual({ session: 's1', path: 'src' });
      expect(core.called('code.read')[0].params).toEqual({ session: 's1', path: 'src/app.ts' });
      expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda/src/app.ts');
      expect(w.get('[data-test=code-line-30]').classes()).toContain('target');
      expect((w.get('[data-test=code-lines]').element as HTMLElement).scrollTop).toBe(29 * 20 - 100);
    } finally { restore(); }
  });

  it('opens at the root when nothing was edited yet', async () => {
    const core = clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: null, clonePresent: true }) });
    const w = open(core);
    await flushPromises();
    expect(core.called('code.list')[0].params.path).toBe('');
    expect(core.called('code.read')).toHaveLength(0);
    expect(w.find('[data-test=code-pick]').exists()).toBe(true);
  });

  it('says plainly when the member has no clone', async () => {
    const w1 = open(clone({ 'code.open': () => ({ session: 's1', root: '', latest: null, clonePresent: false }) }));
    await flushPromises();
    expect(w1.get('[data-test=code-no-clone]').text()).toContain('has no clone');
    const w2 = open(clone({ 'code.open': () => { throw Object.assign(new Error('code.noClone: no clone'), { code: 'code.noClone' }); } }));
    await flushPromises();
    expect(w2.find('[data-test=code-no-clone]').exists()).toBe(true);
  });

  it('lists folders first, with markers, sizes and ages; hidden files and links are greyed and do not open', async () => {
    const core = clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: null, clonePresent: true }) });
    const w = open(core);
    await flushPromises();
    await manual(w);
    const names = w.findAll('.cb-entry .cb-name').map((n) => n.text());
    expect(names).toEqual(['docs', 'src', '.env', 'logo.svg', 'out', 'README.md', 'tool.exe']);
    expect(w.get('[data-test="code-entry-src"] .cb-dot').attributes('data-marker')).toBe('live');
    expect(w.get('[data-test="code-entry-README.md"] .cb-dot').attributes('data-marker')).toBe('branch');
    expect(w.get('[data-test="code-entry-logo.svg"] .cb-dot').attributes('data-marker')).toBe('');
    expect(w.get('[data-test="code-entry-tool.exe"]').text()).toContain('3.4 MB');
    expect(w.get('[data-test="code-entry-tool.exe"]').text()).toContain('edited 3 h ago');
    const env = w.get('[data-test="code-entry-.env"]');
    expect(env.classes()).toContain('hidden');
    expect(env.text()).toContain('hidden: may hold secrets');
    expect(env.get('button').attributes('disabled')).toBeDefined();
    expect(w.get('[data-test="code-entry-out"] button').attributes('disabled')).toBeDefined();
    await env.get('button').trigger('click');
    await flushPromises();
    expect(core.called('code.read')).toHaveLength(0);
  });

  it('"Only changed files" shows only entries with a marker and the folders that hold them', async () => {
    const w = open(clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: null, clonePresent: true }) }));
    await flushPromises();
    await w.get('[data-test=code-only-changed]').setValue(true);
    expect(w.findAll('.cb-entry .cb-name').map((n) => n.text())).toEqual(['src', 'README.md']);
  });

  it('highlights recent lines (fading with age), marks branch lines and removed lines with their text on hover', async () => {
    const core = clone({
      'code.read': () => textFile('src/app.ts', numbered(20), {
        lines: [{ from: 2, to: 2, kind: 'added', at: ago(10_000) }, { from: 5, to: 5, kind: 'changed', at: ago(20 * 60_000) }, { from: 9, to: 9, kind: 'branch' }],
        removed: [{ afterLine: 6, text: 'let gone = true;', at: ago(30_000) }],
      }),
    });
    const w = open(core);
    await flushPromises();
    const l2 = w.get('[data-test=code-line-2]');
    expect(l2.classes()).toContain('recent');
    expect(l2.attributes('style')).toContain('--hl: 1.00');
    const l5 = w.get('[data-test=code-line-5]');
    expect(l5.classes()).toContain('recent');
    expect(Number(/--hl: ([\d.]+)/.exec(l5.attributes('style')!)![1])).toBeLessThan(0.6);
    expect(w.get('[data-test=code-line-9]').classes()).toContain('branch');
    expect(w.get('[data-test=code-line-3]').classes()).not.toContain('recent');
    const gone = w.get('[data-test=code-removed]');
    expect(gone.attributes('data-after')).toBe('6');
    expect(gone.attributes('title')).toContain('let gone = true;');
    expect(w.get('[data-test=code-line-1]').html()).toContain('hljs-keyword'); // highlighted
  });

  it('Jump to latest, Next and Previous move between the changes', async () => {
    const restore = sized(100);
    try {
      const core = clone({
        'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'src/app.ts', line: 1 }, clonePresent: true }),
        'code.read': () => textFile('src/app.ts', numbered(100), {
          lines: [{ from: 10, to: 10, kind: 'added', at: ago(50_000) }, { from: 50, to: 52, kind: 'changed', at: ago(5_000) }, { from: 80, to: 80, kind: 'branch' }],
        }),
      });
      const w = open(core);
      await flushPromises();
      const lines = w.get('[data-test=code-lines]').element as HTMLElement;
      await w.get('[data-test=code-next]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-10]').classes()).toContain('target');
      await w.get('[data-test=code-next]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-50]').classes()).toContain('target');
      await w.get('[data-test=code-prev]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-10]').classes()).toContain('target');
      await w.get('[data-test=code-jump-latest]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-50]').classes()).toContain('target');
      expect(lines.scrollTop).toBe(49 * 20 - 33);
    } finally { restore(); }
  });

  it('a live update keeps the scroll position, and shows the chip when the change is out of view', async () => {
    const restore = sized(100);
    try {
      let content = numbered(80);
      const core = clone({ 'code.read': () => textFile('src/app.ts', content) });
      const w = open(core);
      await flushPromises();
      const lines = w.get('[data-test=code-lines]').element as HTMLElement;
      lines.scrollTop = 400; // the reader is at lines 21-25
      await w.get('[data-test=code-lines]').trigger('scroll');
      content = content.replace('const v3 = 3;', 'const v3 = 33;');
      core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [{ from: 3, to: 3, kind: 'changed' }], at: ago(0) });
      await flushPromises();
      expect(core.called('code.read')).toHaveLength(2);
      expect(w.get('[data-test=code-line-3]').text()).toContain('33');
      expect(lines.scrollTop).toBe(400);
      const chip = w.get('[data-test=code-updated-chip]');
      expect(chip.text()).toBe('Updated: jump to change');
      await chip.trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-3]').classes()).toContain('target');
      expect(w.find('[data-test=code-updated-chip]').exists()).toBe(false);

      // A change in view updates in place without the chip.
      lines.scrollTop = 0;
      core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [{ from: 2, to: 2, kind: 'changed' }], at: ago(0) });
      await flushPromises();
      expect(lines.scrollTop).toBe(0);
      expect(w.find('[data-test=code-updated-chip]').exists()).toBe(false);
      // Another session's change is not ours.
      core.push('code.changed', { session: 'other', path: 'src/app.ts', ranges: [], at: ago(0) });
      await flushPromises();
      expect(core.called('code.read')).toHaveLength(3);
    } finally { restore(); }
  });

  it('code.changed: a removal at afterLine 0 points at line 1, unknown kinds are ignored, empty ranges read the file again', async () => {
    const restore = sized(100);
    try {
      let current = textFile('src/app.ts', numbered(80));
      const core = clone({ 'code.read': () => current });
      const w = open(core);
      await flushPromises();
      const lines = w.get('[data-test=code-lines]').element as HTMLElement;
      lines.scrollTop = 1000;
      core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [{ from: 0, to: 0, kind: 'removed' }, { from: 70, to: 70, kind: 'moved' }], at: ago(0) });
      await flushPromises();
      await w.get('[data-test=code-updated-chip]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-1]').classes()).toContain('target');

      // Empty ranges (lines not known): read again and use the newest marker code.read gives.
      current = textFile('src/app.ts', numbered(80), { lines: [{ from: 60, to: 60, kind: 'changed', at: ago(0) }], linesUnknown: true });
      lines.scrollTop = 0;
      core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [], at: ago(0) });
      await flushPromises();
      expect(core.called('code.read')).toHaveLength(3);
      await w.get('[data-test=code-updated-chip]').trigger('click'); await flushPromises();
      expect(w.get('[data-test=code-line-60]').classes()).toContain('target');
    } finally { restore(); }
  });

  it('a deleted open file (empty ranges, read refused) is no longer shown', async () => {
    let gone = false;
    const core = clone({ 'code.read': () => { if (gone) throw Object.assign(new Error('code.refused: not found'), { code: 'code.refused' }); return textFile('src/app.ts', numbered(5)); } });
    const w = open(core);
    await flushPromises();
    gone = true;
    core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [], at: ago(0) });
    await flushPromises();
    expect(w.find('[data-test=code-lines]').exists()).toBe(false);
    expect(w.get('[data-test=code-file-error]').text()).toContain('not found');
  });

  it('a clone that goes away while the dialog is open shows the no-clone state', async () => {
    let gone = false;
    const noClone = () => Object.assign(new Error('code.noClone: no clone'), { code: 'code.noClone' });
    const core = clone({ 'code.list': (p) => { if (gone) throw noClone(); return { path: p.path, entries: [] }; } });
    const w = open(core);
    await flushPromises();
    gone = true;
    core.push('code.changed', { session: 's1', path: 'src/x.ts', ranges: [], at: ago(0) });
    await flushPromises();
    expect(w.find('[data-test=code-no-clone]').exists()).toBe(true);
  });

  it('a change in the folder lists it again quietly; Follow edits opens the file the agent edits next', async () => {
    const core = clone();
    const w = open(core);
    await flushPromises();
    await manual(w);
    core.push('code.changed', { session: 's1', path: 'src/util.ts', ranges: [{ from: 1, to: 1, kind: 'changed' }], at: ago(0) });
    await flushPromises();
    expect(core.called('code.list')).toHaveLength(2);
    expect(core.called('code.read')).toHaveLength(1); // not followed: the open file stays
    expect(w.find('[data-test=loading-note]').exists()).toBe(false);

    await w.get('[data-test=code-follow]').setValue(true);
    core.push('code.changed', { session: 's1', path: 'README.md', ranges: [{ from: 3, to: 3, kind: 'added' }], at: ago(0) });
    await flushPromises();
    expect(core.called('code.read').at(-1)!.params.path).toBe('README.md');
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda/README.md');
    expect(w.find('[data-test=code-lines]').exists()).toBe(true); // markdown opened on a change: its source lines
  });

  it('renders markdown safely, with a Source toggle', async () => {
    const core = clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'README.md', line: 0 }, clonePresent: true }) });
    const w = open(core);
    await flushPromises();
    const view = w.get('[data-test=code-markdown]');
    expect(view.find('h1').text()).toBe('Title');
    expect(view.find('script').exists()).toBe(false);
    expect(view.find('img').exists()).toBe(false);
    expect(view.text()).toContain('<script>alert(1)</script>');
    await w.get('[data-test=code-source]').trigger('click');
    expect(w.find('[data-test=code-markdown]').exists()).toBe(false);
    expect(w.get('[data-test=code-line-1]').text()).toContain('# Title');
    await w.get('[data-test=code-source]').trigger('click');
    expect(w.find('[data-test=code-markdown]').exists()).toBe(true);
  });

  it('shows an SVG as an <img>, never inline', async () => {
    const w = open(clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'logo.svg', line: 0 }, clonePresent: true }) }));
    await flushPromises();
    const img = w.get('[data-test=code-image] img');
    expect(img.attributes('src')).toMatch(/^data:image\/svg\+xml;base64,/);
    expect(w.find('[data-test=code-image] svg').exists()).toBe(false);
    expect(w.find('script').exists()).toBe(false);
  });

  it("says why a file isn't shown and offers Download; a secret has no Download", async () => {
    const core = clone({ 'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'tool.exe', line: 0 }, clonePresent: true }) });
    const w = open(core);
    await flushPromises();
    expect(w.get('[data-test=code-not-shown]').text()).toContain("This file isn't shown (binary / 3.4 MB)");
    expect(w.find('[data-test=code-not-shown-download]').exists()).toBe(true);

    const big = clone({
      'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'big.log', line: 0 }, clonePresent: true }),
      'code.read': () => ({ path: 'big.log', type: 'notShown', notShownReason: 'tooLarge', size: 2 * 1024 * 1024, lines: [], removed: [], linesUnknown: false }),
    });
    const w2 = open(big);
    await flushPromises();
    expect(w2.get('[data-test=code-not-shown]').text()).toContain('too large / 2 MB');

    const secret = clone({
      'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'id_rsa', line: 0 }, clonePresent: true }),
      'code.read': () => ({ path: 'id_rsa', type: 'notShown', notShownReason: 'secrets', size: 400, lines: [], removed: [], linesUnknown: false }),
    });
    const w3 = open(secret);
    await flushPromises();
    expect(w3.get('[data-test=code-not-shown]').text()).toContain('may hold secrets');
    expect(w3.find('[data-test=code-not-shown-download]').exists()).toBe(false);
    expect(w3.find('[data-test=code-download]').exists()).toBe(false);
  });

  it('Download calls code.download and saves the bytes exactly', async () => {
    const saved: { name: string; bytes: Uint8Array }[] = [];
    const core = clone();
    const w = open(core, { save: (name: string, bytes: Uint8Array) => saved.push({ name, bytes }) });
    await flushPromises();
    await w.get('[data-test=code-download]').trigger('click');
    await flushPromises();
    expect(core.called('code.download')[0].params).toEqual({ session: 's1', path: 'src/app.ts', offset: 0 });
    expect(saved[0].name).toBe('app.ts');
    expect(Array.from(saved[0].bytes)).toEqual(Array.from(new TextEncoder().encode('raw bytes\r\n')));
  });

  it('downloads a large file in chunks, passing the version, and joins them', async () => {
    const file = new Uint8Array(10).map((_, i) => i * 7);
    const b64 = (from: number, to: number) => btoa(String.fromCharCode(...file.subarray(from, to)));
    const saved: Uint8Array[] = [];
    const core = clone({
      'code.download': (p) => {
        const offset = p.offset ?? 0;
        const length = Math.min(4, 10 - offset);
        return { name: 'app.ts', base64: b64(offset, offset + length), size: 10, offset, length, version: 'v1', done: offset + length >= 10 };
      },
    });
    const w = open(core, { save: (_: string, bytes: Uint8Array) => saved.push(bytes) });
    await flushPromises();
    await w.get('[data-test=code-download]').trigger('click');
    await flushPromises();
    expect(core.called('code.download').map((c) => [c.params.offset, c.params.version])).toEqual([[0, undefined], [4, 'v1'], [8, 'v1']]);
    expect(Array.from(saved[0])).toEqual(Array.from(file));
  });

  describe('the chunk loop', () => {
    /** A core that serves `file` in chunks of `chunk` bytes, as the contract says. */
    function chunked(file: Uint8Array, chunk: number, version: (offset: number) => string = () => 'v1') {
      return clone({
        'code.download': (p) => {
          const offset = p.offset ?? 0;
          const length = Math.min(chunk, file.length - offset);
          return { name: 'app.ts', base64: btoa(String.fromCharCode(...file.subarray(offset, offset + length))), size: file.length, offset, length, version: version(offset), done: offset + length >= file.length };
        },
      });
    }
    async function download(core: ReturnType<typeof clone>) {
      const saved: Uint8Array[] = [];
      const w = open(core, { save: (_: string, bytes: Uint8Array) => saved.push(bytes) });
      await flushPromises();
      await w.get('[data-test=code-download]').trigger('click');
      await flushPromises();
      return { w, saved };
    }
    const bytes = (n: number) => new Uint8Array(n).map((_, i) => (i * 31 + 5) & 0xff);

    it('a file that fits one chunk is one call, done on the first answer', async () => {
      const file = bytes(6);
      const core = chunked(file, 8);
      const { saved } = await download(core);
      expect(core.called('code.download').map((c) => c.params)).toEqual([{ session: 's1', path: 'src/app.ts', offset: 0 }]);
      expect(Array.from(saved[0])).toEqual(Array.from(file));
    });

    it('a multi-chunk file asks from offset + length with the first version, and stops when the last chunk says done', async () => {
      const file = bytes(12); // exactly three chunks: done comes with the third, so there is no fourth call
      const core = chunked(file, 4);
      const { saved } = await download(core);
      expect(core.called('code.download').map((c) => [c.params.offset, c.params.version])).toEqual([[0, undefined], [4, 'v1'], [8, 'v1']]);
      expect(saved).toHaveLength(1);
      expect(Array.from(saved[0])).toEqual(Array.from(file));
    });

    it('never mixes two versions: a chunk with another version stops the download and saves nothing', async () => {
      const core = chunked(bytes(12), 4, (offset) => (offset >= 8 ? 'v2' : 'v1'));
      const { w, saved } = await download(core);
      expect(saved).toHaveLength(0);
      expect(w.get('[data-test=code-file-error]').text()).toContain('changed while it was downloading');
    });

    it('a chunk at the wrong offset stops the download', async () => {
      const core = clone({
        'code.download': (p) => ({ name: 'app.ts', base64: btoa('abcd'), size: 12, offset: p.offset ? 8 : 0, length: 4, version: 'v1', done: !!p.offset }),
      });
      const { w, saved } = await download(core);
      expect(saved).toHaveLength(0);
      expect(w.get('[data-test=code-file-error]').text()).toContain('out of order');
    });
  });

  it('a download refused part way (the file changed) says so and saves nothing', async () => {
    const saved: Uint8Array[] = [];
    const core = clone({
      'code.download': (p) => {
        if (p.offset) throw new Error('code.refused: the file changed while it was downloading; download it again');
        return { name: 'app.ts', base64: btoa('abcd'), size: 8, offset: 0, length: 4, version: 'v1', done: false };
      },
    });
    const w = open(core, { save: (_: string, bytes: Uint8Array) => saved.push(bytes) });
    await flushPromises();
    await w.get('[data-test=code-download]').trigger('click');
    await flushPromises();
    expect(saved).toHaveLength(0);
    expect(w.get('[data-test=code-file-error]').text()).toContain('the file changed while it was downloading');
  });

  it('browses with the listing and breadcrumb, and back and forward work', async () => {
    const core = clone();
    const w = open(core);
    await flushPromises();
    await manual(w);
    expect(w.get('[data-test=code-back]').attributes('disabled')).toBeDefined();
    await w.get('[data-test=code-crumb-root]').trigger('click'); await flushPromises();
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda');
    await w.get('[data-test="code-entry-docs"] button').trigger('click'); await flushPromises();
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda/docs');
    expect(w.get('[data-test=code-list-empty]').text()).toBe('This folder is empty.');
    await w.get('[data-test=code-back]').trigger('click'); await flushPromises();
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda');
    await w.get('[data-test=code-back]').trigger('click'); await flushPromises();
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda/src/app.ts');
    await w.get('[data-test=code-forward]').trigger('click'); await flushPromises();
    expect(w.get('[data-test=code-breadcrumb]').text()).toBe('DeveloperAda');
    await w.get('[data-test="code-entry-README.md"] button').trigger('click'); await flushPromises();
    expect(w.find('[data-test=code-markdown]').exists()).toBe(true);
    expect(w.get('[data-test=code-forward]').attributes('disabled')).toBeDefined();
  });

  it('closes its session when it goes away, and stops listening', async () => {
    const core = clone();
    const w = open(core);
    await flushPromises();
    expect(core.listeners.size).toBe(1);
    mounted.splice(mounted.indexOf(w), 1);
    w.unmount();
    await flushPromises();
    expect(core.called('code.close')[0].params).toEqual({ session: 's1' });
    expect(core.listeners.size).toBe(0);
  });
});

describe('the Code browser under a flood of changes, and agent markdown', () => {
  it('reads the open file again at most once at a time, with one more waiting: intermediate changes are never read', async () => {
    let content = numbered(10);
    const gates: (() => void)[] = [];
    let first = true;
    const core = clone({
      'code.read': (p) => {
        const answer = textFile(p.path, content);
        if (first) { first = false; return answer; }
        return new Promise((resolve) => gates.push(() => resolve(answer)));
      },
    });
    const w = open(core);
    await flushPromises();
    expect(core.called('code.read')).toHaveLength(1);

    for (let i = 0; i < 6; i++) {
      content = numbered(10 + i);
      core.push('code.changed', { session: 's1', path: 'src/app.ts', ranges: [{ from: 2, to: 2, kind: 'changed' }], at: ago(0) });
      await flushPromises();
    }
    expect(core.called('code.read')).toHaveLength(2); // one in flight; the other five folded into one waiting
    gates.shift()!();
    await flushPromises();
    expect(core.called('code.read')).toHaveLength(3);
    gates.shift()!();
    await flushPromises();
    expect(core.called('code.read')).toHaveLength(3); // nothing more waited
    expect(w.findAll('.cb-line').length).toBe(15); // the newest content is shown
  });

  it('a batched code.changed lists the folder once and reads the open file once', async () => {
    const core = clone();
    const w = open(core);
    await flushPromises();
    await manual(w);
    const lists = core.called('code.list').length;
    core.push('code.changed', {
      session: 's1', path: 'src/util.ts', ranges: [], at: ago(0), cloud: false,
      changes: [
        { path: 'src/app.ts', ranges: [{ from: 3, to: 3, kind: 'added' }], at: ago(100) },
        { path: 'src/new.ts', ranges: [], at: ago(50) },
        { path: 'src/util.ts', ranges: [], at: ago(0) },
      ],
    });
    await flushPromises();
    expect(core.called('code.list')).toHaveLength(lists + 1);
    expect(core.called('code.read')).toHaveLength(2);
    expect(core.called('code.read').at(-1)!.params.path).toBe('src/app.ts');
  });

  it('never loads an image named by agent markdown (local files, UNC shares, the web): each is its alt text', async () => {
    const readme = [
      '# Notes',
      '![local file](C:/Users/me/.ssh/id_rsa.png)',
      String.raw`![share](\\attacker\share\pixel.png)`,
      '![](file:///etc/passwd)',
      '![web](https://tracker.example.com/p.gif)',
      '[![linked](https://example.com/badge.svg)](https://example.com/)',
    ].join('\n\n');
    const core = clone({
      'code.open': () => ({ session: 's1', root: 'r', latest: { path: 'README.md', line: 0 }, clonePresent: true }),
      'code.read': () => ({ ...textFile('README.md', readme), type: 'markdown' }),
    });
    // Even a renderer that lets images (and raw HTML) through: nothing that fetches reaches the page.
    const loose = new MarkdownIt({ html: true });
    loose.validateLink = () => true;
    const w = open(core, { renderMarkdown: (t: string) => loose.render(t + '\n\n' + String.raw`<img src="\\host\x.png">` + '<picture><source srcset="//h/x.png"></picture><video poster="//h/p.png"></video><p style="background:url(//h/b.png)">styled</p>') });
    await flushPromises();
    const view = w.get('[data-test=code-markdown]');
    expect(view.find('img').exists()).toBe(false);
    expect(view.find('picture').exists()).toBe(false);
    expect(view.find('source').exists()).toBe(false);
    expect(view.find('video').exists()).toBe(false);
    expect(view.html()).not.toMatch(/\bsrc(set)?=|poster=|url\(/);
    const notes = view.findAll('[data-test=code-md-image]').map((n) => n.text());
    expect(notes).toEqual(['[image: local file]', '[image: share]', '[image]', '[image: web]', '[image: linked]', '[image]']);
    expect(view.text()).toContain('styled');
  });
});
