// @vitest-environment happy-dom
//
// THE DOCUMENTS DIALOG'S FOLDER DIMENSION.
//
// There are many folders, and the case that matters most is the one no source scan can see - a
// folder whose TEAM IS GONE must still be reachable and readable, and must say so.
//
// WHY MOUNTED RATHER THAN A `lib/` FUNCTION: "which folder is on screen", "the delete button is
// absent" and "the download link is still there" are all TEMPLATE conditions. A grep finds the
// symbol either way, which is exactly the class of defect `backlog-dialog.mount.spec.ts` guards.
//
// Mounted CLOSED and then opened, because everything here loads from `watch(open)` - which fires on
// a TRANSITION, not on a first render that happens to be true. `mountDialog` owns that rule.
//
// ---
// THE MOCK IS LEGITIMATE HERE, and would not be in `ribbon-documents.mount.spec.ts` - a distinction
// worth stating rather than inferring. This file asks what the dialog DOES with a
// folder list: which one it opens on, what it renders, which controls it withholds. Those are
// answered by the component. It does NOT ask what URL the list came from - that question is the one
// the mock destroys, and it is pinned with no mock at all in `lib/__tests__/documents-api.spec.ts`.
//
// THE MOCKED SHAPE IS THE REAL RECORD: `{ folder, team, label, exists, retired, entries,
// modifiedAt }`, and `folder` is what a route is addressed with. A mock in the wrong shape is not a
// weaker test than no mock, it is a CONFIDENT one.
// ---
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const {
  listDocumentFolders, listDocuments, documentUrl, createFolder, uploadDocument, deleteDocument,
  documentViewUrl,
} = vi.hoisted(() => ({
  documentViewUrl: vi.fn(),
  listDocumentFolders: vi.fn(),
  listDocuments: vi.fn(),
  documentUrl: vi.fn(),
  createFolder: vi.fn(),
  uploadDocument: vi.fn(),
  deleteDocument: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/documents', () => ({
  listDocumentFolders,
  listDocuments,
  documentUrl,
  documentViewUrl,
  createFolder,
  uploadDocument,
  deleteDocument,
}));

import DocumentsDialog from '../DocumentsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asDocumentsFolderKey, asTeamId, type TeamId } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

/**
 * A folder as `GET /api/documents` really answers it.
 *
 * `folder` DEFAULTS TO THE TEAM ID because that is true of every live folder, and the default is
 * what makes the retired case below have to say otherwise explicitly - the same reason the team
 * fixtures let a name differ from an id.
 */
const aFolder = (
  team: string,
  label: string,
  { exists = true, retired = false, folder = team } = {},
) => ({
  folder: asDocumentsFolderKey(folder),
  team: asTeamId(team),
  label,
  exists,
  retired,
  entries: 2,
  modifiedAt: '2026-09-01T00:00:00Z',
});

const file = (name: string) => ({
  name,
  path: name,
  isFolder: false,
  size: 12,
  modifiedAt: '2026-09-01T00:00:00Z',
  children: 0,
});

/** A fresh Pinia the dialog will find, seeded before the mount so `mountDialog` must not make one. */
function seedBoard(activeTeamId = '') {
  setActivePinia(createPinia());
  useConsoleStore().activeTeamId = activeTeamId as TeamId;
}

/** NO ARGUMENT. Documents is a plain button, so the dialog chooses its own folder and that choice is what several cases below
 *  are about. */
async function open() {
  const wrapper = await mountDialog(DocumentsDialog, {}, { pinia: false });
  await flushPromises();

  return wrapper;
}

function buttonLabelled(label: string): HTMLButtonElement | undefined {
  return [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim().includes(label)) as HTMLButtonElement | undefined;
}

beforeEach(() => {
  vi.clearAllMocks();

  listDocumentFolders.mockResolvedValue([
    aFolder('alpha', 'Alpha'),
    aFolder('b9-nothing-measured', 'b9-nothing-measured', { exists: false }),
  ]);
  listDocuments.mockResolvedValue([file('report.md')]);
  documentUrl.mockImplementation(
    (folder: string, path: string) => `/api/teams/${folder}/documents/content?path=${path}`,
  );
});

afterEach(resetBody);

