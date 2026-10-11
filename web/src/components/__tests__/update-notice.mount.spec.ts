// @vitest-environment happy-dom
//
// THE UPDATE NOTICE beside the version: shown only when the Host's release check answered and
// listed a newer release; its dialog says what changed, as text, and the one command that updates.
// "Not checked" is never shown as an alert, and nothing here updates anything.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { readUpdateStatus, checkForUpdates } = vi.hoisted(() => ({
  readUpdateStatus: vi.fn(),
  checkForUpdates: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
  copyToClipboard: vi.fn(() => Promise.resolve()),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  readUpdateStatus,
  checkForUpdates,
}));

import { bodyFind, bodyText, resetBody } from '../../test/mountQuasar';
import UpdateNotice from '../UpdateNotice.vue';
import { useWipStore } from '../../stores/wip';
import { productCli } from '../../presentation/product';
import type { UpdateStatus } from '../../lib/releaseUpdates';

const available: UpdateStatus = {
  current: '2026.10.06.1',
  enabled: true,
  checked: true,
  checkedAt: '2026-10-06T20:00:00Z',
  detail: null,
  latest: '2026.10.08.1',
  updateAvailable: true,
  latestIsPrerelease: true,
  newer: [
    { version: '2026.10.08.1', prerelease: true, publishedAt: '2026-10-08T12:00:00Z', url: 'https://example.test/r/2', notes: '<b>Tabs</b> everywhere\n- one strip' },
    { version: '2026.10.07.1', prerelease: true, publishedAt: '2026-10-07T12:00:00Z', url: 'https://example.test/r/1', notes: '' },
  ],
};

const notChecked: UpdateStatus = {
  ...available,
  checked: false,
  detail: 'Not checked: the release list did not answer in time',
  latest: null,
  updateAvailable: false,
  latestIsPrerelease: null,
  newer: [],
};

async function mountNotice() {
  setActivePinia(createPinia());
  const wrapper = mount(UpdateNotice);
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  readUpdateStatus.mockReset();
  checkForUpdates.mockReset();
});

afterEach(resetBody);

describe('the update notice', () => {
  it('says nothing when nothing newer is known, and nothing when the check could not answer', async () => {
    readUpdateStatus.mockResolvedValue(notChecked);
    const quiet = await mountNotice();
    expect(quiet.find('[data-update-notice]').exists()).toBe(false);
    quiet.unmount();

    readUpdateStatus.mockRejectedValue(new Error('offline'));
    const failed = await mountNotice();
    expect(failed.find('[data-update-notice]').exists()).toBe(false);
  });

  it('opens what changed, as text, and the command that updates, with the pre-release flag', async () => {
    readUpdateStatus.mockResolvedValue(available);
    const wrapper = await mountNotice();

    await wrapper.find('[data-update-notice]').trigger('click');
    await flushPromises();

    expect(bodyText()).toContain('Update available');
    expect(bodyFind('[data-updates-headline]')?.textContent).toContain('v2026.10.08.1 (pre-release) is out. You are on v2026.10.06.1.');
    expect(bodyFind('[data-updates-command]')?.textContent).toBe(`${productCli} update --prerelease`);

    const releases = document.body.querySelectorAll('[data-updates-release]');
    expect(releases).toHaveLength(2);
    // The release's own text, kept as characters: a tag in it is shown, never rendered.
    const notes = bodyFind('[data-updates-notes]')!;
    expect(notes.textContent).toBe('<b>Tabs</b> everywhere\n- one strip');
    expect(notes.querySelector('b')).toBeNull();
    expect(bodyText()).toContain('No notes were published with this release.');
  });

  it('warns that runs in progress stop, and only when there are some', async () => {
    readUpdateStatus.mockResolvedValue(available);
    const wrapper = await mountNotice();
    await wrapper.find('[data-update-notice]').trigger('click');
    await flushPromises();
    expect(bodyFind('[data-updates-running]')).toBeNull();

    useWipStore().$patch({ view: { running: [{ team: 'alpha', member: 'Manager' }, { team: 'beta', member: 'Dev' }], waiting: [] } as never });
    await flushPromises();
    expect(bodyFind('[data-updates-running]')?.textContent).toContain('2 agent runs are in progress now.');
  });

  it('checks now through the route and shows the new answer', async () => {
    readUpdateStatus.mockResolvedValue(available);
    checkForUpdates.mockResolvedValue({ ...available, latest: '2026.10.09.1', newer: [{ ...available.newer[0]!, version: '2026.10.09.1' }] });
    const wrapper = await mountNotice();
    await wrapper.find('[data-update-notice]').trigger('click');
    await flushPromises();

    (bodyFind('[data-updates-check]') as HTMLElement).click();
    await flushPromises();

    expect(checkForUpdates).toHaveBeenCalledTimes(1);
    expect(bodyFind('[data-updates-headline]')?.textContent).toContain('v2026.10.09.1');
  });
});
