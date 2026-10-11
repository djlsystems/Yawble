// @vitest-environment happy-dom
//
// EACH ACTION FROM THE TOOLBAR, THE KEYBOARD AND THE CONTEXT MENU: copy then paste, cut then paste
// (the clipboard empties), Move to…, Delete, Rename inline, Copy as path, New folder, Upload and
// Refresh. A gone folder disables the writes with their reason; many selected leaves Rename and
// Copy as path out. The keyboard map is checked row by row, and the tree has its own menu.
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
  buttonLabelled,
  click,
  crumbLabels,
  doubleClick,
  menuActions,
  menuItem,
  openExplorer,
  pane,
  press,
  questionText,
  rightClick,
  row,
  selectedNames,
  settle,
  toolbarButton,
  treeNode,
  until,
} from '../../test/documentsExplorer';
import { resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;
let windowOpen: ReturnType<typeof vi.spyOn>;
let anchorClick: ReturnType<typeof vi.spyOn>;
const downloaded: string[] = [];

beforeEach(() => {
  localStorage.clear();
  q.copy.mockReset().mockResolvedValue(undefined);
  q.notify.mockReset();
  q.platform.is.mac = false;
  server = documentsServer();
  downloaded.length = 0;
  windowOpen = vi.spyOn(window, 'open').mockReturnValue(null);
  anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloaded.push(this.getAttribute('href') ?? '');
  });
});

afterEach(() => {
  windowOpen.mockRestore();
  anchorClick.mockRestore();
  vi.unstubAllGlobals();
  resetBody();
});

/** Into Alpha › reports, the folder most cases work in. */
async function inReports() {
  await openExplorer('alpha');
  await doubleClick(row('reports'));
}

const transfers = () => [...server.callsTo('move'), ...server.callsTo('copy')];

describe('the clipboard: copy, cut and paste', () => {
  it('copy then paste into another folder sends one copy with every path, from the toolbar', async () => {
    await inReports();
    await click(row('a.md'));
    await click(row('b.pdf'), { ctrlKey: true });
    await click(toolbarButton('copy')!);

    await click(buttonLabelled('Up')!);
    await click(buttonLabelled('Up')!);
    await doubleClick(row('Beta'));
    await click(toolbarButton('paste')!);

    expect(server.callsTo('copy').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/copy', { to: { folder: 'beta', path: '' }, items: [{ path: 'reports/a.md' }, { path: 'reports/b.pdf' }] }],
    ]);
  });

  it('cut then paste moves, and the clipboard empties of what was moved', async () => {
    await inReports();
    await click(row('a.md'));
    await press('x', { ctrlKey: true });
    await click(buttonLabelled('Up')!);
    await press('v', { ctrlKey: true });

    expect(server.callsTo('move')[0]!.body).toEqual({ to: { folder: 'alpha', path: '' }, items: [{ path: 'reports/a.md' }] });
    expect(toolbarButton('paste')!.disabled).toBe(true);
  });

  it('paste into a right-clicked folder pastes there', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));
    await rightClick(row('notes.md'));
    await click(menuItem('copy')!);

    await rightClick(row('reports'));
    await click(menuItem('pasteInto')!);

    expect(server.callsTo('copy')[0]!.body).toEqual({ to: { folder: 'alpha', path: 'reports' }, items: [{ path: 'notes.md' }] });
  });
});

