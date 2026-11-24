import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';
import { DataFolderInUseError, startCore, type CoreLaunch } from '../../src/main/core-process';
import { inUseDialog } from '../../src/main/lifetime-dialogs';

// startCore against a stand-in core process (node -e), as the shell starts the real one.
const dataDir = mkdtempSync(path.join(os.tmpdir(), 'yawble-start-core-'));
afterAll(() => rmSync(dataDir, { recursive: true, force: true }));
const fake = (code: string): CoreLaunch => ({ command: process.execPath, args: ['-e', code, '--'] }); // '--': the core's arguments are not node's

const refused = fake(`process.stderr.write('fatal: another Yawble is using this data folder (pid 4242)\\n'); process.exit(5);`);
const holds = fake(`console.log('YAWBLE-STARTING'); setTimeout(() => { console.log('YAWBLE-READY fake-address'); }, 200); process.stdin.on('end', () => process.exit(0)); process.stdin.resume();`);

describe('startCore with a core that is refused the data folder', () => {
  it('rejects with DataFolderInUseError carrying the core\'s line (pid included) every time, and never says STARTING', async () => {
    // Repeated: the core's stderr and its exit race in the shell; the pid must survive that race.
    for (let i = 0; i < 15; i++) {
      let started = false;
      const e = await startCore(refused, { dataDir, timeoutMs: 15_000, onStarting: () => { started = true; } }).then(() => null, (err: unknown) => err);
      expect(e, `run ${i}`).toBeInstanceOf(DataFolderInUseError);
      expect((e as Error).message, `run ${i}`).toBe('another Yawble is using this data folder (pid 4242)');
      expect(started).toBe(false);
      const d = inUseDialog((e as Error).message);
      expect(d.detail).toContain('Another Yawble is using this data folder (pid 4242).');
      expect(d.buttons).toEqual(['Quit']);
    }
  }, 120_000);
});

describe('startCore with a core that holds the data folder', () => {
  it('calls onStarting on YAWBLE-STARTING, before it resolves on READY', async () => {
    const order: string[] = [];
    const core = await startCore(holds, { dataDir, timeoutMs: 15_000, onStarting: () => order.push('starting') });
    order.push('ready');
    expect(order).toEqual(['starting', 'ready']);
    expect(core.address).toBe('fake-address');
    core.child.stdin!.end();
    await new Promise((r) => core.child.once('exit', r));
  }, 30_000);
});
