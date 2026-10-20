// @vitest-environment happy-dom
//
// THE LIVE CHIP AS A STATUS CONTROL: green with a radiating dot while this window's hub connection is
// up and grey, struck through, when not; the word beside it while the row has room and in its tooltip
// otherwise; a click opens the service's own state, read from `GET /api/service-status` when the
// dropdown opens (once) and said in the reader's words - the connection line, the uptime from the
// Host's two instants, each check, each worker - with "not read" when the read fails.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { serviceStatus } = vi.hoisted(() => ({ serviceStatus: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  serviceStatus,
}));

import LiveStatusControl from '../LiveStatusControl.vue';
import { useConsoleStore } from '../../stores/console';
import { localStamp, wireInstant } from '../../lib/localTime';
import type { ServiceStatus } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

let wrapper: VueWrapper | undefined;

const healthy: ServiceStatus = {
  status: 'healthy',
  startedAt: '2026-10-20T08:02:00Z',
  now: '2026-10-20T10:07:00Z',
  checks: [
    { name: 'database', ok: true, detail: 'open' },
    { name: 'dataRoot', ok: true, detail: 'writable' },
    { name: 'pump', ok: true, detail: 'delivering' },
  ],
  pumpLastPass: '2026-10-20T10:06:59Z',
  build: { version: '2026.10.19.1+30.abc1234', commit: 'abc1234abc1234', builtAt: '2026-10-20T07:00:00Z' },
  workers: [
    { id: 'worker-1', version: '2026.10.19.1+30.abc1234', state: 'connected', connectedSince: '2026-10-20T08:02:10Z', droppedAt: null },
  ],
};

async function mountControl(props: { word?: boolean } = {}) {
  wrapper = mount(LiveStatusControl, { props, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const chip = () => wrapper!.get('[data-test="live-chip"]');
const panel = () => document.body.querySelector<HTMLElement>('[data-test="live-status-panel"]');
const panelText = () => panel()?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

async function openPanel() {
  await chip().trigger('click');
  await flushPromises();
  await flushPromises();
}

beforeEach(() => {
  setActivePinia(createPinia());
  serviceStatus.mockReset().mockResolvedValue(healthy);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

describe('the chip', () => {
  it('is grey and struck through while offline, green with the radiating dot once live, the word beside it', async () => {
    await mountControl();
    expect(chip().text()).toContain('offline');
    expect(chip().attributes('aria-label')).toBe('offline');
    expect(chip().classes()).not.toContain('live-chip-on');
    expect(chip().find('.q-icon').text()).toBe('sensors_off');

    useConsoleStore().setConnected(true);
    await flushPromises();
    expect(chip().text()).toContain('live');
    expect(chip().attributes('aria-label')).toBe('live');
    expect(chip().classes()).toContain('live-chip-on');
    expect(chip().find('.q-icon').text()).toBe('sensors');
  });

  it('short of room is the icon alone with the word in its tooltip and aria-label', async () => {
    await mountControl({ word: false });
    expect(chip().classes()).toContain('live-chip-dot');
    expect(chip().text()).not.toContain('offline');
    expect(chip().attributes('aria-label')).toBe('offline');
    expect(chip().findComponent({ name: 'QTooltip' }).exists()).toBe(true);
  });

  it('reads nothing until it is opened', async () => {
    await mountControl();
    expect(serviceStatus).not.toHaveBeenCalled();
  });
});

describe('the dropdown', () => {
  it("reads the service once on opening and says the connection, the uptime, the build, each check and each worker in the reader's words", async () => {
    await mountControl();
    const board = useConsoleStore();
    board.setConnected(true);
    await flushPromises();

    await openPanel();

    expect(serviceStatus).toHaveBeenCalledTimes(1);
    const text = panelText();
    expect(text).toContain(`Live: this window receives updates, since ${localStamp(board.connectionChangedAt!)}.`);
    expect(text).toContain('Up 2 hours 5 minutes');
    expect(text).toContain(`since ${localStamp(wireInstant('2026-10-20T08:02:00Z'))}`);
    expect(text).toContain('2026.10.19.1+30.abc1234');
    expect(text).toContain('Health · healthy');
    expect(text).toContain('Database: open');
    expect(text).toContain('Data folder: writable');
    expect(text).toContain('Delivery pump: delivering');
    expect(text).toContain(`Last delivery pass ${localStamp(wireInstant('2026-10-20T10:06:59Z'))}`);
    expect(text).toContain(`worker-1: connected since ${localStamp(wireInstant('2026-10-20T08:02:10Z'))}, 2026.10.19.1+30.abc1234`);
    expect(text).toMatch(/Read \d/);

    const checks = [...panel()!.querySelectorAll<HTMLElement>('[data-test="live-status-check"]')];
    expect(checks.map((c) => c.dataset.ok)).toEqual(['true', 'true', 'true']);
  });

  it('says offline since when the connection dropped, a failed check as failed, and a dropped worker as dropped', async () => {
    serviceStatus.mockResolvedValue({
      ...healthy,
      status: 'unhealthy',
      checks: [...healthy.checks.slice(0, 2), { name: 'pump', ok: false, detail: 'no pass for over a minute' }],
      workers: [{ id: 'worker-1', version: null, state: 'dropped', connectedSince: '2026-10-20T08:02:10Z', droppedAt: '2026-10-20T10:00:00Z' }],
    });
    await mountControl();
    const board = useConsoleStore();
    board.setConnected(true);
    board.setConnected(false);
    await flushPromises();

    await openPanel();

    const text = panelText();
    expect(text).toContain(`Offline since ${localStamp(board.connectionChangedAt!)}: what is on screen may be out of date.`);
    expect(text).toContain('Health · unhealthy');
    expect(text).toContain('Delivery pump: no pass for over a minute');
    const pump = panel()!.querySelector<HTMLElement>('[data-test="live-status-check"][data-ok="false"]');
    expect(pump?.textContent).toContain('Delivery pump');
    expect(text).toContain(`worker-1: dropped at ${localStamp(wireInstant('2026-10-20T10:00:00Z'))}`);
  });

  it('says connecting before the hub ever connected, no worker when there is none, and not read when the read fails', async () => {
    serviceStatus.mockRejectedValue(new Error('503 Service Unavailable'));
    await mountControl();

    await openPanel();

    const text = panelText();
    expect(text).toContain('Connecting: this window has not received updates yet.');
    expect(text).toContain('Not read: 503 Service Unavailable');
    expect(text).toContain('Not read yet');

    serviceStatus.mockResolvedValue({ ...healthy, workers: [] });
    const again = [...document.body.querySelectorAll<HTMLElement>('[data-test="live-status-refresh"]')].pop()!;
    again.click();
    await flushPromises();
    expect(panelText()).toContain('No worker is connected: runs wait for one.');
    expect(panelText()).not.toContain('Not read:');
  });

  it('an uptime the Host could not read is not known', async () => {
    serviceStatus.mockResolvedValue({ ...healthy, startedAt: null });
    await mountControl();
    await openPanel();
    expect(panelText()).toContain('Up not known');
  });
});
