// @vitest-environment happy-dom
//
// THE TENANT LOG, BY CURSOR. Opens on the newest rows, reads older ones below the lowest seq
// it holds, and never sends an offset.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { readTenantLog } = vi.hoisted(() => ({ readTenantLog: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  readTenantLog,
}));

import TenantLogDialog from '../TenantLogDialog.vue';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

function event(seq: number, over: Record<string, unknown> = {}) {
  return {
    seq,
    occurredAt: '2026-09-19T10:00:00Z',
    actorId: 'u1',
    actorEmail: 'admin@example.com',
    action: 'team.created',
    subject: 'alpha',
    subjectName: `Team ${seq}`,
    detail: null,
    ...over,
  };
}

/** QVirtualScroll lays out its slice on a 35ms debounce, so a row is on the page only after it. */
async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 60));
  await flushPromises();
}

beforeEach(() => {
  readTenantLog.mockReset();
});

afterEach(resetBody);

describe('the tenant log', () => {
  it('reads the newest rows when it opens, with the words and the total', async () => {
    readTenantLog.mockResolvedValue({ events: [event(9, { action: 'team.deleted' })], total: 1 });

    await mountDialog(TenantLogDialog);
    await settle();

    expect(readTenantLog).toHaveBeenCalledWith(undefined, 50);
    expect(bodyText()).toContain('deleted team');
    expect(bodyText()).toContain('1 of 1 shown');
    expect(bodyText()).toContain('The start of the log.');
  });

  it('reads older rows below the lowest seq it holds, below the rows already shown', async () => {
    readTenantLog.mockResolvedValueOnce({
      events: Array.from({ length: 50 }, (_, i) => event(100 - i)),
      total: 51,
    });
    readTenantLog.mockResolvedValueOnce({ events: [event(3, { subjectName: 'the first team' })], total: 51 });

    await mountDialog(TenantLogDialog);
    await settle();

    (document.body.querySelector('.cursor-sentinel button') as HTMLElement).click();
    await settle();

    expect(readTenantLog).toHaveBeenLastCalledWith(51, 50);
    expect(bodyText()).toContain('51 of 51 shown');

    // The newest row is still the first row: older rows arrive below, never above.
    const first = document.body.querySelector('tbody tr td:nth-child(4)');
    expect(first?.textContent).toBe('Team 100');
  });
});
