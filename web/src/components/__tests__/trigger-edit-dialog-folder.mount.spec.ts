// @vitest-environment happy-dom
//
// The folder-change arm of `TriggerEditDialog`, mounted. `lib/triggers` already pins the
// rules; this asks whether the dialog is wired to them, whether "Test this folder" says what the
// server saw in the server's own words, and whether a refused save is shown inline.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QSelect } from 'quasar';
import TriggerEditDialog from '../TriggerEditDialog.vue';
import type { EventDefinition, FolderTestResult, TeamTrigger } from '../../api/types';
import { asTeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

afterEach(resetBody);

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
    watchRoot: 'documents',
    watchPath: 'inbox',
    watchGlob: '*.pdf',
    pollSeconds: 60,
    quietSeconds: 30,
    minIntervalSeconds: 60,
    ...over,
  };
}

const props = (over: Record<string, unknown> = {}) => ({
  trigger: folderRow(),
  container: 'Manager',
  events: [],
  saving: false,
  watchRoots: [
    { value: 'documents', label: 'Team documents' },
    { value: 'root:Share', label: 'Share' },
  ],
  ...over,
});

const seen: FolderTestResult = {
  ok: true,
  refusal: null,
  folder: 'documents/inbox',
  count: 3,
  truncated: false,
  elapsedMs: 12,
  entries: [
    { path: 'inbox/a.pdf', size: 1024, modifiedAt: '2026-09-23T10:00:00+00:00' },
    { path: 'inbox/b.pdf', size: 2048, modifiedAt: '2026-09-23T10:00:01+00:00' },
    { path: 'inbox/c.pdf', size: 4096, modifiedAt: '2026-09-23T10:00:02+00:00' },
  ],
};

describe('TriggerEditDialog, folder change', () => {
  it('shows the folder fields with the row\'s values, and Save is enabled', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, props());

    expect(probe.field('Path').value).toBe('inbox');
    expect(probe.field('Glob (optional)').value).toBe('*.pdf');
    expect(probe.field('Poll every (s)').value).toBe('60');
    expect(probe.field('Quiet period (s)').value).toBe('30');
    expect(probe.field('Minimum interval (s)').value).toBe('60');
    expect(bodyText()).toContain('Team documents');
    expect(probe.isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it('fills the folder-trigger defaults for a folder row that carries none', async () => {
    const trigger = folderRow({ pollSeconds: null, quietSeconds: null, minIntervalSeconds: null });
    const wrapper = await mountDialog(TriggerEditDialog, props({ trigger }));

    expect(probe.field('Poll every (s)').value).toBe('60');
    expect(probe.field('Quiet period (s)').value).toBe('30');
    expect(probe.field('Minimum interval (s)').value).toBe('60');

    wrapper.unmount();
  });

  it('refuses a poll interval below 15 under the field, and accepts 15', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, props());

    await probe.type('Poll every (s)', '14');
    await probe.blur('Poll every (s)');

    expect(probe.hasError('Poll every (s)')).toBe(true);
    expect(probe.isDisabled('Save')).toBe(true);

    await probe.type('Poll every (s)', '15');

    expect(probe.isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it('refuses an absolute path under the field and does not offer the test', async () => {
    const testFolder = vi.fn();
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    await probe.type('Path', '/etc');
    await probe.blur('Path');

    expect(probe.hasError('Path')).toBe(true);
    expect(probe.isDisabled('Test this folder')).toBe(true);

    wrapper.unmount();
  });

  it('emits the folder fields on Save', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, props());

    await probe.type('Quiet period (s)', '45');
    probe.button('Save').click();
    await probe.settle();

    const [[draft]] = wrapper.emitted('save') as [[Record<string, unknown>]];

    expect(draft).toMatchObject({
      mode: 'folderChange',
      watchRoot: 'documents',
      watchPath: 'inbox',
      watchGlob: '*.pdf',
      pollSeconds: 60,
      quietSeconds: 45,
      minIntervalSeconds: 60,
    });

    wrapper.unmount();
  });

  it('tests the folder it is pointed at and says what it saw and how long listing took', async () => {
    const testFolder = vi.fn().mockResolvedValue(seen);
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    probe.button('Test this folder').click();
    await probe.settle();

    expect(testFolder).toHaveBeenCalledWith({ watchRoot: 'documents', watchPath: 'inbox', watchGlob: '*.pdf' });
    expect(bodyText()).toContain('Sees 3 files in documents/inbox');
    expect(bodyText()).toContain('Listing took 12 ms');
    expect(bodyText()).toContain('inbox/c.pdf');

    wrapper.unmount();
  });

  it('says how many more there were when the list is cut short', async () => {
    const testFolder = vi.fn().mockResolvedValue({ ...seen, count: 250, truncated: true });
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    probe.button('Test this folder').click();
    await probe.settle();

    expect(bodyText()).toContain('and 247 more');

    wrapper.unmount();
  });

  it('shows the server\'s refusal sentence when the folder cannot be watched', async () => {
    const refusal = 'That path leaves the team\'s documents folder.';
    const testFolder = vi.fn().mockResolvedValue({
      ok: false, refusal, folder: null, count: 0, truncated: false, elapsedMs: 0, entries: [],
    });
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    probe.button('Test this folder').click();
    await probe.settle();

    expect(bodyText()).toContain(refusal);
    expect(bodyText()).not.toContain('Sees ');

    wrapper.unmount();
  });

  it('shows a call that failed outright in its own message', async () => {
    const testFolder = vi.fn().mockRejectedValue(new Error('No team is named "Alpha".'));
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    probe.button('Test this folder').click();
    await probe.settle();

    expect(bodyText()).toContain('No team is named "Alpha".');

    wrapper.unmount();
  });

  it('drops a test result once the path it was about changes', async () => {
    const testFolder = vi.fn().mockResolvedValue(seen);
    const wrapper = await mountDialog(TriggerEditDialog, props({ testFolder }));

    probe.button('Test this folder').click();
    await probe.settle();
    expect(bodyText()).toContain('Sees 3 files');

    await probe.type('Path', 'outbox');

    expect(bodyText()).not.toContain('Sees 3 files');

    wrapper.unmount();
  });

  it('shows the server\'s refusal of a save inline, in its own words', async () => {
    const refusal = 'The file-browser root "Share" does not allow watching. Set "allowWatch": true on it under FileBrowser:Roots.';
    const wrapper = await mountDialog(TriggerEditDialog, props({ refusal }));

    expect(document.body.querySelector('.trigger-refusal')?.textContent).toContain(refusal);

    wrapper.unmount();
  });

  it('keeps a root the server no longer offers, rather than re-pointing the row', async () => {
    const trigger = folderRow({ watchRoot: 'root:Gone' });
    const wrapper = await mountDialog(TriggerEditDialog, props({ trigger }));

    expect(bodyText()).toContain('Gone (not watchable)');

    wrapper.unmount();
  });
});

