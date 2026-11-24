import { shallowRef } from 'vue';

// The window's own confirm dialog in place of window.confirm (as the cloud web app's dialog.js does): the
// remote view runs in a browser, and a browser dialog on iPad Safari/Chrome carries "Suppress dialogs", after which
// confirm() answers false for good. One <ConfirmDialog> (mounted once in App.vue) shows what is asked here; a call site
// reads `if (!(await confirmDialog({ message, okLabel: 'Delete', danger: true }))) return;`. Desktop and remote alike.

export interface ConfirmOptions {
  /** The question, shown as asked (the call sites keep the texts they gave window.confirm). */
  message: string;
  /** The OK button's label: name it after the action (Delete, Remove). Default OK. */
  okLabel?: string;
  /** The OK button is red (a removal or a loss). */
  danger?: boolean;
}

export interface ConfirmState extends Required<ConfirmOptions> { resolve: (ok: boolean) => void }

/** The question on screen, or null. */
export const confirmState = shallowRef<ConfirmState | null>(null);

/** Resolves true on OK (or Enter), false on Cancel, Escape or a click on the backdrop. A new question while one is open
 *  cancels the open one first, so a promise never stays pending. */
export function confirmDialog({ message, okLabel = 'OK', danger = false }: ConfirmOptions): Promise<boolean> {
  confirmState.value?.resolve(false);
  return new Promise<boolean>((resolve) => {
    const d: ConfirmState = { message, okLabel, danger, resolve: (ok) => { if (confirmState.value === d) confirmState.value = null; resolve(ok); } };
    confirmState.value = d;
  });
}
