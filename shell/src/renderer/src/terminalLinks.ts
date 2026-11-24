import type { ILinkHandler } from '@xterm/xterm';
import { confirmDialog } from './confirm';

// A terminal link (OSC 8) asks through the window's own confirm dialog (confirm.ts), never xterm's default
// handler, which calls the browser's confirm(): on iPad Safari with "Suppress dialogs" that answers false for good, so
// links in the remote view would never open. On OK the link opens the way the app opens any external link:
// window.open(url, '_blank'), which on the desktop reaches the main process's window-open handler (shell.openExternal,
// web and mail addresses only) and in the remote view opens a new browser tab. Only http and https are opened here.

/** The question xterm's own handler asked, kept as it was. */
export function linkQuestion(uri: string): string {
  return `Do you want to navigate to ${uri}?\n\nWARNING: This link could potentially be dangerous`;
}

/** True for an absolute http: or https: address. */
export function isWebLink(uri: string): boolean {
  try {
    const u = new URL(uri);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

/** Asks, then opens: resolves true when the link was opened. A link of another scheme is refused without asking. */
export async function activateTerminalLink(uri: string, open: (url: string) => unknown = openExternalLink): Promise<boolean> {
  if (!isWebLink(uri)) return false;
  if (!(await confirmDialog({ message: linkQuestion(uri), okLabel: 'Open' }))) return false;
  open(uri);
  return true;
}

/** The app's external-link path (see above); noopener so the opened page cannot reach this window. */
export function openExternalLink(url: string): void {
  window.open(url, '_blank', 'noopener,noreferrer');
}

/** The Terminal's linkHandler option. allowNonHttpProtocols stays off: xterm itself drops other schemes too. */
export const terminalLinkHandler: ILinkHandler = {
  activate: (_event, uri) => { void activateTerminalLink(uri); },
};
