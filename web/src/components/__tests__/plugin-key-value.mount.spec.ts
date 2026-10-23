// @vitest-environment happy-dom
//
// A PLUGIN MEMBER'S SECRETS ARE ITS OWN, typed as values on its own settings screen. Add member (a
// hire) and Member settings (after hire) show one box per secret field - a password box for a masked
// field, a text box showing the saved value for one the manifest declares `masked: false` - labelled
// by the manifest's `label` or the field's name in words, and never a key name (only "As JSON" shows
// a bound one). Every setting is on the one screen in the manifest's order, with no Advanced toggle,
// no info icon and no Default line; the plugin's description shows its first paragraph and a
// Documentation link to its marketplace page, or the whole description and no link when the catalog
// does not list it. A typed value rides the hire and the Save as `secretValues`.
//
// THE MOCK IS OF `api/client`, in the Host's shapes (`docs` B006R API contract): no route is reached.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listCatalog, listPlugins, addMember, getMember, getPluginSettings, savePluginSettings, updateMember } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  getMember: vi.fn(),
  getPluginSettings: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type InstalledPlugin,
  type PluginMemberSettings,
  type PluginSecretState,
  type Team,
  type TeamId,
} from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostField, hostList, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const MarketplacePage = 'https://catalog.example.test/marketplace/sample-dns';

/** A generic fixture plugin: an enum, a number with a default, an `advanced` text field, a masked
 *  secret with no label and an unmasked one with a label. */
const sampleDns: InstalledPlugin = hostPlugin({
  id: 'sample-dns',
  name: 'Sample DNS',
  description: "Keeps a zone's records in step.\n\nSetting it up: create an account key, allow this address, then paste the key.",
  config: {
    mode: hostField({ type: 'string', description: 'dryRun changes nothing.', default: 'dryRun', enum: ['dryRun', 'apply'], setBy: 'person' }),
    ttl: hostField({ type: 'number', description: 'Seconds a record is cached.', default: 300 }),
    clientIp: hostField({ type: 'string', description: 'The address the account allows.', advanced: true, required: true }),
  },
  secrets: {
    apiKey: hostSecret({ description: "The account's key.", required: true }),
    apiUser: hostSecret({ description: 'The account name.', required: true, label: 'Account user', masked: false }),
  },
  marketplaceUrl: MarketplacePage,
});

const secretState = (over: Partial<PluginSecretState> & Pick<PluginSecretState, 'field'>): PluginSecretState => ({
  label: null,
  masked: true,
  required: true,
  needed: true,
  set: false,
  setBy: null,
  setAt: null,
  source: null,
  key: null,
  ...over,
});

const savedStates: PluginSecretState[] = [
  secretState({ field: 'apiKey', set: true, setBy: 'person@example.com', setAt: '2026-10-23T10:00:00Z', source: 'member', key: 'LEGACY_DNS_KEY' }),
  secretState({ field: 'apiUser', label: 'Account user', masked: false, set: true, setBy: 'person@example.com', setAt: '2026-10-23T10:00:00Z', source: 'member', value: 'acct-one' }),
];

/** The member's settings read: values set on the member, and a leftover key binding on apiKey. */
const stored: PluginMemberSettings = hostSettings(sampleDns, {
  team: 'alpha',
  member: 'DnsOne',
  config: { clientIp: '203.0.113.7' },
  secrets: { apiKey: 'LEGACY_DNS_KEY' },
  secretStates: savedStates,
  marketplaceUrl: MarketplacePage,
  description: sampleDns.description,
});

const dnsOne = {
  team: asTeamId('alpha'),
  id: asMemberId('DnsOne'),
  name: 'DnsOne',
  agent: 'plugin:sample-dns',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

beforeEach(() => {
  for (const mock of [listCatalog, listPlugins, addMember, getMember, getPluginSettings, savePluginSettings, updateMember]) mock.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([sampleDns]));
  addMember.mockResolvedValue({ name: 'DnsOne' });
  getPluginSettings.mockResolvedValue(structuredClone(stored));
  savePluginSettings.mockResolvedValue(undefined);
  updateMember.mockResolvedValue({ ...dnsOne });
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function mountHire() {
  const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
  select(wrapper, 'Agent').vm.$emit('update:modelValue', 'plugin:sample-dns');
  await settle();
  return wrapper;
}

async function mountSettings() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [dnsOne] } as unknown as Team];
  board.managerName = asMemberId('Manager');
  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: dnsOne }, { pinia: false });
  await settle();
  return wrapper;
}

