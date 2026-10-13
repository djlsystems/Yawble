// @vitest-environment happy-dom
//
// WHAT A KEY IS FOR, SAID WHERE KEYS ARE MADE. The profile's tab says what a key does, how a
// program sends it, what it is refused, and that Admin, Users revokes all of a person's keys.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listKeys } = vi.hoisted(() => ({
  listKeys: vi.fn().mockResolvedValue([]),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listKeys,
}));

import ProfileDialog from '../ProfileDialog.vue';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

afterEach(resetBody);

function text(selector: string): string {
  return document.body.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('API keys, in words', () => {
  it("the profile's API keys tab says how a key is sent and what it is refused", async () => {
    setActivePinia(createPinia());
    const wrapper = await mountDialog(ProfileDialog, {}, { pinia: false });

    const tab = [...document.body.querySelectorAll<HTMLElement>('.q-tab')].find((t) =>
      t.textContent?.includes('API keys'),
    );
    tab?.click();
    await settle();

    const about = text('[data-keys-about]');
    expect(about).toContain('X-Api-Key');
    expect(about).toContain('as you');
    expect(about).toContain('cannot do what only a person signed in may do');
    expect(about).toContain('shown once');
    expect(about).toContain('Admin, Users');

    wrapper.unmount();
  });
});
