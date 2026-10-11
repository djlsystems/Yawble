// @vitest-environment happy-dom
//
// ADMIN > DIAGNOSTICS, MOUNTED CLOSED AND THEN OPENED.
//
// WHAT THESE CASES CAN SEE THAT `lib/diagnostics.spec.ts` CANNOT. That file pins the DECISION:
// given a view and a filter, which of the four answers is it. Whether the screen actually SAYS that
// answer is a template condition, and a source scan passes over it - the banner is in the file
// either way. These mount the thing and read what is on the page.
//
// The same goes for what is fetched. `diagnosticsQuery` is pinned as a pure function; that the
// dialog calls it with what the controls hold, on the OPENING EDGE rather than on first render, is
// what is checked here.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { readDiagnostics, readCliVersions, readVersion } = vi.hoisted(() => ({
  readDiagnostics: vi.fn(),
  readCliVersions: vi.fn(),
  readVersion: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  readDiagnostics,
  readCliVersions,
  readVersion,
}));

import DiagnosticsDialog from '../DiagnosticsDialog.vue';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

/** The vocabulary the SERVER sends. The screen's kind filter is built from this and never from a
 *  list of its own - see `DiagnosticsView.kinds`. */
const kinds = ['db.busy', 'http.refused', 'http.unhandled-exception', 'startup.instance'];

function view(over: Partial<{ events: unknown[]; total: number; hasAny: boolean | null }> = {}) {
  return {
    page: { events: over.events ?? [], total: over.total ?? 0 },
    hasAny: 'hasAny' in over ? over.hasAny : true,
    kinds,
  };
}

function row(over: Record<string, unknown> = {}) {
  return {
    seq: 12,
    occurredAt: '2026-09-19T10:00:00Z',
    severity: 'Error',
    kind: 'http.unhandled-exception',
    source: 'http',
    route: '/api/tenant-log',
    status: 500,
    exceptionType: 'TenantLogIsUnreadableException',
    message: 'The tenant log could not be read.',
    detail: '{"phase":"read"}',
    ...over,
  };
}

beforeEach(() => {
  readDiagnostics.mockReset();
  readDiagnostics.mockResolvedValue(view());
  readCliVersions.mockReset();
  readCliVersions.mockResolvedValue([]);
  readVersion.mockReset();
  readVersion.mockResolvedValue({
    version: '2026.09.23.1',
    commit: 'eed3fd00b9717e141ce8e505e99dbd76e3586035',
    builtAt: '2026-09-23T12:00:00Z',
  });
});

afterEach(resetBody);

/** QVirtualScroll lays out its slice on a 35ms debounce, so a row is on the page only after it. */
async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 60));
  await flushPromises();
}

async function open() {
  const wrapper = await mountDialog(DiagnosticsDialog);
  await settle();

  return wrapper;
}

/** The sentinel's own button - the same request its observer makes when it scrolls into view. */
async function loadOlder() {
  const button = [...document.body.querySelectorAll('.cursor-sentinel button')][0] as HTMLElement;
  button.click();
  await settle();
}

