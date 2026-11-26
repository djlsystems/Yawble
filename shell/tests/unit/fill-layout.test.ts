// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import DocumentsView from '../../src/renderer/src/components/DocumentsView.vue';
import type { YawbleApi, TeamSummary } from '../../src/shared/api';

// The Documents and Backlog pages use the whole page height (as Settings > Skills does). happy-dom has no layout and
// applies no SFC styles, so this checks the structure (classes, wrappers). Fakes only.

const team = { id: 1, name: 'payments' } as TeamSummary;
const backlogItem = (over: Record<string, unknown> = {}) => ({
  id: 1, label: 'B001', team: 'payments', title: 'Item 1', body: '# Spec', state: 'pending', position: 1, createdAt: '', updatedAt: '',
  archivedAt: null, dispatchedTeam: null, dispatchedDeliverable: null, dispatchedAt: null, inFlight: false, dispatchCount: 0, ...over,
});
const fake = (answers: Record<string, unknown>) => ({
  call: async (m: string) => { if (!(m in answers)) throw new Error(`unexpected ${m}`); return answers[m]; },
  onEvent: () => () => {},
}) as unknown as YawbleApi;

describe('Backlog fills the page', () => {
  const mountBacklog = async (item: Record<string, unknown> = {}) => {
    const api = fake({ 'backlog.list': [backlogItem(item)], 'backlog.item': { id: 1, dispatches: [] } });
    const w = mount(BacklogView, { props: { teams: [team], api, pollMs: 600_000 } });
    await flushPromises();
    return w;
  };

  it('has the fill page and wrapper, the split, and a table in its own scroller', async () => {
    const w = await mountBacklog();
    expect(w.get('[data-test=backlog-view]').classes()).toContain('fill');
    expect(w.get('.wrap').classes()).toContain('fill');
    expect(w.get('[data-test=backlog-split]').element.parentElement!.classList.contains('wrap')).toBe(true);
    expect(w.get('[data-test=backlog-listwrap]').element.querySelector('table.list')).not.toBeNull();
    w.unmount();
  });

  it('the item body (Read, then Edit) is the flexible child of the detail pane, for live and archived items', async () => {
    for (const over of [{}, { archivedAt: '2026-11-01T00:00:00Z' }]) {
      const w = await mountBacklog(over);
      await w.get('[data-test=backlog-row]').trigger('click');
      await flushPromises();
      const pane = w.get('[data-test=backlog-item]');
      expect(pane.get('[data-test=backlog-item-read]').classes()).toContain('ibody');
      expect(pane.findAll('.ibody')).toHaveLength(1);
      expect(pane.get('.ibody').element.parentElement).toBe(pane.element);
      if (!('archivedAt' in over)) {
        await pane.get('[data-test=backlog-item-edit]').trigger('click');
        expect(pane.get('[data-test=backlog-item-body]').classes()).toContain('ibody');
        expect(pane.findAll('.ibody')).toHaveLength(1);
      }
      w.unmount();
    }
  });
});

describe('Documents fills the page', () => {
  it('an open team renders the browser as the page filling column; the folder grid does not', async () => {
    const api = fake({ 'docs.list': { path: '', entries: [] } });
    const w = mount(DocumentsView, { props: { teams: [team], archived: [], api } });
    expect(w.get('[data-test=documents-view]').classes()).not.toContain('fill');
    await w.get('[data-test=documents-team-payments]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=documents-view]').classes()).toContain('fill');
    expect(w.get('.wrap').classes()).toContain('fill');
    expect(w.get('[data-test=docs-browser]').classes()).toContain('fill');
    w.unmount();
  });

  it('the preview content is the flexible child below its header row, in view and in edit mode', async () => {
    const entry = { name: 'a.md', path: 'a.md', folder: false, size: 5, modified: '' };
    const api = fake({
      'docs.list': { path: '', entries: [entry] },
      'docs.read': { path: 'a.md', name: 'a.md', size: 5, modified: '', kind: 'markdown', text: '# Hi' },
    });
    const w = mount(DocumentsView, { props: { teams: [team], archived: [], api } });
    await w.get('[data-test=documents-team-payments]').trigger('click');
    await flushPromises();
    await w.get('[data-test="doc-a.md"]').trigger('click');
    await flushPromises();
    const viewer = w.get('[data-test=docs-viewer]');
    expect(viewer.find('header.vh').exists()).toBe(true);
    expect(viewer.get('.md').classes()).toContain('vbody');
    await viewer.get('[data-test=doc-edit]').trigger('click');
    expect(viewer.get('[data-test=doc-editor]').classes()).toContain('vbody');
    // Save and Cancel live in the header row, outside the scrolling content.
    expect(viewer.get('header.vh').find('[data-test=doc-save]').exists()).toBe(true);
    w.unmount();
  });
});