// `file.changed`'s `changed` is the catalog's first `List` field. The server
// reads a List as its raw JSON text, so only `contains` means anything in a filter on it.
describe('TriggerEditDialog, a List field in the event filter', () => {
  const events: EventDefinition[] = [{
    type: 'file.changed',
    publisher: 'Platform',
    highVolume: false,
    inLedger: true,
    summary: 'Files were added, removed or resized in a watched folder.',
    fields: [
      { name: 'path', kind: 'String', summary: 'The folder that changed.' },
      { name: 'changed', kind: 'List', summary: 'The changed files, at most 100.' },
    ],
  }];

  const eventRow = (filter: string | null) => folderRow({
    kind: 'event', eventType: 'file.changed', filter,
    watchRoot: null, watchPath: null, watchGlob: null, pollSeconds: null, quietSeconds: null, minIntervalSeconds: null,
  });

  const select = (wrapper: Awaited<ReturnType<typeof mountDialog>>, label: string) =>
    wrapper.findAllComponents(QSelect).find((s) => s.props('label') === label)!;

  it('marks the list field, and offers only contains once it is chosen', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, props({ trigger: eventRow('path eq inbox'), events }));

    const labels = (select(wrapper, 'Field').props('options') as { label: string }[]).map((o) => o.label);
    expect(labels).toContain('changed (list) — The changed files, at most 100.');
    expect(labels).toContain('path — The folder that changed.');
    expect((select(wrapper, 'Op').props('options') as { value: string }[]).map((o) => o.value)).toEqual(['eq', 'contains']);

    select(wrapper, 'Field').vm.$emit('update:model-value', 'changed');
    await probe.settle();

    expect(select(wrapper, 'Op').props('modelValue')).toBe('contains');
    expect((select(wrapper, 'Op').props('options') as { value: string }[]).map((o) => o.value)).toEqual(['contains']);

    wrapper.unmount();
  });
});
