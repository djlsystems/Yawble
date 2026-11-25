// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import ConnectionDialog from '../../src/renderer/src/components/ConnectionDialog.vue';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import type { Connection, YawbleApi } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const f = (key: string, label: string, shown: string | null, secret = false) => ({ key, label, secret, shown, set: shown !== null });
const account = (o: Partial<Connection> = {}): Connection => ({
  id: 8, name: 'pat@gmail.com', kind: 'email',
  fields: [
    f('address', 'Email address', 'pat@gmail.com'), f('imapHost', 'IMAP server', 'imap.gmail.com'), f('imapPort', 'IMAP port', '993'),
    f('smtpHost', 'SMTP server', 'smtp.gmail.com'), f('smtpPort', 'SMTP port', '465'), f('user', 'User name', 'pat@gmail.com'),
    f('password', 'Password', '••••••', true),
  ],
  status: 'untested', statusReason: null, testedAt: null, usedBy: [], ...o,
});

describe('Email account connection', () => {
  it('NewEmailAccountWithTheGmailPreset', async () => {
    const { api, calls } = fakeApi({ 'integrations.connectionSave': () => account() });
    const w = mount(ConnectionDialog, { props: { api } });
    expect(w.find('[data-test=connection-kind]').text()).toContain('Email account');
    await w.find('[data-test=connection-kind-email]').setValue(true);
    expect(w.find('[data-test=connection-field-0]').exists()).toBe(false);
    expect(w.find('[data-test=email-gmail-note]').exists()).toBe(false);

    await w.find('[data-test=email-preset-gmail]').trigger('click');
    expect((w.find('[data-test=email-imap-host]').element as HTMLInputElement).value).toBe('imap.gmail.com');
    expect((w.find('[data-test=email-imap-port]').element as HTMLInputElement).value).toBe('993');
    expect((w.find('[data-test=email-smtp-host]').element as HTMLInputElement).value).toBe('smtp.gmail.com');
    expect((w.find('[data-test=email-smtp-port]').element as HTMLInputElement).value).toBe('465');
    expect(w.find('[data-test=email-gmail-note]').text()).toBe('Use a Google app password; it needs 2-Step Verification');
    expect((w.find('[data-test=email-password]').element as HTMLInputElement).type).toBe('password');

    await w.find('[data-test=email-address]').setValue('pat@gmail.com');
    await w.find('[data-test=email-password]').setValue('abcd efgh ijkl mnop');
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{
      method: 'integrations.connectionSave',
      params: {
        kind: 'email', address: 'pat@gmail.com', imapHost: 'imap.gmail.com', imapPort: 993, smtpHost: 'smtp.gmail.com', smtpPort: 465,
        user: '', password: 'abcd efgh ijkl mnop',
      },
    }]);
    expect(w.emitted('saved')).toBeTruthy();
    expect(w.emitted('close')).toBeTruthy();
  });

  it('NewEmailAccountNeedsAPasswordAndPorts', async () => {
    const { api, calls } = fakeApi({});
    const w = mount(ConnectionDialog, { props: { api, startKind: 'email' } });
    await w.find('[data-test=email-address]').setValue('pat@example.com');
    await w.find('[data-test=email-imap-host]').setValue('imap.example.com');
    await w.find('[data-test=email-smtp-host]').setValue('smtp.example.com');
    await w.find('[data-test=email-imap-port]').setValue('abc');
    await w.find('[data-test=email-smtp-port]').setValue('587');
    await w.find('[data-test=connection-save]').trigger('click');
    expect(w.find('[data-test=connection-error]').text()).toContain('ports');
    await w.find('[data-test=email-imap-port]').setValue('143');
    await w.find('[data-test=connection-save]').trigger('click');
    expect(w.find('[data-test=connection-error]').text()).toBe('Give the password.');
    expect(calls).toEqual([]);
  });

  it('EditKeepsThePasswordWhenLeftEmpty', async () => {
    const { api, calls } = fakeApi({ 'integrations.connectionSave': () => account() });
    const w = mount(ConnectionDialog, { props: { api, connection: account() } });
    expect(w.find('[data-test=connection-kind]').exists()).toBe(false);
    expect((w.find('[data-test=email-address]').element as HTMLInputElement).value).toBe('pat@gmail.com');
    expect((w.find('[data-test=email-user]').element as HTMLInputElement).value).toBe(''); // the address is the default
    expect((w.find('[data-test=email-password]').element as HTMLInputElement).value).toBe('');
    expect(w.find('[data-test=email-password]').attributes('placeholder')).toBe('saved; leave empty to keep it');
    expect(w.find('[data-test=email-gmail-note]').exists()).toBe(true);
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    expect(calls[0].params).toEqual({
      kind: 'email', id: 8, name: 'pat@gmail.com', address: 'pat@gmail.com', imapHost: 'imap.gmail.com', imapPort: 993,
      smtpHost: 'smtp.gmail.com', smtpPort: 465, user: '',
    });
  });

  it('PanelNamesTheKindAndTestsIt', async () => {
    const { api, calls } = fakeApi({
      'integrations.connections': () => [account()],
      'integrations.connectionTest': () => ({ status: 'works', reason: 'signed in', changed: true }),
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=connection-8] [data-test=connection-kind]').text()).toBe('Email account');
    expect(w.find('[data-test=connection-8] [data-test=connection-fields]').text()).not.toContain('abcd');
    await w.find('[data-test=connection-test-8]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.connectionTest' && c.params.id === 8)).toBe(true);
  });
});
