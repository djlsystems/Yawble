// @vitest-environment happy-dom
//
// THE MAILBOX FORM'S USERNAME AND NAME: two optional fields a person must be able to tell apart.
// Username is the server login - the email address itself for Gmail, iCloud and Yahoo - so for those
// it sits under an Advanced disclosure that says when it is needed; Other shows it as a plain field.
// Name is the connection's label in Connections, and its help says so.
//
// THE MOCK IS OF `api/client`'s addMailbox, in the shape the Host serves. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

const { addMailbox } = vi.hoisted(() => ({ addMailbox: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  addMailbox,
}));

import MailboxForm from '../MailboxForm.vue';
import type { MailboxPreset } from '../../api/types';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { field, settle, type } from '../../test/formProbe';

const presets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'icloud', name: 'iCloud', imap: { host: 'imap.mail.me.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.me.com', port: 587, security: 'STARTTLS' } },
  { id: 'yahoo', name: 'Yahoo', imap: { host: 'imap.mail.yahoo.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.yahoo.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

beforeEach(() => {
  addMailbox.mockReset();
  addMailbox.mockResolvedValue({ connection: { id: 'imap-1' }, sentence: 'Connected.' });
});

afterEach(resetBody);

async function open(preset: string) {
  const wrapper = mount(MailboxForm, { props: { presets, preset }, attachTo: document.body });
  await settle();
  return wrapper;
}

/** Whether a person can see the element: nothing between it and the body is display:none. */
function shown(element: Element | null): boolean {
  for (let node = element as HTMLElement | null; node && node !== document.body; node = node.parentElement) {
    if (node.style.display === 'none' || node.hasAttribute('hidden')) return false;
  }
  return element !== null;
}

const usernameShown = () => {
  try {
    return shown(field('Username'));
  } catch {
    return false;
  }
};

const fieldHint = (label: string) => field(label).closest('.q-field')!.querySelector('.q-field__messages')?.textContent?.trim() ?? '';

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

/** Opens Advanced and lets its slide finish: unmounted mid-slide, the animation is cancelled and throws. */
async function openAdvanced() {
  await click(bodyFind('[data-mailbox-login-advanced] .q-item'));
  await new Promise((resolve) => setTimeout(resolve, 400));
}

describe('Username, the server login', () => {
  it.each(['gmail', 'icloud', 'yahoo'])('is not shown by default for %s, whose login is the email address', async (preset) => {
    const wrapper = await open(preset);

    expect(usernameShown()).toBe(false);
    expect(bodyFind('[data-mailbox-login-advanced]')).not.toBeNull();

    wrapper.unmount();
  });

  it('sits under Advanced, which says it is the server login and is needed only when the provider asks for something other than the address', async () => {
    const wrapper = await open('icloud');

    await openAdvanced();

    expect(usernameShown()).toBe(true);
    const words = bodyFind('[data-mailbox-login-advanced]')!.textContent!;
    expect(words).toContain('server login');
    expect(words).toContain('other than the email address');
    expect(words).toContain("iCloud's IMAP");

    wrapper.unmount();
  });

  it('typed under Advanced, is still sent with Save', async () => {
    const wrapper = await open('icloud');

    await openAdvanced();
    await type('Email address', 'someone@icloud.com');
    await type('App password', 'abcd-efgh-ijkl-mnop');
    await type('Username', 'someone');
    await (wrapper.vm as unknown as { save: () => Promise<void> }).save();
    await settle();

    expect(addMailbox).toHaveBeenCalledTimes(1);
    expect(addMailbox.mock.calls[0]?.[0]).toMatchObject({ preset: 'icloud', account: 'someone@icloud.com', username: 'someone' });

    wrapper.unmount();
  });

  it('is a plain field for Other, with no Advanced around it', async () => {
    const wrapper = await open('other');

    expect(usernameShown()).toBe(true);
    expect(bodyFind('[data-mailbox-login-advanced]')).toBeNull();

    // And switching to a provider's tile tucks it away again.
    await click(bodyFind('[data-mailbox-preset="gmail"]'));
    expect(usernameShown()).toBe(false);

    wrapper.unmount();
  });
});

describe('Name, the label', () => {
  it('says it is the label shown in Connections, and that empty uses the address', async () => {
    const wrapper = await open('gmail');

    const hint = fieldHint('Name (optional)');
    expect(hint).toContain('label shown in Connections');
    expect(hint).toContain('Leave empty to use the email address');

    wrapper.unmount();
  });
});
