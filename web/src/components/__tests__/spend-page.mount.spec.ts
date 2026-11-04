// @vitest-environment happy-dom
//
// ADMIN → SPEND: the Overview's four tiles with the sentence, two currencies kept apart, an empty Actual
// Spend that shows no estimate in its place, the breakdown and the two separate trend charts; the
// Billing accounts tab with its linked members; and the Expenses tab with each expense's history, its
// void and replace, and no edit or delete. The label lint covers every new component.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({
  getSpendOverview: vi.fn(),
  getLinkedMembers: vi.fn(),
  listBillingAccounts: vi.fn(),
  listExpenses: vi.fn(),
  createBillingAccount: vi.fn(),
  editBillingAccount: vi.fn(),
  setBillingAccountActive: vi.fn(),
  recordExpense: vi.fn(),
  voidExpense: vi.fn(),
  replaceExpense: vi.fn(),
}));

vi.mock('../../api/spend', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  ...api,
}));

import SpendDialog from '../SpendDialog.vue';
import type { BillingAccount, Expense, SpendOverview } from '../../api/spend';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { misnamedOnSpend, readOff } from '../../test/estimateLabels';
import { EstimateTileTitle, ExportFiles, ExportTitle, NoActualSpend, SpendSentence } from '../../lib/spend';

const months = ['2026-06-01', '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01'];
const none = { currency: 'USD', cost: null, agentSeconds: 0, agentRate: null, legacyRates: [] };

function overview(options: { actual?: boolean } = {}): SpendOverview {
  const actual = options.actual ?? true;
  return {
    from: '2026-11-01T00:00:00.0000000+00:00',
    to: '2026-12-01T00:00:00.0000000+00:00',
    actual: actual ? [{ currency: 'EUR', amount: 41.2, count: 1 }, { currency: 'USD', amount: 230, count: 2 }] : [],
    projected: [{ currency: 'USD', amount: 30, count: 1 }],
    projections: [{ accountId: 'seats', accountName: 'Team seats', periodStart: '2026-11-01T00:00:00Z', periodEnd: '2026-12-01T00:00:00Z', amount: 30, currency: 'USD' }],
    billingTypes: [
      { billingType: 'subscription', group: 'fixed', currency: 'USD', actual: actual ? 200 : null, projected: 30 },
      { billingType: 'metered', group: 'variable', currency: 'EUR', actual: actual ? 41.2 : null, projected: null },
    ],
    estimate: { currency: 'USD', cost: 512.34, agentSeconds: 9 * 3600, agentRate: 56.93, legacyRates: [] },
    accounts: [
      { accountId: 'seats', name: 'Team seats', vendor: 'Anthropic', billingType: 'subscription', currency: 'USD', active: true, actual: actual ? 200 : null, projected: 30, expenses: actual ? 1 : 0 },
      { accountId: 'tokens', name: 'Tokens', vendor: 'Mistral', billingType: 'metered', currency: 'EUR', active: true, actual: actual ? 41.2 : null, projected: null, expenses: actual ? 1 : 0 },
    ],
    vendors: [
      { vendor: 'Anthropic', currency: 'USD', actual: actual ? 200 : null, projected: 30, accounts: 1 },
      { vendor: 'Mistral', currency: 'EUR', actual: actual ? 41.2 : null, projected: null, accounts: 1 },
    ],
    months: months.map((monthStart, i) => ({
      monthStart,
      actual: actual && i === 5 ? [{ currency: 'EUR', amount: 41.2, count: 1 }, { currency: 'USD', amount: 230, count: 2 }] : [],
      estimate: i === 5 ? { ...none, cost: 512.34, agentSeconds: 32400 } : none,
    })),
  };
}

const account = (id: string, name: string, extra: Partial<BillingAccount> = {}): BillingAccount => ({
  id, name, vendor: 'Anthropic', billingType: 'subscription', currency: 'USD', billingPeriod: 'monthly', recurringAmount: '30',
  effectiveFrom: '2026-01-01T00:00:00.0000000+00:00', effectiveTo: null, active: true, description: null, scope: 'tenant',
  instanceId: 'i', createdBy: 'p@example.test', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...extra,
});