describe('DocumentsDialog, mounted', () => {
  /**
   * WHOSE DOCUMENTS THESE ARE. It is not enough that the right files are fetched - a person looking at a
   * list of filenames has to be able to tell whose they are, so the screen says so.
   */
  it('names whose documents are on screen', async () => {
    seedBoard('alpha');

    await open();

    expect(bodyText()).toContain('Alpha');
    expect(listDocuments).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), '');
  });

  /** The active team is the folder somebody working in a team means nine times out of ten - but a
   *  GUESS, made against the folders that exist rather than assumed to be one of them. */
  it('opens on the active team when it has a folder', async () => {
    seedBoard('alpha');

    await open();

    expect(listDocuments).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), '');
  });

  it('falls back to a folder that exists when the active team has written nothing', async () => {
    seedBoard('team-with-no-documents');

    await open();

    expect(listDocuments).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), '');
  });

  /**
   * THE RETIRED CASE, AND IT IS WHY `chooseFolder` MATCHES ON `folder` AND NEVER ON `team`.
   *
   * Both fields hold the identifier for a live folder, so the wrong one works everywhere except
   * here. A folder is retired BECAUSE a later team took its identifier - so that team exists by
   * construction, it is the active one in this case, and the predecessor still carries its `team`.
   * `entry.team === activeTeamId` therefore matches the DEAD folder and opens the active team onto
   * somebody else's files.
   *
   * SEEN TO FAIL: rewriting the match to `entry.team` reddens this and nothing else in the file.
   * The retired folder is seeded FIRST so that a `team` match finds it before the live one - with
   * the order reversed this case passes against the defect, which is why it is spelled out here.
   */
  it('opens the active team on its live folder, not on a predecessor that was retired', async () => {
    seedBoard('alpha');
    listDocumentFolders.mockResolvedValue([
      aFolder('alpha', 'Alpha (earlier)', { exists: false, retired: true, folder: 'alpha~2026-09-01' }),
      aFolder('alpha', 'Alpha'),
    ]);

    await open();

    expect(listDocuments).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), '');
    expect(listDocuments).not.toHaveBeenCalledWith(asDocumentsFolderKey('alpha~2026-09-01'), '');
  });

  /**
   * THE CASE THE WHOLE ITEM EXISTS FOR - B9 produced no code and its measurement WAS the output.
   * Reachable, readable, and SAYING that the team is gone: a folder named for a team a person
   * cannot find anywhere else in the product reads as a broken screen otherwise.
   */
  it('reaches a folder whose team no longer exists, and says so', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([
      aFolder('b9-nothing-measured', 'b9-nothing-measured', { exists: false }),
    ]);

    await open();

    expect(listDocuments).toHaveBeenCalledWith(asDocumentsFolderKey('b9-nothing-measured'), '');
    expect(bodyText()).toContain('This team no longer exists');
    expect(bodyText()).toContain('report.md');
  });

  /**
   * TWO SENTENCES FOR TWO FACTS. "The team was deleted" and "a later team took this name" have
   * different fixes and lead a reader to different places - the second says the team on screen
   * right now is a DIFFERENT team from the one these files belong to.
   */
  it('tells a retired folder apart from a deleted team in the banner', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([
      aFolder('alpha', 'Alpha (earlier)', { exists: false, retired: true, folder: 'alpha~2026-09-01' }),
    ]);

    await open();

    expect(bodyText()).toContain('A later team took this name');
    expect(bodyText()).not.toContain('This team no longer exists');
  });

  /**
   * READING IS NEVER WITHHELD FROM A DEAD TEAM'S FOLDER. Asserting the banner alone would pass just
   * as well against a dialog that showed the words over an empty, unusable list - which is the
   * failure that would make keeping the folder pointless.
   */
  it('still offers the download link for a dead team, keyed by the folder', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([
      aFolder('b9-nothing-measured', 'b9-nothing-measured', { exists: false }),
    ]);

    await open();

    const download = bodyFind('[aria-label="Download"]');

    expect(download).not.toBeNull();
    expect(download?.getAttribute('href'))
      .toBe('/api/teams/b9-nothing-measured/documents/content?path=report.md');
  });

  /**
   * ADDING IS WITHHELD FROM A DEAD TEAM, DELETING IS NOT. A person may clear a record out
   * but not add to it: Upload and New folder stay disabled, Delete is on every row, and the whole
   * folder is offered beside the banner. A live team keeps every write and is NOT offered the
   * whole-folder delete - its root cannot be removed.
   */
  it('offers a dead team delete and "Delete all of these documents" but not upload or new folder', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([
      aFolder('b9-nothing-measured', 'b9-nothing-measured', { exists: false }),
    ]);

    await open();

    expect(bodyText()).toContain(
      'This team no longer exists. Its documents were kept. You can read or delete them here.',
    );
    expect(bodyFind('[aria-label="Delete document"]')).not.toBeNull();
    expect(buttonLabelled('Delete all of these documents')).toBeDefined();
    expect(buttonLabelled('Upload')?.disabled).toBe(true);
    expect(buttonLabelled('New folder')?.disabled).toBe(true);

    resetBody();
    seedBoard('alpha');
    listDocumentFolders.mockResolvedValue([aFolder('alpha', 'Alpha')]);

    await open();

    expect(bodyFind('[aria-label="Delete document"]')).not.toBeNull();
    expect(buttonLabelled('Delete all of these documents')).toBeUndefined();
    expect(buttonLabelled('Upload')?.disabled).toBe(false);
    expect(buttonLabelled('New folder')?.disabled).toBe(false);
  });

  /**
   * A FOLDER WITH THINGS IN IT IS DELETED WITH THEM, AFTER A QUESTION THAT SAYS HOW MANY FILES GO.
   * `recursive` is only ever sent past a yes; a no sends nothing at all.
   */
  it('asks before deleting a non-empty folder, with the file count, and sends recursive', async () => {
    seedBoard('alpha');
    listDocumentFolders.mockResolvedValue([aFolder('alpha', 'Alpha')]);
    listDocuments.mockImplementation(async (_folder: string, path: string, recursive?: boolean) =>
      recursive
        ? [file('notes/a.md'), file('notes/b.md'), file('notes/deep/c.md')]
        : path === ''
          ? [{ ...file('notes'), isFolder: true, children: 3 }]
          : []);

    await open();

    (bodyFind('[aria-label="Remove folder"]') as HTMLElement).click();
    await flushPromises();

    expect(bodyText()).toContain(
      'Delete the folder notes and the 3 files in it? This cannot be undone.',
    );
    expect(deleteDocument).not.toHaveBeenCalled();

    buttonLabelled('Cancel')!.click();
    await flushPromises();
    expect(deleteDocument).not.toHaveBeenCalled();

    (bodyFind('[aria-label="Remove folder"]') as HTMLElement).click();
    await flushPromises();
    [...document.body.querySelectorAll('button')]
      .find((candidate) => candidate.textContent?.trim() === 'Delete')!.click();
    await flushPromises();

    expect(deleteDocument).toHaveBeenCalledTimes(1);
    expect(deleteDocument).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), 'notes', true);
  });

  /** An empty folder and a file are deleted as before: no question, no `recursive`. */
  it('deletes a file without asking and without recursive', async () => {
    seedBoard('alpha');
    listDocumentFolders.mockResolvedValue([aFolder('alpha', 'Alpha')]);

    await open();

    (bodyFind('[aria-label="Delete document"]') as HTMLElement).click();
    await flushPromises();

    expect(deleteDocument).toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), 'report.md', false);
  });

  /**
   * THE WHOLE OF A GONE TEAM'S FOLDER, after a question naming the file count. Afterwards the
   * folder has left the picker and the dialog is on the NEXT one, not back at the first.
   */
  it('deletes all of a gone team\'s documents after asking, then moves to the next folder', async () => {
    // Opened ON the gone folder: the dialog opens on the folder keyed like the active team, and it
    // sits between two others so "the next one" is not also "the first one".
    seedBoard('gone');
    const before = [
      aFolder('alpha', 'Alpha'),
      aFolder('gone', 'gone', { exists: false }),
      aFolder('zulu', 'Zulu'),
    ];
    listDocumentFolders.mockResolvedValue(before);
    listDocuments.mockImplementation(async (_folder: string, _path: string, recursive?: boolean) =>
      recursive ? [file('a.md'), file('b.md')] : [file('report.md')]);

    await open();

    expect(listDocuments).toHaveBeenLastCalledWith(asDocumentsFolderKey('gone'), '');

    buttonLabelled('Delete all of these documents')!.click();
    await flushPromises();

    expect(bodyText()).toContain('Delete all 2 documents of gone? This cannot be undone.');
    expect(deleteDocument).not.toHaveBeenCalled();

    listDocumentFolders.mockResolvedValue([before[0], before[2]]);
    [...document.body.querySelectorAll('button')]
      .find((candidate) => candidate.textContent?.trim() === 'Delete')!.click();
    await flushPromises();

    expect(deleteDocument).toHaveBeenCalledWith(asDocumentsFolderKey('gone'), '', true);
    expect(listDocuments).toHaveBeenLastCalledWith(asDocumentsFolderKey('zulu'), '');
    expect(bodyText()).not.toContain('This team no longer exists');
  });

  /** An instance where nobody has written anything is an ORDINARY state, not an error - the same
   *  rule the console's no-teams case follows. */
  it('says nothing has been written rather than failing when there are no folders at all', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([]);

    await open();

    expect(listDocuments).not.toHaveBeenCalled();
    expect(bodyText()).toContain('Nothing has been written yet');
  });
});

