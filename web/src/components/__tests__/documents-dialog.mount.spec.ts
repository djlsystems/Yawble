// @vitest-environment happy-dom
//
// THE DOCUMENTS DIALOG'S FOLDER DIMENSION: which folder it opens on, gone and retired folders
// reached and said so, the read-only state of a gone folder, Delete and Delete all, opening,
// viewing and downloading, and an instance with no folders at all.
//
// Mounted CLOSED and then opened, because everything here loads from `watch(open)`. The fetch
// boundary is the fake Host in `test/documentsServer.ts`, so the explorer's own client builds every
// URL - the shapes are the server's records, `folder` being what a route is addressed with.
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

import { aFile, aFolder, documentsServer } from '../../test/documentsServer';
import {
  buttonLabelled,
  click,
  crumbLabels,
  doubleClick,
  menuItem,
  openExplorer,
  press,
  questionText,
  rightClick,
  row,
  rowNames,
  settle,
  toolbarButton,
  until,
} from '../../test/documentsExplorer';
import { bodyText, resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  localStorage.clear();
  q.screen.lt.sm = false;
  server = documentsServer();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('DocumentsDialog: where it opens', () => {
  /** Projects › Documents names no team: the root, every team's folder, gone and retired included. */
  it('Projects opens at the root, listing every folder with gone and retired marked', async () => {
    await openExplorer();

    expect(rowNames()).toEqual(['Alpha', 'Alpha', 'Beta', 'Gone']);
    expect(crumbLabels()).toEqual(['Documents']);
    expect(bodyText()).toContain('Superseded');
    expect(bodyText()).toContain('Gone team');
    expect(server.callsTo('list')).toHaveLength(0);
  });

  /** Active Team › Documents names the active team: its folder, addressed by the folder key. */
  it("Active Team opens at that team's folder", async () => {
    await openExplorer('alpha');

    expect(server.callsTo('list')[0]!.url).toBe('/api/teams/alpha/documents?path=&recursive=false');
    expect(crumbLabels()).toEqual(['Documents', 'Alpha']);
    expect(rowNames()).toEqual(['reports', 'notes.md']);
  });

  /**
   * MATCHED ON `folder`, NEVER ON `team`. A RETIRED folder keeps `team` and has its `folder` moved
   * aside, so matching on `team` would open the active team onto its predecessor's files.
   */
  it('opens the active team on its live folder, not on a predecessor that was retired', async () => {
    server = documentsServer({
      folders: [aFolder('alpha', 'Alpha', { retired: true, folder: 'alpha~2026' }), aFolder('alpha', 'Alpha')],
    });

    await openExplorer('alpha');

    expect(server.callsTo('list')[0]!.folder).toBe('alpha');
  });

  it('opens at the root when the active team has written nothing yet', async () => {
    await openExplorer('newteam');

    expect(crumbLabels()).toEqual(['Documents']);
    expect(server.callsTo('list')).toHaveLength(0);
  });

  it('says nothing has been written rather than failing when there are no folders at all', async () => {
    server = documentsServer({ folders: [] });

    await openExplorer();

    expect(bodyText()).toContain('Nothing has been written yet');
  });
});

describe('DocumentsDialog: gone and retired folders', () => {
  it('reaches a folder whose team no longer exists, and says so', async () => {
    await openExplorer();
    await doubleClick(row('Gone'));

    expect(rowNames()).toEqual(['left.md', 'over.txt']);
    expect(bodyText()).toContain('This team no longer exists. Its documents were kept.');
    expect(crumbLabels()).toEqual(['Documents', 'Gone']);
  });

  it('tells a retired folder apart from a deleted team in the banner', async () => {
    await openExplorer('alpha~2026');

    expect(bodyText()).toContain('A later team took this name.');
    expect(bodyText()).not.toContain('This team no longer exists');
  });

  it('withholds New folder, Upload, Paste and Rename in a gone folder, keeps Copy, Cut, Move to and Delete', async () => {
    await openExplorer('gone');
    await click(row('left.md'));

    for (const action of ['newFolder', 'upload', 'paste', 'rename']) expect(toolbarButton(action)!.disabled, action).toBe(true);
    for (const action of ['copy', 'cut', 'moveTo', 'delete', 'copyPath']) expect(toolbarButton(action)!.disabled, action).toBe(false);
  });

  it("deletes all of a gone team's documents after asking with the count, then goes to the root", async () => {
    await openExplorer('gone');

    await click(buttonLabelled('Delete all of these documents')!);
    expect(questionText()).toBe('Delete all 2 documents of Gone? This cannot be undone.');

    await click(document.body.querySelector('[data-confirm]')!);

    expect(server.callsTo('delete')[0]!.url).toBe('/api/teams/gone/documents?path=&recursive=true');
    expect(crumbLabels()).toEqual(['Documents']);
  });

  it('deletes all documents of several gone folders selected at the root, after one question naming each', async () => {
    server = documentsServer({
      folders: [aFolder('alpha', 'Alpha'), aFolder('gone', 'Gone', { exists: false }), aFolder('old', 'Old', { exists: false })],
      listings: { 'alpha:': [], 'gone:': [aFile('left.md'), aFile('over.txt')], 'old:': [aFile('x.md')] },
    });
    await openExplorer();

    await click(row('Gone'));
    await click(row('Old'), { ctrlKey: true });
    await rightClick(row('Old'));
    await click(menuItem('deleteAll')!);
    expect(questionText()).toBe('Delete all 3 documents of 2 folders (Gone, Old)? This cannot be undone.');

    await click(document.body.querySelector('[data-confirm]')!);

    expect(server.callsTo('delete').map((call) => call.url)).toEqual([
      '/api/teams/gone/documents?path=&recursive=true',
      '/api/teams/old/documents?path=&recursive=true',
    ]);
    expect(crumbLabels()).toEqual(['Documents']);
  });
});

describe('DocumentsDialog: the context menu', () => {
  /**
   * A SECOND RIGHT-CLICK WHILE THE MENU IS OPEN moves it to the new place and speaks of the new
   * item. The menu was shown once and kept: `show` on a showing menu does nothing, so it stayed
   * where it first opened, offering the first item's actions.
   */
  it('a right-click on another item while the menu is open reopens it for that item', async () => {
    await openExplorer('alpha');
    await rightClick(row('reports'));
    expect(menuItem('pasteInto') ?? menuItem('open')).toBeTruthy();
    expect(menuItem('download')).toBeNull();
    const place = () => {
      const menuElement = document.body.querySelector<HTMLElement>('.q-menu.documents-menu')!;
      return `${menuElement.style.left} ${menuElement.style.top}`;
    };
    const first = await until(() => (place().trim() ? place() : null));

    row('notes.md').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300, clientY: 200 }));
    await until(() => menuItem('download') && place() !== first);

    expect(place()).not.toBe(first);
    expect(document.body.querySelectorAll('.documents-menu-list')).toHaveLength(1);
    expect(menuItem('download')).toBeTruthy();
  });
});

