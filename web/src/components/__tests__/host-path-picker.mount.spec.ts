// @vitest-environment happy-dom
//
// THE HOST PICKER ON THE SHARED BROWSER.
//
// ONE COMPONENT OVER TWO DATA SOURCES: the look and the navigation are shared, the BOUNDARIES are
// not. This
// file is about THIS side of that - `HostPathPicker` rendering `FileBrowser` over
// `lib/fileSystemSource.ts`, which reaches the machine through the four routes `/api/fs` has
// and no fifth.
//
// THE CASE THIS FILE EXISTS FOR IS THE FIRST ONE: rendered with the host-picker source there must be NO delete control at all - absent because the
// capability was never passed, not present and disabled, and not hidden behind a `v-if` of this
// component's own. `/api/fs` has no delete route to answer one and adding a fifth is deliberately
// not done, so a control here would be offering something that cannot exist.
//
// SEEN TO FAIL: binding `:deletion="..."` on the `<FileBrowser>` in `HostPathPicker.vue` reddens
// that case - both halves of it - and nothing else in the file.
//
// THE MOCK IS OF `api/client` AND THAT IS THE LINE IT MUST NOT CROSS. This file asks what the
// picker DOES with what the four routes answer: which rows it draws, which controls it withholds,
// what it emits and when. It does NOT ask what the routes are called or what they return - those
// are the server's, pinned server-side, and a mock that answered them would be the confident kind
// of wrong.
//
// Mounted CLOSED and then opened, because the picker loads from `watch(open)` - which fires on a
// TRANSITION, not on a first render that happens to be true. `mountDialog` owns that rule.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { fileSystemRoots, browseFileSystem, createHostDirectory, uploadHostFile } = vi.hoisted(() => ({
  fileSystemRoots: vi.fn(),
  browseFileSystem: vi.fn(),
  createHostDirectory: vi.fn(),
  uploadHostFile: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', () => ({
  fileSystemRoots,
  browseFileSystem,
  createHostDirectory,
  uploadHostFile,
}));

import HostPathPicker from '../HostPathPicker.vue';
import FileBrowser from '../FileBrowser.vue';
import { hostFileBrowser } from '../../lib/fileSystemSource';
import type { DirectoryListing, FileSystemRoot, HostEntry } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const aRoot = (name: string, path: string, allowCreate = false): FileSystemRoot => ({
  name,
  path,
  isInstance: false,
  allowCreate,
  allowUpdate: false,
  allowDelete: false,
});

/** A browse response as `GET /api/fs/browse` really answers it - `truncated` and `total` ride
 *  together, which is why they are one argument here. */
const aListing = (
  path: string,
  entries: HostEntry[],
  { allowCreate = false, total = entries.length } = {},
): DirectoryListing => ({
  path,
  parent: null,
  permissions: { allowCreate, allowUpdate: false, allowDelete: false },
  entries,
  truncated: total > entries.length,
  total,
});

const someEntries: HostEntry[] = [
  { name: 'notes.txt', type: 'file' },
  { name: 'alpha', type: 'dir' },
];

async function openPicker() {
  const wrapper = await mountDialog(HostPathPicker, {});
  await flushPromises();

  return wrapper;
}

function buttonLabelled(label: string): HTMLButtonElement | undefined {
  return [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim().includes(label)) as HTMLButtonElement | undefined;
}

/** A row is opened by clicking its NAME, which is where the shared browser hangs the handler. */
function rowNamed(name: string): HTMLElement | undefined {
  return [...document.body.querySelectorAll('.q-item__label')]
    .find((candidate) => candidate.textContent?.trim() === name) as HTMLElement | undefined;
}

beforeEach(() => {
  vi.clearAllMocks();

  fileSystemRoots.mockResolvedValue({
    roots: [aRoot('Documents', '/srv/docs'), aRoot('Work', '/srv/work', true)],
  });
  browseFileSystem.mockImplementation((path: string) => Promise.resolve(aListing(path, someEntries)));
});

afterEach(resetBody);

