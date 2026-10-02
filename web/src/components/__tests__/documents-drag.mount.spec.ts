// @vitest-environment happy-dom
//
// DRAG AND DROP. A move by drop onto a folder row, a tree node and a crumb; a copy with Ctrl (or
// Option on macOS); refused drops that accept nothing and send nothing; files from the computer
// uploaded; a multi-selection dragged as one; clashes asked as Paste asks them; and ONE code path -
// a drop sends exactly the request Paste and Move to… send, and only `useDocumentsTransfer` calls
// the move, copy and upload routes.
//
// happy-dom has no `DataTransfer`, so `FakeDataTransfer` stands in, and the events are dispatched
// by hand: dragstart, dragenter, dragover, drop, dragend.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const q = vi.hoisted(() => ({
  screen: { lt: { sm: false } },
  platform: { is: { mac: false } },
  notify: vi.fn(),
  copy: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: q.notify, screen: q.screen, platform: q.platform }),
  copyToClipboard: q.copy,
}));

import { documentsServer } from '../../test/documentsServer';
import {
  FakeDataTransfer,
  click,
  crumb,
  doubleClick,
  dragAndDrop,
  fire,
  openExplorer,
  pane,
  press,
  row,
  rows,
  settle,
  toolbarButton,
  treeNode,
  until,
} from '../../test/documentsExplorer';
import { bodyText, resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  localStorage.clear();
  q.screen.lt.sm = false;
  q.platform.is.mac = false;
  server = documentsServer();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

const transfers = () => [...server.callsTo('move'), ...server.callsTo('copy')];
const hint = () => document.body.querySelector('.documents-drop-hint')?.textContent?.trim() ?? '';

async function inReports() {
  await openExplorer('alpha');
  await doubleClick(row('reports'));
}

describe('a move by drop', () => {
  it('onto a folder row moves there', async () => {
    await openExplorer('alpha');
    const { over } = await dragAndDrop(row('notes.md'), row('reports'));

    expect(over.defaultPrevented).toBe(true);
    expect(over.dataTransfer.dropEffect).toBe('move');
    expect(server.callsTo('move').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/move', { to: { folder: 'alpha', path: 'reports' }, items: [{ path: 'notes.md' }] }],
    ]);
  });

  it('onto a tree node moves there', async () => {
    await openExplorer('alpha');
    await dragAndDrop(row('notes.md'), treeNode('beta')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([{ to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md' }] }]);
  });

  it('onto a breadcrumb crumb moves there', async () => {
    await inReports();
    await dragAndDrop(row('a.md'), crumb('Alpha'));

    expect(server.callsTo('move').map((call) => call.body)).toEqual([{ to: { folder: 'alpha', path: '' }, items: [{ path: 'reports/a.md' }] }]);
  });

  it('highlights the folder under the pointer and hints what the drop does', async () => {
    await openExplorer('alpha');
    const data = new FakeDataTransfer();
    await fire(row('notes.md'), 'dragstart', data);
    await fire(treeNode('beta')!, 'dragover', data);

    expect(treeNode('beta')!.classList).toContain('documents-drop-ok');
    expect(hint()).toContain('Move notes.md to Beta');
    expect(data.getData('application/x-harness-documents')).toBe(JSON.stringify({ folder: 'alpha', paths: ['notes.md'] }));
    expect(data.effectAllowed).toBe('copyMove');
  });
});

describe('a copy by drop', () => {
  it('copies with Ctrl held off macOS: copy effect, Copy in the hint, the copy route', async () => {
    await openExplorer('alpha');
    const data = new FakeDataTransfer();
    await fire(row('notes.md'), 'dragstart', data);
    const over = await fire(row('reports'), 'dragover', data, { ctrlKey: true });

    expect(over.dataTransfer.dropEffect).toBe('copy');
    expect(hint()).toContain('Copy notes.md to reports');

    await fire(row('reports'), 'drop', data, { ctrlKey: true });
    expect(server.callsTo('copy').map((call) => call.body)).toEqual([{ to: { folder: 'alpha', path: 'reports' }, items: [{ path: 'notes.md' }] }]);
    expect(server.callsTo('move')).toHaveLength(0);
  });

  it('copies with Option on macOS, where Ctrl does not copy', async () => {
    q.platform.is.mac = true;
    await openExplorer('alpha');
    const data = new FakeDataTransfer();
    await fire(row('notes.md'), 'dragstart', data);
    const ctrl = await fire(row('reports'), 'dragover', data, { ctrlKey: true });
    expect(ctrl.dataTransfer.dropEffect).toBe('move');

    const option = await fire(row('reports'), 'dragover', data, { altKey: true });
    expect(option.dataTransfer.dropEffect).toBe('copy');
    await fire(row('reports'), 'drop', data, { altKey: true });

    expect(server.callsTo('copy')).toHaveLength(1);
  });
});

describe('a refused drop', () => {
  async function refused(target: () => Element, sentence: string) {
    const data = new FakeDataTransfer();
    await fire(row('a.md'), 'dragstart', data);
    const over = await fire(target(), 'dragover', data);

    expect(target().classList).toContain('documents-drop-refused');
    expect(hint()).toBe(`block${sentence}`);
    expect(over.dataTransfer.dropEffect).toBe('none');
    expect(over.defaultPrevented).toBe(false);

    await fire(target(), 'drop', data);
    expect(transfers()).toHaveLength(0);
  }

  it("onto a gone team's folder", async () => {
    await inReports();
    await refused(() => treeNode('gone')!, 'Gone no longer exists. Nothing can be added to its documents.');
  });

  it('onto a retired folder', async () => {
    await inReports();
    await refused(() => treeNode('alpha~2026')!, 'This folder belongs to an earlier team called Alpha. Nothing can be added to it.');
  });

  it('onto the folder it is already in', async () => {
    await inReports();
    await refused(() => crumb('reports'), 'Already in reports.');
  });

  it('a folder onto itself', async () => {
    await inReports();
    const data = new FakeDataTransfer();
    await fire(row('2026'), 'dragstart', data);
    const over = await fire(row('2026'), 'dragover', data);

    expect(row('2026').classList).toContain('documents-drop-refused');
    expect(hint()).toBe('block2026 cannot be moved into itself.');
    expect(over.defaultPrevented).toBe(false);
    await fire(row('2026'), 'drop', data);
    expect(transfers()).toHaveLength(0);
  });

  it('a file row is not a drop target', async () => {
    await inReports();
    const data = new FakeDataTransfer();
    await fire(row('a.md'), 'dragstart', data);
    const over = await fire(row('b.pdf'), 'dragover', data);

    expect(over.defaultPrevented).toBe(false);
    expect(row('b.pdf').classList).not.toContain('documents-drop-ok');
    expect(row('b.pdf').classList).not.toContain('documents-drop-refused');
    await fire(row('b.pdf'), 'drop', data);
    expect(transfers()).toHaveLength(0);
  });

  it('one the courtesy check allows but the server refuses shows the server sentence', async () => {
    server.reply('move', 409, { error: 'notes.md is a link. A link is never followed, moved or copied.' });

    await openExplorer('alpha');
    await dragAndDrop(row('notes.md'), row('reports'));

    expect(document.body.querySelector('.documents-error')!.textContent).toContain('notes.md is a link. A link is never followed, moved or copied.');
  });
});

describe('a drop from the computer', () => {
  const files = () => [new File(['1'], 'one.md'), new File(['2'], 'two.csv')];

  it('uploads each file into the folder dropped on, asking about clashes, and leaves a folder out', async () => {
    await openExplorer('alpha');
    const data = new FakeDataTransfer(files(), ['photos']);
    const over = await fire(row('reports'), 'dragover', data);

    expect(over.dataTransfer.dropEffect).toBe('copy');
    expect(hint()).toContain('Upload 3 files to reports');

    await fire(row('reports'), 'drop', data);

    expect(server.callsTo('upload').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/upload', { file: 'one.md', path: 'reports', onClash: 'ask' }],
      ['/api/teams/alpha/documents/upload', { file: 'two.csv', path: 'reports', onClash: 'ask' }],
    ]);
    expect(bodyText()).toContain('Folders cannot be uploaded by dropping; drop the files inside them. Left out: photos.');
  });

  it('dropped on empty space, uploads into the folder on screen', async () => {
    await inReports();
    const data = new FakeDataTransfer(files());
    await fire(pane(), 'dragover', data);
    expect(pane().classList).toContain('documents-drop-ok');
    await fire(pane(), 'drop', data);

    expect(server.callsTo('upload').map((call) => (call.body as { path: string }).path)).toEqual(['reports', 'reports']);
  });

  it('a clash opens the clash dialog and resends with the choice; a skip is said', async () => {
    server.reply('upload', 409, { error: 'one.md is already in reports.', clashes: [{ from: 'one.md', to: 'reports/one.md', isFolder: false }] });

    await openExplorer('alpha');
    const data = new FakeDataTransfer([new File(['1'], 'one.md')]);
    await fire(row('reports'), 'dragover', data);
    await fire(row('reports'), 'drop', data);
    await until(() => document.body.querySelector('.documents-clash'));
    expect(document.body.querySelector('.documents-clash')!.textContent).toContain('one.md is already in reports.');

    server.reply('upload', 200, { skipped: true, path: 'reports/one.md' });
    await click(document.body.querySelector('[data-clash="skip"]')!);
    await settle();

    expect(server.callsTo('upload').map((call) => (call.body as { onClash: string }).onClash)).toEqual(['ask', 'skip']);
    expect(q.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Uploaded 0 items, skipped 1' }));
  });
});

describe('a multi-selection', () => {
  it('drags as one: three selected, one move with all three paths', async () => {
    await inReports();
    await click(row('a.md'));
    await click(row('c.png'), { shiftKey: true });
    await dragAndDrop(row('b.pdf'), treeNode('beta')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([
      { to: { folder: 'beta', path: '' }, items: [{ path: 'reports/a.md' }, { path: 'reports/b.pdf' }, { path: 'reports/c.png' }] },
    ]);
  });

  it('a drag from an unselected item drags only that item, and selects it', async () => {
    await inReports();
    await click(row('a.md'));
    await click(row('b.pdf'), { ctrlKey: true });
    await dragAndDrop(row('c.png'), treeNode('beta')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([{ to: { folder: 'beta', path: '' }, items: [{ path: 'reports/c.png' }] }]);
  });
});

describe('around the drop', () => {
  it('a clash on drop opens the same clash dialog as Paste', async () => {
    server.reply('move', 409, { error: '1 of these is already in Beta.', clashes: [{ from: 'notes.md', to: 'notes.md', isFolder: false }] });

    await openExplorer('alpha');
    await dragAndDrop(row('notes.md'), treeNode('beta')!);
    await until(() => document.body.querySelector('.documents-clash'));
    await click(document.body.querySelector('[data-clash="keep-both"]')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([
      { to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md' }] },
      { to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md', onClash: 'keep-both' }] },
    ]);
  });

  it('Escape during a drag does not close the dialog', async () => {
    const wrapper = await openExplorer('alpha');
    const data = new FakeDataTransfer();
    await fire(row('notes.md'), 'dragstart', data);
    await fire(row('reports'), 'dragover', data);
    await press('Escape');

    expect(wrapper.emitted('update:modelValue')).toBeUndefined();
  });

  it('dragend clears every highlight and the hint', async () => {
    await openExplorer('alpha');
    const data = new FakeDataTransfer();
    await fire(row('notes.md'), 'dragstart', data);
    await fire(treeNode('beta')!, 'dragover', data);
    expect(document.body.querySelectorAll('.documents-drop-ok').length).toBe(1);

    await fire(row('notes.md'), 'dragend', data);

    expect(document.body.querySelectorAll('.documents-drop-ok, .documents-drop-refused')).toHaveLength(0);
    expect(hint()).toBe('');
  });

  it('resting on a closed tree folder for 800 ms opens it', async () => {
    await openExplorer('beta');
    expect(treeNode('alpha', 'reports')).toBeNull();

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const data = new FakeDataTransfer([new File(['1'], 'one.md')]);
    // Dispatched by hand: `fire` settles, and settling waits on timers that are fake here.
    const enter = new MouseEvent('dragover', { bubbles: true, cancelable: true });
    Object.defineProperty(enter, 'dataTransfer', { value: data });
    treeNode('alpha')!.dispatchEvent(enter);

    vi.advanceTimersByTime(700);
    expect(server.callsTo('list').filter((call) => call.folder === 'alpha')).toHaveLength(0);
    vi.advanceTimersByTime(150);
    vi.useRealTimers();

    await until(() => treeNode('alpha', 'reports'));
    expect(server.callsTo('list').filter((call) => call.folder === 'alpha').map((call) => call.url))
      .toEqual(['/api/teams/alpha/documents?path=&recursive=false']);
  });

  it('at phone width, rows are not draggable', async () => {
    q.screen.lt.sm = true;
    await openExplorer('alpha');

    expect(rows().some((element) => element.getAttribute('draggable') === 'true')).toBe(false);
  });
});

/**
 * ONE CODE PATH FOR A MOVE. A spy on a module export cannot see a call made inside a composable's
 * closure, so the check is on what crosses the wire: Paste, Move to… and a drop of the same item
 * into the same folder send byte-identical requests. And a scan: nothing in the explorer but
 * `useDocumentsTransfer` imports the move, copy or upload calls.
 */
describe('one code path', () => {
  it('Paste, Move to… and a drop send byte-identical move requests', async () => {
    const requests: string[] = [];
    const take = () => {
      const call = server.callsTo('move').at(-1)!;
      requests.push(`${call.method} ${call.url} ${JSON.stringify(call.body)}`);
    };

    await openExplorer('alpha');
    await click(row('notes.md'));
    await press('x', { ctrlKey: true });
    await click(treeNode('beta')!);
    await press('v', { ctrlKey: true });
    take();

    await click(document.body.querySelector('.documents-titlebar [aria-label="Back"]')!);
    await click(row('notes.md'));
    await click(toolbarButton('moveTo')!);
    const picker = await until(() => document.body.querySelector<HTMLElement>('.documents-move'));
    await click(picker.querySelector('[data-node="beta::"]')!);
    await click(picker.querySelector('[data-move="confirm"]')!);
    take();

    await dragAndDrop(row('notes.md'), treeNode('beta')!);
    take();

    expect(requests).toHaveLength(3);
    expect(new Set(requests).size).toBe(1);
    expect(requests[0]).toBe('POST /api/teams/alpha/documents/move {"to":{"folder":"beta","path":""},"items":[{"path":"notes.md"}]}');
  });

  it('only useDocumentsTransfer imports moveDocuments, copyDocuments and uploadDocument in the explorer', () => {
    const src = join(import.meta.dirname, '../..');
    const files = (dir: string): string[] =>
      readdirSync(dir).flatMap((entry) => {
        const path = join(dir, entry);
        if (statSync(path).isDirectory()) return entry === '__tests__' ? [] : files(path);
        return /\.(ts|vue)$/.test(entry) ? [path] : [];
      });

    const importers = (name: string, scope: (path: string) => boolean) =>
      files(src)
        .map((path) => relative(src, path).replace(/\\/g, '/'))
        .filter(scope)
        .filter((path) => path !== 'api/documents.ts')
        .filter((path) => new RegExp(`import\\s*\\{[^}]*\\b${name}\\b[^}]*\\}\\s*from\\s*['"][./]*api/documents['"]`).test(readFileSync(join(src, path), 'utf8')));

    // Move and copy belong to the explorer alone, so the whole source is scanned.
    expect(importers('moveDocuments', () => true)).toEqual(['lib/useDocumentsTransfer.ts']);
    expect(importers('copyDocuments', () => true)).toEqual(['lib/useDocumentsTransfer.ts']);

    // Upload is also the Solutions screens' (a package's input files); within the explorer it is
    // the transfer's alone.
    const explorer = (path: string) =>
      path === 'components/DocumentsDialog.vue' ||
      path.startsWith('components/documents/') ||
      /^lib\/(documents|useDocuments)[A-Za-z]*\.ts$/.test(path) ||
      path.startsWith('stores/documents');
    expect(importers('uploadDocument', explorer)).toEqual(['lib/useDocumentsTransfer.ts']);

    // And nothing in the explorer reaches them through a namespace import either.
    const namespaced = files(src)
      .map((path) => relative(src, path).replace(/\\/g, '/'))
      .filter(explorer)
      .filter((path) => /api\.(moveDocuments|copyDocuments|uploadDocument)\b/.test(readFileSync(join(src, path), 'utf8')));
    expect(namespaced).toEqual([]);
  });
});
