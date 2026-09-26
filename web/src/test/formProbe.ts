import { flushPromises } from '@vue/test-utils';

/**
 * Reading and driving a validated form the way a person does, wherever Quasar teleported it.
 *
 * Shared by the dialog mount specs so "the submit button" and "the field labelled Email" mean the
 * same thing in every one of them. Each lookup THROWS when it finds nothing: a spec that asserted
 * against a missing button would otherwise pass on `undefined`.
 */

/** By its label, which QBtn renders in `.block` - the icon beside it is a ligature, and part of
 *  `textContent` too. */
export function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => (candidate.querySelector('.block') ?? candidate).textContent?.trim() === label);

  if (!found) throw new Error(`no button labelled "${label}" in the rendered form`);

  return found as HTMLButtonElement;
}

export function isDisabled(label: string): boolean {
  return button(label).hasAttribute('disabled');
}

/** The input or textarea inside the Quasar field whose label reads `label`. */
export function field(label: string): HTMLInputElement | HTMLTextAreaElement {
  const found = [...document.body.querySelectorAll('.q-field')]
    .find((candidate) => candidate.querySelector('.q-field__label')?.textContent?.trim() === label)
    ?.querySelector<HTMLInputElement | HTMLTextAreaElement>('input, textarea');

  if (!found) throw new Error(`no field labelled "${label}" in the rendered form`);

  return found;
}

/** The Quasar field wrapper whose label reads `label`, for its error state. */
export function fieldWrapper(label: string): HTMLElement {
  const found = [...document.body.querySelectorAll<HTMLElement>('.q-field')]
    .find((candidate) => candidate.querySelector('.q-field__label')?.textContent?.trim() === label);

  if (!found) throw new Error(`no field labelled "${label}" in the rendered form`);

  return found;
}

export function hasError(label: string): boolean {
  return fieldWrapper(label).classList.contains('q-field--error');
}

/** Types a whole value into a field and lets Vue and Quasar settle. */
export async function type(label: string, value: string): Promise<void> {
  const element = field(label);
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
}

/** Leaves a field, which is when a `lazy-rules` field first shows its message. */
export async function blur(label: string): Promise<void> {
  await leave(field(label));
}

/** `blur` for a field with no label to find it by. */
export async function leave(element: HTMLElement): Promise<void> {
  element.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
  element.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));

  // TWICE. QField drops `focused` on a timer, and the rule then runs from a watcher on that flag,
  // so the message is two turns away from the event rather than one.
  await settle();
  await settle();
}

/** Presses Enter in a field. QForm validates asynchronously before it emits `submit`. */
export async function pressEnter(label: string): Promise<void> {
  field(label).dispatchEvent(
    new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
  );
  await settle();
}

export async function settle(): Promise<void> {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}
