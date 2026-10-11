// @vitest-environment happy-dom
//
// THE TREE AND THE BREADCRUMB: the whole hierarchy from the documents root, gone and retired
// folders marked; children loaded as a folder opens; opening at a team expands its path; and every
// way up - Up, Backspace, Alt+Up and the crumbs - goes toward the root. In picker mode (Move to…)
// a gone or superseded folder is shown and cannot be chosen.
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
  crumb,
  crumbLabels,
  doubleClick,
  openExplorer,
  press,
  row,
  rowNames,
  settle,
  toolbarButton,
  treeNode,
  until,
} from '../../test/documentsExplorer';
import { resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  localStorage.clear();
  server = documentsServer();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const treeLabels = () =>
  [...document.body.querySelectorAll<HTMLElement>('.documents-tree-pane .documents-tree-node')].map((node) => node.textContent?.trim());

describe('the tree', () => {
  it('lists every folder at the root, gone and retired marked', async () => {
    await openExplorer();

    expect(treeLabels()).toEqual(['folder_sharedAlpha', 'folder_sharedBeta', 'folder_offGone(gone)', 'folder_offAlpha(superseded)']);
  });

  it('loads a folder\'s children only when it opens, folders only', async () => {
    await openExplorer();
    expect(server.callsTo('list')).toHaveLength(0);

    const expander = treeNode('alpha')!.closest('.q-tree__node-header')!.querySelector('.q-tree__arrow')!;
    await click(expander);
    await until(() => treeNode('alpha', 'reports'));

    expect(server.callsTo('list').map((call) => call.url)).toEqual(['/api/teams/alpha/documents?path=&recursive=false']);
    expect(treeNode('alpha', 'notes.md')).toBeNull();
  });

  it('opening at a team expands its path and marks where the person is', async () => {
    await openExplorer('alpha');
    await doubleClick(row('reports'));
    await doubleClick(row('2026'));
    await until(() => treeNode('alpha', 'reports/2026'));

    expect(treeNode('alpha', 'reports/2026')!.closest('.q-tree__node-header')!.classList).toContain('q-tree__node--selected');
  });

  it('a click on a node opens that folder', async () => {
    await openExplorer();
    await click(treeNode('beta')!);

    expect(crumbLabels()).toEqual(['Documents', 'Beta']);
    expect(server.callsTo('list').at(-1)!.url).toBe('/api/teams/beta/documents?path=&recursive=false');
  });

  it('the tree pane collapses from the toolbar, and stays collapsed when reopened', async () => {
    const wrapper = await openExplorer();
    await click(toolbarButton('tree')!);
    expect(document.body.querySelector('.documents-tree-pane')).toBeNull();

    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();

    expect(document.body.querySelector('.documents-tree-pane')).toBeNull();
    expect(JSON.parse(localStorage.getItem('harness.documents.tree')!)).toEqual({ open: false, width: 240 });
  });
});

describe('the breadcrumb, and going up to the root', () => {
  async function deep() {
    await openExplorer('alpha');
    await doubleClick(row('reports'));
    await doubleClick(row('2026'));
    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports', '2026']);
  }

  it('marks a gone folder on its crumb', async () => {
    await openExplorer('gone');

    expect(document.body.querySelector('.documents-titlebar')!.textContent).toContain('(gone)');
  });

  it('Up goes one level at a time to the root', async () => {
    await deep();
    await click(buttonLabelled('Up')!);
    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports']);
    await click(buttonLabelled('Up')!);
    await click(buttonLabelled('Up')!);

    expect(crumbLabels()).toEqual(['Documents']);
    expect(rowNames()).toContain('Beta');
  });

  it('Backspace and Alt+Up go up too', async () => {
    await deep();
    await press('Backspace');
    expect(crumbLabels()).toEqual(['Documents', 'Alpha', 'reports']);
    await press('ArrowUp', { altKey: true });
    expect(crumbLabels()).toEqual(['Documents', 'Alpha']);
    await press('Backspace');

    expect(crumbLabels()).toEqual(['Documents']);
  });

  it('a crumb goes straight to its level, the Documents crumb to the root', async () => {
    await deep();
    await click(crumb('Alpha'));
    expect(crumbLabels()).toEqual(['Documents', 'Alpha']);

    await click(crumb('Documents'));
    expect(crumbLabels()).toEqual(['Documents']);
  });
});

describe('the picker', () => {
  it('shows gone and superseded folders and does not let Move to choose them', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));
    await click(toolbarButton('moveTo')!);
    const picker = await until(() => document.body.querySelector<HTMLElement>('.documents-move'));

    const gone = picker.querySelector<HTMLElement>('[data-node="gone::"]')!;
    expect(gone.getAttribute('aria-disabled')).toBe('true');
    await click(gone);

    expect(picker.querySelector<HTMLButtonElement>('[data-move="confirm"]')!.disabled).toBe(false);
    expect(picker.querySelector('.q-tree__node--selected [data-node="gone::"]')).toBeNull();
  });
});
