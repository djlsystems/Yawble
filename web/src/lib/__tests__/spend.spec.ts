import { describe, expect, it } from 'vitest';
import type { Expense } from '../../api/spend';
import { correctable, currentMonth, expenseChains, perCurrency, rangeOf, shiftMonth } from '../spend';
import { Ribbon, SpendAction } from '../ribbon';

const row = (id: string, extra: Partial<Expense> = {}): Expense => ({
  id, accountId: 'a', amount: '10', currency: 'USD', servicePeriodStart: '2026-11-01T00:00:00Z', servicePeriodEnd: '2026-12-01T00:00:00Z',
  kind: 'invoice', status: 'confirmed', supersedes: null, reference: null, note: null, recordedBy: 'p', recordedAt: `2026-11-0${id.length}T00:00:00Z`,
  supersededBy: null, current: true, ...extra,
});

describe('the Spend page period', () => {
  it('is the current UTC month by default, as two dates with an exclusive end', () => {
    expect(currentMonth(new Date('2026-11-30T23:59:00Z'))).toEqual({ from: '2026-11-01', to: '2026-12-01' });
    expect(currentMonth(new Date('2026-12-31T22:00:00-05:00'))).toEqual({ from: '2027-01-01', to: '2027-02-01' });
    expect(shiftMonth('2026-01-01', -1)).toEqual({ from: '2025-12-01', to: '2026-01-01' });
  });

  it('takes a custom range by its first and last day, the last inclusive', () => {
    expect(rangeOf('2026-10-15', '2026-11-14')).toEqual({ from: '2026-10-15', to: '2026-11-15' });
    expect(rangeOf('2026-10-15', '2026-10-15')).toEqual({ from: '2026-10-15', to: '2026-10-16' });
    expect(rangeOf('2026-10-15', '2026-10-14')).toBeNull();
    expect(rangeOf('', '2026-10-14')).toBeNull();
  });
});

describe('the Spend page figures', () => {
  it('says each currency on its own line, never one sum', () => {
    const lines = perCurrency([{ currency: 'USD', amount: 230, count: 2 }, { currency: 'EUR', amount: 41.2, count: 1 }]);
    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/41\.20/);
    expect(lines[1]).toMatch(/230\.00/);
  });

  it('shows each expense once, by its current row, with what it superseded behind it', () => {
    const rows = [
      row('a', { supersededBy: 'bb', current: false }),
      row('bb', { supersedes: 'a', supersededBy: 'ccc', current: false }),
      row('ccc', { supersedes: 'bb', status: 'void' }),
      row('dddd'),
    ];

    const chains = expenseChains(rows);

    expect(chains.map((c) => c.current.id)).toEqual(['dddd', 'ccc']);
    expect(chains[1]!.history.map((h) => h.id)).toEqual(['bb', 'a']);
    expect(chains.map((c) => correctable(c.current))).toEqual([true, false]);
  });

  it('is opened from Admin → Spend, directly after Outcomes', () => {
    const admin = Ribbon.tabs.find((t) => t.id === 'admin')!;
    const actions = admin.items.map((i) => ('action' in i ? i.action : undefined));
    expect(actions.indexOf(SpendAction)).toBe(actions.indexOf('admin-outcomes') + 1);
  });
});