const row = (id: string, extra: Partial<Expense> = {}): Expense => ({
  id, accountId: 'seats', amount: '200', currency: 'USD', servicePeriodStart: '2026-11-01T00:00:00.0000000+00:00',
  servicePeriodEnd: '2026-12-01T00:00:00.0000000+00:00', kind: 'invoice', status: 'confirmed', supersedes: null, reference: 'INV-7',
  note: null, recordedBy: 'p@example.test', recordedAt: '2026-11-02T10:00:00Z', supersededBy: null, current: true, ...extra,
});

const expenses: Expense[] = [
  row('old', { amount: '180', supersededBy: 'new', current: false, recordedAt: '2026-11-02T09:00:00Z' }),
  row('new', { supersedes: 'old', recordedAt: '2026-11-02T10:00:00Z' }),
  row('gone', { amount: '50', supersededBy: 'void', current: false, recordedAt: '2026-11-03T09:00:00Z' }),
  row('void', { amount: '50', status: 'void', supersedes: 'gone', recordedAt: '2026-11-03T10:00:00Z', note: 'Refunded' }),
];

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
  vi.clearAllMocks();
});

async function openPage(view: SpendOverview = overview()) {
  api.getSpendOverview.mockResolvedValue(view);
  api.listBillingAccounts.mockResolvedValue([account('seats', 'Team seats'), account('tokens', 'Tokens', { vendor: 'Mistral', billingType: 'metered', currency: 'EUR', recurringAmount: null, active: false })]);
  api.listExpenses.mockResolvedValue(expenses);
  api.getLinkedMembers.mockResolvedValue([{ accountId: 'seats', team: 'alpha', teamName: 'Alpha', member: 'Dev', label: 'Developer' }]);
  wrapper = await mountDialog(SpendDialog);
  await flushPromises();
}

async function tab(name: string) {
  bodyFind(`[data-spend-tab="${name}"]`)!.click();
  await flushPromises();
}

