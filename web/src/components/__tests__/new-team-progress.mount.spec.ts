// @vitest-environment happy-dom
//
// A TEAM CREATE CAN OUTLAST THE WAIT FOR IT. While New Team or a backlog dispatch to a new team waits
// on a large clone, the dialog says what it is doing and that closing the window does not stop it;
// when the wait is cut off (no HTTP answer), it says the create carries on on the server instead of
// reporting a failure. A real refusal is still shown as one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const client = vi.hoisted(() => ({
  createTeam: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
  listLocalRepos: vi.fn(),
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  dispatchBacklogItemToNewTeam: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  ...client,
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { mountDialog, resetBody } from '../../test/mountQuasar';

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  for (const fn of Object.values(client)) fn.mockReset();
  client.fileSystemRoots.mockResolvedValue({ roots: [] });
  client.listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  client.listLocalRepos.mockResolvedValue([]);
  client.backlogItems.mockResolvedValue([{
    id: 1, team: null, teamName: null, teamGone: false, title: 'first thing', body: '', state: 'ready',
    archivedAt: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com', inFlight: null,
  }]);
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === label);
  if (!found) throw new Error(`no ${label} button in the rendered dialog`);
  return found as HTMLButtonElement;
}

function shown(testid: string): string | null {
  return document.body.querySelector(`[data-testid="${testid}"]`)?.textContent?.trim() ?? null;
}

/** What `fetch` throws when the connection is lost or the request is aborted: no HTTP status. */
const cutOff = () => new TypeError('Failed to fetch');

/** A refusal as `send` throws it: the server's sentence and its status. */
const refusal = (message: string, status: number) => Object.assign(new Error(message), { status });

async function openNewTeam() {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless'], root: null, repos: [] });
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  const wrapper = await mountDialog(CreateTeamDialog, {}, { pinia: false });
  const name = wrapper.findAllComponents({ name: 'QInput' }).find((input) => input.props('label') === 'Team name');
  await (name as VueWrapper).setValue('Beta');
  await settled();
  return wrapper;
}

async function openDispatchToNew() {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless'], root: null, repos: [] });
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;
  vi.spyOn(board, 'refreshForTeamCreated').mockResolvedValue(undefined as never);
  vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});
  useSessionStore().user = { email: 'admin@example.com' } as never;

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  ([...document.querySelectorAll('button')].find((b) => b.innerHTML.includes('send')) as HTMLElement).click();
  await flushPromises();

  [...document.querySelectorAll('.q-radio')]
    .find((r) => r.textContent?.includes('new team'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

describe('New Team while its repository clones', () => {
  it('shows what it is doing, and that closing the window does not stop it, while it waits', async () => {
    let answer!: (value: unknown) => void;
    client.createTeam.mockReturnValue(new Promise((resolve) => { answer = resolve; }));
    await openNewTeam();

    button('Create team').click();
    await settled();

    expect(shown('create-progress')).toContain('Creating Beta: cloning its repository.');
    expect(shown('create-progress')).toContain('closing this window does not stop it');

    answer({ id: 'Beta', name: 'Beta' });
    await settled();
    expect(shown('create-progress')).toBeNull();
  });

  it('says the create carries on when the wait is cut off, not that it failed', async () => {
    client.createTeam.mockRejectedValue(cutOff());
    await openNewTeam();

    button('Create team').click();
    await settled();

    expect(shown('create-carries-on')).toContain('The connection was lost while Beta was being created.');
    expect(shown('create-carries-on')).toContain('the team appears on the Teams list when it is done');
    expect(document.body.textContent).not.toContain('Failed to fetch');
  });

  it('still shows a refusal as one', async () => {
    client.createTeam.mockRejectedValue(refusal("A team called 'Beta' already exists.", 409));
    await openNewTeam();

    button('Create team').click();
    await settled();

    expect(shown('create-carries-on')).toBeNull();
    expect(document.body.textContent).toContain("A team called 'Beta' already exists.");
  });
});

describe('Backlog dispatch to a new team while its repository clones', () => {
  it('shows what it is doing while it waits', async () => {
    client.dispatchBacklogItemToNewTeam.mockReturnValue(new Promise(() => {}));
    await openDispatchToNew();

    button('Dispatch').click();
    await settled();

    expect(shown('create-progress')).toMatch(/^Creating .+: cloning its repository\./);
    expect(shown('create-progress')).toContain('closing this window does not stop it');
  });

  it('says the create and the dispatch carry on when the wait is cut off', async () => {
    client.dispatchBacklogItemToNewTeam.mockRejectedValue(cutOff());
    await openDispatchToNew();

    button('Dispatch').click();
    await settled();

    expect(shown('create-carries-on')).toContain('The create carries on on the server');
    expect(document.body.textContent).not.toContain('Failed to fetch');
  });
});