describe('Move to…', () => {
  it('opens the picker and sends one move to the folder chosen', async () => {
    await inReports();
    await click(row('a.md'));
    await rightClick(row('a.md'));
    await click(menuItem('moveTo')!);

    const picker = await until(() => document.body.querySelector<HTMLElement>('.documents-move'));
    await click(picker.querySelector('[data-node="beta::"]')!);
    await click(picker.querySelector('[data-move="confirm"]')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([{ to: { folder: 'beta', path: '' }, items: [{ path: 'reports/a.md' }] }]);
  });
});

describe('Delete', () => {
  it('from the menu asks first, naming what goes', async () => {
    await inReports();
    await rightClick(row('b.pdf'));
    await click(menuItem('delete')!);

    expect(questionText()).toBe('Delete b.pdf? This cannot be undone.');
    await click(document.body.querySelector('[data-confirm]')!);

    expect(server.callsTo('delete').map((call) => call.url)).toEqual(['/api/teams/alpha/documents?path=reports%2Fb.pdf']);
  });
});

describe('Rename, inline', () => {
  const box = () => document.body.querySelector<HTMLInputElement>('.documents-rename-input');

  it('F2 opens it with the name selected without its extension; Enter renames', async () => {
    await inReports();
    await click(row('a.md'));
    await press('F2');

    expect(box()!.value).toBe('a.md');
    expect([box()!.selectionStart, box()!.selectionEnd]).toEqual([0, 1]);

    box()!.value = 'renamed.md';
    box()!.dispatchEvent(new Event('input'));
    await press('Enter', {}, box()!);

    expect(server.callsTo('rename').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/rename', { items: [{ path: 'reports/a.md', name: 'renamed.md' }] }],
    ]);
    expect(box()).toBeNull();
  });

  it('Escape cancels and sends nothing, and keys typed in the box do not act', async () => {
    await inReports();
    await click(row('a.md'));
    await click(toolbarButton('rename')!);

    await press('Delete', {}, box()!);
    await press('a', { ctrlKey: true }, box()!);
    expect(questionText()).toBe('');
    expect(selectedNames()).toEqual(['a.md']);

    await press('Escape', {}, box()!);
    expect(box()).toBeNull();
    expect(server.callsTo('rename')).toHaveLength(0);
  });

  it('a refusal keeps the box open with the server sentence under it', async () => {
    server.reply('rename', 409, { error: 'There is already b.pdf in reports.' });

    await inReports();
    await click(row('a.md'));
    await rightClick(row('a.md'));
    await click(menuItem('rename')!);
    box()!.value = 'b.pdf';
    box()!.dispatchEvent(new Event('input'));
    await press('Enter', {}, box()!);

    expect(box()).not.toBeNull();
    expect(document.body.querySelector('.documents-rename-error')!.textContent).toBe('There is already b.pdf in reports.');
  });
});

describe('Copy as path', () => {
  it('copies <root>/<folder>/<path> and says Copied', async () => {
    await inReports();
    await click(row('b.pdf'));
    await click(toolbarButton('copyPath')!);

    expect(q.copy).toHaveBeenCalledWith('/data/documents/alpha/reports/b.pdf');
    expect(q.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Copied', caption: '/data/documents/alpha/reports/b.pdf' }));
  });
});

describe('New folder, Upload and Refresh', () => {
  it('New folder asks a name and creates it in the folder on screen', async () => {
    await inReports();
    await click(toolbarButton('newFolder')!);
    const input = await until(() => document.body.querySelector<HTMLInputElement>('[data-name-input] input, input[data-name-input]'));
    input.value = 'drafts';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(document.body.querySelector('[data-name-confirm]')!);

    expect(server.callsTo('folders').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/folders', { path: 'reports/drafts' }],
    ]);
  });

  it('Upload sends each chosen file into the folder on screen, asking about clashes', async () => {
    await inReports();
    const input = document.body.querySelector<HTMLInputElement>('[data-upload-input]')!;
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'one.md'), new File(['y'], 'two.md')], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle();

    expect(server.callsTo('upload').map((call) => call.body)).toEqual([
      { file: 'one.md', path: 'reports', onClash: 'ask' },
      { file: 'two.md', path: 'reports', onClash: 'ask' },
    ]);
  });

  it('Refresh lists again', async () => {
    await inReports();
    const before = server.callsTo('list').length;
    await click(toolbarButton('refresh')!);

    expect(server.callsTo('list').length).toBeGreaterThan(before);
  });
});

