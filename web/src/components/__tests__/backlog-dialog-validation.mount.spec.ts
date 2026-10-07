// @vitest-environment happy-dom
//
// THE BACKLOG'S THREE FORMS, MOUNTED CLOSED AND THEN OPENED: Add (title), Dispatch (the new team's
// identifier, and the repos it would clone) and Team settings (repository URLs). Each field carries
// a rule from `lib/rules` and each submit button follows those rules. A bad URL is refused on
// screen and the server is never asked - the write routes are mocks that must stay uncalled.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const {
  backlogItems, backlogItem, createBacklogItem, listCatalog, fileSystemRoots,
  dispatchBacklogItemToNewTeam, setTeamRepos,
} = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  createBacklogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  dispatchBacklogItemToNewTeam: vi.fn(),
  setTeamRepos: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getInstanceId: async () => 'instance-a',
  backlogItems,
  backlogItem,
  createBacklogItem,
  listCatalog,
  fileSystemRoots,
  dispatchBacklogItemToNewTeam,
  setTeamRepos,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { MAXIMUM_BACKLOG_TITLE_LENGTH } from '../../lib/rules';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

function item(id: number, title: string) {
  return {
    id,
    team: null,
    teamName: null,
    teamGone: false,
    title,
    body: '',
    state: 'ready',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
  };
}

beforeEach(() => {
  localStorage.clear();
  backlogItems.mockReset();
  backlogItems.mockResolvedValue([item(1, 'first thing')]);
  backlogItem.mockReset();
  backlogItem.mockResolvedValue({ item: item(1, 'first thing'), dispatches: [], stats: [] });
  createBacklogItem.mockReset();
  createBacklogItem.mockResolvedValue(item(2, 'new'));
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }],
  });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  dispatchBacklogItemToNewTeam.mockReset();
  setTeamRepos.mockReset();
});

afterEach(resetBody);

function remember(repos: string[]) {
  localStorage.setItem('harness.newTeamDefaults', JSON.stringify({
    managerAgent: 'claude-headless',
    memberAgents: ['claude-headless'],
    root: null,
  }));
  localStorage.setItem('harness.recentRepos.instance.instance-a', JSON.stringify(repos));
}

async function open() {
  setActivePinia(createPinia());
  useConsoleStore().teams = [{ id: 'alpha', name: 'Alpha' }] as never;
  useSessionStore().user = { email: 'admin@example.com' } as never;

  const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  return wrapper;
}

/** The last button reading `label`; an icon's ligature text may precede it ("tuneTeam settings"). */
function button(label: string): HTMLButtonElement {
  const buttons = [...document.body.querySelectorAll('button')];
  const found = buttons.filter((candidate) => candidate.textContent?.trim() === label).pop()
    ?? buttons.filter((candidate) => candidate.textContent?.trim().endsWith(label)).pop();

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.props('label') === label);

  if (!found) throw new Error(`no ${label} input in the rendered dialog`);

  return found;
}

async function validated() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

async function openNewTeamDispatch() {
  const send = [...document.querySelectorAll('button')]
    .find((candidate) => candidate.innerHTML.includes('send')) as HTMLElement;

  send.click();
  await flushPromises();

  [...document.querySelectorAll('.q-radio')]
    .find((radio) => radio.textContent?.includes('new team'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

describe('BacklogDialog: Add', () => {
  it('disables Add with no title, refuses an overlong one, and enables it with a valid one', async () => {
    const wrapper = await open();

    button('Add item').click();
    await flushPromises();

    expect(button('Add').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Title').setValue('x'.repeat(MAXIMUM_BACKLOG_TITLE_LENGTH + 1));
    await validated();

    expect(bodyText()).toContain(`A title cannot be longer than ${MAXIMUM_BACKLOG_TITLE_LENGTH} characters.`);
    expect(button('Add').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Title').setValue('Form validation');
    await validated();

    expect(button('Add').hasAttribute('disabled')).toBe(false);

    button('Add').click();
    await validated();

    expect(createBacklogItem).toHaveBeenCalledWith(expect.objectContaining({ title: 'Form validation' }));
  });
});

describe('BacklogDialog: Dispatch to a new team', () => {
  it('refuses a new-team name that is not an identifier', async () => {
    remember([]);
    const wrapper = await open();
    await openNewTeamDispatch();

    expect(button('Dispatch').hasAttribute('disabled')).toBe(false);

    await field(wrapper, 'New team name').setValue('my team');
    await validated();

    expect(bodyText()).toContain('Names use letters, digits, - and _, up to 32 characters.');
    expect(button('Dispatch').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'New team name').setValue('');
    await validated();

    expect(button('Dispatch').hasAttribute('disabled')).toBe(true);
  });

  it('refuses a remembered repo URL that cannot be cloned, and asks nobody', async () => {
    remember(['git@github.com:owner/app.git']);
    const wrapper = await open();
    await openNewTeamDispatch();

    expect(bodyText()).toContain('git@github.com:owner/app.git: Use an absolute http or https URL');
    expect(button('Dispatch').hasAttribute('disabled')).toBe(true);

    button('Dispatch').click();
    await validated();

    expect(dispatchBacklogItemToNewTeam).not.toHaveBeenCalled();
  });

  it('shows a taken name in the dialog and marks the name field', async () => {
    remember([]);
    dispatchBacklogItemToNewTeam.mockRejectedValue(new Error("A team called 'b0001-first-thing' already exists."));
    const wrapper = await open();
    await openNewTeamDispatch();

    button('Dispatch').click();
    await validated();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalled();
    expect(field(wrapper, 'New team name').props('error')).toBe(true);
  });
});

describe('BacklogDialog: Team settings repos', () => {
  it('refuses a duplicate or unclonable URL in the list and keeps the button off', async () => {
    remember(['https://github.com/owner/app.git']);
    const wrapper = await open();
    await openNewTeamDispatch();

    button('Team settings').click();
    await flushPromises();

    expect(button('Use these').hasAttribute('disabled')).toBe(false);

    const repos = wrapper.findAllComponents({ name: 'QSelect' })
      .find((select) => select.props('label') === 'Repositories the platform clones')!;

    repos.vm.$emit('update:modelValue', ['https://github.com/owner/app.git', 'https://gitlab.com/x/app']);
    await validated();

    expect(bodyText()).toContain("Another URL in this list already clones into 'app'.");
    expect(button('Use these').hasAttribute('disabled')).toBe(true);

    repos.vm.$emit('update:modelValue', ['ftp://example.com/app']);
    await validated();

    expect(bodyText()).toContain('Use an absolute http or https URL');
    expect(button('Use these').hasAttribute('disabled')).toBe(true);
    expect(setTeamRepos).not.toHaveBeenCalled();
  });
});
