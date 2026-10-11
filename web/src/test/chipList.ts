/**
 * Driving `ChipListInput` from a mount test, the way a person does: the chips a list shows, its ×,
 * its Add button and the dialog that button opens (teleported to <body>).
 */
import { flushPromises } from '@vue/test-utils';

async function settle(): Promise<void> {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

/** The items a list shows, in order. `root` holds exactly one list. */
export function chipsIn(root: Element): string[] {
  return [...root.querySelectorAll('[data-chip]')].map((chip) => chip.getAttribute('data-chip') ?? '');
}

export function addButtonIn(root: Element): HTMLElement | null {
  return root.querySelector<HTMLElement>('[data-chip-add]');
}

/** Opens the list's Add dialog and types `text` into it; each line is one item. Does not submit. */
export async function typeInAddDialog(root: Element, text: string): Promise<void> {
  const add = addButtonIn(root);
  if (!add) throw new Error('this list has no Add button');
  add.click();
  await settle();

  const input = document.body.querySelector<HTMLTextAreaElement>('textarea[data-chip-add-input], [data-chip-add-input] textarea');
  if (!input) throw new Error('the Add dialog did not open');
  input.value = text;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
}

/** Adds `text` through the list's Add dialog, as the dialog's Add button does. */
export async function addChips(root: Element, text: string): Promise<void> {
  await typeInAddDialog(root, text);
  document.body.querySelector<HTMLElement>('[data-chip-add-submit]')!.click();
  await settle();
}

/** Cancels the open Add dialog, adding nothing. */
export async function cancelAddDialog(): Promise<void> {
  const cancel = [...document.body.querySelectorAll<HTMLElement>('[data-chip-add-dialog] button')]
    .find((button) => button.textContent?.trim() === 'Cancel');
  if (!cancel) throw new Error('no Add dialog is open');
  cancel.click();
  await settle();
}

/** The open Add dialog's refusal, or '' when there is none. */
export function addDialogProblem(): string {
  return document.body.querySelector('[data-chip-add-dialog] .q-field__messages')?.textContent?.trim() ?? '';
}

export async function removeChip(root: Element, item: string): Promise<void> {
  const chip = root.querySelector<HTMLElement>(`[data-chip="${CSS.escape(item)}"] .q-chip__icon--remove`);
  if (!chip) throw new Error(`no removable chip ${item}`);
  chip.click();
  await settle();
}

/** Types `text` into the list's own text field and presses Return there, as a person does. */
export async function typeAndReturn(root: Element, text: string): Promise<void> {
  const input = root.querySelector<HTMLInputElement>('[data-chip-list] input');
  if (!input) throw new Error('this list has no text field to type in');
  input.focus();
  input.value = text;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
  await settle();
}

/** What the list's text field still holds. */
export function typedIn(root: Element): string {
  return root.querySelector<HTMLInputElement>('[data-chip-list] input')?.value ?? '';
}

/** The one line the list says under itself - a refusal - or '' when it says nothing. */
export function listProblem(root: Element): string {
  return root.querySelector('[data-chip-list] .q-field__messages')?.textContent?.trim() ?? '';
}
