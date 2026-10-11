// @vitest-environment happy-dom
//
// A WORKFLOW THE WORK LEFT BEHIND, ON THE BACKLOG ROW.
//
// The item's dispatch workflow is still open and Blocked, and a later workflow on the same team
// finished its card. The server says so on the row as `stranded`. These cases pin what a person
// reads - "The work continued in workflow N." on that row and no other - and that the close is
// theirs: nothing is called until the button is pressed, and pressing it calls the existing
// person-only close route for the ORIGINAL workflow with the reason the server suggested.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { backlogItems, backlogItem, listCatalog, fileSystemRoots, closeWorkflow } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  closeWorkflow: vi.fn(),
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
  closeWorkflow,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { mountDialog, resetBody } from '../../test/mountQuasar';

function item(over: Record<string, unknown> & { id: number }) {
  return {
    team: null,
    teamName: null,
    teamGone: false,
    title: `item ${over.id}`,
    body: '',
    state: 'ready',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
    ...over,
  };
}

const stranded = {
  teamId: 'beta',
  workflow: 2229,
  state: 'Blocked',
  continuedIn: 2302,
  cards: ['2229_Tester'],
  notice: 'The work continued in workflow 2302.',
  close: {
    route: '/api/teams/beta/workflows/2229/close',
    by: 'person',
    reason: 'Blocked; the work continued in workflow 2302.',
  },
};

beforeEach(() => {
  backlogItems.mockReset();
  closeWorkflow.mockReset();
  closeWorkflow.mockResolvedValue(undefined);
  backlogItem.mockResolvedValue({ item: item({ id: 1 }), dispatches: [], stats: [] });
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(resetBody);

async function openWithOneStranded() {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.teams = [{ id: 'beta', name: 'Beta' }] as never;

  const session = useSessionStore();
  session.user = { email: 'admin@example.com' } as never;

  backlogItems.mockResolvedValue([
    item({ id: 1, title: 'first thing' }),
    item({
      id: 2,
      title: 'job tracker',
      inFlight: { teamId: 'beta', teamName: 'Beta', correlation: 2229, running: false },
      stranded,
    }),
  ]);

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();
}

function notices(): HTMLElement[] {
  return [...document.body.querySelectorAll('.backlog-stranded')] as HTMLElement[];
}

describe('BacklogDialog stranded workflow', () => {
  it('says where the work continued, on the row of the item it describes', async () => {
    await openWithOneStranded();

    const shown = notices();
    expect(shown).toHaveLength(1);
    expect(shown[0]!.textContent).toContain('The work continued in workflow 2302.');
    expect(shown[0]!.getAttribute('title')).toContain('Nothing has been closed');

    const row = shown[0]!.closest('tr');
    expect(row?.textContent).toContain('job tracker');
    expect(row?.textContent).not.toContain('first thing');
  });

  it('offers the close and changes nothing until a person presses it', async () => {
    await openWithOneStranded();

    const button = notices()[0]!.querySelector('.backlog-stranded-close') as HTMLElement;
    expect(button.textContent).toContain('Close #2229');
    expect(closeWorkflow).not.toHaveBeenCalled();

    // The workflow is closed, so the next read carries no notice.
    backlogItems.mockResolvedValue([item({ id: 1, title: 'first thing' }), item({ id: 2, title: 'job tracker' })]);
    const reads = backlogItems.mock.calls.length;

    button.click();
    await flushPromises();

    expect(closeWorkflow).toHaveBeenCalledOnce();
    expect(closeWorkflow).toHaveBeenCalledWith('beta', 2229, 'Blocked; the work continued in workflow 2302.');
    expect(backlogItems.mock.calls.length).toBeGreaterThan(reads);
    expect(notices()).toHaveLength(0);
  });

  it('draws nothing for an item that is not stranded', async () => {
    setActivePinia(createPinia());
    useConsoleStore().teams = [] as never;
    useSessionStore().user = { email: 'admin@example.com' } as never;
    backlogItems.mockResolvedValue([item({ id: 1, stranded: null })]);

    await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    expect(notices()).toHaveLength(0);
  });
});
