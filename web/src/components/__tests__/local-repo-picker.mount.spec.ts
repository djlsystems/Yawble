// @vitest-environment happy-dom
//
// THE LOCAL REPOSITORY PICKER on its own: "Create a local repository" makes one by name, checked as
// the Host checks it, and attaches it. Team settings offers it; New Team turns it off.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { listLocalRepos, createLocalRepo } = vi.hoisted(() => ({
  listLocalRepos: vi.fn(),
  createLocalRepo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listLocalRepos,
  createLocalRepo,
}));

import LocalRepoPicker from '../LocalRepoPicker.vue';
import { bodyText, resetBody } from '../../test/mountQuasar';

function localRepo(name: string) {
  return { name, reference: `local:${name}`, sizeBytes: 1, defaultBranch: 'main', lastCommit: null, teams: [] };
}

function createButton(): HTMLButtonElement | undefined {
  return [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === 'Create') as HTMLButtonElement | undefined;
}

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listLocalRepos.mockReset();
  listLocalRepos.mockResolvedValue([]);
  createLocalRepo.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('LocalRepoPicker', () => {
  it('creates a local repository by name and attaches it', async () => {
    createLocalRepo.mockResolvedValue(localRepo('gadget'));
    const wrapper = mount(LocalRepoPicker, { props: { attached: [] }, attachTo: document.body });
    await settled();

    await wrapper.find('input').setValue('gadget');
    await settled();
    createButton()!.click();
    await settled();

    expect(createLocalRepo).toHaveBeenCalledWith('gadget');
    expect(wrapper.emitted('attach')).toEqual([['local:gadget']]);
    wrapper.unmount();
  });

  it('refuses an illegal local repository name on the field and creates nothing', async () => {
    const wrapper = mount(LocalRepoPicker, { props: { attached: [] }, attachTo: document.body });
    await settled();

    await wrapper.find('input').setValue('../escape');
    await settled();

    expect(bodyText()).toContain("'../escape' is not a local repository name. Use 1 to 100 letters, digits, '.', '_' or '-'");
    expect(bodyText()).toContain("with no '..'");
    expect(createButton()!.hasAttribute('disabled')).toBe(true);
    expect(createLocalRepo).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('offers no Create when told not to, and still attaches an existing one', async () => {
    listLocalRepos.mockResolvedValue([localRepo('widget')]);
    const wrapper = mount(LocalRepoPicker, { props: { attached: [], offerCreate: false }, attachTo: document.body });
    await settled();

    expect(wrapper.find('input').exists()).toBe(false);
    expect(createButton()).toBeUndefined();

    document.body.querySelector<HTMLElement>('[aria-label="Attach local:widget"]')!.click();
    await settled();
    expect(wrapper.emitted('attach')).toEqual([['local:widget']]);
    wrapper.unmount();
  });
});