/**
 * OPEN IN THE BROWSER. A click on a file row opens `view` in a new tab when a browser
 * can show it, and downloads it when it cannot - and the row says which in its tooltip. The
 * download icon is unchanged either way.
 */
describe('DocumentsDialog, opening a document', () => {
  let windowOpen: ReturnType<typeof vi.spyOn>;
  let anchorClick: ReturnType<typeof vi.spyOn>;
  const clicked: string[] = [];

  beforeEach(() => {
    clicked.length = 0;
    documentViewUrl.mockImplementation(
      (folder: string, path: string) => `/api/teams/${folder}/documents/view?path=${path}`,
    );
    windowOpen = vi.spyOn(window, 'open').mockReturnValue(null);
    anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicked.push(this.getAttribute('href') ?? '');
    });
  });

  afterEach(() => {
    windowOpen.mockRestore();
    anchorClick.mockRestore();
  });

  async function tooltipTexts(wrapper: VueWrapper): Promise<string[]> {
    const tooltips = wrapper.findAllComponents({ name: 'QTooltip' });
    for (const tooltip of tooltips) (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();

    return [...document.body.querySelectorAll('.q-tooltip')].map((t) => t.textContent?.trim() ?? '');
  }

  function row(name: string): HTMLElement {
    const label = [...document.body.querySelectorAll<HTMLElement>('.q-item__label')]
      .find((candidate) => candidate.textContent?.trim() === name);
    expect(label, `a row named ${name}`).toBeDefined();
    return label!;
  }

  it('opens a viewable file in a new tab with noopener', async () => {
    seedBoard('alpha');

    await open();
    row('report.md').click();

    expect(windowOpen).toHaveBeenCalledWith(
      '/api/teams/alpha/documents/view?path=report.md', '_blank', 'noopener',
    );
    expect(clicked).toEqual([]);
    expect(row('report.md').getAttribute('data-row-action')).toBe('Open');
  });

  it('opens every kind the server shows, and only those', async () => {
    seedBoard('alpha');
    listDocuments.mockResolvedValue([
      file('page.html'), file('shot.PNG'), file('data.json'), file('paper.pdf'), file('archive.zip'),
    ]);

    await open();

    expect(row('page.html').getAttribute('data-row-action')).toBe('Open');
    expect(row('shot.PNG').getAttribute('data-row-action')).toBe('Open');
    expect(row('data.json').getAttribute('data-row-action')).toBe('Open');
    expect(row('paper.pdf').getAttribute('data-row-action')).toBe('Open');
    expect(row('archive.zip').getAttribute('data-row-action')).toBe('Download');
    expect(documentViewUrl).not.toHaveBeenCalledWith(asDocumentsFolderKey('alpha'), 'archive.zip');
  });

  it('downloads an unviewable file on click, and its tooltip says Download not Open', async () => {
    seedBoard('alpha');
    listDocuments.mockResolvedValue([file('archive.zip')]);

    const wrapper = await open();
    const label = row('archive.zip');
    label.click();

    expect(windowOpen).not.toHaveBeenCalled();
    expect(clicked).toEqual(['/api/teams/alpha/documents/content?path=archive.zip']);
    expect(label.getAttribute('data-row-action')).toBe('Download');

    // What a person reads: QTooltip is absent from the DOM until shown, so it is shown.
    expect(await tooltipTexts(wrapper)).toContain('Download');
    expect(await tooltipTexts(wrapper)).not.toContain('Open');
  });

  it('says Open in the tooltip of a viewable file', async () => {
    seedBoard('alpha');

    const wrapper = await open();

    expect(await tooltipTexts(wrapper)).toContain('Open');
    expect(await tooltipTexts(wrapper)).not.toContain('Download');
  });

  it('keeps the download icon, which still downloads from content', async () => {
    seedBoard('alpha');

    await open();

    const download = bodyFind('[aria-label="Download"]');
    expect(download?.getAttribute('href')).toBe('/api/teams/alpha/documents/content?path=report.md');
    expect(windowOpen).not.toHaveBeenCalled();
  });

  it('opens a dead team\'s document too, keyed by the folder', async () => {
    seedBoard();
    listDocumentFolders.mockResolvedValue([
      aFolder('b9-nothing-measured', 'b9-nothing-measured', { exists: false }),
    ]);

    await open();
    row('report.md').click();

    expect(windowOpen).toHaveBeenCalledWith(
      '/api/teams/b9-nothing-measured/documents/view?path=report.md', '_blank', 'noopener',
    );
  });
});
