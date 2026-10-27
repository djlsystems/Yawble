// @vitest-environment happy-dom
//
// A PLUGIN'S NAMED SECRETS ARE ONE TABLE ON EVERY PLUGIN SETTINGS SCREEN. A manifest's
// `namedSecrets` block (`{ label, description, max }`) shows as `NamedSecretsTable` on Add member,
// Member settings, the solution wizard's secrets step and the panel's All settings: Name in the first
// column, Value in the second as a password box that starts empty and says who saved it and when, Add
// for a new row, Remove per saved row held until the button with Keep to take it back. The block's
// label and description head it, with help behind `FieldHelp`. A saved value is never read back or
// shown. What is typed rides the screen's own request as `namedSecretValues` (name -> value, null
// removes); the wizard's rides Install as package member -> name -> value. A plugin that declares no
// block shows none of it.
//
// THE FAKE IS THE HOST AT `fetch`, in its shapes: the settings read carries the block beside
// `secretFields`, and `GET .../named-secrets` answers `{ team, member, namedSecrets: [{ name, setBy,
// setAt }] }` - never a value - so the paths and bodies asserted are the ones sent.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import SolutionPanel from '../SolutionPanel.vue';
import SolutionWizard from '../SolutionWizard.vue';
import { useConsoleStore } from '../../stores/console';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type InstalledPlugin,
  type PluginNamedSecrets,
  type Team,
  type TeamId,
} from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, isDisabled, settle, type } from '../../test/formProbe';
import { hostList, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
import { Folder, fakeHost, hostPlan, reply, sent, steps, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';
import { openSection, panelRoutes, scoutSettings } from '../../test/solutionPanelFixtures';

const Block: PluginNamedSecrets = {
  label: 'App secrets',
  description: 'Each secret has a name you choose; the team refers to it by that name.',
  max: 3,
};

/** A plugin with one secret field and the block. */
const cloud: InstalledPlugin = hostPlugin({
  id: 'sample-cloud',
  name: 'Sample Cloud',
  secrets: { apiKey: hostSecret({ description: "The account's key.", label: 'API key' }) },
  namedSecrets: Block,
});

/** The same plugin without the block: nothing of the table shows. */
const plain: InstalledPlugin = hostPlugin({
  id: 'sample-plain',
  name: 'Sample Plain',
  secrets: { apiKey: hostSecret({ description: "The account's key.", label: 'API key' }) },
});

/** What the member holds: names, who and when - and never a value, which the Host does not send. */
const Held = [
  { name: 'MicrosoftClientSecret', setBy: 'person@example.com', setAt: '2026-10-23T10:00:00Z' },
  { name: 'GoogleClientSecret', setBy: 'other@example.com', setAt: '2026-10-24T11:00:00Z' },
];

/** A value typed on a screen, which must never be shown back. */
const Typed = 'typed-value-0123456789';

let calls: Call[] = [];

function serve(routes: Route[]) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(routes, calls)));
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const table = (within = document.body) => within.querySelector<HTMLElement>('[data-named-secrets]');
const row = (name: string) => bodyFind(`[data-named-secret="${name}"]`)!;
const rowState = (name: string) => row(name).querySelector('[data-named-secret-state]')?.textContent?.trim();
const added = () => [...document.body.querySelectorAll<HTMLElement>('[data-named-secret-added]')];

async function fill(input: HTMLInputElement, value: string) {
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
}

async function click(element: HTMLElement) {
  element.click();
  await settle();
}

/** Adds a row on the table inside `within`, and types its name and value. */
async function addRow(within: HTMLElement, name: string, value: string) {
  await click(within.querySelector<HTMLElement>('[data-named-secret-add]')!);
  const rows = within.querySelectorAll<HTMLElement>('[data-named-secret-added]');
  const last = rows[rows.length - 1]!;
  await fill(last.querySelector<HTMLInputElement>('[data-named-secret-name-input] input, input[data-named-secret-name-input]')!, name);
  await fill(last.querySelector<HTMLInputElement>('input[data-named-secret-value]')!, value);
}

