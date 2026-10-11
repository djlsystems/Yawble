// @vitest-environment happy-dom
//
// A NEW ACCOUNT. The dialog was `UserEditDialog` until the tier went; what is left is an address and
// a password, and both are the server's rules read from `lib/rules`.
import { afterEach, describe, expect, it } from 'vitest';
import UserCreateDialog from '../UserCreateDialog.vue';
import { MINIMUM_PASSWORD_LENGTH } from '../../lib/rules';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, field, hasError, isDisabled, pressEnter, type } from '../../test/formProbe';

afterEach(resetBody);

const baseProps = { busy: false, error: '' };
const goodPassword = 'p'.repeat(MINIMUM_PASSWORD_LENGTH);

describe('UserCreateDialog, mounted', () => {
  it('disables Create while the form is empty', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    expect(isDisabled('Create')).toBe(true);

    wrapper.unmount();
  });

  it('enables Create with an address and a long enough password', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    await type('Email', 'grace@example.com');
    await type('Password', goodPassword);

    expect(isDisabled('Create')).toBe(false);

    wrapper.unmount();
  });

  it('is an email field', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    expect(field('Email').getAttribute('type')).toBe('email');

    wrapper.unmount();
  });

  it('refuses a short password under the field', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    await type('Email', 'grace@example.com');
    await type('Password', goodPassword.slice(1));
    await blur('Password');

    expect(hasError('Password')).toBe(true);
    expect(bodyText()).toContain(`at least ${MINIMUM_PASSWORD_LENGTH} characters`);
    expect(isDisabled('Create')).toBe(true);

    wrapper.unmount();
  });

  it('refuses an address without @', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    await type('Email', 'grace');
    await blur('Email');
    await type('Password', goodPassword);

    expect(hasError('Email')).toBe(true);
    expect(isDisabled('Create')).toBe(true);

    wrapper.unmount();
  });

  it('submits on Enter, trimmed', async () => {
    const wrapper = await mountDialog(UserCreateDialog, baseProps);

    await type('Email', ' grace@example.com ');
    await type('Password', goodPassword);
    await pressEnter('Email');

    expect(wrapper.emitted('save')).toEqual([[{ email: 'grace@example.com', password: goodPassword }]]);

    wrapper.unmount();
  });

  it('keeps showing the server refusal in the dialog', async () => {
    const wrapper = await mountDialog(UserCreateDialog, { ...baseProps, error: 'That email is taken.' });

    expect(bodyFind('.q-banner')?.textContent).toContain('That email is taken.');

    wrapper.unmount();
  });
});