const settingsSection = (selector: string) => bodyFind(selector)!;
const shownSettings = (within: HTMLElement) =>
  [...within.querySelectorAll('[data-setting], [data-secret-setting]')].map(
    (element) => element.getAttribute('data-setting') ?? element.getAttribute('data-secret-setting'),
  );
const secretLine = (name: string) => bodyFind(`[data-secret-field="${name}"]`)?.textContent?.trim();

/** What each screen must show of the form itself, whichever dialog holds it. */
function expectOneScreen(section: HTMLElement) {
  // Every setting, in the manifest's order, the `advanced` one included: no toggle.
  expect(shownSettings(section)).toEqual(['mode', 'ttl', 'clientIp', 'apiKey', 'apiUser']);
  expect(section.querySelector('[data-advanced-toggle]')).toBeNull();
  expect(section.textContent).not.toContain('Advanced settings');

  // No info icon, no Default line, no person-only line, and no field description printed.
  expect(section.querySelector('[data-help]')).toBeNull();
  expect(section.textContent).not.toContain('Default:');
  expect(section.textContent).not.toContain('Set by a person only');
  expect(section.textContent).not.toContain('Seconds a record is cached.');

  // A default is its box's placeholder; Reset to default stays; Required stays printed.
  expect(field('TTL').getAttribute('placeholder')).toBe('300');
  expect(section.querySelector('[aria-label="Reset TTL to default"]')).not.toBeNull();
  expect(section.querySelector('[data-setting="clientIp"] [data-required]')?.textContent).toBe('Required');
  expect(section.querySelector('[data-secret-setting="apiKey"] [data-required]')?.textContent).toBe('Required');

  // Labelled by the manifest's label, else the field's name in words.
  expect(field('Client IP')).toBeTruthy();
  expect(field('API key')).toBeTruthy();
  expect(field('Account user')).toBeTruthy();

  // No key-name box and no key name: a masked field is a password box, an unmasked one a text box.
  expect(field('API key').getAttribute('type')).toBe('password');
  expect(field('Account user').getAttribute('type')).toBe('text');
  expect(section.textContent).not.toContain('key name');
  expect(section.textContent).not.toContain('LEGACY_DNS_KEY');
}

