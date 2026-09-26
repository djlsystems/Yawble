import type { Component } from 'vue';
import {
  flushPromises,
  mount,
  type ComponentMountingOptions,
  type VueWrapper,
} from '@vue/test-utils';
import { installQuasarPlugin } from '@quasar/quasar-app-extension-testing-unit-vitest';
import { createPinia, setActivePinia } from 'pinia';

/**
 * THE ONE PLACE A COMPONENT IS MOUNTED, and it exists because three of the four ways to get this
 * wrong are SILENT - they do not throw, they produce an empty DOM and a spec that reads as
 * "the component did not render" when it rendered perfectly.
 *
 * 1. `installQuasarPlugin()` registers a `beforeAll` hook, so it must run at MODULE level, once.
 *    Calling it inside a test throws; importing this file is what calls it.
 * 2. A QDialog TELEPORTS to `<body>`. `wrapper.find()` therefore finds NOTHING. Assert through
 *    `bodyText`/`bodyFind`.
 * 3. The portal renders on a LATER TICK, so a synchronous assertion straight after `mount` sees an
 *    empty body. `mountDialog` awaits `flushPromises()` for you. This is the failure the toolchain
 *    spike hit first, and it looks exactly like point 2.
 * 4. Pinia is global state. Without a fresh one per mount, a store written by one spec is read by
 *    the next and the pair passes or fails depending on FILE ORDER.
 * 5. A DIALOG MUST BE MOUNTED CLOSED AND THEN OPENED, never mounted already open - and this one was
 *    got wrong here first, which is how it earned its comment. Half these dialogs do their loading
 *    from `watch(open)`, which fires on a TRANSITION and not on a first render that happens to be
 *    true. Mounting with `modelValue: true` therefore skips it: AddMemberDialog came up with an
 *    EMPTY Agent picker and the spec read as "the allowlist filtered everything out".
 *    THAT IS THE MOUNT-AT-OPEN DEFECT ITSELF, reproduced by the harness built to catch it - so a
 *    harness that opened dialogs the wrong way would have been blind to the whole class.
 *
 * WHY A HELPER RATHER THAN A `beforeEach` IN EACH SPEC: the four rules above are not discoverable
 * from a failure. A spec that forgets one gets an empty string and no error.
 */
installQuasarPlugin();

export interface DialogMountOptions extends ComponentMountingOptions<Component> {
  /**
   * Set false when the spec has already called `setActivePinia` and seeded a store itself - a fresh
   * Pinia here would discard that seeding, and the assertion would be made against defaults.
   */
  pinia?: boolean;
}

/**
 * Mounts a dialog CLOSED, then opens it, then waits for the teleported portal - which is what a
 * person does and what every `watch(open)` in these components is written against.
 */
export async function mountDialog(
  component: Component,
  props: Record<string, unknown> = {},
  options: DialogMountOptions = {},
): Promise<VueWrapper> {
  const { pinia = true, ...mountOptions } = options;

  if (pinia) setActivePinia(createPinia());

  // THE CAST IS ON THE OPTIONS, NEVER ON THE COMPONENT, and that is the difference between a
  // helper and a hole. `mount` infers its options type FROM the component's own props, so typing
  // the component as `never`/`any` collapses that inference and the props object is then checked
  // against `undefined` - the error this replaced. Widening only the options bag keeps every
  // SPEC's own `mountDialog(Dialog, { ... })` call checked at its own call site, where a wrong
  // prop name can be read; what is given up is only the check on this one merge.
  const wrapper = mount(component, {
    ...mountOptions,
    props: { modelValue: false, ...props },
  } as ComponentMountingOptions<Component>);

  await flushPromises();

  // THE OPENING EDGE. See point 5 above: this is the difference between testing these dialogs and
  // testing them in exactly the state the defect this suite exists to catch puts them in.
  await wrapper.setProps({ modelValue: true });
  await flushPromises();

  return wrapper as VueWrapper;
}

/** What the viewer can read, wherever Quasar teleported it to. */
export function bodyText(): string {
  return document.body.textContent ?? '';
}

export function bodyFind(selector: string): HTMLElement | null {
  return document.body.querySelector(selector);
}

/**
 * happy-dom's `document` is shared by every test in a file, and an unmounted QDialog can leave its
 * portal node behind. Without this in an `afterEach`, the SECOND test in a file asserts against the
 * FIRST one's DOM - which passes, wrongly, whenever the two expect the same text.
 */
export function resetBody(): void {
  document.body.innerHTML = '';
}
