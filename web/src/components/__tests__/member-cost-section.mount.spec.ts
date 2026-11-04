// @vitest-environment happy-dom
//
// A MEMBER'S ESTIMATED HOURLY RATE AND BILLING ACCOUNT in Member settings. The effective configuration
// is the member read's own `effectiveRate` and `billingAccountId`, shown as answered; a save sends the
// rate and the account each only when the person changed it; the picker offers active accounts, and an
// inactive or unknown account only as the value the member already holds.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { getMember, getTenantSettings, listCatalog, listPlugins, getPluginSettings, updateMember, listBillingAccounts } = vi.hoisted(() => ({
  getMember: vi.fn(),
  getTenantSettings: vi.fn(),
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  getPluginSettings: vi.fn(),
  updateMember: vi.fn(),
  listBillingAccounts: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMember,
  getTenantSettings,
  listCatalog,
  listPlugins,
  getPluginSettings,
  updateMember,
}));

vi.mock('../../api/spend', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listBillingAccounts,
}));

import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type MemberDetail, type Team } from '../../api/types';
import type { BillingAccount } from '../../api/spend';
import { mountDialog, resetBody, textAndHelp } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { misnamedOnMemberRate, readOff } from '../../test/estimateLabels';
import { NextRunSentence } from '../../lib/memberCost';

const AnthropicId = '11111111-1111-4111-8111-111111111111';
const EuroId = '22222222-2222-4222-8222-222222222222';
const RetiredId = '33333333-3333-4333-8333-333333333333';
const DanglingId = '44444444-4444-4444-8444-444444444444';

function account(id: string, name: string, vendor: string, currency: string, active = true): BillingAccount {
  return {
    id, name, vendor, currency, active,
    billingType: 'metered', billingPeriod: 'monthly', recurringAmount: null,
    effectiveFrom: '2026-01-01T00:00:00.0000000Z', effectiveTo: null, description: null, scope: 'tenant',
    instanceId: 'i', createdBy: 'p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  };
}

const accounts = [
  account(AnthropicId, 'Claude Team', 'Anthropic', 'USD'),
  account(EuroId, 'Mistral EU', 'Mistral', 'EUR'),
  account(RetiredId, 'Old OpenAI', 'OpenAI', 'USD', false),
];

const agent: ContainerSnapshot = {
  team: asTeamId('alpha'), id: asMemberId('scout'), name: 'Scout', agent: 'claude', state: 'Idle',
  queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: null, sinceSeq: 0,
} as ContainerSnapshot;

const plugin = { ...agent, id: asMemberId('echo'), name: 'Echo', agent: 'plugin:sample-echo', kind: 'plugin' } as ContainerSnapshot;

/** What the member read answers. */
function detail(overrides: Partial<MemberDetail> = {}): MemberDetail {
  return {
    team: asTeamId('alpha'), id: 'scout', name: 'Scout',
    systemPrompt: null, systemPromptSetBy: null, systemPromptSetByKind: null, systemPromptSetAt: null,
    hourlyRate: null, billingAccountId: null, billingSetBy: null, billingSetAt: null,
    effectiveRate: { amount: '60', currency: 'USD', source: 'tenant' },
    ...overrides,
  };
}

beforeEach(() => {
  listCatalog.mockReset().mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockReset().mockResolvedValue({ plugins: [] });
  getPluginSettings.mockReset().mockRejectedValue(new Error('not needed here'));
  updateMember.mockReset().mockResolvedValue({ ...agent });
  getMember.mockReset().mockResolvedValue(detail());
  listBillingAccounts.mockReset().mockResolvedValue(accounts);
  getTenantSettings.mockReset().mockResolvedValue({
    settings: [
      { name: 'outcomes.agentHourlyRate', value: 60, source: 'row' },
      { name: 'outcomes.currency', value: 'usd', source: 'row' },
    ],
    roots: [],
  });
});

afterEach(resetBody);

async function mount(member: ContainerSnapshot = agent) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'] } as unknown as Team];
  board.managerName = asMemberId('Manager');
  const wrapper = mountDialog(MemberSettingsDialog, { snapshot: member }, { pinia: false });
  await settle();
  return wrapper;
}

const section = () => document.body.querySelector('[data-member-cost]') as HTMLElement;
const text = (selector: string) => section().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();

type Wrapper = Awaited<ReturnType<typeof mount>>;
const accountSelect = (wrapper: Wrapper) =>
  wrapper.findAllComponents({ name: 'QSelect' }).find((s) => s.props('label') === 'Billing account')!;