describe('Add member, a plugin with secrets', () => {
  it('shows every setting on one screen, a password box for a masked secret and a text box for an unmasked one, and no key name', async () => {
    const wrapper = await mountHire();

    expectOneScreen(settingsSection('[data-plugin-hire]'));
    expect(field('API key').value).toBe('');
    expect(field('Account user').value).toBe('');
    expect(secretLine('apiKey')).toBe('Not set.');

    wrapper.unmount();
  });

  it("shows the description's first paragraph and a Documentation link opening the marketplace page in a new tab", async () => {
    const wrapper = await mountHire();

    const about = bodyFind('[data-plugin-about]')!;
    expect(about.querySelector('[data-plugin-description]')?.textContent).toBe("Keeps a zone's records in step.");
    expect(about.textContent).not.toContain('Setting it up');
    const link = about.querySelector<HTMLAnchorElement>('[data-plugin-documentation]')!;
    expect(link.textContent).toBe('Documentation');
    expect(link.getAttribute('href')).toBe(MarketplacePage);
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');

    wrapper.unmount();
  });

  it('shows the whole description and no Documentation link when the catalog does not list the plugin', async () => {
    listPlugins.mockResolvedValue(hostList([{ ...sampleDns, marketplaceUrl: null }]));
    const wrapper = await mountHire();

    const about = bodyFind('[data-plugin-about]')!;
    expect(about.querySelector('[data-plugin-description]')?.textContent).toContain("Keeps a zone's records in step.");
    expect(about.querySelector('[data-plugin-description]')?.textContent).toContain('Setting it up: create an account key');
    expect(about.querySelector('[data-plugin-documentation]')).toBeNull();
    expect(bodyText()).not.toContain('Documentation');

    wrapper.unmount();
  });

  it('holds Add member until each required secret has a value, then sends the typed values with the hire', async () => {
    const wrapper = await mountHire();

    await type('Member name', 'DnsOne');
    await type('Client IP', '203.0.113.7');
    expect(isDisabled('Add member')).toBe(true);

    await type('API key', 'key-for-account-one');
    await type('Account user', 'acct-one');
    expect(secretLine('apiKey')).toBe('Saved when you press Add member.');
    expect(isDisabled('Add member')).toBe(false);

    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledTimes(1);
    expect(addMember.mock.calls[0]).toEqual([
      'alpha',
      'DnsOne',
      'plugin:sample-dns',
      {
        config: { clientIp: '203.0.113.7' },
        secrets: {},
        secretValues: { apiKey: 'key-for-account-one', apiUser: 'acct-one' },
      },
    ]);

    wrapper.unmount();
  });

  it("keeps a refused hire's sentence in the dialog, and never shows the value typed", async () => {
    addMember.mockRejectedValue(Object.assign(new Error('`apiKey` is too short.'), { status: 400 }));
    const wrapper = await mountHire();

    await type('Member name', 'DnsOne');
    await type('Client IP', '203.0.113.7');
    await type('API key', 'k-1');
    await type('Account user', 'acct-one');
    button('Add member').click();
    await settle();

    expect(bodyFind('.q-banner')?.textContent).toContain('`apiKey` is too short.');
    expect(bodyText()).not.toContain('k-1');

    wrapper.unmount();
  });
});

