// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS > ADD CONNECTION, A MAILBOX WITH AN APP PASSWORD: tiles Gmail, iCloud, Yahoo and
// Other, read from the Host's presets. Choosing one fills the IMAP and SMTP server, port and security
// (editable; Other leaves them empty to type); the email address, username and app password are
// typed. Save runs the Host's login test and shows its one sentence - connected, or what to fix. The
// app password is write-only: never prefilled, never shown back; a mailbox says only "set". A mailbox
// whose login was refused later shows the login's sentence and an Update password action.
//
// THE MOCK IS OF `api/client`, in the shapes imap-connection-api.md gives. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  addMailbox,
  updateMailboxPassword,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  addMailbox: vi.fn(),
  updateMailboxPassword: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  addMailbox,
  updateMailboxPassword,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { Connection, MailboxPreset } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const presets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'icloud', name: 'iCloud', imap: { host: 'imap.mail.me.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.me.com', port: 587, security: 'STARTTLS' } },
  { id: 'yahoo', name: 'Yahoo', imap: { host: 'imap.mail.yahoo.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.yahoo.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

const appPassword = 'abcd efgh ijkl mnop';

function mailbox(connection: Partial<Connection> & Pick<Connection, 'id'>): Connection {
  return hostConnection({
    provider: 'imap',
    providerKind: 'imap',
    kind: 'imap',
    name: 'Work mail',
    account: 'me@example.com',
    username: 'me@example.com',
    imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' },
    smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' },
    preset: 'gmail',
    passwordSet: true,
    scopes: [],
    refreshedAt: '2026-10-07T15:00:00Z',
    ...connection,
  });
}

const saved = mailbox({ id: 'conn-mail' });

const refusedSentence = 'The server refused the password. For Gmail, make an app password: it needs 2-Step Verification.';

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, addMailbox, updateMailboxPassword]) {
    mock.mockReset();
  }
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft' })]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue(presets);
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

/** Opens Add connection from Admin, Connections. */
async function openAdd() {
  const wrapper = await mountDialog(ConnectionsDialog);
  await settle();
  button('Add connection').click();
  await settle();
  return wrapper;
}

/** Clicks a mailbox tile: Gmail, iCloud, Yahoo or Other. */
async function choosePreset(id: string) {
  const tile = bodyFind(`[data-mailbox-preset="${id}"]`);
  if (!tile) throw new Error(`no mailbox tile "${id}"`);
  tile.click();
  await settle();
}

