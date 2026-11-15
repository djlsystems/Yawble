// The two dialogs about the app's own lifetime, as plain data (index.ts shows them with Electron's dialog):
// - another Yawble holds this data folder (the core exited with CORE_IN_USE_EXIT_CODE before it held the folder);
// - the core's removal watch saw the app uninstalled or its data removed (the core's app.removed push).

export interface LifetimeDialog {
  type: 'error' | 'warning';
  message: string;
  detail: string;
  buttons: string[];
  defaultId: number;
  cancelId: number;
  noLink: true;
}

/** The core's push when its executable was deleted or its data folder removed or replaced (Lifetime/RemovalWatch.cs). */
export const REMOVED_EVENT = 'app.removed';

/** The dialog for a second Yawble on the same data folder: the core's own line ("... (pid N)"), and Quit. */
export function inUseDialog(coreMessage: string): LifetimeDialog {
  const line = coreMessage.charAt(0).toUpperCase() + coreMessage.slice(1);
  return {
    type: 'error',
    message: 'Yawble is already running',
    detail: `${line}. Close that Yawble first, then start this one again.`,
    buttons: ['Quit'],
    defaultId: 0,
    cancelId: 0,
    noLink: true,
  };
}

/** The dialog for the core's app.removed push; null for any other event. Quit is the only button. */
export function removedDialog(event: { event: string; data: unknown }): LifetimeDialog | null {
  if (event.event !== REMOVED_EVENT) return null;
  const d = (event.data && typeof event.data === 'object' ? event.data : {}) as Record<string, unknown>;
  const what: string[] = [];
  if (d.executableDeleted === true) what.push('Its program files were deleted while it was running.');
  if (d.dataFolderChanged === true) what.push('Its data folder was removed or replaced while it was running.');
  return {
    type: 'warning',
    message: typeof d.message === 'string' && d.message ? d.message : 'Yawble was uninstalled or its data was removed',
    detail: [...what, 'Quit Yawble now; reinstall it or start it again to go on.'].join(' '),
    buttons: ['Quit'],
    defaultId: 0,
    cancelId: 0,
    noLink: true,
  };
}
