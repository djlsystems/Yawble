// @vitest-environment happy-dom
// The Timeline dialog's axis break: a long stretch with nothing changing is drawn short, so a band across it looks
// far shorter than it is (a Manager's 2h 37m Blocked looked like 10 minutes). Each row gets its own short break mark
// (never one slanted line across the labels), the first row shows the time the break hides, and a band that crosses a
// break says how much of it is drawn shorter.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TimelineDialog from '../../src/renderer/src/timeline/TimelineDialog.vue';
import { BreakMinMs, buildAxis, cutMs, openTimeline } from '../../src/renderer/src/timeline/time';
import { createFakeCore } from '../e2e/phone/fake-core';
import type { YawbleApi } from '../../src/shared/api';

const MIN = 60_000;

function fakeApi(): YawbleApi {
  const core = createFakeCore();
  let id = 0;
  return {
    getInfo: async () => ({}) as any,
    getAvailableBackends: async () => [],
    call: async (method: string, params?: unknown) => {
      const r = JSON.parse(core.answer(JSON.stringify({ id: ++id, method, params }))!);
      if (r.error) throw Object.assign(new Error(r.error.message), { code: r.error.code });
      return r.result;
    },
    onEvent: () => () => {},
  } as any;
}

describe('the axis break: what it hides', () => {
  it('a break knows its real start and end, and a band is cut by exactly its overlap with it', () => {
    // 20 minutes of work, 3 hours with nothing changing, 20 minutes of work.
    const a = buildAxis(0, 220 * MIN, [20 * MIN, 200 * MIN]);
    expect(a.breaks).toHaveLength(1);
    expect(a.breaks[0]).toMatchObject({ from: 20 * MIN, to: 200 * MIN, ms: 180 * MIN });
    expect(cutMs(a, 0, 20 * MIN)).toBe(0); // before it
    expect(cutMs(a, 10 * MIN, 210 * MIN)).toBe(180 * MIN); // right across it
    expect(cutMs(a, 100 * MIN, 210 * MIN)).toBe(100 * MIN); // the second half of it
    expect(cutMs(buildAxis(0, 4 * BreakMinMs, []), 0, 4 * BreakMinMs)).toBe(0); // no break, nothing cut
  });
});

describe('the axis break in the dialog', () => {
  it('each row has its own break mark, the first row says what it hides, and a band across it says so', async () => {
    const c = createFakeCore(); c.setExpiry(true); // the expiry fixtures list deliverable #9, whose axis has a break
    try {
      const w = mount(TimelineDialog, { props: { api: fakeApi() }, attachTo: document.body });
      openTimeline({ kind: 'deliverable', id: 9 });
      await flushPromises();
      const rows = w.findAll('[data-test="timeline-row"]');
      expect(rows.length).toBeGreaterThan(0);
      // A short mark inside every row's track, none outside a track (the old one ran across the labels).
      for (const r of rows) expect(r.findAll('.track .brk')).toHaveLength(1);
      expect(w.findAll('.brk').every((b) => b.element.closest('.track') !== null)).toBe(true);
      expect(w.findAll('[data-test="timeline-break"]')).toHaveLength(1);
      // The first row shows the time the break hides.
      const chip = w.get('[data-test="timeline-break-chip"]');
      expect(chip.text()).toMatch(/^\d+[dhm]( \d+[hm])?$/); // formatShort: "2d 5h", "2h 37m", "45m"
      expect(rows[0].find('[data-test="timeline-break-chip"]').exists()).toBe(true);
      expect(w.findAll('[data-test="timeline-break-chip"]')).toHaveLength(1); // the first row only
      // A band that crosses it says how much of it is drawn shorter.
      const crossing = w.findAll('[data-test="timeline-band"]').filter((b) => (b.attributes('aria-label') ?? '').includes('drawn shorter, behind the break'));
      expect(crossing.length).toBeGreaterThan(0);
      await crossing[0].trigger('mouseenter');
      expect(w.get('[data-test="timeline-readout"]').text()).toContain('drawn shorter, behind the break');
      w.unmount();
    } finally {
      c.setExpiry(false);
    }
  });
});