describe('the Spend page', () => {
  it('opens on the current UTC month and shows the four tiles with the sentence exactly', async () => {
    await openPage();

    const now = new Date();
    const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString().slice(0, 10);
    expect(api.getSpendOverview).toHaveBeenCalledWith(expect.objectContaining({ from }));

    for (const tile of ['actual', 'projected', 'types', 'estimate']) expect(bodyFind(`[data-spend-tile="${tile}"]`)).not.toBeNull();
    expect(bodyFind('[data-spend-tile="estimate"]')!.textContent).toContain(EstimateTileTitle);
    expect(bodyFind('[data-spend-tile="estimate"]')!.textContent).toContain('9.0 Agent Hours');
    expect(bodyFind('[data-spend-sentence]')!.textContent!.trim()).toBe(SpendSentence);
    expect(SpendSentence).toBe(
      'Actual Spend is what you paid vendors. Estimated Execution Cost is agent and plugin time at your estimated hourly rates. They are different measurements and need not reconcile.');
  });

  it('keeps two currencies apart: one figure each, never one sum and never converted', async () => {
    await openPage();

    const lines = [...document.body.querySelectorAll('[data-spend-actual]')].map((e) => e.textContent!.trim());
    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/41\.20/);
    expect(lines[0]).toMatch(/€|EUR/);
    expect(lines[1]).toMatch(/230\.00/);
    expect(lines[1]).toMatch(/\$|USD/);
    // 271.20 would be the two added together.
    expect(bodyText()).not.toMatch(/271\.2/);

    // The projected charge is apart, labelled projected, and not in Actual Spend.
    expect(bodyFind('[data-spend-projected]')!.textContent).toMatch(/30\.00.*projected/);
    expect(bodyFind('[data-spend-tile="actual"]')!.textContent).not.toMatch(/(?<![\d,])30\.00/);

    // The trend is separate charts: one Actual Spend chart per currency and one estimate chart.
    const charts = [...document.body.querySelectorAll('[data-spend-chart]')].map((e) => e.getAttribute('data-spend-chart'));
    expect(charts).toEqual(['Actual Spend per month (EUR)', 'Actual Spend per month (USD)', 'Estimated Execution Cost per month (USD)']);
  });

  it('says there is no Actual Spend when there are no expenses, and shows no estimate in its place', async () => {
    await openPage(overview({ actual: false }));

    const tile = bodyFind('[data-spend-tile="actual"]')!;
    expect(tile.querySelector('[data-spend-actual-none]')!.textContent!.trim()).toBe(NoActualSpend);
    expect(tile.textContent).not.toMatch(/512|Agent Hours|Estimated/);
    expect(bodyFind('[data-spend-trend-none]')).not.toBeNull();
    // The estimate is still shown, in its own tile.
    expect(bodyFind('[data-spend-estimate]')!.textContent).toMatch(/512\.34/);
  });

  it('breaks the period down by account and by vendor', async () => {
    await openPage();

    expect(bodyFind('[data-spend-by-account]')!.textContent).toMatch(/Team seats.*Anthropic.*200\.00.*30\.00 projected/s);
    expect(bodyFind('[data-spend-by-vendor]')!.textContent).toMatch(/Mistral.*EUR.*41\.20/s);
  });

  it('lists the billing accounts with their linked members, and edits send only what moved', async () => {
    await openPage();
    await tab('accounts');

    const seats = bodyFind('[data-billing-account="seats"]')!;
    expect(seats.querySelector('[data-billing-account-members]')!.textContent).toMatch(/Developer.*Alpha/);
    expect(bodyFind('[data-billing-account="tokens"] [data-billing-account-inactive]')).not.toBeNull();
    expect(document.body.querySelector('[data-billing-account] button[aria-label="Delete"]')).toBeNull();

    api.setBillingAccountActive.mockResolvedValue(account('seats', 'Team seats', { active: false }));
    (seats.querySelector('[data-billing-account-toggle]') as HTMLElement).click();
    await flushPromises();
    expect(api.setBillingAccountActive).toHaveBeenCalledWith('seats', false);

    (bodyFind('[data-billing-account="seats"] [data-billing-account-edit]') as HTMLElement).click();
    await flushPromises();
    const name = bodyFind('[data-billing-account-form] [data-field="name"] input, [data-billing-account-form] input[data-field="name"]') as HTMLInputElement;
    name.value = 'Seats (annual)';
    name.dispatchEvent(new Event('input'));
    api.editBillingAccount.mockResolvedValue(account('seats', 'Seats (annual)'));
    bodyFind('[data-billing-account-save]')!.click();
    await flushPromises();
    expect(api.editBillingAccount).toHaveBeenCalledWith('seats', { name: 'Seats (annual)' });
  });

  it('shows each expense with its history, void and replace for a confirmed current one, and no edit or delete', async () => {
    await openPage();
    await tab('expenses');

    const current = bodyFind('[data-expense="new"]')!;
    expect(current.textContent).toMatch(/Team seats.*Invoice.*200\.00.*Confirmed/s);
    expect(current.querySelector('[data-expense-replace]')).not.toBeNull();
    expect(current.querySelector('[data-expense-void]')).not.toBeNull();

    const voided = bodyFind('[data-expense="void"]')!;
    expect(voided.getAttribute('data-expense-status')).toBe('void');
    expect(voided.querySelector('[data-expense-replace]')).toBeNull();
    expect(voided.querySelector('[data-expense-void]')).toBeNull();

    // Superseded rows are history, not rows of their own.
    expect(bodyFind('[data-expense="old"]')).toBeNull();
    (current.querySelector('[data-expense-history-toggle]') as HTMLElement).click();
    await flushPromises();
    expect(bodyFind('[data-expense-history="old"]')!.textContent).toMatch(/180\.00.*Replaced/s);
    (voided.querySelector('[data-expense-history-toggle]') as HTMLElement).click();
    await flushPromises();
    expect(bodyFind('[data-expense-history="gone"]')!.textContent).toMatch(/50\.00.*Voided/s);

    expect(bodyText()).not.toMatch(/\bDelete\b/);

    api.voidExpense.mockResolvedValue(row('v2', { status: 'void', supersedes: 'new' }));
    (current.querySelector('[data-expense-void]') as HTMLElement).click();
    await flushPromises();
    bodyFind('[data-expense-void-confirm]')!.click();
    await flushPromises();
    expect(api.voidExpense).toHaveBeenCalledWith('new', null);
  });

  it('exports each CSV file for the period the page shows', async () => {
    await openPage();
    const control = bodyFind('[data-spend-export]')!;
    expect(control.textContent).toContain(ExportTitle);

    const links = async () => {
      bodyFind('[data-spend-export]')!.click();
      await flushPromises();
      return [...document.body.querySelectorAll<HTMLAnchorElement>('[data-spend-export-file]')];
    };

    const now = new Date();
    const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString().slice(0, 10);
    const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1)).toISOString().slice(0, 10);
    const first = await links();
    expect(first.map((a) => a.dataset.spendExportFile)).toEqual(['billing-accounts', 'expenses', 'period-summary', 'estimated-execution-cost']);
    for (const a of first) {
      expect(a.getAttribute('href')).toBe(`/api/spend/export/${a.dataset.spendExportFile}?from=${from}&to=${to}`);
      expect(a.hasAttribute('download')).toBe(true);
    }
    expect(first.map((a) => a.textContent)).toEqual(ExportFiles.map((f) => expect.stringContaining(f.label)));

    // The previous month: every file follows the page's period.
    bodyFind('[data-spend-prev]')!.click();
    await flushPromises();
    const prior = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1)).toISOString().slice(0, 10);
    for (const a of await links()) expect(a.getAttribute('href')).toBe(`/api/spend/export/${a.dataset.spendExportFile}?from=${prior}&to=${from}`);
  });

  it('names nothing wrongly on any tab: the estimate is never Spend, Spent or Cost alone, and no value word appears', async () => {
    await openPage();
    const titles = [...document.body.querySelectorAll('[data-spend-dialog] .text-overline, [data-spend-dialog] .os-dialog-title, [data-spend-chart] figcaption')]
      .map((e) => e.textContent ?? '');
    expect(titles).toContain(EstimateTileTitle);

    // The page's own title, Spend, read on its own; everything else as a person reads it.
    const page = () => {
      const copy = bodyFind('[data-spend-dialog]')!.cloneNode(true) as HTMLElement;
      const title = copy.querySelector('.os-dialog-title')!;
      const words = [title.textContent ?? ''];
      title.remove();
      return [...words, ...readOff(copy)];
    };
    const texts = page();
    for (const name of ['accounts', 'expenses']) {
      await tab(name);
      texts.push(...page());
    }

    // The forms too: a billing account's and an expense's.
    await tab('accounts');
    bodyFind('[data-billing-account-add]')!.click();
    await flushPromises();
    texts.push(...readOff(bodyFind('[data-billing-account-form]')));
    await tab('expenses');
    bodyFind('[data-expense-record]')!.click();
    await flushPromises();
    texts.push(...readOff(bodyFind('[data-expense-form]')));
    expect(texts.join(' ')).toMatch(/Billing type.*Service period from/s);

    // The Export control and its menu.
    bodyFind('[data-spend-export]')!.click();
    await flushPromises();
    const menu = bodyFind('[data-spend-export-file]')!.closest('.q-menu') as HTMLElement;
    texts.push(...readOff(bodyFind('[data-spend-export]')), ...readOff(menu));
    expect(texts.join(' ')).toContain(`${EstimateTileTitle.split(' · ')[0]} by team and outcome`);

    expect(misnamedOnSpend(texts, titles)).toEqual([]);
    // The lint bites: these would be refused.
    expect(misnamedOnSpend(['Spend this month', 'Cost', 'ROI 3x'], ['Cost · this month'])).toHaveLength(3);
  });
});
