// @vitest-environment happy-dom
//
// WHO MARKED AN ITEM IMPLEMENTED, IN ITS HISTORY.
//
// A person who merged the work outside the platform can mark an item implemented on their own word
// when landed cannot be proven, so the item's detail names who confirmed it - and says when the
// Concierge wrote it for them. An item nobody marked shows no such line.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { backlogItems, backlogItem, listCatalog, fileSystemRoots } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  backlogItems,
  backlogItem,
  listCatalog,
  fileSystemRoots,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { mountDialog, resetBody } from '../../test/mountQuasar';

const item = {
  id: 4,
  team: null,
  teamName: null,
  teamGone: false,
  title: 'Merged by hand',
  body: 'A spec.',
  state: 'implemented',
  archivedAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-02T00:00:00Z',
  createdBy: 'someone@example.com',
  inFlight: null,
};

async function openItem(implementedBy: unknown) {
  setActivePinia(createPinia());
  useConsoleStore().teams = [] as never;
  useSessionStore().user = { email: 'admin@example.com' } as never;

  backlogItems.mockResolvedValue([item]);
  backlogItem.mockResolvedValue({ item, dispatches: [], stats: [], implementedBy });

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  [...document.querySelectorAll('tr')]
    .find((row) => row.textContent?.includes('Merged by hand'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

const line = () => document.querySelector('.backlog-implemented-by');

beforeEach(() => {
  backlogItems.mockReset();
  backlogItem.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(resetBody);

describe('BacklogDialog implemented by', () => {
  it('names who confirmed the item, and that the Concierge wrote it for them', async () => {
    await openItem({ by: 'person@example.test', at: '2026-09-30T10:00:00Z', viaConcierge: true });

    const text = line()!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('Marked implemented by person@example.test');
    expect(text).toContain('(through the Concierge)');
    expect(text).toContain('2026-09-30');
  });

  it('shows no line for an item nobody is recorded as marking', async () => {
    await openItem(null);

    expect(line()).toBeNull();
  });
});
