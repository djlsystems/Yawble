// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import BackupsSection from '../../src/renderer/src/components/BackupsSection.vue';
import HealthPanel from '../../src/renderer/src/components/HealthPanel.vue';
import type { YawbleApi } from '../../src/shared/api';

// A fake API only: no core, no network. Each method answers from `answers`; anything else fails the test.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const check = (id: string, status: string, fixes: any[] = []) => ({ id, title: `Check ${id}`, status, detail: `detail ${id}`, fix: fixes[0] ?? null, fixes });

describe('HealthPanel', () => {
  it('ShowsEachCheckWithItsStatus_AndRunAgainAsksTheDoctorAgain', async () => {
    let n = 0;
    const { api, calls } = fakeApi({ 'ops.doctor': () => ({ checks: n++ === 0 ? [check('db', 'ok'), check('disk', 'fail')] : [check('db', 'ok')] }) });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    const rows = w.findAll('[data-test=health-check]');
    expect(rows.map(r => r.attributes('data-status'))).toEqual(['ok', 'fail']);
    expect(rows[1].text()).toContain('detail disk');
    await w.find('[data-test=health-run]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=health-check]')).toHaveLength(1);
    expect(calls.filter(c => c.method === 'ops.doctor')).toHaveLength(2);
    expect(w.emitted('changed')).toHaveLength(2);
  });

  it('ShowsTheErrorWhenTheDoctorFails', async () => {
    const { api } = fakeApi({ 'ops.doctor': () => { throw new Error('core is down'); } });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=health-error]').text()).toContain('core is down');
    expect(w.find('[data-test=health-check]').exists()).toBe(false);
  });

  it('AFixThatOnlyOpensAScreenEmitsOpen_AndRunsNothing', async () => {
    const open = { id: 'open-connections', label: 'Open Connections', kind: 'open', confirm: false };
    const { api, calls } = fakeApi({ 'ops.doctor': () => ({ checks: [check('connections', 'warn', [open])] }) });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=health-fix]').trigger('click');
    expect(w.emitted('open')?.[0]).toEqual([open]);
    expect(calls.map(c => c.method)).toEqual(['ops.doctor']);
  });

  it('ARunFixCallsDoctorFixAndShowsItsMessage_ThenChecksAgain', async () => {
    const run = { id: 'rotate-logs', label: 'Rotate logs', kind: 'run', confirm: false };
    const { api, calls } = fakeApi({
      'ops.doctor': () => ({ checks: [check('logs', 'warn', [run])] }),
      'ops.doctorFix': () => ({ done: true, needsConfirm: false, message: 'Logs rotated.' }),
    });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=health-fix]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'ops.doctorFix')?.params).toEqual({ fix: 'rotate-logs', confirm: undefined });
    expect(w.find('[data-test=health-message]').text()).toBe('Logs rotated.');
    expect(calls.filter(c => c.method === 'ops.doctor')).toHaveLength(2);
  });

  it('ADestructiveFixAsksFirst_ShowingTheExactKeys_NothingRunsUntilYes_AndCancelRunsNothing', async () => {
    const remove = { id: 'remove-orphans', label: 'Remove unused credentials', kind: 'run', confirm: true };
    const { api, calls } = fakeApi({
      'ops.doctor': () => ({ checks: [check('creds', 'warn', [remove])] }),
      'ops.doctorFix': (p) => p.confirm
        ? { done: true, needsConfirm: false, message: 'removed 2 unused credential(s)' }
        : { done: false, needsConfirm: true, message: 'Remove 2 saved credential(s)?', targets: ['conn:old/token', 'conn:gone/key'] },
    });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=health-fix]').trigger('click');
    await flushPromises();
    // Asking only previews (no confirm): the doctor says what it would remove, and the panel shows exactly those keys.
    expect(calls.filter(c => c.method === 'ops.doctorFix').map(c => c.params)).toEqual([{ fix: 'remove-orphans' }]);
    const confirm = w.get('[data-test=health-confirm]');
    expect(confirm.findAll('[data-test=health-confirm-target]').map(t => t.text())).toEqual(['conn:old/token', 'conn:gone/key']);
    expect(confirm.text()).toContain('Remove 2 saved credential(s)?');
    await w.find('[data-test=health-confirm-no]').trigger('click');
    expect(w.find('[data-test=health-confirm]').exists()).toBe(false);
    expect(calls.filter(c => c.method === 'ops.doctorFix')).toHaveLength(1);

    await w.find('[data-test=health-fix]').trigger('click');
    await flushPromises();
    await w.find('[data-test=health-confirm-yes]').trigger('click');
    await flushPromises();
    // The yes carries the list shown, so the doctor can refuse if it moved.
    expect(calls.filter(c => c.method === 'ops.doctorFix').map(c => c.params).at(-1))
      .toEqual({ fix: 'remove-orphans', confirm: true, expect: ['conn:old/token', 'conn:gone/key'] });
    expect(w.find('[data-test=health-message]').text()).toBe('removed 2 unused credential(s)');
    expect(w.find('[data-test=health-confirm]').exists()).toBe(false);
  });

  it('WhenTheListChangedTheDoctorRefuses_AndThePanelShowsTheNewListToConfirmAgain', async () => {
    const remove = { id: 'remove-orphans', label: 'Remove unused credentials', kind: 'run', confirm: true };
    let removed = false;
    const { api, calls } = fakeApi({
      'ops.doctor': () => ({ checks: [check('creds', 'warn', [remove])] }),
      'ops.doctorFix': (p) => {
        if (!p.confirm) return { done: false, needsConfirm: true, message: 'Remove 1?', targets: ['conn:a/k'] };
        if (p.expect.length === 1) return { done: false, needsConfirm: true, changed: true, message: 'The list changed since it was confirmed. Remove 2?', targets: ['conn:a/k', 'conn:b/k'] };
        removed = true;
        return { done: true, needsConfirm: false, message: 'removed 2 unused credential(s)' };
      },
    });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=health-fix]').trigger('click');
    await flushPromises();
    await w.find('[data-test=health-confirm-yes]').trigger('click');
    await flushPromises();
    expect(removed).toBe(false);
    const again = w.get('[data-test=health-confirm]');
    expect(again.get('[data-test=health-changed]').text()).toContain('The list changed');
    expect(again.findAll('[data-test=health-confirm-target]').map(t => t.text())).toEqual(['conn:a/k', 'conn:b/k']);
    await w.find('[data-test=health-confirm-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter(c => c.method === 'ops.doctorFix').map(c => c.params).at(-1))
      .toEqual({ fix: 'remove-orphans', confirm: true, expect: ['conn:a/k', 'conn:b/k'] });
    expect(removed).toBe(true);
    expect(w.find('[data-test=health-message]').text()).toBe('removed 2 unused credential(s)');
  });

  it('APreviewThatFindsNothingToDoJustSaysSo', async () => {
    const remove = { id: 'remove-orphans', label: 'Remove unused credentials', kind: 'run', confirm: true };
    const { api } = fakeApi({
      'ops.doctor': () => ({ checks: [check('creds', 'warn', [remove])] }),
      'ops.doctorFix': () => ({ done: true, needsConfirm: false, message: 'no unused credentials', targets: [] }),
    });
    const w = mount(HealthPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=health-fix]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=health-confirm]').exists()).toBe(false);
    expect(w.find('[data-test=health-message]').text()).toBe('no unused credentials');
  });
});

