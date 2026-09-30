// @vitest-environment happy-dom
//
// WHERE A DISPATCH STARTED WAS NOT RECORDED (B0028).
//
// Only a dispatch with a recorded start has `landed` kept after its team is gone, so an item whose
// start was not recorded says so in its detail, in the server's sentence, and offers a person
// Record where it started now while the team has not committed. An item whose start was recorded
// shows nothing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { backlogItems, backlogItem, listCatalog, fileSystemRoots, recordBacklogItemStart } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  recordBacklogItemStart: vi.fn(),
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
  recordBacklogItemStart,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { mountDialog, resetBody } from '../../test/mountQuasar';

const sentence =
  'Where this dispatch started was not recorded (the fetch from origin failed); its landed state is read live '
  + 'and is not kept after its team is gone.';

const base = {
  id: 7,
  team: null,
  teamName: null,
  teamGone: false,
  title: 'Started offline',
  body: 'A spec.',
  state: 'ready',
  archivedAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-02T00:00:00Z',
  createdBy: 'someone@example.com',
  inFlight: null,
};

async function openItem(start: Record<string, unknown>) {
  setActivePinia(createPinia());
  useConsoleStore().teams = [] as never;
  useSessionStore().user = { email: 'admin@example.com' } as never;

  const item = { ...base, ...start };
  backlogItems.mockResolvedValue([item]);
  backlogItem.mockResolvedValue({ item, dispatches: [], stats: [], implementedBy: null });

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  [...document.querySelectorAll('tr')]
    .find((row) => row.textContent?.includes('Started offline'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

const notice = () => document.querySelector('.backlog-start-unrecorded');
const button = () => document.querySelector<HTMLButtonElement>('.backlog-record-start');

beforeEach(() => {
  backlogItems.mockReset();
  backlogItem.mockReset();
  recordBacklogItemStart.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(resetBody);

describe('BacklogDialog unrecorded start', () => {
  it('shows the sentence and Record where it started now, which records it', async () => {
    await openItem({ startRecorded: false, startDetail: sentence, startRecordable: true });

    expect(notice()!.textContent!.replace(/\s+/g, ' ')).toContain(sentence);
    expect(button()!.textContent).toContain('Record where it started now');

    recordBacklogItemStart.mockResolvedValue(new Response(null, { status: 200 }));
    backlogItem.mockResolvedValue({
      item: { ...base, startRecorded: true, startDetail: null, startRecordable: false },
      dispatches: [], stats: [], implementedBy: null,
    });
    button()!.click();
    await flushPromises();

    expect(recordBacklogItemStart).toHaveBeenCalledWith(7);
    expect(notice()).toBeNull();
  });

  it('shows the sentence without the button once the team has committed', async () => {
    await openItem({
      startRecorded: false,
      startDetail: sentence.replace('the fetch from origin failed', 'team/t had commits of its own'),
      startRecordable: false,
    });

    expect(notice()!.textContent).toContain('had commits of its own');
    expect(button()).toBeNull();
  });

  it('shows nothing for a recorded start', async () => {
    await openItem({ startRecorded: true, startDetail: null, startRecordable: false });

    expect(notice()).toBeNull();
  });
});
