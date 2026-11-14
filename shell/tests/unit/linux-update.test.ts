import { describe, expect, it } from 'vitest';
import { linuxUpdatePlan } from '../../src/main/linux-update';

// Restart to update on Linux: the plan only (paths and arguments); nothing is copied, spawned or installed here.
describe('Linux Restart to update', () => {
  it('runs a copy of the install\'s apply-update.sh from outside the install, with the tarball, the install and a log', () => {
    const plan = linuxUpdatePlan('/home/dana/.local/opt/yawble/yawble', '/home/dana/.local/share/yawble/data/updates/yawble-2026.11.20.1-linux-x64.tar.gz',
      '/tmp', '/home/dana/.local/share/yawble/data', 4242);
    expect(plan.script).toBe('/home/dana/.local/opt/yawble/apply-update.sh');
    expect(plan.copy).toBe('/tmp/yawble-apply-update-4242.sh');
    expect(plan.copy.startsWith('/home/dana/.local/opt/yawble/')).toBe(false);
    expect(plan.program).toBe('/bin/sh');
    expect(plan.args).toEqual([
      '/tmp/yawble-apply-update-4242.sh',
      '/home/dana/.local/share/yawble/data/updates/yawble-2026.11.20.1-linux-x64.tar.gz',
      '/home/dana/.local/opt/yawble',
      '/home/dana/.local/share/yawble/data/logs/update.log',
    ]);
  });
});