describe('BackupsSection', () => {
  const backup = (name: string, size = 2048) => ({ name, time: '2026-11-10T08:00:00Z', size, reason: 'daily' });

  it('ListsBackupsAndTheKeepSetting_OrSaysThereAreNone', async () => {
    const { api } = fakeApi({ 'ops.backups': () => [backup('a.db'), backup('b.db', 3 * 1024 * 1024)], 'ops.keepDaily': () => ({ keepDaily: 5 }) });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    const rows = w.findAll('[data-test=backup-row]');
    expect(rows).toHaveLength(2);
    expect(rows[0].text()).toContain('2 KB');
    expect(rows[1].text()).toContain('3.0 MB');
    expect((w.find('[data-test=keep-daily]').element as HTMLInputElement).value).toBe('5');

    const empty = fakeApi({ 'ops.backups': () => [], 'ops.keepDaily': () => ({ keepDaily: 7 }) });
    const e = mount(BackupsSection, { props: { api: empty.api } });
    await flushPromises();
    expect(e.find('[data-test=no-backups]').exists()).toBe(true);
  });

  it('BackUpNowCallsTheCoreAndReloadsTheList', async () => {
    let list = [backup('a.db')];
    const { api, calls } = fakeApi({
      'ops.backups': () => list, 'ops.keepDaily': () => ({ keepDaily: 7 }),
      'ops.backupNow': () => { list = [backup('new.db'), ...list]; return backup('new.db'); },
    });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    await w.find('[data-test=backup-now]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=backups-message]').text()).toBe('Backed up: new.db');
    expect(w.findAll('[data-test=backup-row]')).toHaveLength(2);
    expect(calls.some(c => c.method === 'ops.backupNow')).toBe(true);
  });

  it('KeepDailyRefusesLessThanOne_AndSavesAValidCount', async () => {
    const { api, calls } = fakeApi({
      'ops.backups': () => [], 'ops.keepDaily': () => ({ keepDaily: 7 }), 'ops.setKeepDaily': (p: any) => ({ keepDaily: p.count }),
    });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    await w.find('[data-test=keep-daily]').setValue('0');
    await flushPromises();
    expect(w.find('[data-test=backups-error]').text()).toContain('at least 1');
    expect(calls.some(c => c.method === 'ops.setKeepDaily')).toBe(false);
    await w.find('[data-test=keep-daily]').setValue('3');
    await flushPromises();
    expect(calls.find(c => c.method === 'ops.setKeepDaily')?.params).toEqual({ count: 3 });
    expect(w.find('[data-test=backups-error]').exists()).toBe(false);
  });

  it('RestoreAsksFirst_AndOnlyYesCallsRestoreWithTheBackupName', async () => {
    const { api, calls } = fakeApi({
      'ops.backups': () => [backup('a.db')], 'ops.keepDaily': () => ({ keepDaily: 7 }), 'ops.restore': () => ({}),
    });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    await w.find('[data-test=backup-restore]').trigger('click');
    expect(w.find('[data-test=restore-confirm]').exists()).toBe(true);
    expect(calls.some(c => c.method === 'ops.restore')).toBe(false);
    await w.find('[data-test=restore-no]').trigger('click');
    expect(w.find('[data-test=restore-confirm]').exists()).toBe(false);
    expect(calls.some(c => c.method === 'ops.restore')).toBe(false);

    await w.find('[data-test=backup-restore]').trigger('click');
    await w.find('[data-test=restore-yes]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'ops.restore')?.params).toEqual({ backup: 'a.db', confirm: true });
    // Staged only: it is applied when the core restarts, so the window must not claim it is done yet.
    expect(w.find('[data-test=backups-message]').text()).not.toContain('Restored');
    expect(w.find('[data-test=backups-message]').text()).toContain('restart');
  });

  const status = (last: any, pending: string | null = null) => () => ({ pending, last });

  it('AfterTheRestart_ARestoreStillWaitingSaysPendingAndWhy_NeverRestored', async () => {
    const { api } = fakeApi({
      'ops.backups': () => [backup('a.db')], 'ops.keepDaily': () => ({ keepDaily: 7 }),
      'ops.restoreStatus': status({ backup: 'a.db', state: 'pending', reason: 'the database was still in use', at: '2026-11-11T08:00:00Z' }, 'a.db'),
    });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    const s = w.find('[data-test=restore-status]');
    expect(s.attributes('data-state')).toBe('pending');
    expect(s.text()).toContain('not been applied yet');
    expect(s.text()).toContain('the database was still in use');
    expect(s.text()).not.toContain('Restored');
  });

  it('AfterTheRestart_AFailedRestoreShowsTheReason_AndARestoredOneSaysRestored', async () => {
    const failed = fakeApi({
      'ops.backups': () => [backup('a.db')], 'ops.keepDaily': () => ({ keepDaily: 7 }),
      'ops.restoreStatus': status({ backup: 'a.db', state: 'failed', reason: 'disk said no; the database from before was kept', at: '2026-11-11T08:00:00Z' }),
    });
    const f = mount(BackupsSection, { props: { api: failed.api } });
    await flushPromises();
    expect(f.find('[data-test=restore-status]').attributes('data-state')).toBe('failed');
    expect(f.find('[data-test=restore-status]').text()).toContain('disk said no');
    expect(f.find('[data-test=restore-status]').text()).not.toContain('Restored');

    const ok = fakeApi({
      'ops.backups': () => [backup('a.db')], 'ops.keepDaily': () => ({ keepDaily: 7 }),
      'ops.restoreStatus': status({ backup: 'a.db', state: 'restored', reason: null, at: '2026-11-11T08:00:00Z' }),
    });
    const o = mount(BackupsSection, { props: { api: ok.api } });
    await flushPromises();
    expect(o.find('[data-test=restore-status]').text()).toContain('Restored a.db');
  });

  it('ARestoreThatFailsShowsTheError', async () => {
    const { api } = fakeApi({
      'ops.backups': () => [backup('a.db')], 'ops.keepDaily': () => ({ keepDaily: 7 }), 'ops.restore': () => { throw new Error('database is locked'); },
    });
    const w = mount(BackupsSection, { props: { api } });
    await flushPromises();
    await w.find('[data-test=backup-restore]').trigger('click');
    await w.find('[data-test=restore-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=backups-error]').text()).toContain('database is locked');
  });
});
