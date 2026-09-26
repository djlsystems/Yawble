// @vitest-environment happy-dom
//
// `TriggersDialog` with a folder-change trigger: the row says when the folder was last
// listed, how long that took and when the trigger last fired; a poll that could not list says why;
// and a save the server refuses is said INSIDE the editor, in the server's sentence, not as a toast.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers: vi.fn(),
  listEvents: vi.fn(),
  listWatchRoots: vi.fn(),
  testTriggerFolder: vi.fn(),
  updateSchedule: vi.fn(),
}));

import TriggersDialog from '../TriggersDialog.vue';
import {
  listContainerTriggers,
  listEvents,
  listWatchRoots,
  testTriggerFolder,
  updateSchedule,
} from '../../api/client';
import { asTeamId, type TeamTrigger } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

function folderRow(over: Partial<TeamTrigger> = {}): TeamTrigger {
  return {
    id: 'f1',
    team: asTeamId('Alpha'),
    container: 'Manager',
    name: 'Inbox',
    instruction: 'Sort the inbox.',
    kind: 'folderChange',
    expression: null,
    timezone: null,
    intervalSeconds: null,
    fireAt: null,
    idleOnly: true,
    enabled: true,
    nextDueAt: null,
    lastFiredAt: null,
    lastOutcome: null,
    lastSeq: null,
    missedCount: 0,
    createdAt: '2026-09-23T10:00:00Z',
    createdBy: 'u1',
    eventType: 'file.changed',
    filter: null,
    watchRoot: 'root:Share',
    watchPath: 'scans',
    watchGlob: null,
    pollSeconds: 60,
    quietSeconds: 30,
    minIntervalSeconds: 60,
    lastPollAt: minutesAgo(2),
    lastPollMs: 2400,
    lastPollEntries: 12,
    lastPollError: null,
    lastChangeAt: minutesAgo(180),
    ...over,
  };
}

beforeEach(() => {
  vi.mocked(listEvents).mockResolvedValue([]);
  vi.mocked(listWatchRoots).mockResolvedValue([
    { value: 'documents', label: 'Team documents' },
    { value: 'root:Share', label: 'Share' },
  ]);
});

afterEach(() => {
  vi.mocked(listContainerTriggers).mockReset();
  vi.mocked(updateSchedule).mockReset();
  vi.mocked(testTriggerFolder).mockReset();
  resetBody();
});

const props = { team: asTeamId('Alpha'), container: 'Manager', subscribes: [] };

describe('TriggersDialog, a folder-change row', () => {
  it('says what it watches, when it last polled, how long that took, and when it last fired', async () => {
    vi.mocked(listContainerTriggers).mockResolvedValue([folderRow()]);
    const wrapper = await mountDialog(TriggersDialog, props);

    expect(bodyText()).toContain('when files change in Share/scans');
    expect(bodyText()).toContain('last poll 2 minutes ago, took 2.4 s, 12 files');
    expect(bodyText()).toContain('last fired 3 hours ago');

    wrapper.unmount();
  });

  it('says why the last poll could not list the folder', async () => {
    const lastPollError = 'There is no folder at "scans" in the root "Share".';
    vi.mocked(listContainerTriggers).mockResolvedValue([folderRow({ lastPollError })]);
    const wrapper = await mountDialog(TriggersDialog, props);

    expect(document.body.querySelector('.folder-poll-error')?.textContent).toContain(lastPollError);

    wrapper.unmount();
  });

  it('says it has not polled yet for a new folder trigger', async () => {
    vi.mocked(listContainerTriggers).mockResolvedValue([
      folderRow({ lastPollAt: null, lastPollMs: null, lastPollEntries: null, lastChangeAt: null }),
    ]);
    const wrapper = await mountDialog(TriggersDialog, props);

    expect(bodyText()).toContain('not polled yet • never fired');

    wrapper.unmount();
  });

  it('shows a refused save inside the editor, in the server\'s sentence', async () => {
    const refusal = 'pollSeconds must be at least 15.';
    vi.mocked(listContainerTriggers).mockResolvedValue([folderRow()]);
    vi.mocked(updateSchedule).mockRejectedValue(new Error(refusal));
    const wrapper = await mountDialog(TriggersDialog, props);

    (document.body.querySelector('[aria-label="Edit this trigger"]') as HTMLButtonElement).click();
    await probe.settle();

    await probe.type('Quiet period (s)', '10');
    probe.button('Save').click();
    await probe.settle();

    expect(updateSchedule).toHaveBeenCalledWith(asTeamId('Alpha'), 'f1', { quietSeconds: 10 });
    expect(document.body.querySelector('.trigger-refusal')?.textContent).toContain(refusal);

    wrapper.unmount();
  });

  it('hands the editor a Test action bound to this team', async () => {
    vi.mocked(listContainerTriggers).mockResolvedValue([folderRow()]);
    vi.mocked(testTriggerFolder).mockResolvedValue({
      ok: true, refusal: null, folder: 'Share/scans', count: 0, truncated: false, elapsedMs: 840, entries: [],
    });
    const wrapper = await mountDialog(TriggersDialog, props);

    (document.body.querySelector('[aria-label="Edit this trigger"]') as HTMLButtonElement).click();
    await probe.settle();
    probe.button('Test this folder').click();
    await probe.settle();

    expect(testTriggerFolder).toHaveBeenCalledWith(asTeamId('Alpha'), {
      watchRoot: 'root:Share', watchPath: 'scans', watchGlob: null,
    });
    expect(bodyText()).toContain('Sees 0 files in Share/scans');
    expect(bodyText()).toContain('Listing took 840 ms');

    wrapper.unmount();
  });
});
