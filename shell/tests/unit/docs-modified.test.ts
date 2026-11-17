// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import DocumentsBrowser from '../../src/renderer/src/components/DocumentsBrowser.vue';
import type { YawbleApi, TeamSummary } from '../../src/shared/api';

// The documents list shows when each entry was last modified, after its size; the full time is the tooltip. Fakes only.
const team = { id: 1, name: 'payments' } as TeamSummary;
const thisYear = new Date().getFullYear();
const recent = new Date(thisYear, 0, 5, 14, 7).toISOString();
const old = new Date(2020, 5, 9, 9, 30).toISOString();
const api = {
  call: async (m: string) => {
    if (m !== 'docs.list') throw new Error(`unexpected ${m}`);
    return { path: '', entries: [
      { name: 'plans', path: 'plans', folder: true, size: null, modified: recent },
      { name: 'old.md', path: 'old.md', folder: false, size: 2048, modified: old },
      { name: 'none.md', path: 'none.md', folder: false, size: 1, modified: '' },
    ] };
  },
  onEvent: () => () => {},
} as unknown as YawbleApi;

describe('the documents list: a Modified column', () => {
  it('shows each entry\'s modified date and time after its size, the year only when not this year', async () => {
    const w = mount(DocumentsBrowser, { props: { team, api } });
    await flushPromises();
    const cells = w.findAll('[data-test=doc-modified]');
    expect(cells).toHaveLength(3);
    const fmt = (iso: string, year: boolean) => new Date(iso).toLocaleString(undefined,
      { month: 'short', day: 'numeric', ...(year ? { year: 'numeric' } : {}), hour: 'numeric', minute: '2-digit' });
    expect(cells[0].text()).toBe(fmt(recent, false));
    expect(cells[1].text()).toBe(fmt(old, true));
    expect(cells[1].text()).toContain('2020');
    expect(cells[1].attributes('title')).toBe(`Modified ${new Date(old).toLocaleString()}`);
    // No time known: an empty cell, no tooltip.
    expect(cells[2].text()).toBe('');
    expect(cells[2].attributes('title') ?? '').toBe('');
    // After the size, before the actions.
    const row = cells[1].element.parentElement!;
    const order = [...row.children].map((c) => c.classList.contains('size') ? 'size' : c.classList.contains('modified') ? 'modified' : c.classList.contains('acts') ? 'acts' : 'name');
    expect(order).toEqual(['name', 'size', 'modified', 'acts']);
    w.unmount();
  });
});
