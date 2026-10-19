// @vitest-environment happy-dom
//
// "DISCUSS WITH CONCIERGE", AT THE PANEL: what a card's click makes the Concierge panel do. On a
// real xterm, because the request is pasted through xterm's own `paste` and a fake would only test
// itself; the API, the socket and Quasar's platform are replaced. The ready signal is real time:
// attached, first output, then `ReadyQuietMs` of quiet.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { concierge, endConcierge, listCatalog, connectConcierge, sockets, platform } = vi.hoisted(() => {
  const sockets: { sendInput: ReturnType<typeof vi.fn>; output: (bytes: Uint8Array) => void }[] = [];
  return {
    concierge: vi.fn(),
    endConcierge: vi.fn(),
    listCatalog: vi.fn(),
    sockets,
    connectConcierge: vi.fn((_size: unknown, handlers: { onData: (bytes: Uint8Array) => void }) => {
      const socket = { sendInput: vi.fn(), sendResize: vi.fn(), dispose: vi.fn(), output: handlers.onData };
      sockets.push(socket);
      return socket;
    }),
    platform: { has: { touch: false } },
  };
});

vi.mock('quasar', async (importOriginal) => {
  const { reactive } = await import('vue');
  const screen = reactive({ lt: { sm: false } });
  return {
    ...(await importOriginal<Record<string, unknown>>()),
    useQuasar: () => ({ notify: vi.fn(), screen, platform, dark: { isActive: false } }),
  };
});

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  endConcierge,
  listCatalog,
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

import ConciergePanel from '../ConciergePanel.vue';
import { useConsoleStore } from '../../stores/console';
import { useConciergeAskStore } from '../../stores/conciergeAsk';
import { useSessionStore } from '../../stores/session';
import { ReadyQuietMs, UnsentTextSentence, type DecisionAsk } from '../../lib/conciergeDecision';
import { resetBody } from '../../test/mountQuasar';

const Me = { id: 'u-1', email: 'person@example.com' };

const Decision: DecisionAsk = {
  team: 'alpha-team',
  teamName: 'Alpha Team',
  member: 'Manager',
  workflow: 4100,
  seq: 4187,
};

/** happy-dom has no layout engine; the panel will not build a terminal onto a zero-sized box. */
function giveTheDomALayout(): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 800, configurable: true });
  }
  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 600, configurable: true });
  }
}

const wait = (ms: number) => new Promise((settled) => setTimeout(settled, ms));

let wrapper: VueWrapper | undefined;
let setActiveTeam: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  sockets.length = 0;
  vi.clearAllMocks();
  platform.has.touch = false;
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-interactive', mode: 'Interactive' }] });
  endConcierge.mockResolvedValue(undefined);
  giveTheDomALayout();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

  setActivePinia(createPinia());
  useSessionStore().user = Me;
  const board = useConsoleStore();
  vi.spyOn(board, 'refresh').mockResolvedValue();
  setActiveTeam = vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
  document.body.style.overflow = '';
});

/** The Concierge setting, with the person's own session in the list or not. */
function sessionRunning(running: boolean) {
  concierge.mockResolvedValue({
    agent: 'claude-interactive',
    sessions: running
      ? [{ user: Me.id, email: Me.email, startedAt: '2026-10-19T00:00:00Z', viewer: false }]
      : [{ user: 'someone-else', email: 'other@example.com', startedAt: '2026-10-19T00:00:00Z', viewer: true }],
  });
}

/** The panel, mounted closed with its v-model wired as the layout wires it. */
async function mountPanel() {
  wrapper = mount(ConciergePanel, {
    attachTo: document.body,
    props: {
      team: null,
      modelValue: false,
      'onUpdate:modelValue': (value: boolean) => wrapper?.setProps({ modelValue: value }),
    },
  });
  await flushPromises();
}

async function openPanel() {
  await wrapper!.setProps({ modelValue: true });
  await flushPromises();
  await flushPromises();
}

const socket = () => sockets.at(-1)!;
const sent = () => socket().sendInput.mock.calls.map(([data]) => data as string);
const isOpen = () => document.querySelector('.concierge-shell') !== null;
const notice = () => document.querySelector('[data-test="concierge-ask-notice"] .col')?.textContent?.trim() ?? '';

/** What the CLI printed, through the socket; the terminal's parse of it is asynchronous. */
async function cliPrints(text: string) {
  socket().output(new TextEncoder().encode(text));
  await wait(20);
}

/** A CLI that has started: bracketed-paste mode on and a prompt drawn, then quiet until ready. */
async function cliStartsAndSettles() {
  await cliPrints('\x1b[?2004h> ');
  await wait(ReadyQuietMs + 100);
}

/** The person pastes text at the prompt, as a paste into xterm's textarea. */
function personPastes(text: string) {
  const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'clipboardData', {
    value: { types: ['text/plain'], files: [], items: [], getData: () => text },
  });
  document.querySelector('.concierge-host textarea')!.dispatchEvent(event);
}

function click(decision: DecisionAsk = Decision) {
  useConciergeAskStore().ask(decision);
}