describe('Admin > Diagnostics', () => {
  /** The Host's own build, beside the CLI versions, without opening anything. */
  it('shows the version, commit and build time the host answers', async () => {
    await open();

    expect(readVersion).toHaveBeenCalledTimes(1);
    const text = bodyText();
    expect(text).toContain('2026.09.23.1');
    expect(text).toContain('eed3fd00b9717e141ce8e505e99dbd76e3586035');
    expect(text).toContain(new Date('2026-09-23T12:00:00Z').toLocaleString());
  });

  it('says the version could not be read rather than showing nothing', async () => {
    readVersion.mockRejectedValue(new Error('503 Service Unavailable'));

    await open();

    expect(bodyText()).toContain('The version could not be read: 503 Service Unavailable');
  });

  /** Each start's versions, newest first, with the CLI missing at a start said so. */
  it('shows which CLI versions each start of the volume had', async () => {
    readCliVersions.mockResolvedValue([
      { at: '2026-09-23T08:00:00Z', versions: { claude: '2.1.280 (Claude Code)', agy: null } },
      { at: '2026-09-20T08:00:00Z', versions: { claude: '2.1.270 (Claude Code)', agy: null } },
    ]);

    await open();
    (document.body.querySelector('.d-cli .q-item') as HTMLElement).click();
    await settle();

    expect(readCliVersions).toHaveBeenCalledTimes(1);
    const text = bodyText();
    expect(text).toContain('Agent CLI versions');
    expect(text).toContain('2.1.280 (Claude Code)');
    expect(text).toContain('2.1.270 (Claude Code)');
    expect(text).toContain('not installed');
    expect(document.body.querySelectorAll('.d-cli-changed')).toHaveLength(1);
  });

  it('says no start recorded its CLI versions rather than showing an empty table', async () => {
    await open();
    (document.body.querySelector('.d-cli .q-item') as HTMLElement).click();
    await settle();

    expect(bodyText()).toContain('No start has recorded its CLI versions');
  });

  it('reads the newest page when it opens', async () => {
    await open();

    expect(readDiagnostics).toHaveBeenCalledTimes(1);
    expect(readDiagnostics).toHaveBeenCalledWith({}, undefined, 50);
  });

  // Every table's columns resize (lib/resizableColumns.ts); this pins that the events table is one,
  // its columns known by name so hiding one never hands its width to another.
  it('gives each event column a resize handle and takes back the widths stored for this table', async () => {
    readDiagnostics.mockResolvedValue(view({ events: [row()], total: 1 }));
    localStorage.setItem('harness.columns.diagnostics', JSON.stringify({ occurredAt: 210 }));
    try {
      await open();

      const cells = Array.from(document.body.querySelectorAll('.diagnostics-head th')) as HTMLElement[];
      expect(cells.length).toBeGreaterThan(1);
      expect(cells.every((th) => th.querySelector('.os-col-resizer'))).toBe(true);
      expect(cells.find((th) => th.dataset.col === 'occurredAt')?.style.width).toBe('210px');
    } finally {
      localStorage.removeItem('harness.columns.diagnostics');
    }
  });

  it('lists what was captured', async () => {
    readDiagnostics.mockResolvedValue(view({ events: [row()], total: 1 }));

    await open();

    const text = bodyText();

    // The columns, as a reader sees them. `kind` reads as words with the dotted
    // name in a tooltip - a log a person has to decode is one they stop reading.
    expect(text).toContain('Unhandled exception');
    expect(text).toContain('/api/tenant-log');
    expect(text).toContain('500');
    expect(text).toContain('TenantLogIsUnreadableException');
    expect(text).toContain('The tenant log could not be read.');
    expect(text).toContain('Error');
  });

  /**
   * EMPTY BECAUSE NOTHING WAS CAPTURED IS NOT EMPTY BECAUSE NOTHING WENT WRONG, and the screen
   * says which. Three states, three sentences, and no two
   * of them the same.
   */
  it('says nothing was ever captured differently from nothing having gone wrong', async () => {
    readDiagnostics.mockResolvedValue(view({ hasAny: false }));
    await open();
    const never = bodyText();
    resetBody();

    readDiagnostics.mockResolvedValue(view({ hasAny: true }));
    await open();
    const quiet = bodyText();
    resetBody();

    readDiagnostics.mockResolvedValue(view({ hasAny: null }));
    await open();
    const unreadable = bodyText();

    expect(never).toContain('Nothing has ever been captured');
    expect(quiet).toContain('aged out');
    expect(unreadable).toContain('(unknown)');

    // Stated as a set too: the point is not the wording, it is that a reader is never shown the
    // same sentence for a broken store and a healthy one.
    expect(new Set([never, quiet, unreadable]).size).toBe(3);
  });

  it('says the store could not be read rather than showing an empty grid', async () => {
    readDiagnostics.mockResolvedValue(view({ hasAny: null }));

    await open();

    expect(bodyText()).toContain('could not be read');
  });

  /** An empty page under a filter is not the store being empty, and the screen must not say it is:
   *  the way out is to widen the filter, not to worry about the instance. */
  it('blames the filter when the store has rows and the filter matched none', async () => {
    readDiagnostics.mockResolvedValue(view({ events: [row()], total: 1 }));

    const wrapper = await open();

    readDiagnostics.mockResolvedValue(view({ hasAny: true }));

    const search = wrapper.findAllComponents({ name: 'QInput' }).at(-1);
    await search?.setValue('nothing matches this');
    await flushPromises();

    expect(bodyText()).toContain('Nothing matches these filters');
    expect(bodyText()).not.toContain('Nothing has ever been captured');
  });

  it('sends what was typed in the search box, wildcards and all', async () => {
    const wrapper = await open();

    const search = wrapper.findAllComponents({ name: 'QInput' }).at(-1);
    await search?.setValue('100% busy');
    await settle();

    expect(readDiagnostics).toHaveBeenLastCalledWith({ search: '100% busy' }, undefined, 50);
  });

  /**
   * THE EXPLANATION MATCHES THE ROWS, NOT THE KEYSTROKES SINCE. The empty state is read against the
   * filter the rows were FETCHED with; reading it against the live one would explain an empty grid
   * with a narrowing the person typed after it came back.
   */
  it('explains the page it is showing rather than the filter being typed', async () => {
    readDiagnostics.mockResolvedValue(view({ hasAny: false }));

    const wrapper = await open();
    expect(bodyText()).toContain('Nothing has ever been captured');

    // A filter typed against a store that answered "never captured" must not silently become
    // "nothing matches these filters" before the next answer arrives.
    readDiagnostics.mockImplementation(() => new Promise(() => {}));

    const search = wrapper.findAllComponents({ name: 'QInput' }).at(-1);
    await search?.setValue('busy');
    await flushPromises();

    expect(bodyText()).not.toContain('Nothing matches these filters');
  });

  /** The kind list is the SERVER's, so a kind added on the server reaches the filter with no change
   *  here. A copy maintained in the SPA is the three-place mistake `lib/ribbon.ts` records. */
  it('offers the kind vocabulary the server sent', async () => {
    const wrapper = await open();

    const kindSelect = wrapper.findAllComponents({ name: 'QSelect' })[0];

    expect(kindSelect?.props('options')).toEqual([
      { label: 'Database busy', value: 'db.busy' },
      { label: 'Refused', value: 'http.refused' },
      { label: 'Unhandled exception', value: 'http.unhandled-exception' },
      { label: 'Instance', value: 'startup.instance' },
    ]);
  });

  /** THREE AND NO MORE. The severity scale is the one closed set the screen may hold itself, and a
   *  fourth appearing here would mean somebody widened a scale that exists to be narrow. */
  it('offers exactly the three severities', async () => {
    const wrapper = await open();

    const severitySelect = wrapper.findAllComponents({ name: 'QSelect' })[1];

    expect(severitySelect?.props('options')).toEqual([
      { label: 'Error', value: 'Error' },
      { label: 'Warning', value: 'Warning' },
      { label: 'Info', value: 'Info' },
    ]);
  });

  /** Older rows by cursor - the lowest seq held - and never by offset. */
  it('reads older rows below the last one it holds', async () => {
    const page = Array.from({ length: 50 }, (_, i) => row({ seq: 200 - i }));
    readDiagnostics.mockResolvedValueOnce(view({ events: page, total: 60 }));
    readDiagnostics.mockResolvedValueOnce(view({ events: [row({ seq: 12, message: 'the oldest' })], total: 60 }));

    await open();
    await loadOlder();

    expect(readDiagnostics).toHaveBeenLastCalledWith({}, 151, 50);
    expect(bodyText()).toContain('51 of 60 shown');
    expect(bodyText()).toContain('Nothing older.');
  });

  /** A narrowing that kept the loaded rows would show rows it excludes; it reads from the top. */
  it('clears and reads from the top when the filter changes', async () => {
    const page = Array.from({ length: 50 }, (_, i) => row({ seq: 200 - i }));
    readDiagnostics.mockResolvedValue(view({ events: page, total: 500 }));

    const wrapper = await open();
    await loadOlder();

    const search = wrapper.findAllComponents({ name: 'QInput' }).at(-1);
    await search?.setValue('busy');
    await settle();

    expect(readDiagnostics).toHaveBeenLastCalledWith({ search: 'busy' }, undefined, 50);
  });

  /** A fetch that FAILED is this browser's problem - not the store reporting it cannot be read. The
   *  banner says what happened, and the screen must not put (unknown) on the store's own state. */
  it('shows a failed fetch as a failed fetch rather than as an unreadable store', async () => {
    readDiagnostics.mockRejectedValue(new Error('Network is down.'));

    await open();

    const text = bodyText();

    expect(text).toContain('Network is down.');
    expect(text).not.toContain('(unknown)');
  });
});

