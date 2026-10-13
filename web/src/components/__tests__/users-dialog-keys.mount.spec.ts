// @vitest-environment happy-dom
//
// THE USERS SCREEN REVOKES ANOTHER PERSON'S KEYS. Each row says how many API keys the account
// holds, and Revoke all their keys asks first, then revokes them through the server and leaves the
// account. The dialog says only "Users on this instance."; New user carries no description.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listUsers, revokeUserKeys } = vi.hoisted(() => ({
  listUsers: vi.fn(),
  revokeUserKeys: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listUsers,
  revokeUserKeys,
}));

import UsersDialog from '../UsersDialog.vue';
import UserCreateDialog from '../UserCreateDialog.vue';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

beforeEach(() => {
  listUsers.mockReset();
  revokeUserKeys.mockReset();
  listUsers.mockResolvedValue([
    { id: 'u1', email: 'ada@example.com', apiKeys: 2 },
    { id: 'u2', email: 'grace@example.com', apiKeys: 0 },
  ]);
  revokeUserKeys.mockResolvedValue({ revoked: 2 });
});

afterEach(resetBody);

async function mountUsers() {
  setActivePinia(createPinia());
  const wrapper = await mountDialog(UsersDialog, {}, { pinia: false });
  await settle();
  return wrapper;
}

const revokeButtons = () => [...document.body.querySelectorAll<HTMLButtonElement>('[data-revoke-keys]')];

describe('Users, and their API keys', () => {
  it('says only "Users on this instance."', async () => {
    const wrapper = await mountUsers();

    expect(bodyText()).toContain('Users on this instance.');
    expect(bodyText()).not.toContain('per-team access list');

    wrapper.unmount();
  });

  it('counts each account\'s keys and offers the revoke only where there are keys', async () => {
    const wrapper = await mountUsers();

    const counts = [...document.body.querySelectorAll('[data-user-keys]')].map((n) => n.textContent?.trim());
    expect(counts).toEqual(['2 API keys']);
    expect(revokeButtons().map((b) => b.disabled || b.getAttribute('aria-disabled') === 'true')).toEqual([false, true]);

    wrapper.unmount();
  });

  it('asks first, then revokes every key of that account and reads the list again', async () => {
    const wrapper = await mountUsers();

    revokeButtons()[0]!.click();
    await settle();

    const confirm = document.body.querySelector('[data-revoke-keys-confirm]');
    expect(confirm?.textContent).toContain('ada@example.com');
    expect(confirm?.textContent).toContain('The account stays');
    expect(revokeUserKeys).not.toHaveBeenCalled();

    const go = [...(confirm?.querySelectorAll<HTMLButtonElement>('button') ?? [])].find((b) =>
      b.textContent?.includes('Revoke 2 keys'),
    );
    go!.click();
    await settle();

    expect(revokeUserKeys).toHaveBeenCalledWith('u1');
    expect(listUsers).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('New user carries no description', async () => {
    const wrapper = await mountDialog(UserCreateDialog, { busy: false, error: '' });

    expect(bodyText()).not.toContain('Every account reaches every team');
    expect(document.body.querySelector('.q-card .text-caption.os-text-muted')).toBeNull();

    wrapper.unmount();
  });
});