/** Every value any input on the page holds, and the page's text: where a value could be shown. */
const shown = () => [bodyText(), ...[...document.body.querySelectorAll('input')].map((input) => input.value)].join('\n');

function expectHead(within: HTMLElement) {
  expect(within.querySelector('[data-named-secrets-label]')?.textContent).toBe('App secrets');
  expect(within.querySelector('[data-named-secrets-description]')?.textContent).toBe(Block.description);
  expect(within.querySelector('[data-help]')).not.toBeNull();
  const heads = [...within.querySelectorAll('th')].map((cell) => cell.textContent?.trim());
  expect(heads.slice(0, 2)).toEqual(['Name', 'Value']);
}

// --- Add member ---------------------------------------------------------------------------------

function hireRoutes(plugins: InstalledPlugin[]): Route[] {
  return [
    (call) => (call.method === 'GET' && call.url === '/api/agents' ? reply(200, { agents: [{ name: 'claude', mode: 'Headless' }] }) : undefined),
    (call) => (call.method === 'GET' && call.url === '/api/plugins' ? reply(200, hostList(plugins)) : undefined),
    (call) => (call.method === 'POST' && call.url === '/api/teams/alpha/containers' ? reply(200, { name: 'CloudOne' }) : undefined),
  ];
}

async function mountHire(reference: string) {
  const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
  const agent = wrapper.findAllComponents(QSelect).find((select) => select.props('label') === 'Agent')!;
  agent.vm.$emit('update:modelValue', reference);
  await settle();
  return wrapper;
}

