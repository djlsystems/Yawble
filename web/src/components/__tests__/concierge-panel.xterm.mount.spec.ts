// @vitest-environment happy-dom
//
// THE CONCIERGE PANEL ON A REAL XTERM, for the two rules that are xterm's and not the panel's:
// whether a pasted path is bracketed, and which bytes Alt+<key> sends. `concierge-panel.mount.spec.ts`
// fakes the terminal; a fake that brackets or encodes keys would only test itself, so these cases
// mount the real one. Only the API and the socket are replaced, and the socket's own `onData` is
// how the CLI's DECSET 2004 reaches the terminal, as it does in production.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { concierge, listCatalog, uploadConciergeAttachment, connectConcierge, sockets } = vi.hoisted(() => {
  const sockets: { sendInput: ReturnType<typeof vi.fn>; output: (bytes: Uint8Array) => void }[] = [];
  return {
    concierge: vi.fn(),
    listCatalog: vi.fn(),
    uploadConciergeAttachment: vi.fn(),
    sockets,
    connectConcierge: vi.fn((_size: unknown, handlers: { onData: (bytes: Uint8Array) => void }) => {
      const socket = { sendInput: vi.fn(), sendResize: vi.fn(), dispose: vi.fn(), output: handlers.onData };
      sockets.push(socket);
      return socket;
    }),
  };
});

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  uploadConciergeAttachment,
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

import ConciergePanel from '../ConciergePanel.vue';
import { useConsoleStore } from '../../stores/console';
import { resetBody } from '../../test/mountQuasar';

const Stored = '/data/concierge/attachments/20261001T120000Z-1.png';

/** happy-dom has no layout engine; the panel will not build a terminal onto a zero-sized box. */
function giveTheDomALayout(): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 800, configurable: true });
  }
  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 600, configurable: true });
  }
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  sockets.length = 0;
  vi.clearAllMocks();
  concierge.mockResolvedValue({ agent: 'claude-interactive' });
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-interactive', mode: 'Interactive' }] });
  uploadConciergeAttachment.mockResolvedValue({ path: Stored, size: 4, type: 'image/png' });
  giveTheDomALayout();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
  document.body.style.overflow = '';
});

async function openPanel() {
  setActivePinia(createPinia());
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue();

  wrapper = mount(ConciergePanel, { attachTo: document.body, props: { team: null, modelValue: false } });
  await flushPromises();
  await wrapper.setProps({ modelValue: true });
  await flushPromises();
  await flushPromises();
  expect(document.querySelector('.concierge-host .xterm'), 'the real terminal did not open').not.toBeNull();
}

/** What the CLI printed, through the socket, and the terminal's parse of it, which is asynchronous. */
async function cliPrints(text: string) {
  sockets.at(-1)!.output(new TextEncoder().encode(text));
  await new Promise((settled) => setTimeout(settled, 20));
}

const sent = () => sockets.at(-1)!.sendInput.mock.calls.map(([data]) => data as string);

function pasteImage() {
  const image = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'shot.png', { type: 'image/png' });
  const data = {
    types: ['Files'],
    files: [image],
    items: [{ kind: 'file', type: 'image/png', getAsFile: () => image }],
    getData: () => '',
  };
  const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'clipboardData', { value: data });
  document.querySelector('.concierge-host .xterm')!.dispatchEvent(event);
}

describe('the uploaded path, pasted the way xterm pastes', () => {
  it('is bare when the CLI has not turned bracketed-paste mode on', async () => {
    await openPanel();

    pasteImage();
    await flushPromises();

    expect(sent()).toEqual([Stored]);
  });

  it('is wrapped in the markers once the CLI turns it on, and bare again once it turns it off', async () => {
    await openPanel();

    await cliPrints('\x1b[?2004h');
    pasteImage();
    await flushPromises();

    await cliPrints('\x1b[?2004l');
    pasteImage();
    await flushPromises();

    expect(sent()).toEqual([`\x1b[200~${Stored}\x1b[201~`, Stored]);
  });
});

describe('Alt and the key where QWERTY has V', () => {
  it('on a Dvorak layout, where it types k, reaches the CLI as ESC k and uploads nothing', async () => {
    await openPanel();
    const read = vi.fn();
    Object.defineProperty(navigator, 'clipboard', { value: { read }, configurable: true });

    document.querySelector('.concierge-host textarea')!.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'k',
        code: 'KeyV',
        keyCode: 75,
        altKey: true,
        bubbles: true,
        cancelable: true,
      } as KeyboardEventInit),
    );
    await flushPromises();

    expect(sent()).toEqual(['\x1bk']);
    expect(read).not.toHaveBeenCalled();
    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
    delete (navigator as { clipboard?: unknown }).clipboard;
  });
});