describe('what is offered where', () => {
  it('in a gone folder the writes are disabled with their reason', async () => {
    await openExplorer('gone');
    await rightClick(pane());

    expect(menuItem('newFolder')!.classList).toContain('disabled');
    expect(menuItem('newFolder')!.textContent).toContain("Nothing can be added to a gone team's documents");
  });

  it('with many selected, Rename and Copy as path are not in the menu and are disabled on the toolbar', async () => {
    await inReports();
    await press('a', { ctrlKey: true });
    await rightClick(row('a.md'));

    expect(menuActions()).toEqual(['cut', 'copy', 'moveTo', 'delete']);
    expect(toolbarButton('rename')!.disabled).toBe(true);
    expect(toolbarButton('copyPath')!.disabled).toBe(true);
  });

  it('on empty space: New folder, Upload, Paste, Refresh, View and Sort by', async () => {
    await inReports();
    await rightClick(pane());

    expect(menuActions()).toEqual(['newFolder', 'upload', 'paste', 'refresh']);
    expect(document.body.querySelectorAll('.documents-menu [data-submenu]')).toHaveLength(2);
  });
});

describe('the tree context menu', () => {
  async function treeMenu(folder: string, path = '') {
    await rightClick(treeNode(folder, path)!);
  }

  it('offers Open, Paste into, Copy as path and New folder inside', async () => {
    await openExplorer('alpha');
    await treeMenu('beta');

    expect(menuActions()).toEqual(['open', 'pasteInto', 'copyPath', 'newFolderInside']);
  });

  it('Open goes there, Copy as path copies its path, Paste into pastes there, New folder inside creates there', async () => {
    await openExplorer('alpha');
    await treeMenu('beta');
    await click(menuItem('open')!);
    expect(crumbLabels()).toEqual(['Documents', 'Beta']);

    await treeMenu('alpha');
    await click(menuItem('copyPath')!);
    expect(q.copy).toHaveBeenCalledWith('/data/documents/alpha');

    await click(buttonLabelled('Up')!);
    await doubleClick(row('Alpha'));
    await click(row('notes.md'));
    await press('c', { ctrlKey: true });
    await treeMenu('beta');
    await click(menuItem('pasteInto')!);
    expect(server.callsTo('copy').at(-1)!.body).toEqual({ to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md' }] });

    await treeMenu('beta');
    await click(menuItem('newFolderInside')!);
    const input = await until(() => document.body.querySelector<HTMLInputElement>('[data-name-input] input, input[data-name-input]'));
    input.value = 'inbox';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(document.body.querySelector('[data-name-confirm]')!);
    expect(server.callsTo('folders').at(-1)!.url).toBe('/api/teams/beta/documents/folders');
    expect(server.callsTo('folders').at(-1)!.body).toEqual({ path: 'inbox' });
  });
});

/**
 * THE KEYBOARD MAP, ROW BY ROW (plan 2.6). Each row starts in Alpha › reports with `a.md`
 * selected, presses its key in the view pane, and checks what happened.
 */
describe('the keyboard map', () => {
  type Check = () => void | Promise<void>;
  const rowsOfTheMap: [string, string, KeyboardEventInit, Check][] = [
    ['ArrowDown selects the next', 'ArrowDown', {}, () => expect(selectedNames()).toEqual(['b.pdf'])],
    ['ArrowUp selects the previous', 'ArrowUp', {}, () => expect(selectedNames()).toEqual(['2026'])],
    ['Shift+ArrowDown extends', 'ArrowDown', { shiftKey: true }, () => expect(selectedNames()).toEqual(['a.md', 'b.pdf'])],
    ['Ctrl+ArrowDown moves focus only', 'ArrowDown', { ctrlKey: true }, () => {
      expect(selectedNames()).toEqual(['a.md']);
      expect(row('b.pdf').classList).toContain('documents-item-focus');
    }],
    ['Space toggles', ' ', {}, () => expect(selectedNames()).toEqual([])],
    ['Enter on a viewable file views it', 'Enter', {}, () => expect(windowOpen).toHaveBeenCalledWith('/api/teams/alpha/documents/view?path=reports%2Fa.md', '_blank', 'noopener')],
    ['Backspace goes up', 'Backspace', {}, () => expect(crumbLabels()).toEqual(['Documents', 'Alpha'])],
    ['Alt+Up goes up', 'ArrowUp', { altKey: true }, () => expect(crumbLabels()).toEqual(['Documents', 'Alpha'])],
    ['Alt+Left goes back', 'ArrowLeft', { altKey: true }, () => expect(crumbLabels()).toEqual(['Documents', 'Alpha'])],
    ['Ctrl+A selects all', 'a', { ctrlKey: true }, () => expect(selectedNames()).toEqual(['2026', 'a.md', 'b.pdf', 'c.png'])],
    ['Cmd+A selects all', 'a', { metaKey: true }, () => expect(selectedNames()).toHaveLength(4)],
    ['Ctrl+C copies', 'c', { ctrlKey: true }, () => expect(toolbarButton('paste')!.disabled).toBe(false)],
    ['Ctrl+X cuts', 'x', { ctrlKey: true }, () => expect(toolbarButton('paste')!.disabled).toBe(false)],
    ['Delete asks to delete', 'Delete', {}, () => expect(questionText()).toBe('Delete a.md? This cannot be undone.')],
    ['F2 renames', 'F2', {}, () => expect(document.body.querySelector('.documents-rename-input')).not.toBeNull()],
    ['Ctrl+Shift+C copies as path', 'C', { ctrlKey: true, shiftKey: true }, () => expect(q.copy).toHaveBeenCalledWith('/data/documents/alpha/reports/a.md')],
    ['Ctrl+Shift+N makes a new folder', 'N', { ctrlKey: true, shiftKey: true }, () => expect(document.body.querySelector('[data-name-confirm]')).not.toBeNull()],
    ['F5 refreshes', 'F5', {}, () => expect(server.callsTo('root').length).toBeGreaterThan(1)],
  ];

  it.each(rowsOfTheMap)('%s', async (_name, key, modifiers, check) => {
    await inReports();
    await click(row('a.md'));

    await press(key, modifiers);
    await check();
  });

  it('Enter on a folder enters it', async () => {
    await openExplorer('alpha');
    await click(row('reports'));
    await press('Enter');

    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports']);
  });

  it('Enter downloads a file that cannot be viewed', async () => {
    server = documentsServer({ listings: { 'alpha:': [{ name: 'x.zip', path: 'x.zip', isFolder: false, size: 1, modifiedAt: '', children: 0 }] } });
    await openExplorer('alpha');
    await click(row('x.zip'));
    await press('Enter');

    expect(downloaded).toEqual(['/api/teams/alpha/documents/content?path=x.zip']);
  });

  it('Alt+Right goes forward again after Alt+Left', async () => {
    await inReports();
    await press('ArrowLeft', { altKey: true });
    await press('ArrowRight', { altKey: true });

    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports']);
  });

  it('Ctrl+V pastes into the folder on screen', async () => {
    await inReports();
    await click(row('a.md'));
    await press('c', { ctrlKey: true });
    await press('Backspace');
    await press('v', { ctrlKey: true });

    expect(server.callsTo('copy')[0]!.body).toEqual({ to: { folder: 'alpha', path: '' }, items: [{ path: 'reports/a.md' }] });
  });

  it('Cmd+Backspace deletes on macOS', async () => {
    q.platform.is.mac = true;
    await inReports();
    await click(row('a.md'));
    await press('Backspace', { metaKey: true });

    expect(questionText()).toBe('Delete a.md? This cannot be undone.');
    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports']);
  });

  it('Escape clears the selection first, then closes the dialog', async () => {
    const wrapper = await openExplorer('alpha');
    await click(row('notes.md'));

    await press('Escape');
    expect(selectedNames()).toEqual([]);
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();

    await press('Escape');
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('every keyboard action leaves the server alone when nothing applies', async () => {
    await openExplorer();
    await press('Delete');
    await press('F2');
    await press('x', { ctrlKey: true });

    expect(questionText()).toBe('');
    expect(transfers()).toHaveLength(0);
    expect(document.body.querySelector('.documents-rename-input')).toBeNull();
  });
});