describe('Add member: named secrets', () => {
  beforeEach(() => serve(hireRoutes([cloud, plain])));

  it('shows the block as a table headed by its label and description, with help behind FieldHelp, and no row yet', async () => {
    const wrapper = await mountHire('plugin:sample-cloud');

    const section = table()!;
    expect(section).not.toBeNull();
    expectHead(section);
    expect(section.querySelector('[data-named-secrets-empty]')).not.toBeNull();

    wrapper.unmount();
  });

  it('shows nothing of the table for a plugin that declares no block', async () => {
    const wrapper = await mountHire('plugin:sample-plain');

    expect(bodyFind('[data-plugin-hire]')).not.toBeNull();
    expect(table()).toBeNull();
    expect(bodyText()).not.toContain('App secrets');

    wrapper.unmount();
  });

  it('adds a row by Add, holds Add member until it has a good name and a value, and sends it as namedSecretValues', async () => {
    const wrapper = await mountHire('plugin:sample-cloud');
    await type('Member name', 'CloudOne');
    expect(isDisabled('Add member')).toBe(false);

    const section = table()!;
    await click(section.querySelector<HTMLElement>('[data-named-secret-add]')!);
    expect(added()).toHaveLength(1);
    const nameBox = added()[0]!.querySelector<HTMLInputElement>('input[data-named-secret-name-input]')!;
    const valueBox = added()[0]!.querySelector<HTMLInputElement>('input[data-named-secret-value]')!;
    expect(valueBox.getAttribute('type')).toBe('password');

    // A name with no value, a name it cannot have and the plugin's own field are each held.
    await fill(nameBox, 'MicrosoftClientSecret');
    expect(added()[0]!.textContent).toContain('Type its value.');
    expect(isDisabled('Add member')).toBe(true);
    await fill(valueBox, Typed);
    await fill(nameBox, '1bad name');
    expect(added()[0]!.textContent).toContain('is not a name a secret can have');
    expect(isDisabled('Add member')).toBe(true);
    await fill(nameBox, 'APIKEY');
    expect(added()[0]!.textContent).toContain("is the name of one of this plugin's own secrets");
    expect(isDisabled('Add member')).toBe(true);

    await fill(nameBox, 'MicrosoftClientSecret');
    expect(added()[0]!.textContent).toContain('Saved when you press Add member.');
    expect(isDisabled('Add member')).toBe(false);

    // A second row, then dropped again by its Remove: an added row is not saved, so it goes at once.
    await addRow(section, 'Spare', 'spare-value');
    expect(added()).toHaveLength(2);
    await click(added()[1]!.querySelector<HTMLElement>('[data-named-secret-remove]')!);
    expect(added()).toHaveLength(1);

    button('Add member').click();
    await settle();

    const hires = sent(calls, 'POST', '/api/teams/alpha/containers');
    expect(hires).toHaveLength(1);
    expect(hires[0]!.body).toEqual({
      name: 'CloudOne',
      agent: 'plugin:sample-cloud',
      config: {},
      secrets: {},
      namedSecretValues: { MicrosoftClientSecret: Typed },
    });

    wrapper.unmount();
  });

  it('refuses a name twice in any case, and stops Add at the block\'s max', async () => {
    const wrapper = await mountHire('plugin:sample-cloud');
    const section = table()!;

    await addRow(section, 'ApiToken', 'value-one');
    await addRow(section, 'apitoken', 'value-two');
    expect(added()[1]!.textContent).toContain('"apitoken" is named twice.');

    await addRow(section, 'Third', 'value-three');
    expect(section.querySelector('[data-named-secrets-count]')?.textContent).toBe('3 of 3');
    expect(section.querySelector('[data-named-secret-add]')?.hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });
});

// --- Member settings ----------------------------------------------------------------------------

const cloudOne = {
  team: asTeamId('alpha'),
  id: asMemberId('CloudOne'),
  name: 'CloudOne',
  agent: 'plugin:sample-cloud',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

function settingsRoutes(plugin: InstalledPlugin, held: () => typeof Held): Route[] {
  const settings = hostSettings(plugin, { team: 'alpha', member: 'CloudOne', namedSecrets: plugin.namedSecrets ?? null });
  return [
    (call) => (call.method === 'GET' && call.url === '/api/agents' ? reply(200, { agents: [{ name: 'claude', mode: 'Headless' }] }) : undefined),
    (call) => (call.method === 'GET' && call.url === '/api/teams/alpha/containers/CloudOne' ? reply(200, { systemPrompt: null }) : undefined),
    (call) => (call.url === '/api/teams/alpha/members/CloudOne/plugin-settings' && call.method === 'GET' ? reply(200, settings) : undefined),
    (call) => (call.url === '/api/teams/alpha/members/CloudOne/plugin-settings' && call.method === 'PUT' ? reply(204) : undefined),
    (call) =>
      call.method === 'GET' && call.url === '/api/teams/alpha/members/CloudOne/named-secrets'
        ? reply(200, { team: 'alpha', member: 'CloudOne', namedSecrets: held() })
        : undefined,
  ];
}

async function mountSettings() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [cloudOne] } as unknown as Team];
  board.managerName = asMemberId('Manager');
  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: cloudOne }, { pinia: false });
  await settle();
  return wrapper;
}

