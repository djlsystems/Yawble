// @vitest-environment happy-dom
//
// AN ADMINISTRATOR RESETS A PASSWORD. The length it asks for is the server's, read from `lib/rules`
// rather than typed here, so the dialog and the server cannot disagree about it.
import { afterEach, describe, expect, it } from 'vitest';
import UserPasswordDialog from '../UserPasswordDialog.vue';
import { MINIMUM_PASSWORD_LENGTH } from '../../lib/rules';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, button, hasError, isDisabled, type } from '../../test/formProbe';

afterEach(resetBody);

const baseProps = { user: { id: 'u1', email: 'grace@example.com' }, busy: false, error: '' };
const goodPassword = 'p'.repeat(MINIMUM_PASSWORD_LENGTH);

describe('UserPasswordDialog, mounted', () => {
  it('disables Change it while the field is empty', async () => {
    const wrapper = await mountDialog(UserPasswordDialog, baseProps);

    expect(isDisabled('Change it')).toBe(true);

    wrapper.unmount();
  });

  it('disables Change it one character below the minimum, and says so under the field', async () => {
    const wrapper = await mountDialog(UserPasswordDialog, baseProps);

    await type('New password', goodPassword.slice(1));
    await blur('New password');

    expect(isDisabled('Change it')).toBe(true);
    expect(hasError('New password')).toBe(true);
    expect(bodyText()).toContain(`at least ${MINIMUM_PASSWORD_LENGTH} characters`);

    wrapper.unmount();
  });

  it('enables Change it at the minimum and sends the password', async () => {
    const wrapper = await mountDialog(UserPasswordDialog, baseProps);

    await type('New password', goodPassword);
    await blur('New password');

    expect(isDisabled('Change it')).toBe(false);
    expect(hasError('New password')).toBe(false);

    button('Change it').click();

    expect(wrapper.emitted('save')).toEqual([[goodPassword]]);

    wrapper.unmount();
  });
});
