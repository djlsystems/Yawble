// @vitest-environment happy-dom
//
// THE ACTIVITY MONITOR: the app-bar gauge's colours and tooltip, the dropdown's figures, "not
// measured" never read as 0, and figures that have gone stale saying how old they are. It reads the
// one route once and the live connection after that.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';

const { getCapacity } = vi.hoisted(() => ({ getCapacity: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getCapacity,
}));

import CapacityMonitor from '../CapacityMonitor.vue';
import CapacityPanel from '../CapacityPanel.vue';
import { useCapacityStore } from '../../stores/capacity';
import { useConsoleStore } from '../../stores/console';
import type { CapacitySample, WorkerSample } from '../../api/types';
import { memoryAt, pressure, sample, unmeasured } from '../../test/capacityFixtures';
import { resetBody } from '../../test/mountQuasar';

const At = '2026-10-01T10:00:00Z';

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
  vi.setSystemTime(Date.parse(At) + 3_000);
  getCapacity.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

/** A fresh store per mount: a second gauge in one test reads the route as the first one did. */
async function mountGauge(latest: CapacitySample | null) {
  setActivePinia(createPinia());
  getCapacity.mockResolvedValue({ intervalSeconds: 5, latest, history: latest ? [latest] : [] });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:any(.*)', component: { template: '<div />' } }] });
  const wrapper = mount(CapacityMonitor, { global: { plugins: [router] } });
  await flushPromises();
  return wrapper;
}

const level = (wrapper: Awaited<ReturnType<typeof mountGauge>>) =>
  wrapper.get('[data-test="capacity-gauge"]').attributes('data-level');

const label = (wrapper: Awaited<ReturnType<typeof mountGauge>>) =>
  wrapper.get('[data-test="capacity-gauge"]').attributes('aria-label');

describe('the app-bar gauge', () => {
  it('reads the one route once when it mounts', async () => {
    await mountGauge(sample());

    expect(getCapacity).toHaveBeenCalledTimes(1);
  });

  it('opens the dropdown with the figures when clicked', async () => {
    const wrapper = await mountGauge(memoryAt(87));

    await wrapper.get('[data-test="capacity-gauge"]').trigger('click');
    await flushPromises();

    expect(document.body.querySelector('[data-test="capacity-memory"]')?.textContent).toContain('11.2 of 12.9 GB in use');
  });

  // The Concierge panel sits at 7000 and a QMenu at 6000: without the lift the dropdown opens
  // behind an open panel. The class is what `app.scss` raises above it.
  it('opens its dropdown above the Concierge panel', async () => {
    const wrapper = await mountGauge(memoryAt(87));

    await wrapper.get('[data-test="capacity-gauge"]').trigger('click');
    await flushPromises();

    const menu = document.body.querySelector('[data-test="capacity-memory"]')?.closest('.q-menu');
    expect(menu?.classList.contains('above-concierge')).toBe(true);
  });

  it('is green, amber and red by memory in use, naming the figure that set it', async () => {
    expect(level(await mountGauge(memoryAt(50)))).toBe('green');
    expect(level(await mountGauge(memoryAt(80)))).toBe('amber');

    const red = await mountGauge(memoryAt(95));
    expect(level(red)).toBe('red');
    expect(label(red)).toBe(
      "Activity monitor: Memory of this worker's container, against its own limit: 12.3 of 12.9 GB in use (95%)",
    );
  });

  it('turns red on memory pressure with memory low, and its tooltip says pressure set it', async () => {
    const s = memoryAt(30);
    s.memory.pressure = pressure(93);

    const wrapper = await mountGauge(s);

    expect(level(wrapper)).toBe('red');
    expect(label(wrapper)).toContain('Memory pressure: work waited for memory 93% of the last 10 s');
  });

  it('turns amber on CPU pressure', async () => {
    const s = memoryAt(30);
    s.cpu.pressure = pressure(78);

    expect(level(await mountGauge(s))).toBe('amber');
  });

  it('reads not measured, not green, when nothing was measured', async () => {
    const wrapper = await mountGauge(unmeasured());

    expect(level(wrapper)).toBe('unmeasured');
    expect(label(wrapper)).toContain('not measured');
  });

  it('follows each pushed sample, and goes stale when the pushes stop', async () => {
    const wrapper = await mountGauge(memoryAt(50));
    const capacity = useCapacityStore();

    capacity.apply({ ...memoryAt(92), at: '2026-10-01T10:00:05Z' });
    await flushPromises();
    expect(level(wrapper)).toBe('red');

    vi.advanceTimersByTime(60_000);
    await flushPromises();
    expect(level(wrapper)).toBe('stale');
    expect(label(wrapper)).toContain('Figures are stale: last measured 58 s ago');
    expect(getCapacity).toHaveBeenCalledTimes(1);
  });
});