describe('DocumentsDialog: delete', () => {
  it('asks first, naming what goes and saying a folder is not empty, and sends recursive only for it', async () => {
    await openExplorer('alpha');
    await click(row('reports'));
    await click(row('notes.md'), { ctrlKey: true });

    await click(toolbarButton('delete')!);
    expect(questionText()).toBe('Delete reports and notes.md? reports is a folder with 4 items. This cannot be undone.');
    expect(server.callsTo('delete')).toHaveLength(0);

    await click(document.body.querySelector('[data-confirm]')!);

    expect(server.callsTo('delete').map((call) => call.url)).toEqual([
      '/api/teams/alpha/documents?path=reports&recursive=true',
      '/api/teams/alpha/documents?path=notes.md',
    ]);
  });

  it('names the first two and counts the rest', async () => {
    await openExplorer('alpha');
    await doubleClick(row('reports'));
    await press('a', { ctrlKey: true });
    await press('Delete');

    expect(questionText()).toBe('Delete 2026, a.md and 2 more? This cannot be undone.');
  });

  it('sends nothing when the person cancels', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));
    await press('Delete');
    await click(buttonLabelled('Cancel')!);

    expect(server.callsTo('delete')).toHaveLength(0);
  });

  it('gathers per-item refusals into one banner with the server sentences', async () => {
    server.reply('delete', 409, { error: 'reports is not empty.' });

    await openExplorer('alpha');
    await click(row('notes.md'));
    await click(row('reports'), { ctrlKey: true });
    await press('Delete');
    await click(document.body.querySelector('[data-confirm]')!);

    expect(bodyText()).toContain('Not deleted:');
    expect(bodyText()).toContain('reports is not empty.');
  });
});

describe('DocumentsDialog: opening a document', () => {
  let windowOpen: ReturnType<typeof vi.spyOn>;
  let anchorClick: ReturnType<typeof vi.spyOn>;
  const clicked: string[] = [];

  beforeEach(() => {
    clicked.length = 0;
    windowOpen = vi.spyOn(window, 'open').mockReturnValue(null);
    anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this.getAttribute('href') ?? '');
    });
  });

  afterEach(() => {
    windowOpen.mockRestore();
    anchorClick.mockRestore();
  });

  it('opens a viewable file in a new tab with noopener on a double click', async () => {
    await openExplorer('alpha');
    await doubleClick(row('notes.md'));

    expect(windowOpen).toHaveBeenCalledWith('/api/teams/alpha/documents/view?path=notes.md', '_blank', 'noopener');
    expect(clicked).toEqual([]);
  });

  it('downloads a file the server does not show, from content', async () => {
    server = documentsServer({ listings: { 'alpha:': [aFile('archive.zip')] } });

    await openExplorer('alpha');
    await doubleClick(row('archive.zip'));

    expect(windowOpen).not.toHaveBeenCalled();
    expect(clicked).toEqual(['/api/teams/alpha/documents/content?path=archive.zip']);
  });

  it('a single click selects and does not open', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));

    expect(windowOpen).not.toHaveBeenCalled();
    expect(row('notes.md').getAttribute('aria-selected')).toBe('true');
  });

  it("opens a dead team's document too, keyed by the folder", async () => {
    await openExplorer('gone');
    await doubleClick(row('left.md'));

    expect(windowOpen).toHaveBeenCalledWith('/api/teams/gone/documents/view?path=left.md', '_blank', 'noopener');
  });

  it('View and Download from the menu go to view and content', async () => {
    await openExplorer('alpha');
    await rightClick(row('notes.md'));
    await click(menuItem('view')!);
    await rightClick(row('notes.md'));
    await click(menuItem('download')!);
    await settle();

    expect(windowOpen).toHaveBeenCalledWith('/api/teams/alpha/documents/view?path=notes.md', '_blank', 'noopener');
    expect(clicked).toEqual(['/api/teams/alpha/documents/content?path=notes.md']);
  });
});
