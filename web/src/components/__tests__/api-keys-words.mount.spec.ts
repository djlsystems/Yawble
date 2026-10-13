// @vitest-environment happy-dom
//
// WHAT A KEY IS FOR, SAID WHERE KEYS ARE SEEN. Admin's list can only revoke, so it says where a key
// is made (the person's own profile); the profile's tab says what a key does, how a program sends
// it, and what it is refused.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listKeys, listAllKeys } = vi.hoisted(() => ({
  listKeys: vi.fn().mockResolvedValue([]),
  listAllKeys: vi.fn().mockResolvedValue([]),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listKeys,
  listAllKeys,
}));

import ApiKeysDialog from '../ApiKeysDialog.vue';
import ProfileDialog from '../ProfileDialog.vue';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

afterEach(resetBody);

function text(selector: string): string {
  return document.body.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('API keys, in words', () => {
  it("Admin's list says what a key is for and that it is made under User profile, API keys", async () => {
    const wrapper = await mountDialog(ApiKeysDialog, {});

    expect(text('[data-keys-about]')).toContain('use this instance as the person who made it');
    expect(text('[data-keys-about]')).toContain('revoke');
    expect(text('[data-keys-where]')).toContain('User profile');
    expect(text('[data-keys-where]')).toContain('API keys');

    wrapper.unmount();
  });

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
    expect(about).toContain('Admin, Keys');

    wrapper.unmount();
  });
});