async function mountPanel(latest: CapacitySample | null, over: { ageSeconds?: number; stale?: boolean } = {}) {
  const wrapper = mount(CapacityPanel, {
    props: {
      sample: latest,
      history: latest ? [latest] : [],
      ageSeconds: over.ageSeconds ?? 3,
      stale: over.stale ?? false,
    },
  });
  await flushPromises();
  return wrapper;
}

const section = (wrapper: Awaited<ReturnType<typeof mountPanel>>, name: string) =>
  wrapper.get(`[data-test="capacity-${name}"]`).text();

describe('the dropdown', () => {
  it('shows memory, CPU, pressure and processes against their limits', async () => {
    const s = memoryAt(87);
    s.memory.pressure = pressure(14);

    const wrapper = await mountPanel(s);

    expect(section(wrapper, 'memory')).toContain('11.2 of 12.9 GB in use (87%)');
    expect(section(wrapper, 'memory')).toContain('work waited for memory 14% of the last 10 s');
    expect(section(wrapper, 'cpu')).toContain('1.0 of 4 CPUs in use (25%)');
    expect(section(wrapper, 'pids')).toContain('312 of 4096 processes');
    expect(wrapper.findAll('svg.capacity-spark')).toHaveLength(2);
  });

  // The engine's total (the size screen, doctor) is another figure: this one is the container's
  // own memory against the container's own limit, and says so rather than reading as a disagreement.
  it("names the memory figure as this worker's container against its own limit", async () => {
    const wrapper = await mountPanel(memoryAt(87));

    expect(wrapper.get('[data-test="capacity-memory"] .text-subtitle2').text()).toBe(
      "Memory of this worker's container, against its own limit",
    );
  });

  it('shows runs running and waiting against the limit, the reserved slot, and each waiter\'s reason', async () => {
    const s = sample({
      runs: {
        limit: 4, managerReserved: 1, runningCount: 4, waitingCount: 1, running: [],
        waiting: [{ team: 'alpha', member: 'DeveloperA', since: At, reason: 'waiting for memory: 11.2 of 12.9 GB in use' }],
      },
      admission: { memoryPercent: 80, memoryPressurePercent: 10, holding: 'waiting for memory: 11.2 of 12.9 GB in use' },
    });

    const runs = section(await mountPanel(s), 'runs');

    expect(runs).toContain('4 of 4 running, 1 waiting, 1 more reserved for a Manager');
    expect(runs).toContain('DeveloperA on alpha: waiting for memory: 11.2 of 12.9 GB in use');
  });

  it('shows the heavy lease\'s holder and queue, and "not available" with no lease', async () => {
    const s = sample({
      heavyLease: {
        holders: 1,
        holding: [{ team: 'alpha', member: 'DeveloperA', since: At }],
        queued: [{ team: 'beta', member: 'DeveloperB', since: At }, { team: null, member: 'Concierge', since: At }],
      },
    });

    const lease = section(await mountPanel(s), 'lease');
    expect(lease).toContain('Held by DeveloperA on alpha');
    expect(lease).toContain('Queued: 1. DeveloperB on beta, 2. the Concierge');

    expect(section(await mountPanel(sample({ heavyLease: null })), 'lease')).toContain('not available');
  });

  it('shows the top runs by memory and CPU with team and member, each team a link', async () => {
    const s = sample({
      topByMemory: [{ team: 'alpha', member: 'DeveloperA', processes: 14, residentBytes: 3.4e9, cpuPercent: 180 }],
      topByCpu: [{ team: 'beta', member: 'DeveloperB', processes: 3, residentBytes: 2e8, cpuPercent: 250 }],
    });

    const wrapper = await mountPanel(s);

    expect(section(wrapper, 'top-memory')).toContain('alpha DeveloperA: 3.4 GB, 14 processes');
    expect(section(wrapper, 'top-cpu')).toContain('beta DeveloperB: 250% of a CPU');

    await wrapper.get('[data-test="capacity-top-cpu"] a').trigger('click');
    expect(wrapper.emitted('team')).toEqual([['beta']]);
  });

  it('reads "not measured" for every figure the Host could not read, and never 0', async () => {
    const wrapper = await mountPanel(unmeasured());

    for (const name of ['memory', 'cpu', 'pids']) {
      expect(section(wrapper, name), name).toContain('not measured');
      expect(section(wrapper, name), name).not.toMatch(/\b0(\.0)? (GB|of|CPUs|processes)\b/);
    }
    expect(section(wrapper, 'memory')).toContain('Pressure: not measured');
    expect(section(wrapper, 'cpu')).toContain('Pressure: not measured');
  });

  it('says the figures are stale and how old they are', async () => {
    const wrapper = await mountPanel(memoryAt(50), { ageSeconds: 95, stale: true });

    expect(wrapper.get('[data-test="capacity-stale"]').text()).toContain('Figures are stale: last measured 1 min 35 s ago');
  });

  it('says nothing is stale while the figures are fresh', async () => {
    expect((await mountPanel(memoryAt(50))).find('[data-test="capacity-stale"]').exists()).toBe(false);
  });
});

