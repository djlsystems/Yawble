// @vitest-environment happy-dom
//
// THE FRONT DOOR. Submit stays dark until both fields hold something, the create form holds the
// server's password length, and Enter in either field signs in - the one form nobody should have to
// reach for a mouse to finish.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import AuthForm from '../AuthForm.vue';
import { useSessionStore } from '../../stores/session';
import { MINIMUM_PASSWORD_LENGTH } from '../../lib/rules';
import { resetBody } from '../../test/mountQuasar';
import { blur, hasError, isDisabled, pressEnter, settle, type } from '../../test/formProbe';

afterEach(resetBody);

async function mountForm(mode: 'create' | 'login') {
  setActivePinia(createPinia());
  const session = useSessionStore();
  const signIn = vi.spyOn(session, 'signIn').mockResolvedValue(undefined);
  const createFirstAccount = vi.spyOn(session, 'createFirstAccount').mockResolvedValue(undefined);

  const wrapper = mount(AuthForm, { props: { mode }, attachTo: document.body });
  await settle();

  return { wrapper, signIn, createFirstAccount };
}

describe('AuthForm, mounted', () => {
  it('disables Log in while the form is empty', async () => {
    const { wrapper } = await mountForm('login');

    expect(isDisabled('Log in')).toBe(true);

    await type('Email', 'ada@example.com');
    expect(isDisabled('Log in')).toBe(true);

    wrapper.unmount();
  });

  it('enables Log in with an email and a password', async () => {
    const { wrapper } = await mountForm('login');

    await type('Email', 'ada@example.com');
    await type('Password', 'x');

    expect(isDisabled('Log in')).toBe(false);

    wrapper.unmount();
  });

  it('refuses an address without @ under the field', async () => {
    const { wrapper } = await mountForm('login');

    await type('Email', 'ada');
    await blur('Email');
    await type('Password', 'secret');

    expect(hasError('Email')).toBe(true);
    expect(isDisabled('Log in')).toBe(true);

    wrapper.unmount();
  });

  it('submits on Enter', async () => {
    const { wrapper, signIn } = await mountForm('login');

    await type('Email', ' ada@example.com ');
    await type('Password', 'secret');
    await pressEnter('Password');

    expect(signIn).toHaveBeenCalledTimes(1);
    expect(signIn).toHaveBeenCalledWith('ada@example.com', 'secret');
    expect(wrapper.emitted('done')).toHaveLength(1);

    wrapper.unmount();
  });

  it('does not submit on Enter while the form is incomplete', async () => {
    const { wrapper, signIn } = await mountForm('login');

    await type('Email', 'ada@example.com');
    await pressEnter('Email');

    expect(signIn).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('holds the create form to the minimum password length', async () => {
    const { wrapper, createFirstAccount } = await mountForm('create');

    await type('Email', 'ada@example.com');
    await type('Password', 'x'.repeat(MINIMUM_PASSWORD_LENGTH - 1));
    expect(isDisabled('Create account')).toBe(true);

    await type('Password', 'x'.repeat(MINIMUM_PASSWORD_LENGTH));
    expect(isDisabled('Create account')).toBe(false);

    await pressEnter('Email');
    expect(createFirstAccount).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });

  // After ten wrong passwords the server answers 429 with a sentence in `error`. The form
  // shows that sentence as the server wrote it - through the REAL client, not a spy, because the
  // thing at stake is how `send` turns a 429 into the message a person reads.
  it('shows the server sentence when sign-in is refused with 429', async () => {
    setActivePinia(createPinia());
    const sentence = 'Too many wrong passwords for this account. Wait 5 minutes and try again.';
    const fetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ error: sentence }), {
        status: 429,
        headers: { 'content-type': 'application/json', 'retry-after': '300' },
      }),
    );

    try {
      const wrapper = mount(AuthForm, { props: { mode: 'login' }, attachTo: document.body });
      await settle();

      await type('Email', 'ada@example.com');
      await type('Password', 'wrong');
      await pressEnter('Password');
      await settle();

      expect(document.querySelector('.auth-error')?.textContent?.trim()).toBe(sentence);
      expect(wrapper.emitted('done')).toBeUndefined();

      wrapper.unmount();
    } finally {
      fetch.mockRestore();
    }
  });
});