describe('Member settings, a plugin member with secrets', () => {
  it('shows every setting on one screen, a password box for a masked secret and a text box for an unmasked one, and no key name', async () => {
    const wrapper = await mountSettings();

    expectOneScreen(settingsSection('[data-member-plugin-settings]'));

    // Masked: starts empty and says when it was saved and by whom - never the value.
    expect(field('API key').value).toBe('');
    expect(secretLine('apiKey')).toMatch(/^Saved by person@example\.com on /);
    // Unmasked: the saved value, as text.
    expect(field('Account user').value).toBe('acct-one');

    wrapper.unmount();
  });

  it('shows the bound key name only in As JSON', async () => {
    const wrapper = await mountSettings();

    expect(bodyText()).not.toContain('LEGACY_DNS_KEY');
    button('As JSON').click();
    await settle();
    expect(bodyFind('[data-settings-json]')?.textContent).toContain('LEGACY_DNS_KEY');

    wrapper.unmount();
  });

  it("shows the description's first paragraph and a Documentation link opening the marketplace page in a new tab", async () => {
    const wrapper = await mountSettings();

    const about = bodyFind('[data-member-plugin-settings] [data-plugin-about]')!;
    expect(about.querySelector('[data-plugin-description]')?.textContent).toBe("Keeps a zone's records in step.");
    expect(about.textContent).not.toContain('Setting it up');
    const link = about.querySelector('[data-plugin-documentation]') as HTMLAnchorElement | null;
    expect(link?.textContent).toBe('Documentation');
    expect(link?.getAttribute('href')).toBe(MarketplacePage);
    expect(link?.getAttribute('target')).toBe('_blank');

    wrapper.unmount();
  });

  it('shows the whole description and no Documentation link when the catalog does not list the plugin', async () => {
    getPluginSettings.mockResolvedValue({ ...structuredClone(stored), marketplaceUrl: null });
    const wrapper = await mountSettings();

    const description = bodyFind('[data-member-plugin-settings] [data-plugin-description]')!;
    expect(description.textContent).toContain("Keeps a zone's records in step.");
    expect(description.textContent).toContain('Setting it up: create an account key, allow this address, then paste the key.');
    expect(bodyFind('[data-plugin-documentation]')).toBeNull();
    expect(bodyText()).not.toContain('Documentation');

    wrapper.unmount();
  });

  it('shows no description and no empty block when the plugin has none, and no link when the catalog does not list it', async () => {
    getPluginSettings.mockResolvedValue({ ...structuredClone(stored), description: null });
    const listed = await mountSettings();

    expect(bodyFind('[data-member-plugin-settings] [data-plugin-description]')).toBeNull();
    expect(bodyFind('[data-member-plugin-settings] [data-plugin-about]')?.textContent?.trim()).toBe('Documentation');
    listed.unmount();
    resetBody();

    getPluginSettings.mockResolvedValue({ ...structuredClone(stored), description: null, marketplaceUrl: null });
    const unlisted = await mountSettings();

    expect(bodyFind('[data-member-plugin-settings]')).not.toBeNull();
    expect(bodyFind('[data-plugin-about]')).toBeNull();
    expect(bodyFind('[data-plugin-description]')).toBeNull();

    unlisted.unmount();
  });

  it('says "Not set." under a field with no value, and holds Save while a required one has none', async () => {
    getPluginSettings.mockResolvedValue({
      ...structuredClone(stored),
      secrets: {},
      secretStates: [secretState({ field: 'apiKey' }), savedStates[1]!],
    });
    const wrapper = await mountSettings();

    expect(secretLine('apiKey')).toBe('Not set.');
    expect(bodyFind('[data-secret-field="apiKey"]')?.className).toContain('text-warning');
    expect(isDisabled('Save')).toBe(true);

    await type('API key', 'key-for-account-one');
    expect(isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it('sends a typed value with Save as the member\'s own, replacing the saved one, and says what the Host answered', async () => {
    savePluginSettings.mockResolvedValue({
      ...structuredClone(stored),
      secretStates: [
        secretState({ field: 'apiKey', set: true, setBy: 'other@example.com', setAt: '2026-10-23T12:00:00Z', source: 'member' }),
        savedStates[1]!,
      ],
    });
    const wrapper = await mountSettings();

    await type('API key', 'key-for-account-two');
    expect(secretLine('apiKey')).toBe('Saved when you press Save.');

    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    // No `secrets`: the Host keeps the key binding. The value rides the same request.
    expect(savePluginSettings.mock.calls[0]).toEqual([
      'alpha',
      'DnsOne',
      { config: { clientIp: '203.0.113.7' }, secretValues: { apiKey: 'key-for-account-two' } },
    ]);

    wrapper.unmount();
  });

  it('sends an edited unmasked value with Save, and nothing for one set back to what was saved', async () => {
    const wrapper = await mountSettings();

    await type('Account user', 'acct-two');
    await type('Account user', 'acct-one');
    button('Save').click();
    await settle();
    expect(savePluginSettings).not.toHaveBeenCalled();

    wrapper.unmount();

    const again = await mountSettings();
    await type('Account user', 'acct-two');
    button('Save').click();
    await settle();
    expect(savePluginSettings.mock.calls[0]![2]).toEqual({ config: { clientIp: '203.0.113.7' }, secretValues: { apiUser: 'acct-two' } });

    again.unmount();
  });

  it('Clear removes the saved value on Save, and Keep takes it back', async () => {
    const wrapper = await mountSettings();

    button('Clear').click();
    await settle();
    expect(secretLine('apiKey')).toBe('Cleared when you press Save.');
    button('Keep').click();
    await settle();
    expect(secretLine('apiKey')).toMatch(/^Saved by /);

    // The leftover key binding is what a cleared value falls back to, so Save is not held.
    button('Clear').click();
    await settle();
    expect(isDisabled('Save')).toBe(false);
    button('Save').click();
    await settle();

    expect(savePluginSettings.mock.calls[0]![2]).toEqual({ config: { clientIp: '203.0.113.7' }, secretValues: { apiKey: null } });

    wrapper.unmount();
  });
});
