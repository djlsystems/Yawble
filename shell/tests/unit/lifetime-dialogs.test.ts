import { describe, expect, it } from 'vitest';
import { CORE_IN_USE_EXIT_CODE, DataFolderInUseError, earlyExitError } from '../../src/main/core-process';
import { inUseDialog, removedDialog } from '../../src/main/lifetime-dialogs';

// One Yawble per data folder, and the notice when the app or its data is removed under it.
describe('a second Yawble on the same data folder', () => {
  it('is told apart from other early exits by the core\'s exit code, with the core\'s own line', () => {
    const e = earlyExitError(CORE_IN_USE_EXIT_CODE, 'fatal: another Yawble is using this data folder (pid 4242)\n');
    expect(e).toBeInstanceOf(DataFolderInUseError);
    expect(e.message).toBe('another Yawble is using this data folder (pid 4242)');
    expect(earlyExitError(4, 'fatal: restoring failed\n')).not.toBeInstanceOf(DataFolderInUseError);
    expect(earlyExitError(CORE_IN_USE_EXIT_CODE, '').message).toBe('another Yawble is using this data folder');
  });

  it('gets a dialog naming the pid, with Quit only', () => {
    const d = inUseDialog('another Yawble is using this data folder (pid 4242)');
    expect(d.type).toBe('error');
    expect(d.detail).toContain('Another Yawble is using this data folder (pid 4242).');
    expect(d.buttons).toEqual(['Quit']);
    expect(d.cancelId).toBe(0);
  });
});

describe('the "it was removed" notice', () => {
  it('comes from the core\'s app.removed push, with Quit only', () => {
    const d = removedDialog({ event: 'app.removed', data: { message: 'Yawble was uninstalled or its data was removed', executableDeleted: true, dataFolderChanged: false } });
    expect(d).not.toBeNull();
    expect(d!.message).toBe('Yawble was uninstalled or its data was removed');
    expect(d!.detail).toContain('program files were deleted');
    expect(d!.detail).not.toContain('data folder');
    expect(d!.buttons).toEqual(['Quit']);
    expect(d!.defaultId).toBe(0);
    expect(d!.cancelId).toBe(0); // Escape quits too: there is nothing else to do
  });

  it('says the data folder went when that is what changed, and keeps its message without data', () => {
    expect(removedDialog({ event: 'app.removed', data: { dataFolderChanged: true } })!.detail).toContain('data folder was removed or replaced');
    expect(removedDialog({ event: 'app.removed', data: null })!.message).toBe('Yawble was uninstalled or its data was removed');
  });

  it('ignores every other push', () => {
    expect(removedDialog({ event: 'board.changed', data: null })).toBeNull();
  });
});
