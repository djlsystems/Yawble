// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import DocumentsBrowser from '../../src/renderer/src/components/DocumentsBrowser.vue';
import ConfirmDialog from '../../src/renderer/src/components/ConfirmDialog.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { backlogRequest } from '../../src/renderer/src/workflows/page';
import type { BacklogItem, DocFile, YawbleApi, TeamSummary } from '../../src/shared/api';

// The Documents preview's "Move to Backlog" button: shown for a text document the editor opens (not while editing),
// disabled for a Visitor; the app's own confirm; docs.moveToBacklog with the team and the path; then the preview
// closes, the list reloads and a note links to the new item in the Backlog. Fakes only.
const team = { id: 7, name: 'payments' } as TeamSummary;

const files: Record<string, DocFile> = {
  'plan.md': { name: 'plan.md', path: 'plan.md', kind: 'markdown', size: 20, text: '# A plan\n\nSome text.' } as DocFile,
  'notes.txt': { name: 'notes.txt', path: 'notes.txt', kind: 'text', size: 5, text: 'notes' } as DocFile,
  'pic.png': { name: 'pic.png', path: 'pic.png', kind: 'image', size: 9, dataUrl: 'data:image/png;base64,AA==' } as DocFile,
};
// What backlog.add answers (BoardService.ItemJson), with made-up values.
const item: BacklogItem = {
  id: 41, label: 'B007', team: 'payments', title: 'A plan', body: 'Some text.', state: 'pending', archivedAt: null,
  dispatchedTeam: null, dispatchedWorkflow: null, dispatchedAt: null, inFlight: false,
  createdAt: '2026-01-02T03:04:05Z', updatedAt: '2026-01-02T03:04:05Z', dispatchCount: 0,
};

function fake(o: { fail?: string } = {}) {
  const calls: { method: string; params: Record<string, unknown> }[] = [];
  let entries = Object.values(files).map((f) => ({ name: f.name, path: f.path, folder: false, size: f.size, modified: '' }));
  const api = {
    call: async (method: string, params: Record<string, unknown>) => {
      calls.push({ method, params });
      if (method === 'docs.list') return { path: '', entries };
      if (method === 'docs.read') return files[params.path as string];
      if (method === 'docs.moveToBacklog') {
        if (o.fail) throw new Error(o.fail);
        entries = entries.filter((e) => e.path !== params.path);
        return item;
      }
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls, lists: () => calls.filter((c) => c.method === 'docs.list').length };
}

let wrappers: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
beforeEach(() => { applyRemoteAccess('full'); backlogRequest.value = null; });
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; applyRemoteAccess('full'); document.body.innerHTML = ''; });

async function opened(path: string, f = fake()) {
  const w = keep(mount(DocumentsBrowser, { props: { team, api: f.api }, attachTo: document.body }));
  const dialog = keep(mount(ConfirmDialog, { attachTo: document.body }));
  await flushPromises();
  await w.get(`[data-test="doc-${path}"]`).trigger('click');
  await flushPromises();
  return { w, dialog, f };
}
const button = (w: VueWrapper) => w.find('[data-test=doc-move-to-backlog]');

describe('Documents: Move to Backlog', () => {
  it('shows for a Markdown or text document, not for an image, and not while editing', async () => {
    const md = await opened('plan.md');
    expect(button(md.w).exists()).toBe(true);
    expect(button(md.w).text()).toBe('Move to Backlog');
    expect((button(md.w).element as HTMLButtonElement).disabled).toBe(false);
    await md.w.get('[data-test=doc-edit]').trigger('click');
    expect(md.w.find('[data-test=doc-editor]').exists()).toBe(true);
    expect(button(md.w).exists()).toBe(false);

    const txt = await opened('notes.txt');
    expect(button(txt.w).exists()).toBe(true);

    const img = await opened('pic.png');
    expect(button(img.w).exists()).toBe(false);
  });

  it('is disabled for a Visitor (read-only view)', async () => {
    const { w, f } = await opened('plan.md');
    applyRemoteAccess('read');
    await flushPromises();
    const b = button(w).element as HTMLButtonElement;
    expect(b.disabled).toBe(true);
    expect(b.dataset.readOnly).toBe('1');
    await button(w).trigger('click');
    await flushPromises();
    expect(document.querySelector('[data-test=confirm-dialog]')).toBeNull();
    expect(f.calls.some((c) => c.method === 'docs.moveToBacklog')).toBe(false);
  });

  it('asks with the app\'s confirm dialog; Cancel moves nothing', async () => {
    const { w, f } = await opened('plan.md');
    await button(w).trigger('click');
    await flushPromises();
    expect(document.querySelector('[data-test=confirm-message]')!.textContent)
      .toBe("Move plan.md to the backlog? It becomes a backlog item for payments and is removed from the team's documents.");
    expect(document.querySelector('[data-test=confirm-ok]')!.textContent).toBe('Move to Backlog');
    expect(document.querySelector('[data-test=confirm-cancel]')!.textContent).toBe('Cancel');
    (document.querySelector('[data-test=confirm-cancel]') as HTMLButtonElement).click();
    await flushPromises();
    expect(f.calls.some((c) => c.method === 'docs.moveToBacklog')).toBe(false);
    expect(w.find('.vname').text()).toBe('plan.md');
  });

  it('on OK calls docs.moveToBacklog with the team and path, closes the preview, reloads the list and links the item', async () => {
    const { w, f } = await opened('plan.md');
    const listsBefore = f.lists();
    await button(w).trigger('click');
    await flushPromises();
    (document.querySelector('[data-test=confirm-ok]') as HTMLButtonElement).click();
    await flushPromises();

    const moves = f.calls.filter((c) => c.method === 'docs.moveToBacklog');
    expect(moves).toEqual([{ method: 'docs.moveToBacklog', params: { teamId: 7, path: 'plan.md' } }]);
    expect(w.find('.vname').exists()).toBe(false);
    expect(w.find('.pick').exists()).toBe(true);
    expect(f.lists()).toBe(listsBefore + 1);
    expect(w.find('[data-test="doc-plan.md"]').exists()).toBe(false);
    expect(w.find('[data-test="doc-notes.txt"]').exists()).toBe(true);

    const note = w.get('[data-test=docs-moved]');
    expect(note.text().replace(/\s+/g, ' ')).toBe('Moved to the backlog as B007');
    await note.get('[data-test=docs-moved-link]').trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B007', archived: false });
    expect(w.find('[data-test=docs-error]').exists()).toBe(false);
  });

  it('shows a refusal in the error line and keeps the document open and listed', async () => {
    const { w, f } = await opened('notes.txt', fake({ fail: 'notes.txt is not a text document the Backlog can take' }));
    await button(w).trigger('click');
    await flushPromises();
    (document.querySelector('[data-test=confirm-ok]') as HTMLButtonElement).click();
    await flushPromises();
    expect(w.get('[data-test=docs-error]').text()).toContain('notes.txt is not a text document the Backlog can take');
    expect(w.find('.vname').text()).toBe('notes.txt');
    expect(w.find('[data-test="doc-notes.txt"]').exists()).toBe(true);
    expect(w.find('[data-test=docs-moved]').exists()).toBe(false);
    expect(f.calls.filter((c) => c.method === 'docs.moveToBacklog')).toHaveLength(1);
    expect(backlogRequest.value).toBeNull();
  });
});