describe('a team link', () => {
  it('opens that team on the board', async () => {
    const wrapper = await mountGauge(sample());
    const board = useConsoleStore();
    const setActiveTeam = vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    await wrapper.get('[data-test="capacity-gauge"]').trigger('click');
    await flushPromises();
    wrapper.findComponent(CapacityPanel).vm.$emit('team', 'alpha');

    expect(setActiveTeam).toHaveBeenCalledWith('alpha');
  });
});

describe('the workers', () => {
  const worker = (id: string, over: Partial<WorkerSample> = {}): WorkerSample => ({
    id,
    version: '2026.10.02.1',
    connectedSince: At,
    state: 'connected',
    droppedAt: null,
    capacity: {
      cpus: 4, memoryLimitBytes: 10e9, bound: 3, sampledAt: At, memoryInUseBytes: 2e9, memoryPercent: 20, notMeasured: [],
    },
    holding: null,
    runs: [],
    ...over,
  });

  it('lists each worker with its own memory, CPUs, bound and runs', async () => {
    const s = sample({
      workers: [
        worker('w1', { runs: [{ team: 'alpha', member: 'DeveloperA', since: At }] }),
        worker('w2', {
          capacity: { cpus: 8, memoryLimitBytes: 10e9, bound: 2, sampledAt: At, memoryInUseBytes: 9.5e9, memoryPercent: 95, notMeasured: [] },
          holding: 'waiting for memory: 9.5 of 10.0 GB in use',
        }),
      ],
    });

    const workers = section(await mountPanel(s), 'workers');

    expect(workers).toContain('w1');
    expect(workers).toContain('2.0 of 10.0 GB in use, 4 CPUs, up to 3 runs, 1 running');
    expect(workers).toContain('9.5 of 10.0 GB in use, 8 CPUs, up to 2 runs, 0 running');
    expect(workers).toContain('a run asking it now would be waiting for memory: 9.5 of 10.0 GB in use');
  });

  it('says a dropped worker is waiting to come back, and "not measured" for what it did not measure', async () => {
    const s = sample({
      workers: [
        worker('w1', {
          state: 'dropped',
          droppedAt: At,
          capacity: { cpus: null, memoryLimitBytes: null, bound: 1, sampledAt: null, memoryInUseBytes: null, memoryPercent: null, notMeasured: ['memory.limit'] },
        }),
      ],
    });

    const workers = section(await mountPanel(s), 'workers');

    expect(workers).toContain('memory not measured, CPUs not measured, up to 1 run');
    expect(workers).toContain('connection dropped, waiting for it to come back');
    expect(workers).not.toMatch(/\b0(\.0)? of\b/);
  });

  it('names the worker of each top run when there is more than one', async () => {
    const s = sample({
      workers: [worker('w1'), worker('w2')],
      topByMemory: [{ team: 'alpha', member: 'DeveloperA', processes: 14, residentBytes: 3.4e9, cpuPercent: 180, worker: 'w2' }],
    });

    expect(section(await mountPanel(s), 'top-memory')).toContain('alpha DeveloperA on w2: 3.4 GB, 14 processes');
  });

  it('says nothing new for the Host\'s own one worker', async () => {
    const s = sample({
      workers: [worker('local')],
      topByMemory: [{ team: 'alpha', member: 'DeveloperA', processes: 14, residentBytes: 3.4e9, cpuPercent: 180, worker: 'local' }],
    });

    const wrapper = await mountPanel(s);

    expect(wrapper.find('[data-test="capacity-workers"]').exists()).toBe(false);
    expect(section(wrapper, 'top-memory')).toContain('alpha DeveloperA: 3.4 GB, 14 processes');
  });
});