describe('Admin > Diagnostics, date range', () => {
  /** The date boxes are debounced by 400ms, the way a person types a date. */
  async function typeDate(label: string, value: string) {
    await probe.type(label, value);
    await new Promise((resolve) => setTimeout(resolve, 450));
    await probe.settle();
  }

  it('refuses a To before From on the To field, and does not ask the server', async () => {
    await open();
    readDiagnostics.mockClear();

    await typeDate('From', '2026-09-20');
    expect(readDiagnostics).toHaveBeenCalledTimes(1);

    await typeDate('To', '2026-09-19');

    expect(probe.hasError('To')).toBe(true);
    expect(bodyText()).toContain('To is before From');
    expect(readDiagnostics).toHaveBeenCalledTimes(1);
  });

  it('accepts a range in order', async () => {
    await open();
    readDiagnostics.mockClear();

    await typeDate('From', '2026-09-19');
    await typeDate('To', '2026-09-20');

    expect(probe.hasError('To')).toBe(false);
    expect(readDiagnostics).toHaveBeenCalledTimes(2);
    expect(readDiagnostics).toHaveBeenLastCalledWith({ from: '2026-09-19', to: '2026-09-20' }, undefined, 50);
  });

  /** Older rows come through the list's own reader, not through a filter control. It must meet the
   *  same guard: a range that ends before it starts clears the list, offers no "older" to ask for,
   *  and asks the server for nothing. */
  it('does not ask the server for older rows while To is before From', async () => {
    const page = Array.from({ length: 50 }, (_, i) => row({ seq: 200 - i }));
    readDiagnostics.mockResolvedValue(view({ events: page, total: 200 }));
    await open();

    await typeDate('From', '2026-09-20');
    await typeDate('To', '2026-09-19');
    await settle();
    readDiagnostics.mockClear();

    expect(document.body.querySelector('.cursor-sentinel button')).toBeNull();
    expect(probe.hasError('To')).toBe(true);
    expect(bodyText()).not.toContain('of 200 shown');
    expect(readDiagnostics).not.toHaveBeenCalled();
  });

  /** And the empty-state banner stays quiet: the To field already says why there is nothing. */
  it('does not claim nothing matches while To is before From', async () => {
    await open();

    await typeDate('From', '2026-09-20');
    await typeDate('To', '2026-09-19');
    await settle();

    expect(document.body.querySelectorAll('.q-banner').length).toBe(0);
  });

  it('reads older rows of a range in order', async () => {
    const page = Array.from({ length: 50 }, (_, i) => row({ seq: 200 - i }));
    readDiagnostics.mockResolvedValue(view({ events: page, total: 200 }));
    await open();

    await typeDate('From', '2026-09-19');
    await typeDate('To', '2026-09-20');
    await settle();
    readDiagnostics.mockClear();

    await loadOlder();

    expect(readDiagnostics).toHaveBeenCalledTimes(1);
    expect(readDiagnostics).toHaveBeenLastCalledWith({ from: '2026-09-19', to: '2026-09-20' }, 151, 50);
  });
});
