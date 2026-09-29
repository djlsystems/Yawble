// @vitest-environment happy-dom
//
// ADMIN > REPOSITORIES. Every local repository with its reference, size, default branch, last
// commit and the teams using it; Delete asks first and sends nothing until confirmed, and the
// Host's refusal (a team still uses it) is shown in its own words.
//
// THE MOCK IS OF `api/client`: what the Host does is pinned server-side (LocalReposTests).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listLocalRepos, deleteLocalRepo } = vi.hoisted(() => ({
  listLocalRepos: vi.fn(),
  deleteLocalRepo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listLocalRepos,
  deleteLocalRepo,
}));

import LocalReposDialog from '../LocalReposDialog.vue';
import { useConsoleStore } from '../../stores/console';
import type { LocalRepo, Team } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';

const widget: LocalRepo = {
  name: 'widget',
  reference: 'local:widget',
  sizeBytes: 5 * 1024 * 1024,
  defaultBranch: 'main',
  lastCommit: { sha: '0123456789abcdef', subject: 'Add the widget', committedAt: '2026-09-28T10:00:00Z' },
  teams: ['alpha'],
};

const gadget: LocalRepo = {
  name: 'gadget',
  reference: 'local:gadget',
  sizeBytes: 900,
  defaultBranch: null,
  lastCommit: null,
  teams: [],
};

beforeEach(() => {
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [{ id: 'alpha', name: 'Alpha Team' } as unknown as Team], overviewLanded: true });

  listLocalRepos.mockReset();
  listLocalRepos.mockResolvedValue([widget, gadget]);
  deleteLocalRepo.mockReset();
});

afterEach(() => {
  resetBody();
});

async function open() {
  const wrapper = await mountDialog(LocalReposDialog, {}, { pinia: false });
  await settle();
  return wrapper;
}

describe('Admin > Repositories', () => {
  it('lists each local repository with its size, default branch, last commit and teams', async () => {
    await open();

    const row = bodyFind('[data-local-repo="widget"]')!.textContent ?? '';
    expect(row).toContain('local:widget');
    expect(row).toContain('5.0 MB');
    expect(row).toContain('main');
    expect(row).toContain('0123456');
    expect(row).toContain('Add the widget');
    expect(row).toContain('Alpha Team');

    const empty = bodyFind('[data-local-repo="gadget"]')!.textContent ?? '';
    expect(empty).toContain('900 B');
    expect(empty).toContain('not known');
    expect(empty).toContain('none');
  });

  // B001F: a deleted team's local repository is kept, so an unused one is said to be unused.
  it('marks a repository no team uses as unused, and one a team uses by its team', async () => {
    listLocalRepos.mockResolvedValue([{ ...widget, unused: false }, { ...gadget, unused: true }]);
    await open();

    expect(bodyFind('[data-local-repo="gadget"] [data-local-repo-unused]')?.textContent).toContain('unused');
    expect(bodyFind('[data-local-repo="widget"] [data-local-repo-unused]')).toBeNull();
    expect(bodyFind('[data-local-repo="widget"]')!.textContent).toContain('Alpha Team');
  });

  // Every table's columns resize (lib/resizableColumns.ts); this pins that the dialog's table is one.
  it('gives each column a resize handle and takes back the widths stored for this table', async () => {
    localStorage.setItem('harness.columns.local-repos', JSON.stringify({ Name: 220 }));
    try {
      await open();

      const headers = Array.from(document.body.querySelectorAll('thead th')) as HTMLElement[];
      expect(headers.length).toBe(6);
      expect(headers.every((th) => th.querySelector('.os-col-resizer'))).toBe(true);
      expect(headers[0]?.style.width).toBe('220px');
    } finally {
      localStorage.removeItem('harness.columns.local-repos');
    }
  });

  it('asks before deleting, sends nothing on Cancel, and deletes and re-lists on confirm', async () => {
    await open();

    (bodyFind('[aria-label="Delete gadget"]') as HTMLButtonElement).click();
    await settle();
    expect(bodyText()).toContain('Delete gadget?');
    expect(deleteLocalRepo).not.toHaveBeenCalled();

    button('Cancel').click();
    await settle();
    expect(deleteLocalRepo).not.toHaveBeenCalled();

    (bodyFind('[aria-label="Delete gadget"]') as HTMLButtonElement).click();
    await settle();
    listLocalRepos.mockResolvedValue([widget]);
    button('Delete gadget').click();
    await settle();

    expect(deleteLocalRepo).toHaveBeenCalledWith('gadget');
    expect(bodyFind('[data-local-repo="gadget"]')).toBeNull();
  });

  it('shows the Host refusing a repository a team still uses, in its own words', async () => {
    deleteLocalRepo.mockRejectedValue(new Error("'widget' is used by alpha, so it was not deleted."));
    await open();

    (bodyFind('[aria-label="Delete widget"]') as HTMLButtonElement).click();
    await settle();
    expect(bodyText()).toContain('Used by Alpha Team');

    button('Delete widget').click();
    await settle();

    expect(bodyText()).toContain("'widget' is used by alpha, so it was not deleted.");
    expect(bodyFind('[data-local-repo="widget"]')).not.toBeNull();
  });
});
