import { describe, expect, it } from 'vitest';
import { earlyExitError, StderrTail } from '../../src/main/core-process';

// A core that stops before READY says why on stderr; the person sees that reason, not just an exit code.
describe('core start failure', () => {
  it('carries the core\'s fatal lines in the error', () => {
    const tail = new StderrTail();
    tail.push('backup: something earlier\nfatal: Yawble could not start: restoring the backup b1 failed. The database from before is at C:\\d\\backups\\x.db;');
    tail.push(' the backup being restored is at C:\\d\\yawble.restore.db.\n');
    const e = earlyExitError(4, tail.text());
    expect(e.message).toContain('Yawble could not start');
    expect(e.message).toContain('C:\\d\\backups\\x.db');
    expect(e.message).toContain('C:\\d\\yawble.restore.db');
    expect(e.message).not.toContain('something earlier');
    expect(e.message).not.toContain('fatal:');
  });

  it('falls back to the last line the core wrote, then to the exit code', () => {
    expect(earlyExitError(3, 'one\nthe update was refused: no backup\n\n').message).toContain('the update was refused: no backup');
    expect(earlyExitError(1, '').message).toBe('the core exited with code 1 before READY');
  });

  it('keeps only the end of a long stderr', () => {
    const tail = new StderrTail(100);
    tail.push('x'.repeat(500));
    tail.push('\nfatal: the end');
    expect(tail.text().length).toBeLessThanOrEqual(100);
    expect(earlyExitError(4, tail.text()).message).toContain('the end');
  });
});
