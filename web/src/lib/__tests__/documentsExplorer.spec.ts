import { describe, expect, it } from 'vitest';
import { asDocumentsFolderKey, asTeamId, type DocumentsFolder } from '../../api/types';
import {
  agentPath,
  availability,
  clashQueue,
  clearSelection,
  compareEntries,
  crumbsOf,
  dragMode,
  dropHint,
  dropVerdict,
  extend,
  itemOfEntry,
  itemOfFolder,
  kindOf,
  menuFor,
  moveFocus,
  pruneSelection,
  RootLocation,
  selectAll,
  selectOnly,
  sizeText,
  toggle,
  upOf,
  type DocumentsSort,
  type ExplorerItem,
  type MenuContext,
  type MenuEntry,
} from '../documentsExplorer';

const key = asDocumentsFolderKey;

const folder = (name: string, { exists = true, retired = false, label = name } = {}): DocumentsFolder => ({
  folder: key(name),
  team: asTeamId(name),
  label,
  exists,
  retired,
  entries: 3,
  modifiedAt: '2026-09-01T00:00:00Z',
});

const Folders = [folder('alpha', { label: 'Alpha' }), folder('beta', { label: 'Beta' }), folder('gone', { exists: false, label: 'Gone' }), folder('alpha~1', { retired: true, label: 'Alpha' })];

const entry = (path: string, { isFolder = false, size = 10, children = 0, modifiedAt = '2026-09-01T00:00:00Z' } = {}): ExplorerItem =>
  itemOfEntry(key('alpha'), { name: path.slice(path.lastIndexOf('/') + 1), path, isFolder, size, children, modifiedAt });

describe('kindOf', () => {
  it.each([
    ['a.docx', 'Document', 'description'],
    ['a.XLSX', 'Spreadsheet', 'table_chart'],
    ['a.csv', 'Spreadsheet', 'table_chart'],
    ['a.pptx', 'Presentation', 'slideshow'],
    ['a.pdf', 'PDF', 'picture_as_pdf'],
    ['a.png', 'Image', 'image'],
    ['a.mp4', 'Video', 'movie'],
    ['a.mp3', 'Audio', 'audio_file'],
    ['a.tar.gz', 'Archive', 'folder_zip'],
    ['a.ts', 'Code', 'code'],
    ['a.md', 'Markdown', 'article'],
    ['a.json', 'JSON', 'data_object'],
    ['a.log', 'Text', 'text_snippet'],
    ['a.unknownthing', 'File', 'draft'],
    ['Makefile', 'File', 'draft'],
    ['.env', 'File', 'draft'],
  ])('%s is %s', (name, kind, icon) => {
    expect(kindOf(name, false)).toEqual({ kind, icon });
  });

  it('a folder is always a folder, whatever its name', () => {
    expect(kindOf('photos.png', true)).toEqual({ kind: 'Folder', icon: 'folder' });
  });

  it('marks a gone or superseded team folder, and a live one as shared', () => {
    expect(itemOfFolder(Folders[0]!).icon).toBe('folder_shared');
    expect(itemOfFolder(Folders[2]!).icon).toBe('folder_off');
    expect(itemOfFolder(Folders[3]!).icon).toBe('folder_off');
    expect(itemOfFolder(Folders[3]!).kind).toBe('Superseded');
  });
});

describe('sizeText', () => {
  it('says bytes, kilobytes and megabytes, and a folder its count', () => {
    expect(sizeText({ isFolder: false, size: 12, children: 0 })).toBe('12 B');
    expect(sizeText({ isFolder: false, size: 4096, children: 0 })).toBe('4 KB');
    expect(sizeText({ isFolder: false, size: 3 * 1024 * 1024, children: 0 })).toBe('3.0 MB');
    expect(sizeText({ isFolder: true, size: 0, children: 1 })).toBe('1 item');
    expect(sizeText({ isFolder: true, size: 0, children: 12 })).toBe('12 items');
  });
});

