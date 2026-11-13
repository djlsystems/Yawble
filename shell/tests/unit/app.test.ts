// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import HostChecksBanner from '../../src/renderer/src/components/HostChecksBanner.vue';
import SystemPanel from '../../src/renderer/src/components/SystemPanel.vue';
import type { CoreInfo, YawbleApi } from '../../src/shared/api';

const ui = {
  call: async () => { throw new Error('not used'); },
  onEvent: () => () => {},
  openTerminal: async () => {},
  pickFolder: async () => null,
};

const info: CoreInfo = {
  product: 'Yawble core', version: '0.1.0', platform: 'linux-x64', os: 'Linux', dotnet: '10.0.0', pid: 1,
  sandbox: { sdkVersion: '1.0.1', nativeVersion: '1.0.1', requestContract: '1.0.0', telemetryEnabled: false },
  schema: ['0001_baseline'],
};

describe('System panel (the core\'s identity and backends)', () => {
  it('shows the core version and the backends from the core', async () => {
    const api: YawbleApi = {
      ...ui,
      getInfo: async () => info,
      getAvailableBackends: async () => [{ backend: 'bubblewrap', tier: null, capabilities: ['proxyEnforcement'], warnings: [] }],
    };
    const w = mount(SystemPanel, { props: { api } });
    await flushPromises();
    expect(w.get('[data-test=core-version]').text()).toBe('0.1.0');
    expect(w.get('[data-test=sandbox-contract]').text()).toContain('1.0.0 · telemetry off');
    expect(w.findAll('[data-test=backend-row]')).toHaveLength(1);
    expect(w.get('[data-test=backend-row]').text()).toContain('bubblewrap');
  });

  it('says so plainly when no backend is available', async () => {
    const api: YawbleApi = { ...ui, getInfo: async () => info, getAvailableBackends: async () => [] };
    const w = mount(SystemPanel, { props: { api } });
    await flushPromises();
    expect(w.get('[data-test=no-backends]').text()).toContain('No containment backend');
    expect(w.get('[data-test=backends-json]').text()).toBe('[]');
  });

  it('says clearly when the AppArmor profile is missing, with the fix, and shows nothing when every check passes', async () => {
    const hostChecks = [
      { id: 'apparmor-userns', ok: false, message: 'The Yawble AppArmor profile is missing: …', fix: "Run this once as root, then restart Yawble: sudo '/opt/yawble/install-apparmor.sh'" },
      { id: 'bwrap', ok: true, message: 'bwrap is installed.', fix: null },
    ];
    const missing = mount(HostChecksBanner, { props: { api: { ...ui, getInfo: async () => ({ ...info, hostChecks }), getAvailableBackends: async () => [] } } });
    await flushPromises();
    expect(missing.get('[data-test=host-check-apparmor-userns]').text()).toContain('AppArmor profile is missing');
    expect(missing.get('[data-test=host-check-apparmor-userns]').text()).toContain("sudo '/opt/yawble/install-apparmor.sh'");
    expect(missing.find('[data-test=host-check-bwrap]').exists()).toBe(false);

    const ready = mount(HostChecksBanner, { props: { api: { ...ui, getInfo: async () => ({ ...info, hostChecks: [hostChecks[1]] }), getAvailableBackends: async () => [] } } });
    await flushPromises();
    expect(ready.find('[data-test=host-checks]').exists()).toBe(false);
    expect(ready.get('[data-test=host-checks-probe]').attributes('data-state')).toBe('read');
  });

  it('shows the error when the core is unreachable', async () => {
    const api: YawbleApi = { ...ui, getInfo: async () => { throw new Error('the core is not running'); }, getAvailableBackends: async () => [] };
    const w = mount(SystemPanel, { props: { api } });
    await flushPromises();
    expect(w.get('[data-test=error]').text()).toContain('the core is not running');
  });
});
