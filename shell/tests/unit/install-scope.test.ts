// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { installScope, isAdministrator, otherSessionsRunning, updaterArgs } from '../../src/main/install-scope';
import UpdateBanner from '../../src/renderer/src/components/UpdateBanner.vue';
import UpdatesSection from '../../src/renderer/src/components/UpdatesSection.vue';
import type { UpdateInfo, UpdateStatus, YawbleApi } from '../../src/shared/api';

// Made-up paths and process lists only: nothing is run.
const env = { ProgramFiles: 'C:\\Program Files', 'ProgramFiles(x86)': 'C:\\Program Files (x86)', ProgramW6432: 'C:\\Program Files' } as NodeJS.ProcessEnv;

describe('install scope', () => {
  it('ProgramFilesIsForEveryone_LocalAppDataIsForOnePerson', () => {
    expect(installScope('C:\\Program Files\\Yawble\\yawble.exe', env)).toBe('machine');
    expect(installScope('c:\\program files\\yawble\\yawble.exe', env)).toBe('machine');
    expect(installScope('C:\\Users\\ada\\AppData\\Local\\Programs\\Yawble\\yawble.exe', env)).toBe('user');
    expect(installScope('C:\\Program FilesX\\Yawble\\yawble.exe', env)).toBe('user');
  });

  it('UpdatesInstallInTheSameScope', () => {
    expect(updaterArgs('machine')).toEqual(['--updated', '/S', '--force-run', '/allusers']);
    expect(updaterArgs('user')).toEqual(['--updated', '/S', '--force-run', '/currentuser']);
  });

  it('AnAdministratorIsInTheAdministratorsGroup', () => {
    expect(isAdministrator(() => '"BUILTIN\\Administrators","Alias","S-1-5-32-544","Group used for deny only"')).toBe(true);
    expect(isAdministrator(() => '"BUILTIN\\Users","Alias","S-1-5-32-545","Mandatory group"')).toBe(false);
    expect(isAdministrator(() => '')).toBe(false);
  });

  it('CountsOtherSessionsWithTheAppOpen_NotThisOne', () => {
    const list = [
      '"yawble.exe","100","Console","1","120,000 K"',
      '"yawble.exe","101","Console","1","80,000 K"',
      '"yawble.exe","200","RDP-Tcp#3","3","90,000 K"',
      '"yawble.exe","300","RDP-Tcp#4","4","90,000 K"',
      '"yawble.exe","301","RDP-Tcp#4","4","70,000 K"',
    ].join('\r\n');
    expect(otherSessionsRunning('yawble.exe', 100, () => list)).toBe(2);
    expect(otherSessionsRunning('yawble.exe', 999, () => list)).toBe(0);
    expect(otherSessionsRunning('yawble.exe', 100, () => 'INFO: No tasks are running which match the specified criteria.')).toBe(0);
  });
});

const ready = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({
  current: '2026.11.13.1', state: 'ready', latest: '2026.11.20.1', available: true, notesUrl: 'https://github.com/djlsystems/Yawble/releases',
  canInstall: true, auto: true, checkedAt: null, error: null, downloaded: 0, size: 0, ...extra,
});

function api(status: UpdateStatus, info: UpdateInfo | null, running = 0) {
  let installs = 0;
  const a: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      if (method === 'updates.status') return status as any;
      if (method === 'app.overview') return { slots: { running, limit: 4, queued: 0 }, teams: [] } as any;
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    shellSettings: async () => ({ runInBackground: false, startAtLogin: false, packaged: true }),
    installUpdate: async () => { installs++; },
    ...(info ? { updateInfo: async () => info } : {}),
  };
  return { a, installs: () => installs };
}

describe('updating an install for everyone', () => {
  it('AnAdministratorIsToldWindowsAsksForPermission', async () => {
    const f = api(ready(), { allUsers: true, admin: true, otherUsers: 0 });
    const w = mount(UpdateBanner, { props: { api: f.a } });
    await flushPromises();
    expect(w.find('[data-test=update-banner]').text()).toContain('Windows asks for administrator permission');
    await w.find('[data-test=update-install]').trigger('click');
    await flushPromises();
    expect(f.installs()).toBe(1);
  });

  it('SomeoneWhoIsNotAnAdministrator_IsToldTheirAdministratorUpdates_AndCannotInstall', async () => {
    const w = mount(UpdateBanner, { props: { api: api(ready(), { allUsers: true, admin: false, otherUsers: 0 }).a } });
    await flushPromises();
    expect(w.find('[data-test=update-admin-only]').text()).toContain('your administrator installs updates');
    expect(w.find('[data-test=update-install]').exists()).toBe(false);
    expect(w.find('[data-test=update-download]').exists()).toBe(false);
  });

  it('OtherPeopleWithTheAppOpen_AreNamedBeforeTheirCopyIsClosed', async () => {
    const f = api(ready(), { allUsers: true, admin: true, otherUsers: 2 });
    const w = mount(UpdateBanner, { props: { api: f.a } });
    await flushPromises();
    await w.find('[data-test=update-install]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=update-confirm]').text()).toContain('open for 2 other people signed in to this computer');
    expect(f.installs()).toBe(0);
    await w.find('[data-test=update-anyway]').trigger('click');
    await flushPromises();
    expect(f.installs()).toBe(1);
  });

  it('WhenTheOrganizationManagesUpdates_NothingIsOffered', async () => {
    const managed = ready({ managed: true, state: 'managed' });
    const banner = mount(UpdateBanner, { props: { api: api(managed, { allUsers: true, admin: true, otherUsers: 0 }).a } });
    await flushPromises();
    expect(banner.find('[data-test=update-banner]').exists()).toBe(false);
    const section = mount(UpdatesSection, { props: { api: api(managed, null).a } });
    await flushPromises();
    expect(section.find('[data-test=updates-managed]').text()).toContain('managed by your organization');
    expect(section.find('[data-test=updates-auto]').exists()).toBe(false);
    expect(section.find('[data-test=updates-check]').exists()).toBe(false);
  });
});