describe('compareEntries', () => {
  const list = [
    entry('b.md', { size: 30, modifiedAt: '2026-09-03T00:00:00Z' }),
    entry('zeta', { isFolder: true, children: 1 }),
    entry('a10.pdf', { size: 10, modifiedAt: '2026-09-01T00:00:00Z' }),
    entry('a2.png', { size: 20, modifiedAt: '2026-09-02T00:00:00Z' }),
    entry('alpha', { isFolder: true, children: 5 }),
  ];
  const sorted = (sort: DocumentsSort) => [...list].sort((a, b) => compareEntries(a, b, sort)).map((item) => item.name);

  it('puts folders first whatever the sort, in both directions', () => {
    expect(sorted({ column: 'size', descending: true }).slice(0, 2)).toEqual(['alpha', 'zeta']);
    expect(sorted({ column: 'name', descending: true }).slice(0, 2)).toEqual(['zeta', 'alpha']);
  });

  it('sorts by name naturally, and by each column both ways', () => {
    expect(sorted({ column: 'name', descending: false })).toEqual(['alpha', 'zeta', 'a2.png', 'a10.pdf', 'b.md']);
    expect(sorted({ column: 'size', descending: false }).slice(2)).toEqual(['a10.pdf', 'a2.png', 'b.md']);
    expect(sorted({ column: 'size', descending: true }).slice(2)).toEqual(['b.md', 'a2.png', 'a10.pdf']);
    expect(sorted({ column: 'modified', descending: true }).slice(2)).toEqual(['b.md', 'a2.png', 'a10.pdf']);
    expect(sorted({ column: 'kind', descending: false }).slice(2)).toEqual(['a2.png', 'b.md', 'a10.pdf']);
  });
});

describe('the selection model', () => {
  const order = ['a', 'b', 'c', 'd', 'e'];

  it('a click selects one; Ctrl toggles; Shift selects the range from the anchor', () => {
    let selection = selectOnly('b');
    expect(selection.keys).toEqual(['b']);

    selection = toggle(selection, 'd');
    expect(selection.keys).toEqual(['b', 'd']);
    selection = toggle(selection, 'b');
    expect(selection.keys).toEqual(['d']);

    selection = extend(selectOnly('b'), order, 'e');
    expect(selection.keys).toEqual(['b', 'c', 'd', 'e']);
    selection = extend(selection, order, 'a');
    expect(selection.keys).toEqual(['a', 'b']);
  });

  it('select all, clear, and a refresh that drops what is gone', () => {
    expect(selectAll(order).keys).toEqual(order);
    expect(clearSelection(selectAll(order)).keys).toEqual([]);
    expect(pruneSelection({ keys: ['a', 'x'], anchor: 'x', focus: 'a' }, order)).toEqual({ keys: ['a'], anchor: null, focus: 'a' });
  });

  it('arrows move the focus: plain selects it, Shift extends, Ctrl only moves', () => {
    expect(moveFocus(selectOnly('b'), order, 1, 'select').keys).toEqual(['c']);
    expect(moveFocus(selectOnly('b'), order, 2, 'extend').keys).toEqual(['b', 'c', 'd']);
    const moved = moveFocus(selectOnly('b'), order, 1, 'focus');
    expect(moved.keys).toEqual(['b']);
    expect(moved.focus).toBe('c');
    expect(moveFocus({ keys: [], anchor: null, focus: null }, order, 1, 'select').keys).toEqual(['a']);
    expect(moveFocus(selectOnly('e'), order, 1, 'select').keys).toEqual(['e']);
  });
});