describe('HostPathPicker, mounted on the shared FileBrowser', () => {
  /**
   * THE ACCEPTANCE. Delete must be ABSENT here, and absent because `HostPathPicker` never passes
   * the capability - see the `deletion` prop on `FileBrowser`, which is the whole of the
   * mechanism. Asserted at BOTH levels, because a picker that only withheld it at the roots would
   * pass a check made in one place.
   */
  it('offers no delete control, at the roots level or inside a root', async () => {
    await openPicker();

    expect(bodyText()).toContain('Documents');
    expect(document.body.querySelectorAll('[aria-label^="Delete"]')).toHaveLength(0);
    expect(bodyFind('[aria-label="Remove folder"]')).toBeNull();

    rowNamed('Work')?.click();
    await flushPromises();

    expect(bodyText()).toContain('alpha');
    expect(document.body.querySelectorAll('[aria-label^="Delete"]')).toHaveLength(0);
    expect(bodyFind('[aria-label="Remove folder"]')).toBeNull();
  });

  /**
   * The same fact one level down, where it is a property of the SOURCE rather than of the markup:
   * `documentsSource` hands back `{ source, deletion }` and this one hands back no capability at
   * all, so there is nothing here for a caller to pass even by accident.
   */
  it('hands back no delete capability for anything to pass', () => {
    expect(hostFileBrowser()).not.toHaveProperty('deletion');
  });

  /**
   * THE OTHER HALF OF THE ACCEPTANCE: "a grep should find one breadcrumb, one row template, one
   * empty state". Asserted on the RENDERED picker: one `FileBrowser`, handed no delete capability,
   * and every row on screen drawn by it - if a second list of rows ever comes back into this
   * component, a row outside the browser reddens this.
   */
  it('renders every row through the one FileBrowser, with no delete capability bound', async () => {
    const wrapper = await openPicker();

    const browsers = wrapper.findAllComponents(FileBrowser);
    expect(browsers).toHaveLength(1);
    expect(browsers[0]!.props('deletion')).toBeUndefined();

    // Searched through the component's own subtree: `FileBrowser` renders a fragment, so its
    // `element` is not a container for what it drew.
    const drawnByBrowser = browsers[0]!.findAll('.q-item').map((row) => row.element);
    const onScreen = [...document.body.querySelectorAll('.q-item')];
    expect(onScreen.length).toBeGreaterThan(0);
    expect(onScreen.filter((row) => !drawnByBrowser.includes(row))).toEqual([]);
  });

  /**
   * THE ROOTS ARE A LISTING: entering one is an ordinary row click through the shared renderer
   * rather than a screen of this component's own. The allowlist is read ONCE per open, and nothing
   * is browsed until a root is picked.
   */
  it('draws the allowlist as rows and enters a root on a click', async () => {
    await openPicker();

    expect(fileSystemRoots).toHaveBeenCalledTimes(1);
    expect(browseFileSystem).not.toHaveBeenCalled();
    expect(bodyText()).toContain('/srv/docs');

    rowNamed('Documents')?.click();
    await flushPromises();

    expect(browseFileSystem).toHaveBeenCalledWith('/srv/docs');
  });

  /**
   * EMPTY ROOTS IS A REAL STATE - an operator configured roots and every one of them was dropped -
   * and the sentence naming the setting is what makes it a configuration problem rather than a
   * broken screen. It lives in the browser's `empty` slot, and it must be reachable there.
   */
  it('says why there is nothing to browse when no root is configured', async () => {
    fileSystemRoots.mockResolvedValue({ roots: [] });

    await openPicker();

    expect(bodyText()).toContain('No host access configured');
    expect(bodyText()).toContain('FileBrowser:Roots');
  });

  /**
   * WRITES ARE HIDDEN, NOT DISABLED, at a root without `allowCreate` - `writes: 'absent'` rather
   * than `'refused'`. That is the Host's half of a three-state flag the two surfaces need
   * different pairs of: Documents wants the controls present and dead in a dead team's folder,
   * and here a control that is present and dead reads as a bug.
   */
  it('hides the write controls at a read-only root and offers them at a writable one', async () => {
    await openPicker();

    rowNamed('Documents')?.click();
    await flushPromises();

    expect(buttonLabelled('New folder')).toBeUndefined();
    expect(buttonLabelled('Upload')).toBeUndefined();

    browseFileSystem.mockImplementation(
      (path: string) => Promise.resolve(aListing(path, someEntries, { allowCreate: true })),
    );
    buttonLabelled('All locations')?.click();
    await flushPromises();

    rowNamed('Work')?.click();
    await flushPromises();

    expect(buttonLabelled('New folder')?.disabled).toBe(false);
    expect(buttonLabelled('Upload')?.disabled).toBe(false);
  });

  /**
   * SELECTION IS A SEPARATE GESTURE FROM NAVIGATION. A row click navigates and emits nothing; only
   * the button confirms. One click that did both is how a person picks a folder by accident on
   * their way past it.
   */
  it('navigates on a row click and emits only from Choose this folder', async () => {
    const wrapper = await openPicker();

    rowNamed('Work')?.click();
    await flushPromises();
    expect(wrapper.emitted('chose')).toBeUndefined();

    rowNamed('alpha')?.click();
    await flushPromises();
    expect(wrapper.emitted('chose')).toBeUndefined();

    buttonLabelled('Choose this folder')?.click();
    await flushPromises();

    expect(wrapper.emitted('chose')).toEqual([['/srv/work/alpha']]);
  });

  /** There is nothing to choose at the roots level, so the one control that confirms says so. */
  it('cannot choose the roots level itself', async () => {
    const wrapper = await openPicker();

    expect(buttonLabelled('Choose this folder')?.disabled).toBe(true);

    buttonLabelled('Choose this folder')?.click();
    await flushPromises();

    expect(wrapper.emitted('chose')).toBeUndefined();
  });

  /**
   * THE WINDOW THIS BUTTON HAS ALWAYS HAD TO SHUT. The loading overlay covers the listing and not
   * the actions row, so a click landing between a navigation firing and its response arriving acts
   * on the STALE listing - Choose would emit the path of the folder just navigated AWAY from while
   * reading as having emitted the one just clicked into.
   *
   * Driven by a promise this test resolves itself rather than by a timer, so it measures the state
   * and not the machine.
   */
  it('disables Choose while a navigation is in flight', async () => {
    const wrapper = await openPicker();

    rowNamed('Work')?.click();
    await flushPromises();
    expect(buttonLabelled('Choose this folder')?.disabled).toBe(false);

    let land: (listing: DirectoryListing) => void = () => {};
    browseFileSystem.mockImplementationOnce(
      () => new Promise<DirectoryListing>((resolve) => { land = resolve; }),
    );

    rowNamed('alpha')?.click();
    await flushPromises();

    expect(buttonLabelled('Choose this folder')?.disabled).toBe(true);

    buttonLabelled('Choose this folder')?.click();
    await flushPromises();
    expect(wrapper.emitted('chose')).toBeUndefined();

    land(aListing('/srv/work/alpha', someEntries));
    await flushPromises();

    expect(buttonLabelled('Choose this folder')?.disabled).toBe(false);

    buttonLabelled('Choose this folder')?.click();
    await flushPromises();

    expect(wrapper.emitted('chose')).toEqual([['/srv/work/alpha']]);
  });

  /** A listing that silently stops at the server's cap reads as a small folder. */
  it('says out loud when a listing was capped', async () => {
    browseFileSystem.mockImplementation(
      (path: string) => Promise.resolve(aListing(path, someEntries, { total: 4096 })),
    );

    await openPicker();
    rowNamed('Documents')?.click();
    await flushPromises();

    expect(bodyText()).toContain('Showing the first 2 of 4096');
  });

  /**
   * THE WAY BACK OUT OF A ROOT, which is a swap of the source rather than a navigation the picker
   * asks for: `FileBrowser` re-lists from `start` whenever the object it was given is replaced.
   * The allowlist is re-read on the way, which is the right answer for a dialog left open a while.
   */
  it('returns to the roots level from inside a root', async () => {
    await openPicker();

    rowNamed('Work')?.click();
    await flushPromises();
    expect(bodyText()).toContain('alpha');

    buttonLabelled('All locations')?.click();
    await flushPromises();

    expect(fileSystemRoots).toHaveBeenCalledTimes(2);
    expect(bodyText()).toContain('/srv/docs');
    expect(buttonLabelled('All locations')).toBeUndefined();
  });

  /** Folder mode: a file row is rendered so a person can see the folder is not empty, and it is
   *  not a place to navigate into. */
  it('renders file rows without letting them be opened', async () => {
    await openPicker();

    rowNamed('Documents')?.click();
    await flushPromises();
    expect(browseFileSystem).toHaveBeenCalledTimes(1);

    rowNamed('notes.txt')?.click();
    await flushPromises();

    expect(browseFileSystem).toHaveBeenCalledTimes(1);
  });

  /**
   * A FAILED ROOTS READ IS SAID, not swallowed into a blank panel that reads as an empty
   * allowlist - which is a different fact with a different fix.
   */
  it('renders the reason the allowlist could not be read', async () => {
    fileSystemRoots.mockRejectedValue(new Error('403 Forbidden'));

    await openPicker();

    expect(bodyText()).toContain('403 Forbidden');
    expect(bodyText()).not.toContain('No host access configured');
  });
});
