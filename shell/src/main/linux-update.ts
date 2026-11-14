import path from 'node:path';

/**
 * Restart to update on Linux: the core has downloaded the release's tarball and checked it again (its SHA-256 from
 * the release's SHA256SUMS-linux, and its layout). The app runs a copy of the install's apply-update.sh, detached,
 * and quits: the script waits until nothing runs from the install, unpacks the tarball, runs its install.sh over the
 * same folder and starts Yawble again. The copy lives outside the install, which install.sh replaces.
 */
export interface LinuxUpdatePlan {
  /** The install's own apply-update.sh, and where its copy runs from. */
  script: string;
  copy: string;
  program: string;
  args: string[];
}

export function linuxUpdatePlan(exe: string, tarball: string, tmpDir: string, dataDir: string, pid: number): LinuxUpdatePlan {
  const prefix = path.posix.dirname(exe);
  const script = path.posix.join(prefix, 'apply-update.sh');
  const copy = path.posix.join(tmpDir, `yawble-apply-update-${pid}.sh`);
  const log = path.posix.join(dataDir, 'logs', 'update.log');
  return { script, copy, program: '/bin/sh', args: [copy, tarball, prefix, log] };
}