describe('menuFor', () => {
  const context = (over: Partial<MenuContext> = {}): MenuContext => ({
    location: { folder: key('alpha'), path: 'reports' },
    folder: Folders[0]!,
    selected: [],
    clipboardFull: false,
    viewable: (item) => item.name.endsWith('.md'),
    ...over,
  });
  const actions = (entries: MenuEntry[]) => entries.flatMap((entry) => (entry.kind === 'action' ? [entry.action] : []));
  const disabled = (entries: MenuEntry[], action: string) =>
    entries.find((entry) => entry.kind === 'action' && entry.action === action) as { disabled?: string } | undefined;

  it('on one file: open, view, download, the clipboard, rename, move, copy as path, delete', () => {
    expect(actions(menuFor('item', context({ selected: [entry('reports/a.md')] }))))
      .toEqual(['open', 'view', 'download', 'cut', 'copy', 'rename', 'moveTo', 'copyPath', 'delete']);
  });

  it('on a folder with something on the clipboard: Paste into this folder, no view or download', () => {
    const list = actions(menuFor('item', context({ selected: [entry('reports/2026', { isFolder: true })], clipboardFull: true })));
    expect(list).toContain('pasteInto');
    expect(list).not.toContain('view');
    expect(list).not.toContain('download');
  });

  it('on many: no rename, copy as path, open, view or download', () => {
    const list = actions(menuFor('item', context({ selected: [entry('reports/a.md'), entry('reports/b.md')] })));
    expect(list).toEqual(['cut', 'copy', 'moveTo', 'delete']);
  });

  it('on empty space: new folder, upload, paste, refresh, and the view and sort submenus', () => {
    const entries = menuFor('empty', context());
    expect(actions(entries)).toEqual(['newFolder', 'upload', 'paste', 'refresh']);
    expect(entries.filter((entry) => entry.kind === 'submenu')).toHaveLength(2);
    expect(disabled(entries, 'paste')?.disabled).toBe('Nothing to paste');
  });

  it("at the root: a live team's folder is opened, copied and its path copied - never cut, moved or deleted", () => {
    const list = actions(menuFor('item', context({ location: RootLocation, folder: null, selected: [itemOfFolder(Folders[0]!)] })));
    expect(list).toEqual(['open', 'copy', 'copyPath']);
  });

  it('at the root: a gone folder offers Delete all of these documents', () => {
    expect(actions(menuFor('item', context({ location: RootLocation, folder: null, selected: [itemOfFolder(Folders[2]!)] }))))
      .toContain('deleteAll');
  });

  it('at the root: several gone or earlier folders offer Delete all for every one of them', () => {
    const entries = menuFor('item', context({ location: RootLocation, folder: null, selected: [itemOfFolder(Folders[2]!), itemOfFolder(Folders[3]!)] }));
    expect(actions(entries)).toEqual(['copy', 'deleteAll']);
    expect(entries.find((entry) => entry.kind === 'action' && entry.action === 'deleteAll')).toMatchObject({ label: 'Delete all documents of 2 folders' });
    expect(disabled(entries, 'deleteAll')?.disabled).toBeUndefined();
  });

  it("at the root: Delete all is refused, with its reason, while a live team's folder is selected too", () => {
    const entries = menuFor('item', context({ location: RootLocation, folder: null, selected: [itemOfFolder(Folders[2]!), itemOfFolder(Folders[0]!)] }));
    expect(disabled(entries, 'deleteAll')?.disabled).toBe("A live team's folder is selected");
  });

  it('at the root: several live folders offer no Delete all', () => {
    const list = actions(menuFor('item', context({ location: RootLocation, folder: null, selected: [itemOfFolder(Folders[0]!), itemOfFolder(Folders[1]!)] })));
    expect(list).toEqual(['copy']);
  });

  it("in a gone folder: writes are refused with the reason, copy, cut, move to and delete are not", () => {
    const gone = context({ location: { folder: key('gone'), path: '' }, folder: Folders[2]!, selected: [entry('a.md')], clipboardFull: true });
    expect(availability('newFolder', gone)).toEqual({ ok: false, reason: "Nothing can be added to a gone team's documents" });
    expect(availability('upload', gone).ok).toBe(false);
    expect(availability('paste', gone).ok).toBe(false);
    expect(availability('rename', gone).ok).toBe(false);
    for (const action of ['copy', 'cut', 'moveTo', 'delete', 'copyPath'] as const) expect(availability(action, gone).ok).toBe(true);

    const retired = context({ location: { folder: key('alpha~1'), path: '' }, folder: Folders[3]!, selected: [] });
    expect(availability('newFolder', retired)).toEqual({ ok: false, reason: "Nothing can be added to an earlier team's documents" });
  });

  it('rename and copy as path need exactly one', () => {
    const many = context({ selected: [entry('a.md'), entry('b.md')] });
    expect(availability('rename', many)).toEqual({ ok: false, reason: 'Select one item to rename' });
    expect(availability('copyPath', many)).toEqual({ ok: false, reason: 'Select one item to copy its path' });
  });

  it('in the tree: open, paste into, copy as path, new folder inside', () => {
    expect(actions(menuFor('tree', context({ selected: [entry('reports', { isFolder: true })] }))))
      .toEqual(['open', 'pasteInto', 'copyPath', 'newFolderInside']);
  });
});

describe('agentPath, crumbs and up', () => {
  it('is the root, the folder and the path, as an agent sees it', () => {
    expect(agentPath('/data/documents', key('alpha'), 'reports/a.md')).toBe('/data/documents/alpha/reports/a.md');
    expect(agentPath('/data/documents/', key('alpha'), '')).toBe('/data/documents/alpha');
  });

  it('crumbs run from the root down, marking a gone folder', () => {
    const crumbs = crumbsOf({ folder: key('gone'), path: 'x/y' }, Folders);
    expect(crumbs.map((crumb) => crumb.label)).toEqual(['Documents', 'Gone', 'x', 'y']);
    expect(crumbs[1]!.note).toBe('gone');
    expect(crumbs[3]!.target).toEqual({ folder: key('gone'), path: 'x/y' });
  });

  it('up goes to the parent, then the root', () => {
    expect(upOf({ folder: key('alpha'), path: 'a/b' })).toEqual({ folder: key('alpha'), path: 'a' });
    expect(upOf({ folder: key('alpha'), path: '' })).toEqual(RootLocation);
    expect(upOf(RootLocation)).toEqual(RootLocation);
  });
});

