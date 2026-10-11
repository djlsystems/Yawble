// @vitest-environment happy-dom
//
// THE RIBBON'S TEAM CHOOSER AND BACKLOG DISPATCH LEAVE AN ARCHIVED TEAM OUT, MOUNTED. An archived
// team does no work, so neither offers it; the console store still holds it for the Teams list.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const api = vi.hoisted(() => ({
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
  getInstanceId: async () => 'instance-a',
  backlogItems: api.backlogItems,
  backlogItem: api.backlogItem,
  listCatalog: api.listCatalog,
  fileSystemRoots: api.fileSystemRoots,
}));

import BacklogDialog from '../BacklogDialog.vue';
import RibbonTeamList from '../RibbonTeamList.vue';
import { useConsoleStore } from '../../stores/console';
import { mountDialog, resetBody } from '../../test/mountQuasar';

const teams = [
  { id: 'alpha', name: 'Alpha', containers: [] },
  { id: 'beta', name: 'Beta', containers: [], archived: true },
];

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  api.backlogItems.mockResolvedValue([{
    id: 1,
    team: null,
    teamName: null,
    teamGone: false,
    title: 'first thing',
    body: '',
    state: 'ready',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
  }]);
  api.backlogItem.mockResolvedValue({ item: null, dispatches: [], stats: [] });
  api.listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  api.fileSystemRoots.mockResolvedValue({ roots: [] });
});

afterEach(() => {
  resetBody();
  vi.unstubAllGlobals();
});

describe('the ribbon team chooser', () => {
  it('lists active teams only', async () => {
    setActivePinia(createPinia());
    useConsoleStore().$patch({ teams: teams as never });

    const wrapper = mount(RibbonTeamList);
    await flushPromises();

    expect(wrapper.findAll('.q-item').map((row) => row.text().trim())).toEqual(['groupsAlpha']);
  });
});

describe('backlog dispatch', () => {
  it('offers active teams only as dispatch targets', async () => {
    setActivePinia(createPinia());
    useConsoleStore().$patch({ teams: teams as never });

    const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    const row = [...document.querySelectorAll('tr')].find((entry) => entry.textContent?.includes('first thing'))!;
    ([...row.querySelectorAll('button')].find((entry) => entry.innerHTML.includes('send')) as HTMLElement).click();
    await flushPromises();

    const offered = wrapper.findAllComponents({ name: 'QSelect' })
      .filter((select) => select.props('label') === 'Team')
      .flatMap((select) => (select.props('options') as { value: string | null }[]).map((option) => option.value));
    expect(offered).toContain('alpha');
    expect(offered).not.toContain('beta');
  });
});
