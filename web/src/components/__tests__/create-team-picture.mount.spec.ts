// @vitest-environment happy-dom
//
// NEW TEAM OFFERS THE TEAM PICTURE: off unless ticked, sent in the create request itself (never a
// follow-up call), and a page that could not be published is said in the Host's words while the
// team is still made.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { createTeam, fileSystemRoots, getInstanceId, listCatalog, setTeamPicture, notify } = vi.hoisted(() => ({
  createTeam: vi.fn(),
  getInstanceId: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
  setTeamPicture: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getInstanceId,
  createTeam,
  fileSystemRoots,
  listCatalog,
  setTeamPicture,
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { TeamPictureLabel } from '../../lib/teamPicture';

/** createTeam's 12th argument is the Team picture (see `createTeam` in api/client.ts). */
const TeamPictureArgument = 11;

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  createTeam.mockReset();
  createTeam.mockResolvedValue({ id: 'beta', name: 'Beta' });
  getInstanceId.mockReset();
  getInstanceId.mockResolvedValue('instance-a');
  fileSystemRoots.mockReset();
  fileSystemRoots.mockResolvedValue({ roots: [] });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  setTeamPicture.mockReset();
  notify.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open() {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless'], root: null, repos: [] }, 'instance-a');
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true, workflowSpendLimit: null });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });
  return mountDialog(CreateTeamDialog, {}, { pinia: false });
}

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function checkbox(): HTMLElement {
  const found = document.body.querySelector<HTMLElement>('[data-team-picture]');
  if (!found) throw new Error('no Team picture option in the rendered dialog');
  return found;
}

function create() {
  const found = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Create team');
  if (!found) throw new Error('no Create team button');
  (found as HTMLButtonElement).click();
}

async function named(wrapper: Awaited<ReturnType<typeof open>>, name: string) {
  const input = wrapper.findAllComponents({ name: 'QInput' }).find((i) => i.props('label') === 'Team name')!;
  await input.setValue(name);
  await settled();
}

describe('New Team: the Team picture option', () => {
  it('is on the General tab, labelled, and off unless ticked', async () => {
    const wrapper = await open();
    expect(checkbox().textContent).toContain(TeamPictureLabel);
    expect(checkbox().getAttribute('aria-checked')).toBe('false');

    await named(wrapper, 'Beta');
    create();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![TeamPictureArgument]).toBe(false);
    expect(setTeamPicture).not.toHaveBeenCalled();
  });

  it('asks for the Team picture in the create request itself when ticked', async () => {
    const wrapper = await open();
    await named(wrapper, 'Beta');
    checkbox().click();
    await settled();
    expect(checkbox().getAttribute('aria-checked')).toBe('true');

    create();
    await settled();

    expect(createTeam.mock.calls[0]![TeamPictureArgument]).toBe(true);
    expect(setTeamPicture).not.toHaveBeenCalled();
  });

  it("says, in the Host's words, when the team was made but its page was not published", async () => {
    createTeam.mockResolvedValue({ id: 'beta', name: 'Beta', teamPictureError: 'The Team picture template is missing from this build.' });
    const wrapper = await open();
    await named(wrapper, 'Beta');
    checkbox().click();
    await settled();
    create();
    await settled();

    expect(notify).toHaveBeenCalledWith(expect.objectContaining({
      type: 'warning',
      message: 'The Team picture template is missing from this build.',
    }));
  });
});
