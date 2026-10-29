// @vitest-environment happy-dom
//
// THE CONCIERGE OPEN OVER A DIALOG GETS THE KEYBOARD, AND GIVES IT BACK. A real Quasar dialog with
// a backdrop (as Backlog, Marketplace, Documents and Git are) and the real panel on a real xterm:
// the dialog's focus trap on <body> used to pull every focus inside the panel straight back, so
// what the person typed never reached the Concierge. See `lib/conciergeFocus.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { defineComponent, h, ref } from 'vue';
import { QBtn, QCard, QDialog } from 'quasar';

const { concierge, listCatalog, connectConcierge, sockets } = vi.hoisted(() => {
  const sockets: { sendInput: ReturnType<typeof vi.fn> }[] = [];
  return {
    concierge: vi.fn(),
    listCatalog: vi.fn(),
    sockets,
    connectConcierge: vi.fn(() => {
      const socket = { sendInput: vi.fn(), sendResize: vi.fn(), dispose: vi.fn() };
      sockets.push(socket);
      return socket;
    }),
  };
});

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

import ConciergePanel from '../ConciergePanel.vue';
import { useConsoleStore } from '../../stores/console';
import { resetBody } from '../../test/mountQuasar';

function giveTheDomALayout(): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 800, configurable: true });
  }
  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 600, configurable: true });
  }
}

const dialogOpen = ref(false);
const panelOpen = ref(false);

/** A dialog as the ribbon's are - modal, with a backdrop and two buttons to Tab between - and the panel. */
const Shell = defineComponent({
  setup() {
    return () => [
      h(QDialog, { modelValue: dialogOpen.value, 'onUpdate:modelValue': (v: boolean) => (dialogOpen.value = v) }, () =>
        h(QCard, { class: 'backlog-like' }, () => [
          h(QBtn, { label: 'First', class: 'first' }),
          h(QBtn, { label: 'Second', class: 'second' }),
        ]),
      ),
      h(ConciergePanel, {
        team: null,
        modelValue: panelOpen.value,
        'onUpdate:modelValue': (v: boolean) => (panelOpen.value = v),
      }),
    ];
  },
});

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  sockets.length = 0;
  vi.clearAllMocks();
  concierge.mockResolvedValue({ agent: 'claude-interactive' });
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-interactive', mode: 'Interactive' }] });
  giveTheDomALayout();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  dialogOpen.value = false;
  panelOpen.value = false;
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
  document.body.style.overflow = '';
});

const settle = async () => {
  await flushPromises();
  await new Promise((done) => setTimeout(done, 400));
  await flushPromises();
};

const dialogInner = () => document.querySelector<HTMLElement>('.q-dialog .q-dialog__inner')!;
const terminalInput = () => document.querySelector<HTMLTextAreaElement>('.concierge-host textarea')!;
const inDialog = () => dialogInner().contains(document.activeElement);
const inTerminal = () => document.querySelector('.concierge-host')!.contains(document.activeElement);
const sent = () => sockets.at(-1)!.sendInput.mock.calls.map(([data]) => data as string).join('');

/** What a click does to focus: it lands on the element, and `focusin` bubbles from it. */
function clickInto(element: HTMLElement) {
  element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
  element.focus();
}

async function dialogThenConcierge() {
  setActivePinia(createPinia());
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue();
  wrapper = mount(Shell, { attachTo: document.body });
  await flushPromises();

  dialogOpen.value = true;
  await settle();
  expect(inDialog(), 'the dialog did not take focus when it opened').toBe(true);

  panelOpen.value = true;
  await settle();
  expect(terminalInput(), 'the real terminal did not open').not.toBeNull();
}

describe('the Concierge opened over a dialog', () => {
  it('takes keyboard focus on opening, and what is typed reaches the terminal', async () => {
    await dialogThenConcierge();

    expect(inTerminal(), 'the dialog kept focus over the Concierge').toBe(true);

    terminalInput().dispatchEvent(new KeyboardEvent('keydown', { key: 'h', keyCode: 72, bubbles: true }));
    terminalInput().dispatchEvent(new KeyboardEvent('keypress', { key: 'h', charCode: 104, bubbles: true }));
    expect(sent()).toContain('h');
  });

  it('keeps focus when clicked into after the dialog took it back', async () => {
    await dialogThenConcierge();

    clickInto(dialogInner());
    expect(inDialog()).toBe(true);

    clickInto(terminalInput());
    await flushPromises();
    expect(inTerminal(), 'the dialog trap pulled focus back out of the Concierge').toBe(true);
  });

  it('does not let an Escape meant for the agent close the dialog behind', async () => {
    await dialogThenConcierge();

    terminalInput().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    terminalInput().dispatchEvent(new KeyboardEvent('keyup', { key: 'Escape', keyCode: 27, bubbles: true }));
    await settle();

    expect(dialogOpen.value).toBe(true);
    expect(panelOpen.value).toBe(true);
  });

  it('gives the dialog its keyboard back when clicked: Escape closes it, as before', async () => {
    await dialogThenConcierge();

    clickInto(dialogInner());
    expect(inDialog()).toBe(true);

    dialogInner().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    dialogInner().dispatchEvent(new KeyboardEvent('keyup', { key: 'Escape', keyCode: 27, bubbles: true }));
    await settle();

    expect(dialogOpen.value, 'Escape in the dialog no longer closed it').toBe(false);
    expect(panelOpen.value, 'one Escape closed the Concierge as well').toBe(true);
  });

  it('lets Tab move between the dialog\'s own controls, and the trap still holds focus in the dialog', async () => {
    await dialogThenConcierge();

    const first = document.querySelector<HTMLElement>('.backlog-like .first')!;
    const second = document.querySelector<HTMLElement>('.backlog-like .second')!;
    clickInto(first);
    expect(document.activeElement).toBe(first);

    // happy-dom does not move focus on Tab itself; this is the browser's move, and what the
    // dialog's trap does with it.
    second.focus();
    expect(document.activeElement).toBe(second);

    // Focus leaving for the page behind - neither dialog nor Concierge - is still pulled back.
    const behind = document.createElement('button');
    document.body.appendChild(behind);
    behind.focus();
    expect(inDialog(), 'the dialog no longer traps focus that leaves it for the page').toBe(true);
    behind.remove();
  });

  it('does not strand focus when it closes: it goes back to the dialog', async () => {
    await dialogThenConcierge();
    expect(inTerminal()).toBe(true);

    panelOpen.value = false;
    await settle();

    expect(document.querySelector('.concierge-shell')).toBeNull();
    expect(inDialog(), `focus was left on ${document.activeElement?.tagName}`).toBe(true);
  });
});