describe('Member settings: named secrets', () => {
  let held = Held;
  beforeEach(() => {
    held = Held;
    serve(settingsRoutes(cloud, () => held));
  });

  it('lists each name the member holds with an empty password box saying who saved it, and never a value', async () => {
    const wrapper = await mountSettings();

    const section = table()!;
    expectHead(section);
    expect([...section.querySelectorAll('[data-named-secret]')].map((element) => element.getAttribute('data-named-secret'))).toEqual([
      'MicrosoftClientSecret',
      'GoogleClientSecret',
    ]);
    const box = row('MicrosoftClientSecret').querySelector<HTMLInputElement>('input[data-named-secret-value]')!;
    expect(box.getAttribute('type')).toBe('password');
    expect(box.value).toBe('');
    expect(rowState('MicrosoftClientSecret')).toMatch(/^Saved by person@example\.com on /);
    expect(rowState('GoogleClientSecret')).toMatch(/^Saved by other@example\.com on /);

    wrapper.unmount();
  });

  it('shows nothing of the table, and reads no names, for a plugin that declares no block', async () => {
    serve(settingsRoutes(plain, () => Held));
    const wrapper = await mountSettings();

    expect(bodyFind('[data-secret-setting="apiKey"]')).not.toBeNull();
    expect(table()).toBeNull();
    expect(sent(calls, 'GET', '/api/teams/alpha/members/CloudOne/named-secrets')).toHaveLength(0);

    wrapper.unmount();
  });

  it('holds Remove until Save, Keep takes it back, and Save sends null for a removed name', async () => {
    const wrapper = await mountSettings();

    await click(row('GoogleClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    expect(rowState('GoogleClientSecret')).toBe('Removed when you press Save.');
    expect(row('GoogleClientSecret').hasAttribute('data-named-secret-removing')).toBe(true);
    expect(row('GoogleClientSecret').querySelector('[data-named-secret-remove]')?.textContent).toContain('Keep');
    // Held: nothing is sent yet.
    expect(sent(calls, 'PUT', '/api/teams/alpha/members/CloudOne/plugin-settings')).toHaveLength(0);

    await click(row('GoogleClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    expect(rowState('GoogleClientSecret')).toMatch(/^Saved by other@example\.com/);
    expect(row('GoogleClientSecret').hasAttribute('data-named-secret-removing')).toBe(false);

    await click(row('GoogleClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    held = [Held[0]!];
    button('Save').click();
    await settle();

    const puts = sent(calls, 'PUT', '/api/teams/alpha/members/CloudOne/plugin-settings');
    expect(puts).toHaveLength(1);
    expect(puts[0]!.body).toEqual({ config: {}, namedSecretValues: { GoogleClientSecret: null } });

    wrapper.unmount();
  });

  it('sends a replaced value and an added row with Save, and shows neither value afterwards', async () => {
    const wrapper = await mountSettings();

    await fill(row('MicrosoftClientSecret').querySelector<HTMLInputElement>('input[data-named-secret-value]')!, Typed);
    expect(rowState('MicrosoftClientSecret')).toBe('Replaced when you press Save.');
    await addRow(table()!, 'StripeKey', 'stripe-value-123');

    held = [...Held, { name: 'StripeKey', setBy: 'person@example.com', setAt: '2026-10-26T09:00:00Z' }];
    button('Save').click();
    await settle();

    const puts = sent(calls, 'PUT', '/api/teams/alpha/members/CloudOne/plugin-settings');
    expect(puts.map((call) => call.body)).toEqual([
      { config: {}, namedSecretValues: { MicrosoftClientSecret: Typed, StripeKey: 'stripe-value-123' } },
    ]);
    // The names are read again after the save; the read carries no value, and none is shown.
    expect(sent(calls, 'GET', '/api/teams/alpha/members/CloudOne/named-secrets').length).toBeGreaterThanOrEqual(2);
    expect(shown()).not.toContain(Typed);
    expect(shown()).not.toContain('stripe-value-123');

    wrapper.unmount();
  });
});

// --- The solution wizard's secrets step ---------------------------------------------------------

/** The sample plan with the block on its plugin. */
function planWithBlock() {
  const plan = hostPlan();
  plan.plugins = plan.plugins.map((plugin) => ({ ...plugin, namedSecrets: Block }));
  return plan;
}

describe('Solution wizard: named secrets', () => {
  async function yourPart(withBlock = true) {
    const plan = withBlock ? planWithBlock() : hostPlan();
    serve([
      (call) =>
        call.method === 'POST' && call.url === '/api/solutions/install'
          ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8), secrets: [], unset: [], unsetFields: [] })
          : undefined,
      (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
      ...wizardRoutes({ plan }),
    ]);
    setActivePinia(createPinia());
    const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    for (let index = 0; index < 2; index++) {
      button('Next').click();
      await settle();
    }
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
    return wrapper as VueWrapper;
  }

  it("shows the table for each plugin member whose plugin declares the block, headed by its label and description", async () => {
    const wrapper = await yourPart();

    const member = bodyFind('[data-named-secrets-member="Scout"]')!;
    expect(member).not.toBeNull();
    expectHead(member);
    expect(bodyText()).not.toContain('This package needs nothing from you.');

    wrapper.unmount();
  });

  it('shows nothing of the table when the plugin declares no block', async () => {
    const wrapper = await yourPart(false);

    expect(table()).toBeNull();

    wrapper.unmount();
  });

  it('holds Next on a row with no value, and sends each row with Install as that member\'s own', async () => {
    const wrapper = await yourPart();
    const member = bodyFind('[data-named-secrets-member="Scout"]')!;

    await click(member.querySelector<HTMLElement>('[data-named-secret-add]')!);
    await fill(member.querySelector<HTMLInputElement>('input[data-named-secret-name-input]')!, 'BoardToken');
    expect(isDisabled('Next')).toBe(true);
    await fill(member.querySelector<HTMLInputElement>('input[data-named-secret-value]')!, Typed);
    expect(member.textContent).toContain('Saved when you press Install.');
    expect(isDisabled('Next')).toBe(false);

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();
    await settle();

    const installs = sent(calls, 'POST', '/api/solutions/install');
    expect(installs).toHaveLength(1);
    expect((installs[0]!.body as { namedSecretValues?: unknown }).namedSecretValues).toEqual({ Scout: { BoardToken: Typed } });
    expect(shown()).not.toContain(Typed);

    wrapper.unmount();
  });
});

// --- The solution panel's settings --------------------------------------------------------------

describe("A solution's panel: named secrets under All settings", () => {
  function servePanel(withBlock: boolean) {
    serve([
      (call) =>
        call.method === 'GET' && call.url === '/api/teams/job-tracker/members/scout/plugin-settings'
          ? reply(200, scoutSettings(withBlock ? { namedSecrets: Block } : {}))
          : undefined,
      (call) =>
        call.method === 'GET' && call.url === '/api/teams/job-tracker/members/scout/named-secrets'
          ? reply(200, { team: 'job-tracker', member: 'scout', namedSecrets: Held })
          : undefined,
      (call) => (call.method === 'PUT' && call.url.endsWith('/plugin-settings') ? reply(204) : undefined),
      ...panelRoutes(),
    ]);
  }

  // Not unmounted: the expansion's animation is cancelled on unmount, as in the panel's own specs.
  async function allSettings() {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();
    await openSection('controls');
    await click(bodyFind('[data-all-settings] .q-item')!);
  }

  it('shows the table with the names the member holds, Remove held with Keep, and Save carrying namedSecretValues', async () => {
    servePanel(true);
    await allSettings();

    const all = bodyFind('[data-all-settings-member="scout"]')!;
    const section = table(all)!;
    expect(section).not.toBeNull();
    expectHead(section);
    // Only All settings shows it: the listed settings and Connections are not the member's whole form.
    expect(bodyFind('[data-listed-settings="scout"] [data-named-secrets]')).toBeNull();
    expect(row('MicrosoftClientSecret').querySelector<HTMLInputElement>('input[data-named-secret-value]')!.value).toBe('');

    await click(row('MicrosoftClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    expect(rowState('MicrosoftClientSecret')).toBe('Removed when you press Save.');
    await click(row('MicrosoftClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    expect(rowState('MicrosoftClientSecret')).toMatch(/^Saved by /);
    await click(row('MicrosoftClientSecret').querySelector<HTMLElement>('[data-named-secret-remove]')!);
    await addRow(section, 'BoardToken', Typed);

    await click(all.querySelector<HTMLElement>('[data-settings-save]')!);

    const puts = sent(calls, 'PUT', '/api/teams/job-tracker/members/scout/plugin-settings');
    expect(puts.map((call) => call.body)).toEqual([
      { config: { keywords: ['dotnet'], region: 'EU' }, connections: {}, namedSecretValues: { MicrosoftClientSecret: null, BoardToken: Typed } },
    ]);
    expect(shown()).not.toContain(Typed);
  });

  it('shows nothing of the table, and reads no names, when the plugin declares no block', async () => {
    servePanel(false);
    await allSettings();

    expect(bodyFind('[data-all-settings-member="scout"]')).not.toBeNull();
    expect(table()).toBeNull();
    expect(sent(calls, 'GET', '/api/teams/job-tracker/members/scout/named-secrets')).toHaveLength(0);
  });
});
