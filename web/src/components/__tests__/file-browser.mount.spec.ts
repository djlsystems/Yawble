// @vitest-environment happy-dom
//
// THE SHARED FILE BROWSER.
//
// ONE COMPONENT OVER TWO DATA SOURCES: the look and the navigation are shared, the BOUNDARIES are
// not. This
// file is about the component alone, with sources made up here - the Documents boundary is pinned
// where it lives, in `lib/__tests__/documents-api.spec.ts` against no mock at all, and the Host
// picker's in its own suite.
//
// THE CASE THIS FILE EXISTS FOR IS THE FIRST ONE: rendered the way the Host picker will render it -
// WITHOUT a `deletion` prop - there must be no delete control at all. Absent because it was never
// passed, not present and disabled. `/api/fs` has four routes and no delete among them, and adding
// a fifth is deliberately not done, so a browser that offered the control there would be
// offering something that cannot exist.
//
// Seen to fail: dropping the `v-if="deletion"` and rendering the delete button unconditionally
// reddens that case and only that case. Rendering it always-but-disabled reddens it too, which is
// exactly why it asserts ABSENCE rather than `disabled`.
//
// WHY MOUNTED RATHER THAN A `lib/` FUNCTION: "the control is absent", "the crumb is there", "the
// write buttons are disabled rather than gone" are all TEMPLATE conditions, and a grep finds the
// symbol either way.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type DOMWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import FileBrowser from '../FileBrowser.vue';
import type {
  FileBrowserDeletion,
  FileBrowserListing,
  FileBrowserSource,
  FileBrowserWrites,
} from '../../lib/fileBrowser';
import { resetBody } from '../../test/mountQuasar';

/**
 * A source over a tiny fixed tree, in the shape the Host picker's will be: whole paths rather than
 * relative ones, no download links, file rows disabled, and NO delete anywhere - because there is
 * no route that would answer one.
 *
 * THE PATHS ARE THE SOURCE'S OWN STRINGS AND NOTHING SPLITS THEM. That is the contract: the crumbs
 * and the child paths below were made HERE, and the component only ever hands them back.
 */
function aSource(writes: FileBrowserWrites = 'allowed'): FileBrowserSource {
  const listing = (path: string): FileBrowserListing => ({
    path,
    writes,
    crumbs: path === '/mnt'
      ? [{ label: '/mnt', path: '/mnt' }]
      : [{ label: '/mnt', path: '/mnt' }, { label: 'work', path: '/mnt/work' }],
    up: path === '/mnt' ? null : '/mnt',
    entries: [
      { path: '/mnt/work', name: 'work', isFolder: true },
      { path: '/mnt/notes.txt', name: 'notes.txt', isFolder: false, disabled: true },
    ],
  });

  return {
    start: '/mnt',
    list: vi.fn((path: string) => Promise.resolve(listing(path))),
    createFolder: vi.fn(() => Promise.resolve()),
    upload: vi.fn(() => Promise.resolve()),
  };
}

const aDeletion = (): FileBrowserDeletion => ({
  label: (entry) => (entry.isFolder ? 'Remove folder' : 'Delete document'),
  remove: vi.fn(() => Promise.resolve()),
});

async function render(props: Record<string, unknown>) {
  const wrapper = mount(FileBrowser, { props: { source: null, ...props } });
  await flushPromises();

  return wrapper;
}

/** Quasar renders its own markup around a label, so a button is found by the words on it. */
function buttonsSaying(wrapper: { findAll: (selector: string) => DOMWrapper<Element>[] }, words: string) {
  return wrapper.findAll('button').filter((button) => button.text().includes(words));
}

afterEach(resetBody);

