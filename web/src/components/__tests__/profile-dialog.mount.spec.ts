// @vitest-environment happy-dom
//
// THE SIGNED-IN PERSON'S OWN ADDRESS. The email tab opens first, and its three boxes carry the
// email rule from `lib/rules`; the confirmation also has to match the new address.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listKeys } = vi.hoisted(() => ({ listKeys: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listKeys,
}));

import ProfileDialog from '../ProfileDialog.vue';
import { useSessionStore } from '../../stores/session';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, hasError, isDisabled, settle, type, button } from '../../test/formProbe';

beforeEach(() => {
  listKeys.mockReset();
  listKeys.mockResolvedValue([]);
});

afterEach(resetBody);

async function mountProfile() {
  setActivePinia(createPinia());
  const session = useSessionStore();
  const changeEmail = vi.spyOn(session, 'changeEmail').mockResolvedValue(undefined);
  const wrapper = await mountDialog(ProfileDialog, {}, { pinia: false });

  return { wrapper, changeEmail };
}

describe('ProfileDialog, mounted', () => {
  it('disables Update email while the form is empty', async () => {
    const { wrapper } = await mountProfile();

    expect(isDisabled('Update email')).toBe(true);

    wrapper.unmount();
  });

  it('enables Update email with two valid, matching addresses', async () => {
    const { wrapper, changeEmail } = await mountProfile();

    await type('Current email', 'ada@example.com');
    await type('New email', 'ada@example.org');
    await type('Confirm new email', 'ADA@example.org');

    expect(isDisabled('Update email')).toBe(false);

    button('Update email').click();
    await settle();

    expect(changeEmail).toHaveBeenCalledWith('ada@example.com', 'ada@example.org');

    wrapper.unmount();
  });

  it('refuses a new address without @ under the field', async () => {
    const { wrapper } = await mountProfile();

    await type('Current email', 'ada@example.com');
    await type('New email', 'ada');
    await blur('New email');
    await type('Confirm new email', 'ada');

    expect(hasError('New email')).toBe(true);
    expect(isDisabled('Update email')).toBe(true);

    wrapper.unmount();
  });

  it('marks a confirmation that does not match', async () => {
    const { wrapper } = await mountProfile();

    await type('Current email', 'ada@example.com');
    await type('New email', 'ada@example.org');
    await type('Confirm new email', 'ada@example.net');
    await blur('Confirm new email');

    expect(hasError('Confirm new email')).toBe(true);
    expect(isDisabled('Update email')).toBe(true);

    wrapper.unmount();
  });
});
