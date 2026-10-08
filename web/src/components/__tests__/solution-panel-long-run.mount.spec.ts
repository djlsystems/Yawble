// @vitest-environment happy-dom
//
// A LONG RUN AFTER RUN NOW: the run outlasts the panel's short watch. The watch's own pace is kept here
// (no mock of its timings), and the clock is faked so minutes pass in a moment.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';
import { RunWatchEveryMs, RunWatchSlowEveryMs, RunWatchTries } from '../../lib/solutionPanel';

const Running = 'Scan for postings is running now.';
const ShortWatchMs = RunWatchEveryMs * RunWatchTries;

let calls: Call[] = [];
let runEnded = false;
let wrapper: VueWrapper | undefined;

const before = panelRead();
const finished: PanelShape = {
  ...before,
  recentRuns: [{ ...before.recentRuns[0]!, seq: 42, endedAt: '2026-09-30T10:00:00Z' }, ...before.recentRuns],
};

const routes: Route[] = [
  (call) =>
    call.method === 'POST' && call.url === '/api/teams/job-tracker/triggers/trg_scan/run'
      ? reply(200, { outcome: 'fired', reason: null, seq: 77, trigger: {} })
      : undefined,
  (call) =>
    call.method === 'PATCH' && call.url.startsWith('/api/teams/job-tracker/triggers/') ? reply(200, {}) : undefined,
  ...panelRoutes(() => (runEnded ? finished : before)),
];

const reads = () => sent(calls, 'GET', '/api/teams/job-tracker/solution/panel').length;
const notice = () => bodyFind('[data-control-notice]')?.textContent;

/** Lets the faked clock run on, and every read it fires answer. */
async function pass(ms: number) {
  await vi.advanceTimersByTimeAsync(ms);
  await flushPromises();
}

async function runNow() {
  wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
  await settle();
  await openSection('controls');
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  bodyFind('[data-control-trigger="trg_scan"] [data-run-now]')!.click();
  await pass(0);
  expect(notice()).toBe(Running);
}

beforeEach(() => {
  calls = [];
  runEnded = false;
  vi.stubGlobal('fetch', vi.fn(fakeHost(routes, calls)));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

describe('the control panel: a run that outlasts the short watch', () => {
  it('drops "is running now" when a run ends after the short watch, without Refresh', async () => {
    await runNow();
    await pass(ShortWatchMs + RunWatchSlowEveryMs);
    expect(notice()).toBe(Running);

    // Ten minutes in, the run ends; the next slow read shows it and the line goes.
    await pass(10 * 60_000 - ShortWatchMs - RunWatchSlowEveryMs);
    runEnded = true;
    await pass(RunWatchSlowEveryMs);

    expect(notice()).toBeUndefined();
    const readsWhenSeen = reads();
    await pass(5 * RunWatchSlowEveryMs);
    expect(reads()).toBe(readsWhenSeen);
  });

  it('reads at the slow pace once the short watch is spent', async () => {
    await runNow();
    await pass(ShortWatchMs);
    const readsAfterShort = reads();

    await pass(RunWatchSlowEveryMs - 1);
    expect(reads()).toBe(readsAfterShort);
    await pass(1);
    expect(reads()).toBe(readsAfterShort + 1);
  });

  it('stops watching a long run once another notice replaces its line', async () => {
    await runNow();
    // Another control's own notice takes the line's place while the run goes on.
    bodyFind('[data-control-trigger="trg_scan"] [data-trigger-enabled]')!.click();
    await pass(0);
    expect(notice()).not.toBe(Running);

    await pass(ShortWatchMs + RunWatchSlowEveryMs);
    const readsAfter = reads();
    await pass(10 * RunWatchSlowEveryMs);
    expect(reads()).toBe(readsAfter);
  });

  it('shows no "is running now" after the panel is closed and opened again', async () => {
    await runNow();
    await wrapper!.setProps({ modelValue: false });
    await pass(0);
    await wrapper!.setProps({ modelValue: true });
    await pass(0);
    bodyFind('[data-section-tab="controls"]')!.click();
    await pass(0);

    expect(notice()).toBeUndefined();
  });

  it('shows no "is running now" after switching team', async () => {
    await runNow();
    await wrapper!.setProps({ team: 'other-team' });
    await pass(0);

    expect(notice()).toBeUndefined();
    // The old team's run is no longer watched.
    const readsAfter = reads();
    await pass(ShortWatchMs + 3 * RunWatchSlowEveryMs);
    expect(reads()).toBe(readsAfter);
  });
});
