// @vitest-environment happy-dom
// The window's own confirm (confirm.ts + ConfirmDialog.vue) in place of window.confirm, which in the remote
// view (iPad Safari) brings "Suppress dialogs". OK / Enter -> true; Cancel / Escape / the backdrop -> false; the keys
// are taken by a document capture listener added on open and removed on close; Tab stays inside; the focus goes back
// to the opener; Escape never also closes the dialog under it (escapeClose.ts).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ConfirmDialog from '../../src/renderer/src/components/ConfirmDialog.vue';
import { confirmDialog, confirmState } from '../../src/renderer/src/confirm';
import { pushEscape } from '../../src/renderer/src/escapeClose';

const q = <T extends HTMLElement = HTMLElement>(t: string) => document.querySelector<T>(`[data-test="${t}"]`);
const key = (k: string, o: KeyboardEventInit = {}, on: EventTarget = document.activeElement ?? document.body) =>
  on.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true, ...o }));

let host: VueWrapper | null = null;
let opener: HTMLButtonElement;
beforeEach(async () => {
  window.confirm = vi.fn(() => true) as unknown as typeof window.confirm;
  opener = document.createElement('button');
  opener.textContent = 'Delete';
  document.body.appendChild(opener);
  opener.focus();
  host = mount(ConfirmDialog, { attachTo: document.body });
  await flushPromises();
});
afterEach(() => {
  confirmState.value?.resolve(false);
  host?.unmount();
  host = null;
  document.body.innerHTML = '';
  vi.restoreAllMocks();
});

describe('confirmDialog', () => {
  it('OK resolves true: the message as asked, OK named after the action and red, OK has the focus; then it closes and the focus is back', async () => {
    const p = confirmDialog({ message: 'Delete the trigger nightly?', okLabel: 'Delete', danger: true });
    await flushPromises();
    expect(q('confirm-dialog')!.getAttribute('role')).toBe('alertdialog');
    expect(q('confirm-dialog')!.getAttribute('aria-modal')).toBe('true');
    expect(q('confirm-message')!.textContent).toBe('Delete the trigger nightly?');
    expect(q('confirm-ok')!.textContent).toBe('Delete');
    expect(q('confirm-ok')!.classList.contains('ok-danger')).toBe(true);
    expect(document.activeElement).toBe(q('confirm-ok'));
    q('confirm-ok')!.click();
    await expect(p).resolves.toBe(true);
    await flushPromises();
    expect(q('confirm-dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
    expect(window.confirm).not.toHaveBeenCalled();
  });

  it('Cancel, Escape and a press on the backdrop resolve false; OK defaults to "OK", not red', async () => {
    let p = confirmDialog({ message: 'A?' });
    await flushPromises();
    expect(q('confirm-ok')!.textContent).toBe('OK');
    expect(q('confirm-ok')!.classList.contains('primary')).toBe(true);
    q('confirm-cancel')!.click();
    await expect(p).resolves.toBe(false);

    p = confirmDialog({ message: 'B?' });
    await flushPromises();
    key('Escape');
    await expect(p).resolves.toBe(false);

    p = confirmDialog({ message: 'C?' });
    await flushPromises();
    q('confirm-backdrop')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    await expect(p).resolves.toBe(false);
    await flushPromises();
    expect(q('confirm-dialog')).toBeNull();
    expect(window.confirm).not.toHaveBeenCalled();
  });

  it('Enter resolves true when the focus is on the text or the page; on Cancel it is Cancel\'s own press', async () => {
    let p = confirmDialog({ message: 'A?' });
    await flushPromises();
    q('confirm-dialog')!.focus(); // a click on the text
    expect(key('Enter')).toBe(false); // taken (default prevented)
    await expect(p).resolves.toBe(true);

    p = confirmDialog({ message: 'B?' });
    await flushPromises();
    q('confirm-cancel')!.focus();
    expect(key('Enter')).toBe(true); // left to the button (a browser turns it into Cancel's click)
    expect(confirmState.value).not.toBeNull();
    q('confirm-cancel')!.click();
    await expect(p).resolves.toBe(false);
  });

  it('the capture keydown listener is added on open and removed on close (the same function)', async () => {
    const add = vi.spyOn(document, 'addEventListener');
    const remove = vi.spyOn(document, 'removeEventListener');
    const p = confirmDialog({ message: 'A?' });
    await flushPromises();
    const added = add.mock.calls.filter(([t, , o]) => t === 'keydown' && o === true);
    expect(added).toHaveLength(1);
    expect(remove.mock.calls.filter(([t]) => t === 'keydown')).toHaveLength(0);
    q('confirm-ok')!.click();
    await p;
    const removed = remove.mock.calls.filter(([t, , o]) => t === 'keydown' && o === true);
    expect(removed).toHaveLength(1);
    expect(removed[0]![1]).toBe(added[0]![1]);
    // Closed: Escape and Enter on the page are not touched any more.
    expect(key('Escape', {}, document.body)).toBe(true);
    expect(key('Enter', {}, document.body)).toBe(true);
  });

  it('Escape cancels only the confirm, never also the dialog under it', async () => {
    const closeUnder = vi.fn();
    const release = pushEscape(closeUnder);
    const p = confirmDialog({ message: 'Close and lose them?' });
    await flushPromises();
    key('Escape');
    await expect(p).resolves.toBe(false);
    expect(closeUnder).not.toHaveBeenCalled();
    key('Escape', {}, document.body); // the confirm is closed: now it is the dialog's
    expect(closeUnder).toHaveBeenCalledTimes(1);
    release();
  });

  it('Tab and Shift+Tab stay inside (Cancel <-> OK)', async () => {
    const p = confirmDialog({ message: 'A?' });
    await flushPromises();
    expect(document.activeElement).toBe(q('confirm-ok'));
    key('Tab');
    expect(document.activeElement).toBe(q('confirm-cancel'));
    key('Tab');
    expect(document.activeElement).toBe(q('confirm-ok'));
    key('Tab', { shiftKey: true });
    expect(document.activeElement).toBe(q('confirm-cancel'));
    key('Tab', { shiftKey: true });
    expect(document.activeElement).toBe(q('confirm-ok'));
    opener.focus(); // the focus somehow outside: Tab brings it back in
    key('Tab', {}, opener);
    expect(document.activeElement).toBe(q('confirm-cancel'));
    key('Escape');
    await expect(p).resolves.toBe(false);
  });

  it('a new question while one is open cancels the open one (no promise stays pending)', async () => {
    const first = confirmDialog({ message: 'First?' });
    const second = confirmDialog({ message: 'Second?' });
    await expect(first).resolves.toBe(false);
    await flushPromises();
    expect(q('confirm-message')!.textContent).toBe('Second?');
    q('confirm-ok')!.click();
    await expect(second).resolves.toBe(true);
  });

  it('unmounted while open: the question is cancelled and the listener removed', async () => {
    const remove = vi.spyOn(document, 'removeEventListener');
    const p = confirmDialog({ message: 'A?' });
    await flushPromises();
    host!.unmount();
    host = null;
    await expect(p).resolves.toBe(false);
    expect(remove.mock.calls.filter(([t, , o]) => t === 'keydown' && o === true)).toHaveLength(1);
  });
});