const modeGroup = (wrapper: Wrapper) => wrapper.findComponent({ name: 'QOptionGroup' });

async function save() {
  button('Save').click();
  await settle();
}

describe('Member settings: estimated hourly rate and billing account', () => {
  it('shows an agent override as the member read answered it: amount as written, source member override', async () => {
    getMember.mockResolvedValue(detail({ hourlyRate: '62.5', billingAccountId: AnthropicId, effectiveRate: { amount: '62.5', currency: 'USD', source: 'member' } }));
    const wrapper = await mount();

    expect(text('[data-effective-preset]')).toBe('claude');
    expect(text('[data-effective-model]')).toBe('not identifiable');
    expect(text('[data-effective-rate]')).toBe('62.5 USD/h · source: member override');
    expect(text('[data-effective-account]')).toBe('Claude Team · Anthropic · USD');
    expect(modeGroup(wrapper).props('modelValue')).toBe('override');
    expect(field('Estimated hourly rate').value).toBe('62.5');
    // The Inherit choice names the stored tenant default.
    expect(section().textContent).toContain('Inherit default (60 USD/h)');
    wrapper.unmount();
  });

  it('shows an agent inheriting at the tenant default the read answered', async () => {
    const wrapper = await mount();

    expect(text('[data-effective-rate]')).toBe('60 USD/h · source: tenant default');
    expect(text('[data-effective-account]')).toBe('No account');
    expect(modeGroup(wrapper).props('modelValue')).toBe('inherit');
    expect(section().textContent).toContain('Inherit default (60 USD/h)');
    wrapper.unmount();
  });

  it("shows an agent at '0' as not priced, held as an override of 0", async () => {
    getMember.mockResolvedValue(detail({ hourlyRate: '0', effectiveRate: { amount: null, currency: null, source: 'none' } }));
    const wrapper = await mount();

    expect(text('[data-effective-rate]')).toBe('Not priced · source: none');
    expect(modeGroup(wrapper).props('modelValue')).toBe('override');
    expect(field('Estimated hourly rate').value).toBe('0');
    wrapper.unmount();
  });

  it('shows a plugin with no rate of its own as Not priced, source none, never the agent default', async () => {
    getMember.mockResolvedValue(detail({ id: 'echo', name: 'Echo', effectiveRate: { amount: null, currency: null, source: 'none' } }));
    const wrapper = await mount(plugin);

    expect(getMember).toHaveBeenCalledWith('alpha', 'echo');
    expect(text('[data-effective-preset]')).toBe('plugin:sample-echo');
    expect(section().textContent).toContain('Plugin');
    expect(text('[data-effective-rate]')).toBe('Not priced · source: none');
    expect(section().textContent).toContain('Inherit default (not priced: a plugin member has no default)');
    expect(section().textContent).not.toContain('60 USD/h');
    wrapper.unmount();
  });

  it('shows a dangling account id as unknown and keeps it on a save that does not change it', async () => {
    getMember.mockResolvedValue(detail({ billingAccountId: DanglingId }));
    const wrapper = await mount();

    expect(text('[data-effective-account]')).toBe(`Unknown account (${DanglingId})`);
    expect(accountSelect(wrapper).props('modelValue')).toBe(DanglingId);

    await type('Member name', 'Scout Two');
    await save();

    expect(updateMember.mock.calls[0]![2]).toEqual({ name: 'Scout Two' });
    wrapper.unmount();
  });

  it('shows the effective configuration as stored, not as being typed', async () => {
    const wrapper = await mount();

    await modeGroup(wrapper).vm.$emit('update:modelValue', 'override');
    await settle();
    await type('Estimated hourly rate', '80');

    expect(text('[data-effective-rate]')).toBe('60 USD/h · source: tenant default');
    wrapper.unmount();
  });
});