describe('clashQueue', () => {
  const clashes = ['a', 'b', 'c'].map((name) => ({ from: `r/${name}`, to: `i/${name}`, isFolder: false }));

  it('asks each in turn', async () => {
    const asked: number[] = [];
    const chosen = await clashQueue(clashes, async (_clash, remaining) => {
      asked.push(remaining);
      return { choice: 'skip', rest: false };
    });

    expect(asked).toEqual([2, 1, 0]);
    expect([...chosen!.values()]).toEqual(['skip', 'skip', 'skip']);
  });

  it('"do this for the rest" answers every one left', async () => {
    let asks = 0;
    const chosen = await clashQueue(clashes, async () => {
      asks++;
      return { choice: 'keep-both', rest: true };
    });

    expect(asks).toBe(1);
    expect(Object.fromEntries(chosen!)).toEqual({ 'r/a': 'keep-both', 'r/b': 'keep-both', 'r/c': 'keep-both' });
  });

  it('cancel sends nothing', async () => {
    expect(await clashQueue(clashes, async () => null)).toBeNull();
  });
});

describe('dropVerdict', () => {
  const items = (paths: string[], from = 'alpha') => ({ kind: 'items' as const, folder: key(from), paths });

  it('accepts a live folder elsewhere', () => {
    expect(dropVerdict(items(['reports/a.md']), { folder: key('beta'), path: '' }, Folders, 'move')).toEqual({ ok: true });
    expect(dropVerdict(items(['reports/a.md']), { folder: key('alpha'), path: 'reports/2026' }, Folders, 'move')).toEqual({ ok: true });
  });

  it.each([
    ['a gone team', { folder: key('gone'), path: '' }, 'Gone no longer exists. Nothing can be added to its documents.'],
    ['a retired folder', { folder: key('alpha~1'), path: 'x' }, 'This folder belongs to an earlier team called Alpha. Nothing can be added to it.'],
    ['its own folder', { folder: key('alpha'), path: 'reports' }, 'Already in reports.'],
    ['the documents root', { folder: null, path: '' }, "Drop into a team's folder."],
  ])('refuses %s', (_name, target, reason) => {
    expect(dropVerdict(items(['reports/a.md']), target, Folders, 'move')).toEqual({ ok: false, reason });
  });

  it('refuses a folder into itself and into its own subfolder, saying move or copy', () => {
    expect(dropVerdict(items(['reports']), { folder: key('alpha'), path: 'reports' }, Folders, 'move'))
      .toEqual({ ok: false, reason: 'reports cannot be moved into itself.' });
    expect(dropVerdict(items(['reports']), { folder: key('alpha'), path: 'reports/2026' }, Folders, 'copy'))
      .toEqual({ ok: false, reason: 'reports cannot be copied into itself.' });
  });

  it('refuses a folder dropped from the computer, and accepts files', () => {
    expect(dropVerdict({ kind: 'files', files: [{ name: 'photos', isFolder: true }] }, { folder: key('alpha'), path: '' }, Folders, 'copy'))
      .toEqual({ ok: false, reason: 'Folders cannot be uploaded by dropping; drop the files inside them.' });
    expect(dropVerdict({ kind: 'files', files: null }, { folder: key('alpha'), path: '' }, Folders, 'copy')).toEqual({ ok: true });
  });

  it('copies with Ctrl off macOS and with Option on macOS, and moves otherwise', () => {
    expect(dragMode({ ctrlKey: true, altKey: false }, false)).toBe('copy');
    expect(dragMode({ ctrlKey: false, altKey: true }, false)).toBe('move');
    expect(dragMode({ ctrlKey: false, altKey: true }, true)).toBe('copy');
    expect(dragMode({ ctrlKey: true, altKey: false }, true)).toBe('move');
    expect(dragMode({ ctrlKey: false, altKey: false }, false)).toBe('move');
  });

  it('hints what the drop would do', () => {
    const target = { folder: key('beta'), path: '' };
    expect(dropHint(items(['a', 'b', 'c']), target, Folders, 'move', { ok: true })).toBe('Move 3 items to Beta');
    expect(dropHint(items(['r/a.md']), target, Folders, 'copy', { ok: true })).toBe('Copy a.md to Beta');
    expect(dropHint({ kind: 'files', files: [{ name: '', isFolder: false }, { name: '', isFolder: false }] }, target, Folders, 'copy', { ok: true }))
      .toBe('Upload 2 files to Beta');
  });
});