describe('FileBrowser', () => {
  /**
   * THE ACCEPTANCE. This is how the Host picker mounts it, and delete must be absent BECAUSE the
   * capability was never passed.
   */
  it('offers no delete when the capability is not passed', async () => {
    const wrapper = await render({ source: aSource() });

    expect(wrapper.text()).toContain('notes.txt');
    expect(wrapper.find('[aria-label="Remove folder"]').exists()).toBe(false);
    expect(wrapper.find('[aria-label="Delete document"]').exists()).toBe(false);
    expect(wrapper.findAll('[aria-label^="Delete"]')).toHaveLength(0);
  });

  /** And the other half, or the case above would pass against a component with no delete at all. */
  it('offers delete, named per row, when the capability is passed', async () => {
    const wrapper = await render({ source: aSource(), deletion: aDeletion() });

    expect(wrapper.find('[aria-label="Remove folder"]').exists()).toBe(true);
    expect(wrapper.find('[aria-label="Delete document"]').exists()).toBe(true);
  });

  /**
   * `writes` IS THREE STATES AND NOT TWO, because the two surfaces need different pairs and both
   * are right: Documents DISABLES its write buttons in a dead team's folder, so a person can see
   * the feature is withheld rather than gone; the Host HIDES them at a read-only root, where a
   * control that is present and dead reads as a bug.
   */
  it('disables the write controls when refused, and removes them when absent', async () => {
    const refused = await render({ source: aSource('refused') });

    expect(buttonsSaying(refused, 'New folder')[0]?.attributes('disabled')).toBeDefined();
    expect(buttonsSaying(refused, 'Upload')[0]?.attributes('disabled')).toBeDefined();

    const absent = await render({ source: aSource('absent') });

    expect(buttonsSaying(absent, 'New folder')).toHaveLength(0);
    expect(buttonsSaying(absent, 'Upload')).toHaveLength(0);
  });

  /**
   * NOTHING HERE TAKES A PATH APART - the source hands back crumbs already made, because a renderer
   * that split them would be a THIRD thing resolving paths, and that is the boundary the whole
   * decision refuses to move.
   */
  it('renders the trail the source made, and navigates with the source own strings', async () => {
    const source = aSource();
    const wrapper = await render({ source });

    expect(source.list).toHaveBeenCalledWith('/mnt');
    expect(buttonsSaying(wrapper, '/mnt')).toHaveLength(1);

    await wrapper.findAll('.q-item__label').filter((label) => label.text() === 'work')[0]!.trigger('click');
    await flushPromises();

    expect(source.list).toHaveBeenCalledWith('/mnt/work');
    expect(buttonsSaying(wrapper, 'work')).toHaveLength(1);
  });

  /** A row the source marked disabled must not navigate - the Host picker's folder mode. */
  it('does not navigate into a row the source disabled', async () => {
    const source = aSource();
    const wrapper = await render({ source });

    await wrapper.findAll('.q-item__label').filter((label) => label.text() === 'notes.txt')[0]!.trigger('click');
    await flushPromises();

    expect(source.list).toHaveBeenCalledTimes(1);
  });

  /**
   * A NULL SOURCE IS A REAL STATE, not a loading one - an instance where nobody has written a
   * document has no folder to browse. Nothing is fetched, and the caller says so in its own words
   * instead of keeping a second list renderer.
   */
  it('fetches nothing and renders the empty slot when there is no source', async () => {
    const wrapper = mount(FileBrowser, {
      props: { source: null },
      slots: { empty: 'Nothing has been written yet.' },
    });
    await flushPromises();

    expect(wrapper.text()).toContain('Nothing has been written yet.');
  });

  /**
   * REPLACING THE SOURCE IS HOW A CALLER CHANGES ROOT. The Documents dialog changes folder this
   * way, and the browser must start over rather than keep a path that means nothing in the new one.
   */
  it('re-lists from the start when the source is replaced', async () => {
    const first = aSource();
    const wrapper = await render({ source: first });

    await wrapper.findAll('.q-item__label').filter((label) => label.text() === 'work')[0]!.trigger('click');
    await flushPromises();

    const second = aSource();
    await wrapper.setProps({ source: second });
    await flushPromises();

    expect(second.list).toHaveBeenCalledWith('/mnt');
    expect(second.list).toHaveBeenCalledTimes(1);
  });
});