describe('Add connection, a mailbox with an app password', () => {
  it('offers Gmail, iCloud, Yahoo and Other as tiles, in the Host order', async () => {
    const wrapper = await openAdd();

    const tiles = [...document.body.querySelectorAll('[data-mailbox-preset] [data-tile-name]')].map((tile) => tile.textContent?.trim());
    expect(tiles).toEqual(['Gmail', 'iCloud', 'Yahoo', 'Other']);

    wrapper.unmount();
  });

  it('fills the IMAP and SMTP server, port and security from the chosen preset, and they stay editable', async () => {
    const wrapper = await openAdd();

    await choosePreset('gmail');
    expect(field('IMAP server').value).toBe('imap.gmail.com');
    expect(field('IMAP port').value).toBe('993');
    expect(select(wrapper, 'IMAP security').props('modelValue')).toBe('TLS');
    expect(field('SMTP server').value).toBe('smtp.gmail.com');
    expect(field('SMTP port').value).toBe('465');
    expect(select(wrapper, 'SMTP security').props('modelValue')).toBe('TLS');

    await choosePreset('icloud');
    expect(field('IMAP server').value).toBe('imap.mail.me.com');
    expect(field('SMTP server').value).toBe('smtp.mail.me.com');
    expect(field('SMTP port').value).toBe('587');
    expect(select(wrapper, 'SMTP security').props('modelValue')).toBe('STARTTLS');

    await type('IMAP server', 'imap.example.com');
    expect(field('IMAP server').value).toBe('imap.example.com');

    await choosePreset('other');
    for (const label of ['IMAP server', 'IMAP port', 'SMTP server', 'SMTP port']) expect(field(label).value).toBe('');

    wrapper.unmount();
  });

  it('runs the login test on Save and shows its sentence, sending what was typed', async () => {
    addMailbox.mockResolvedValue({ sentence: 'Connected: 1,240 messages in Inbox', connection: saved });
    const wrapper = await openAdd();

    await choosePreset('gmail');
    expect(isDisabled('Save')).toBe(true);
    await type('Email address', 'me@example.com');
    await type('App password', appPassword);
    await type('Name (optional)', 'Work mail');
    listConnections.mockResolvedValue([saved]);
    button('Save').click();
    await settle();

    expect(addMailbox).toHaveBeenCalledWith({
      preset: 'gmail',
      name: 'Work mail',
      account: 'me@example.com',
      username: null,
      password: appPassword,
      imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' },
      smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' },
    });
    expect(bodyFind('[data-mailbox-sentence]')!.textContent?.trim()).toBe('Connected: 1,240 messages in Inbox');
    expect(bodyFind('[data-mailbox-sentence]')!.classList.contains('text-positive')).toBe(true);
    // The list behind is read again, with the new mailbox on it.
    expect(listConnections).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('shows what to fix when the login is refused, and Save can be tried again', async () => {
    addMailbox.mockRejectedValue(Object.assign(new Error(refusedSentence), { status: 422 }));
    const wrapper = await openAdd();

    await choosePreset('gmail');
    await type('Email address', 'me@example.com');
    await type('App password', 'wrong password');
    button('Save').click();
    await settle();

    expect(bodyFind('[data-mailbox-sentence]')!.textContent?.trim()).toBe(refusedSentence);
    expect(bodyFind('[data-mailbox-sentence]')!.classList.contains('text-negative')).toBe(true);
    expect(isDisabled('Save')).toBe(false);
    expect(listConnections).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });

  it('keeps the app password write-only: a password field, empty on open, never shown back', async () => {
    addMailbox.mockResolvedValue({ sentence: 'Connected: 3 messages in Inbox', connection: saved });
    const wrapper = await openAdd();

    await choosePreset('gmail');
    expect(field('App password').type).toBe('password');
    expect(field('App password').value).toBe('');
    await type('Email address', 'me@example.com');
    await type('App password', appPassword);
    listConnections.mockResolvedValue([saved]);
    button('Save').click();
    await settle();

    // Saved: the password is gone from the form and from the page; the mailbox says only "set".
    expect(document.body.innerHTML).not.toContain(appPassword);
    button('Finish').click();
    await settle();
    expect(bodyFind('[data-connection="conn-mail"] [data-connection-password]')!.textContent).toBe('set');
    expect(document.body.innerHTML).not.toContain(appPassword);

    // Opened again, the field starts empty.
    button('Add connection').click();
    await settle();
    await choosePreset('gmail');
    expect(field('App password').value).toBe('');

    wrapper.unmount();
  });
});

describe('Admin, Connections, a mailbox', () => {
  it('shows its servers and "set" for the password, never a value', async () => {
    listConnections.mockResolvedValue([saved]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    const tile = bodyFind('[data-connection="conn-mail"]')!;
    expect(tile.querySelector('[data-connection-provider]')!.textContent).toBe('Mailbox (IMAP)');
    expect(tile.querySelector('[data-connection-servers]')!.textContent).toContain('imap.gmail.com:993 TLS');
    expect(tile.querySelector('[data-connection-servers]')!.textContent).toContain('smtp.gmail.com:465 TLS');
    expect(tile.querySelector('[data-connection-password]')!.textContent).toBe('set');

    wrapper.unmount();
  });

  it('shows a needs-reconnect mailbox its sentence and Update password, which clears it after a good login', async () => {
    const stale = mailbox({ id: 'conn-mail', status: 'needs-reconnect', statusReason: refusedSentence });
    listConnections.mockResolvedValue([stale]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    const tile = bodyFind('[data-connection="conn-mail"]')!;
    expect(tile.querySelector('[data-connection-reason]')!.textContent).toContain(refusedSentence);
    expect(tile.querySelector('[aria-label="Reconnect Work mail"]')).toBeNull();
    (tile.querySelector('[aria-label="Update password Work mail"]') as HTMLElement).click();
    await settle();

    expect(field('New app password').type).toBe('password');
    expect(field('New app password').value).toBe('');

    // A refused one says what to fix and changes nothing.
    updateMailboxPassword.mockRejectedValueOnce(Object.assign(new Error(refusedSentence), { status: 422 }));
    await type('New app password', 'still wrong');
    button('Update password').click();
    await settle();
    expect(bodyFind('[data-password-problem]')!.textContent?.trim()).toBe(refusedSentence);

    updateMailboxPassword.mockResolvedValueOnce({ sentence: 'Connected: 12 messages in Inbox', connection: saved });
    listConnections.mockResolvedValue([saved]);
    await type('New app password', appPassword);
    button('Update password').click();
    await settle();

    expect(updateMailboxPassword).toHaveBeenLastCalledWith('conn-mail', appPassword);
    expect(bodyFind('[data-connection="conn-mail"] [data-row-notice]')!.textContent?.trim()).toBe('Connected: 12 messages in Inbox');
    expect(bodyFind('[data-connection="conn-mail"] [data-connection-status]')!.textContent).toBe('ok');
    expect(bodyFind('[data-connection="conn-mail"] [data-connection-reason]')).toBeNull();
    expect(document.body.innerHTML).not.toContain(appPassword);
    expect(bodyText()).not.toContain(appPassword);

    wrapper.unmount();
  });
});

// THE APP-PASSWORD SHAPE WARNING: a password that does not look like the provider's app password gets
// a plain warning beside the field, never a refusal - Save and Update password still go through, and
// the Host's login test decides. Only a shape the provider publishes warns: Google's (16 characters,
// spaces and hyphens ignored). Apple and Yahoo publish none, so iCloud and Yahoo never warn, nor Other.
const ordinary = 'hunter2pwd';
const sixteenWithHyphens = 'abcd-efgh-ijkl-mnop';

const warning = () => bodyFind('[data-app-password-warning]');

describe('A password that is not app-password shaped', () => {
  it('on Add connection, Gmail, a 10-character password is warned about and Save still goes through', async () => {
    addMailbox.mockResolvedValue({ sentence: 'Connected: 3 messages in Inbox', connection: saved });
    const wrapper = await openAdd();

    await choosePreset('gmail');
    await type('Email address', 'me@example.com');
    await type('App password', ordinary);

    expect(warning()).not.toBeNull();
    expect(warning()!.textContent).toContain('looks like an ordinary account password');
    expect(warning()!.textContent).toContain('Gmail wants an app password');
    expect(warning()!.textContent).toContain('For Gmail, make an app password: it needs 2-Step Verification.');
    expect(warning()!.classList.contains('text-negative')).toBe(false);
    expect(isDisabled('Save')).toBe(false);

    button('Save').click();
    await settle();
    expect(addMailbox).toHaveBeenCalledWith(expect.objectContaining({ preset: 'gmail', password: ordinary }));
    // The warning never repeats the password.
    expect(bodyText()).not.toContain(ordinary);

    wrapper.unmount();
  });

  it('on Add connection, Gmail, 16 letters with hyphens is not warned about', async () => {
    const wrapper = await openAdd();

    await choosePreset('gmail');
    await type('App password', sixteenWithHyphens);
    expect(warning()).toBeNull();
    await type('App password', appPassword);
    expect(warning()).toBeNull();

    wrapper.unmount();
  });

  it.each(['other', 'icloud', 'yahoo'])('on Add connection, %s is never warned about, whatever is typed', async (preset) => {
    const wrapper = await openAdd();

    await choosePreset(preset);
    for (const typed of [ordinary, 'x', sixteenWithHyphens, 'a much longer ordinary password!']) {
      await type('App password', typed);
      expect(warning()).toBeNull();
    }

    wrapper.unmount();
  });

  async function openUpdate(connection: Connection) {
    listConnections.mockResolvedValue([connection]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();
    (bodyFind(`[aria-label="Update password ${connection.name}"]`) as HTMLElement).click();
    await settle();
    return wrapper;
  }

  it('on Update password, a Gmail mailbox, a 10-character password is warned about and the update still goes through', async () => {
    updateMailboxPassword.mockResolvedValue({ sentence: 'Connected: 3 messages in Inbox', connection: saved });
    const wrapper = await openUpdate(mailbox({ id: 'conn-mail', status: 'needs-reconnect', statusReason: refusedSentence }));

    await type('New app password', ordinary);

    expect(warning()).not.toBeNull();
    expect(warning()!.textContent).toContain('looks like an ordinary account password');
    expect(warning()!.textContent).toContain('Gmail wants an app password');
    expect(isDisabled('Update password')).toBe(false);

    button('Update password').click();
    await settle();
    expect(updateMailboxPassword).toHaveBeenLastCalledWith('conn-mail', ordinary);

    wrapper.unmount();
  });

  it('on Update password, a Gmail mailbox, 16 letters with hyphens is not warned about', async () => {
    const wrapper = await openUpdate(mailbox({ id: 'conn-mail' }));

    await type('New app password', sixteenWithHyphens);
    expect(warning()).toBeNull();

    wrapper.unmount();
  });

  it.each(['other', 'icloud', 'yahoo', null])('on Update password, a %s mailbox is never warned about, whatever is typed', async (preset) => {
    const wrapper = await openUpdate(mailbox({ id: 'conn-mail', preset }));

    for (const typed of [ordinary, 'x', sixteenWithHyphens]) {
      await type('New app password', typed);
      expect(warning()).toBeNull();
    }

    wrapper.unmount();
  });
});