/** The request typed (the last paste), unwrapped from the paste markers, and what followed it. */
function typedRequest(): { request: string; after: string[] } {
  const calls = sent();
  const index = calls.findLastIndex((data) => data.startsWith('\x1b[200~'));
  expect(index, 'no request was pasted').toBeGreaterThanOrEqual(0);
  return { request: calls[index]!.slice(6, -6), after: calls.slice(index + 1) };
}

describe('a click with the panel open on a ready session', () => {
  it('types one request naming the team, member, workflow and seq, then submits it', async () => {
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliStartsAndSettles();

    click();
    await wait(100);

    const pasted = sent().filter((data) => data.startsWith('\x1b[200~'));
    expect(pasted).toHaveLength(1);

    const { request, after } = typedRequest();
    expect(request).toContain('"Alpha Team"');
    expect(request).toContain('alpha-team');
    expect(request).toContain('Manager');
    expect(request).toContain('workflow 4100');
    expect(request).toContain('seq 4187');
    expect(request).not.toMatch(/[\r\n]/);
    expect(after).toEqual(['\r']);
  });

  it('goes into the session as it is, on another team: nothing is started, ended or switched', async () => {
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliStartsAndSettles();

    click({ ...Decision, team: 'beta-team', teamName: 'Beta' });
    await wait(100);

    expect(typedRequest().request).toContain('beta-team');
    expect(connectConcierge).toHaveBeenCalledTimes(1);
    expect(endConcierge).not.toHaveBeenCalled();
    expect(setActiveTeam).not.toHaveBeenCalled();
  });

  it('a second click before the first is typed still types one request', async () => {
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliPrints('\x1b[?2004h> ');

    click();
    click({ ...Decision, seq: 4200 });
    await wait(ReadyQuietMs + 200);

    const pasted = sent().filter((data) => data.startsWith('\x1b[200~'));
    expect(pasted).toHaveLength(1);
    expect(pasted[0]).toContain('seq 4200');
  });
});

describe('a click with the panel closed', () => {
  it('opens the panel, and with no session running starts one on the card\'s team and types only once it is ready', async () => {
    sessionRunning(false);
    await mountPanel();

    click();
    await flushPromises();
    await flushPromises();

    expect(isOpen()).toBe(true);
    expect(connectConcierge).toHaveBeenCalledTimes(1);
    expect(setActiveTeam).toHaveBeenCalledWith('alpha-team');

    // Attached, nothing printed yet: not ready.
    await wait(ReadyQuietMs + 100);
    expect(sent()).toEqual([]);

    // First output, but not yet quiet for long enough: still not ready.
    await cliPrints('\x1b[?2004hstarting');
    await wait(ReadyQuietMs - 600);
    expect(sent()).toEqual([]);

    // More output pushes the moment back.
    await cliPrints('> ');
    await wait(ReadyQuietMs - 600);
    expect(sent()).toEqual([]);

    await wait(800);
    const { request, after } = typedRequest();
    expect(request).toContain('seq 4187');
    expect(after).toEqual(['\r']);
    expect(endConcierge).not.toHaveBeenCalled();
  });

  it('with a session running on another team, types into it once attached and ready, and switches nothing', async () => {
    sessionRunning(true);
    await mountPanel();

    click();
    await flushPromises();
    await flushPromises();

    expect(isOpen()).toBe(true);
    await cliStartsAndSettles();
    await wait(100);

    expect(typedRequest().request).toContain('alpha-team');
    expect(connectConcierge).toHaveBeenCalledTimes(1);
    expect(endConcierge).not.toHaveBeenCalled();
    expect(setActiveTeam).not.toHaveBeenCalled();
  });
});

describe('the person\'s own unsent text', () => {
  it('at the prompt: nothing is typed, and the panel says so in one sentence', async () => {
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliStartsAndSettles();
    personPastes('half a thought');
    await wait(50);
    const before = sent().length;

    click();
    await wait(100);
    await flushPromises();

    expect(sent().length).toBe(before);
    expect(notice()).toBe(UnsentTextSentence);
  });

  it('once submitted, no longer stops the next click', async () => {
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliStartsAndSettles();
    personPastes('a question of my own');
    document.querySelector('.concierge-host textarea')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }),
    );
    await wait(50);

    click();
    await wait(100);

    expect(typedRequest().request).toContain('seq 4187');
    expect(notice()).toBe('');
  });

  it('in the touch compose bar: nothing is typed, and the panel says so', async () => {
    platform.has.touch = true;
    sessionRunning(true);
    await mountPanel();
    await openPanel();
    await cliStartsAndSettles();
    const field = document.querySelector('input[aria-label="Compose a line for the console"]') as HTMLInputElement;
    field.value = 'dictated, not sent';
    field.dispatchEvent(new Event('input'));
    await flushPromises();

    click();
    await wait(100);
    await flushPromises();

    expect(sent().filter((data) => data.startsWith('\x1b[200~'))).toEqual([]);
    expect(sent()).not.toContain('\r');
    expect(notice()).toBe(UnsentTextSentence);
  });
});