describe('Member settings: what a save sends', () => {
  it('a rate change sends only the rate', async () => {
    getMember.mockResolvedValue(detail({ billingAccountId: AnthropicId }));
    const wrapper = await mount();

    await modeGroup(wrapper).vm.$emit('update:modelValue', 'override');
    await settle();
    await type('Estimated hourly rate', '80');
    await save();

    expect(updateMember.mock.calls[0]![2]).toEqual({ hourlyRate: '80' });
    wrapper.unmount();
  });

  it('an account change sends only the account', async () => {
    getMember.mockResolvedValue(detail({ hourlyRate: '75', effectiveRate: { amount: '75', currency: 'USD', source: 'member' } }));
    const wrapper = await mount();

    await accountSelect(wrapper).vm.$emit('update:modelValue', EuroId);
    await save();

    expect(updateMember.mock.calls[0]![2]).toEqual({ billingAccountId: EuroId });
    wrapper.unmount();
  });

  it('Inherit clears an override, and No account clears the account', async () => {
    getMember.mockResolvedValue(detail({ hourlyRate: '75', billingAccountId: AnthropicId, effectiveRate: { amount: '75', currency: 'USD', source: 'member' } }));
    const wrapper = await mount();

    await modeGroup(wrapper).vm.$emit('update:modelValue', 'inherit');
    await accountSelect(wrapper).vm.$emit('update:modelValue', '');
    await save();

    expect(updateMember.mock.calls[0]![2]).toEqual({ hourlyRate: null, billingAccountId: null });
    wrapper.unmount();
  });

  it('a save that changes nothing sends nothing', async () => {
    getMember.mockResolvedValue(detail({ hourlyRate: '75', billingAccountId: DanglingId, effectiveRate: { amount: '75', currency: 'USD', source: 'member' } }));
    const wrapper = await mount();

    await save();

    expect(updateMember).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("shows the server's refusal as it comes back", async () => {
    updateMember.mockRejectedValue(new Error('The hourly rate must be a plain number of zero or more, such as 60 or 62.5, or blank to clear it.'));
    const wrapper = await mount();

    await modeGroup(wrapper).vm.$emit('update:modelValue', 'override');
    await settle();
    await type('Estimated hourly rate', '-5');
    await save();

    expect(updateMember.mock.calls[0]![2]).toEqual({ hourlyRate: '-5' });
    expect(document.body.textContent).toContain('The hourly rate must be a plain number of zero or more, such as 60 or 62.5, or blank to clear it.');
    wrapper.unmount();
  });
});

describe('Member settings: the billing account picker', () => {
  it('lists No account and the active accounts, in both currencies, and no inactive one', async () => {
    const wrapper = await mount();

    const options = accountSelect(wrapper).props('options') as { label: string; value: string }[];
    expect(options.map((o) => o.label)).toEqual(['No account', 'Claude Team · Anthropic · USD', 'Mistral EU · Mistral · EUR']);
    wrapper.unmount();
  });

  it('lists an inactive account only as the current value of a member that holds it', async () => {
    getMember.mockResolvedValue(detail({ billingAccountId: RetiredId }));
    const wrapper = await mount();

    const options = accountSelect(wrapper).props('options') as { label: string; value: string; current?: boolean }[];
    expect(options.map((o) => o.label)).toEqual([
      'No account', 'Old OpenAI · OpenAI · USD (inactive)', 'Claude Team · Anthropic · USD', 'Mistral EU · Mistral · EUR',
    ]);
    expect(options.find((o) => o.value === RetiredId)?.current).toBe(true);
    expect(text('[data-effective-account]')).toBe('Old OpenAI · OpenAI · USD (inactive)');
    wrapper.unmount();
  });
});

describe('Member settings: words', () => {
  it('says the next-run sentence exactly', async () => {
    const wrapper = await mount();
    expect(text('[data-next-run]')).toBe(NextRunSentence);
    expect(NextRunSentence).toBe('Changes apply to the next run; past runs keep the rate they started with.');
    wrapper.unmount();
  });

  it('never calls the rate a cost, spend or value, in text, help or labels (the label lint)', async () => {
    for (const [member, read] of [
      [agent, detail({ hourlyRate: '62.5', billingAccountId: RetiredId, effectiveRate: { amount: '62.5', currency: 'USD', source: 'member' } })],
      [plugin, detail({ id: 'echo', billingAccountId: DanglingId, effectiveRate: { amount: null, currency: null, source: 'none' } })],
    ] as const) {
      getMember.mockResolvedValue(read);
      const wrapper = await mount(member);
      const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((s) => String(s.props('label')));
      expect(misnamedOnMemberRate([...readOff(section()), textAndHelp(section()), ...labels])).toEqual([]);
      expect(section().textContent).toContain('Estimated hourly rate');
      expect(section().textContent).toContain('Billing account');
      wrapper.unmount();
      resetBody();
    }
  });

  it('the lint catches a rate called a cost, a spend or a value', () => {
    expect(misnamedOnMemberRate(['Hourly cost', 'Spend per hour', 'Value of an hour', 'Estimated hourly rate'])).toEqual([
      'Hourly cost', 'Spend per hour', 'Value of an hour',
    ]);
  });
});
