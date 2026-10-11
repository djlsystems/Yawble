// @vitest-environment happy-dom
//
// THE BACKLOG ITEM SHOWS THE PACKAGE NOTICE of each dispatch's workflow: ready with the link to
// review and install, or the problems, under the dispatch that wrote it.
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

const folder = '/data/documents/builders/job-tracker-1.0.0';

const item = {
  id: 3,
  team: null,
  teamName: null,
  teamGone: false,
  title: 'Job tracker',
  body: 'Build it as a package.',
  state: 'declared',
  archivedAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-02T00:00:00Z',
  createdBy: 'someone@example.com',
  inFlight: null,
};

const stats = (notices: unknown[]) => ({
  correlation: 40,
  members: ['Manager'],
  instructions: 2,
  outcome: 'completed',
  tokens: 1000,
  runsWithUsage: 2,
  runsWithoutUsage: 0,
  elapsedSeconds: 60,
  notices,
});

async function openItem(notices: unknown[]) {
  setActivePinia(createPinia());
  useConsoleStore().teams = [{ id: 'builders', name: 'Builders' }] as never;
  useSessionStore().user = { email: 'admin@example.com' } as never;

  backlogItems.mockResolvedValue([item]);
  backlogItem.mockResolvedValue({
    item,
    dispatches: [{
      id: 1, teamId: 'builders', teamName: 'Builders', correlation: 40,
      dispatchedAt: '2026-09-29T09:00:00Z', dispatchedBy: 'admin@example.com', frozenAt: null, teamGone: false,
    }],
    stats: [stats(notices)],
  });

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  [...document.querySelectorAll('tr')]
    .find((row) => row.textContent?.includes('Job tracker'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

const notice = () => document.querySelector('.backlog-notice');

beforeEach(() => {
  backlogItems.mockReset();
  backlogItem.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(resetBody);

describe('BacklogDialog solution notice', () => {
  it('shows a ready package under its dispatch with a link to review and install', async () => {
    await openItem([{
      ok: true, text: 'Job Tracker 1.0.0 is ready. **Review and install**', folder,
      name: 'Job Tracker', version: '1.0.0', link: '#/solutions/install?folder=x', problems: [],
    }]);

    expect(notice()?.textContent).toContain('Job Tracker 1.0.0 is ready.');
    const link = notice()!.querySelector('a')!;
    expect(link.textContent).toBe('Review and install');
    expect(link.getAttribute('href')).toBe(`#/solutions/install?folder=${encodeURIComponent(folder)}`);
  });

  it('shows the problems of a failing package, one line each, and no link', async () => {
    await openItem([{
      ok: false, text: 'Job Tracker 1.0.0 did not pass the check:\nsolution.json triggers[0].member: names no member; its members are Coordinator, Scout, Writer.\nsolution.json members[1].pluginId: is not a plugin in this package.\nskills/job-search-playbook.md roles: must say who it is for.', folder,
      name: 'Job Tracker', version: null, link: null,
      problems: [
        'solution.json triggers[0].member: names no member; its members are Coordinator, Scout, Writer.',
        'solution.json members[1].pluginId: is not a plugin in this package',
        'skills/job-search-playbook.md roles: must say who it is for.',
      ],
    }]);

    const text = notice()!.textContent!;
    expect(text).toContain('did not pass the check:');
    expect(text).not.toContain('.;');
    expect(text.trim().split('\n').slice(1)).toEqual([
      'solution.json triggers[0].member: names no member; its members are Coordinator, Scout, Writer.',
      'solution.json members[1].pluginId: is not a plugin in this package.',
      'skills/job-search-playbook.md roles: must say who it is for.',
    ]);
    expect((notice() as HTMLElement).style.whiteSpace).toBe('pre-line');
    expect(notice()!.querySelector('a')).toBeNull();
  });

  it('shows nothing for a workflow that wrote no package', async () => {
    await openItem([]);
    expect(notice()).toBeNull();
  });
});
