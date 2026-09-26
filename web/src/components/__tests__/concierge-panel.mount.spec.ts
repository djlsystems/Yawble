// @vitest-environment happy-dom
//
// THE CONCIERGE PANEL, MOUNTED CLOSED AND THEN OPENED. Mounted rather than read as text: a
// substring of `ConciergePanel.vue` can be present while the behaviour it names is broken.
//
// XTERM IS FAKED, AND ONLY XTERM. happy-dom has no canvas and no layout, so the real terminal can
// be opened (see `main-layout.mount.spec.ts`) but not observed: its options, its fit and its key
// handler are what this file asserts, so the fake records exactly those. Everything else - the
// panel, the display store, the socket wiring - is the real code.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const fake = vi.hoisted(() => {
  class FakeTerminal {
    static instances: FakeTerminal[] = [];
    options: Record<string, unknown>;
    cols = 80;
    rows = 24;
    keyHandler: ((event: KeyboardEvent) => boolean) | null = null;
    refresh = vi.fn();
    write = vi.fn();
    clear = vi.fn();
    dispose = vi.fn();
    scrollLines = vi.fn();

    constructor(options: Record<string, unknown>) {
      this.options = { ...options };
      FakeTerminal.instances.push(this);
    }

    loadAddon(addon: { terminal?: FakeTerminal }) {
      addon.terminal = this;
    }

    open(host: HTMLElement) {
      const element = document.createElement('div');
      element.className = 'xterm';
      host.appendChild(element);
    }

    onData() {}

    attachCustomKeyEventHandler(handler: (event: KeyboardEvent) => boolean) {
      this.keyHandler = handler;
    }
  }

  /** What the next `fit()` sizes the terminal to; unchanged unless a case says otherwise. */
  const nextSize = null as { cols: number; rows: number } | null;
  const fits: string[] = [];

  class FakeFitAddon {
    terminal?: FakeTerminal;

    fit() {
      fits.push('fit');
      if (fake.nextSize && this.terminal) {
        this.terminal.cols = fake.nextSize.cols;
        this.terminal.rows = fake.nextSize.rows;
      }
    }
  }

  return { FakeTerminal, FakeFitAddon, nextSize, fits };
});

const { concierge, endConcierge, listCatalog, setConcierge, connectConcierge, sockets, notify, screen } =
  vi.hoisted(() => {
    const sockets: { sendInput: ReturnType<typeof vi.fn>; sendResize: ReturnType<typeof vi.fn>; dispose: ReturnType<typeof vi.fn> }[] = [];
    return {
      concierge: vi.fn(),
      endConcierge: vi.fn(),
      listCatalog: vi.fn(),
      setConcierge: vi.fn(),
      sockets,
      connectConcierge: vi.fn(() => {
        const socket = { sendInput: vi.fn(), sendResize: vi.fn(), dispose: vi.fn() };
        sockets.push(socket);
        return socket;
      }),
      notify: vi.fn(),
      screen: { lt: { sm: false } },
    };
  });

vi.mock('@xterm/xterm', () => ({ Terminal: fake.FakeTerminal }));
vi.mock('@xterm/addon-fit', () => ({ FitAddon: fake.FakeFitAddon }));

vi.mock('quasar', async (importOriginal) => {
  const { reactive: makeReactive } = await import('vue');
  const reactiveScreen = makeReactive(screen);
  return {
    ...(await importOriginal<Record<string, unknown>>()),
    useQuasar: () => ({ notify, screen: reactiveScreen, platform: { has: { touch: false } }, dark: { isActive: false } }),
  };
});

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  endConcierge,
  listCatalog,
  setConcierge,
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

import ConciergePanel from '../ConciergePanel.vue';
import { useConsoleStore } from '../../stores/console';
import { useTerminalDisplayStore } from '../../stores/terminalDisplay';
import { conciergeNewline } from '../../lib/conciergeNewline';
import { useQuasar } from 'quasar';
import type { TeamId } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

/** happy-dom has no layout engine; the panel will not build a terminal onto a zero-sized box. */
function giveTheDomALayout(width = 800, height = 600): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: width, configurable: true });
  }
  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: height, configurable: true });
  }
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  fake.FakeTerminal.instances = [];
  fake.nextSize = null;
  fake.fits.length = 0;
  sockets.length = 0;
  vi.clearAllMocks();
  localStorage.clear();
  useQuasar().screen.lt.sm = false;

  concierge.mockResolvedValue({ agent: 'claude-interactive' });
  endConcierge.mockResolvedValue(undefined);
  setConcierge.mockResolvedValue(undefined);
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-interactive', mode: 'Interactive' }, { name: 'codex-interactive', mode: 'Interactive' }],
  });

  giveTheDomALayout();
  for (const method of ['setPointerCapture', 'releasePointerCapture'] as const) {
    Object.defineProperty(window.Element.prototype, method, { value: () => {}, configurable: true });
  }
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
  document.body.style.overflow = '';
  delete (document as { fonts?: unknown }).fonts;
});

async function openPanel(props: Record<string, unknown> = {}, before: () => void = () => {}) {
  setActivePinia(createPinia());
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue();
  before();

  wrapper = mount(ConciergePanel, {
    attachTo: document.body,
    props: { team: null, modelValue: false, ...props },
  });
  await flushPromises();
  await wrapper.setProps({ modelValue: true });
  await flushPromises();
  await flushPromises();
  return wrapper;
}

const shell = () => document.body.querySelector('.concierge-shell') as HTMLElement;
const text = () => shell()?.textContent?.replace(/\s+/g, ' ') ?? '';
const terminal = () => fake.FakeTerminal.instances.at(-1)!;
const button = (label: string) =>
  document.body.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement | null;

async function click(label: string) {
  const found = button(label);
  if (!found) throw new Error(`no "${label}" button in the panel`);
  found.click();
  await flushPromises();
  await flushPromises();
}

function pointer(type: string, target: Element, x: number, y: number) {
  target.dispatchEvent(new PointerEvent(type, { clientX: x, clientY: y, bubbles: true, pointerId: 1 }));
}

describe('the panel header', () => {
  it('states the active team the shell is on', async () => {
    await openPanel({ activeTeam: 'beta', activeTeamName: 'Beta' });

    expect(shell().querySelector('.concierge-active-team')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe('active team Beta');
  });

  it('gives no active team words rather than a blank line', async () => {
    await openPanel();

    expect(shell().querySelector('.concierge-active-team')?.textContent?.trim()).toBe('no active team');
  });

  /**
   * NO HEDGE. A `this session stays on <X>` clause would claim a session is pinned to the team it
   * spawned on; it is not - the agent resolves its current team per command.
   */
  it('does not hedge the header with a launch team', async () => {
    await openPanel({ team: 'alpha', teamName: 'Alpha', activeTeam: 'beta', activeTeamName: 'Beta' });

    expect(text()).not.toContain('stays on');
    expect(text()).not.toContain('Alpha');
  });

  /**
   * THE SOURCE: the header reports the TENANT setting the child was
   * launched with, read at attach - never a team row, and never bound live.
   */
  it('snapshots the tenant setting at attach, and never a team row', async () => {
    const panel = await openPanel({ team: 'alpha' }, () => {
      useConsoleStore().$patch({ teams: [{ id: 'alpha' as TeamId, name: 'Alpha', concierge: 'from-the-team-row', containers: [] }] as never });
    });

    const picker = panel.findAllComponents({ name: 'QSelect' })[0]!;
    expect(concierge).toHaveBeenCalledTimes(1);
    expect(picker.props('modelValue')).toBe('claude-interactive');
    expect(text()).not.toMatch(/prompt/i);
    expect(text()).not.toContain('from-the-team-row');
  });
});

describe('the pre-flight', () => {
  const effective = {
    agent: 'claude-interactive',
    agentSource: 'chosen',
    auth: { installed: true, signedIn: true, detail: null },
  };
  const banner = () => shell().querySelector('.concierge-preflight');
  const bannerText = () => banner()?.querySelector('.concierge-preflight-text')?.textContent?.replace(/\s+/g, ' ').trim();

  it('shows no banner when both are chosen and the agent is signed in', async () => {
    concierge.mockResolvedValue({ agent: 'claude-interactive', effective });

    await openPanel();

    expect(banner()).toBeNull();
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  it('names a default agent in the header from effective, and opens', async () => {
    concierge.mockResolvedValue({
      agent: 'claude-interactive',
      effective: { ...effective, agent: 'grok', agentSource: 'default' },
    });

    const panel = await openPanel();

    expect(bannerText()).toBe(
      'Using grok, the first signed-in agent; change it in Tenant Settings.',
    );
    expect(panel.findAllComponents({ name: 'QSelect' })[0]!.props('modelValue')).toBe('grok');
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  /** A fresh volume: nobody has chosen, so the stored agent and every team's `concierge` are NULL. */
  it('with no agent chosen, names the default and opens, and never says "null"', async () => {
    concierge.mockResolvedValue({
      agent: null,
      effective: { ...effective, agent: 'grok', agentSource: 'default' },
    });

    const panel = await openPanel({ team: 'alpha' }, () => {
      useConsoleStore().$patch({ teams: [{ id: 'alpha' as TeamId, name: 'Alpha', concierge: null, containers: [] }] as never });
    });

    expect(bannerText()).toBe('Using grok, the first signed-in agent; change it in Tenant Settings.');
    expect(panel.findAllComponents({ name: 'QSelect' })[0]!.props('modelValue')).toBe('grok');
    expect(text()).not.toContain('null');
    expect(shell().querySelector('.concierge-moved')).toBeNull();
    expect(connectConcierge).toHaveBeenCalledTimes(1);

    panel.findAllComponents({ name: 'QSelect' })[0]!.vm.$emit('update:modelValue', 'codex-interactive');
    await flushPromises();
    await flushPromises();

    expect(setConcierge).toHaveBeenCalledWith({ agent: 'codex-interactive' });
  });

  it('says an installed agent is not signed in, and still opens', async () => {
    concierge.mockResolvedValue({
      agent: 'claude',
      effective: { ...effective, agent: 'claude', auth: { installed: true, signedIn: false, detail: null } },
    });

    await openPanel();

    expect(bannerText()).toBe(
      'claude is installed but not signed in; sign in from a terminal or add a key, or choose another agent in Tenant Settings.',
    );
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  it('with no agent, shows the sentence and the link and opens no socket', async () => {
    concierge.mockResolvedValue({
      agent: 'claude',
      effective: { ...effective, agent: null, agentSource: 'default', auth: { installed: false, signedIn: false, detail: null } },
    });

    await openPanel();

    expect(bannerText()).toContain('No installed agent can run the Concierge');
    expect(banner()!.classList).toContain('concierge-preflight-blocked');
    expect(connectConcierge).not.toHaveBeenCalled();
    expect(fake.FakeTerminal.instances).toHaveLength(0);
    expect(text()).not.toContain('Concierge ended');
  });

  it('opens Tenant Settings on the Concierge tab from the link', async () => {
    concierge.mockResolvedValue({
      agent: 'claude',
      effective: { ...effective, agent: null, auth: { installed: false, signedIn: false, detail: null } },
    });
    await openPanel();

    (shell().querySelector('.concierge-preflight-link') as HTMLAnchorElement).click();
    await flushPromises();

    const settings = wrapper!.findComponent({ name: 'TenantSettingsDialog' });
    expect(settings.props('modelValue')).toBe(true);
    expect(settings.props('initialTab')).toBe('concierge');
  });

  /** Fixed in Settings, the panel tries again on close rather than staying held back. */
  it('tries again when Settings closes, and attaches once it can start', async () => {
    concierge.mockResolvedValue({
      agent: 'claude',
      effective: { ...effective, agent: null, auth: { installed: false, signedIn: false, detail: null } },
    });
    await openPanel();
    expect(connectConcierge).not.toHaveBeenCalled();

    concierge.mockResolvedValue({ agent: 'claude-interactive', effective });
    wrapper!.findComponent({ name: 'TenantSettingsDialog' }).vm.$emit('update:modelValue', false);
    await flushPromises();
    await flushPromises();

    expect(banner()).toBeNull();
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });
});

describe('windowed and full screen', () => {
  it('is a window at 600px and wider, with resize handles and the page left scrollable', async () => {
    await openPanel();

    expect(shell().classList).toContain('is-windowed');
    expect(shell().querySelectorAll('.concierge-resize-e, .concierge-resize-s, .concierge-resize-se')).toHaveLength(3);
    expect(document.body.style.overflow).toBe('');
  });

  it('is full screen below 600px, and locks the page behind it', async () => {
    await openPanel({}, () => {
      useQuasar().screen.lt.sm = true;
    });

    expect(shell().classList).not.toContain('is-windowed');
    expect(document.body.style.overflow).toBe('hidden');
  });

  it('maximises and restores from the toolbar', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();

    await click('Maximize');
    expect(display.maximised).toBe(true);
    expect(shell().classList).not.toContain('is-windowed');
    expect(document.body.style.overflow).toBe('hidden');

    await click('Restore');
    expect(display.maximised).toBe(false);
    expect(document.body.style.overflow).toBe('');
  });

  it('sizes the window to the viewport on open if it has no geometry yet', async () => {
    let initialize: ReturnType<typeof vi.spyOn> | undefined;
    await openPanel({}, () => {
      initialize = vi.spyOn(useTerminalDisplayStore(), 'initializeGeometryIfNeeded');
    });

    expect(initialize).toHaveBeenCalledWith(window.innerWidth, window.innerHeight);
  });
});

describe('the terminal', () => {
  it('is built with the display store\'s font, not a literal', async () => {
    await openPanel({}, () => {
      useTerminalDisplayStore().set({ fontSize: 17, fontFamily: 'Consolas, monospace' });
    });

    expect(terminal().options.fontSize).toBe(17);
    expect(terminal().options.fontFamily).toBe('Consolas, monospace');
    expect(document.body.querySelector('.xterm')).not.toBeNull();
  });

  it('applies a font change live, without reattaching', async () => {
    await openPanel();

    useTerminalDisplayStore().set({ fontSize: 19 });
    await flushPromises();

    expect(fake.FakeTerminal.instances).toHaveLength(1);
    expect(terminal().options.fontSize).toBe(19);
  });

  /** A zero-sized host is where a terminal would be drawn for a screen nobody has. */
  it('builds nothing onto a box with no size, and attaches once the box appears', async () => {
    const observers: (() => void)[] = [];
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: () => void) {
        observers.push(callback);
      }
      observe() {}
      disconnect() {}
    });
    giveTheDomALayout(0, 0);

    await openPanel();
    expect(fake.FakeTerminal.instances).toHaveLength(0);
    expect(connectConcierge).not.toHaveBeenCalled();

    giveTheDomALayout();
    observers[0]!();
    await flushPromises();

    expect(fake.FakeTerminal.instances).toHaveLength(1);
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  /** A fit measured against a fallback font sizes the terminal for glyphs it will not draw. */
  it('waits for a Plex font to load before the first fit and the connect', async () => {
    let loaded: () => void = () => {};
    Object.defineProperty(document, 'fonts', {
      configurable: true,
      value: { load: vi.fn(() => new Promise<void>((resolve) => { loaded = resolve; })), ready: Promise.resolve() },
    });

    await openPanel({}, () => {
      useTerminalDisplayStore().set({ fontFamily: '"IBM Plex Mono", Consolas, monospace' });
    });

    expect(fake.fits).toHaveLength(0);
    expect(connectConcierge).not.toHaveBeenCalled();

    loaded();
    await flushPromises();

    expect(fake.fits.length).toBeGreaterThan(0);
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  /**
   * SHIFT+ENTER IS A NEWLINE, and all three parts of the decision are honoured: the browser's
   * default is prevented, the newline is sent, and xterm is told not to send Enter as well. The
   * decision itself is `lib/conciergeKeystroke.ts`'s and tested there.
   */
  it('turns Shift+Enter into a newline and swallows the Enter', async () => {
    await openPanel();
    const event = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, cancelable: true });

    const passOn = terminal().keyHandler!(event);

    expect(event.defaultPrevented).toBe(true);
    expect(sockets[0]!.sendInput).toHaveBeenCalledWith(conciergeNewline());
    expect(passOn).toBe(false);
  });
});

describe('Refresh and Reload', () => {
  it('Refresh redraws and reattaches without ending the agent', async () => {
    await openPanel();
    const first = sockets[0]!;

    await click('Refresh display — does not restart the agent');

    expect(endConcierge).not.toHaveBeenCalled();
    expect(first.dispose).toHaveBeenCalled();
    expect(connectConcierge).toHaveBeenCalledTimes(2);
    expect(first.sendResize).not.toHaveBeenCalled();
  });

  it('Refresh sends a resize only when the geometry actually changed', async () => {
    await openPanel();
    const first = sockets[0]!;
    fake.nextSize = { cols: 100, rows: 30 };

    await click('Refresh display — does not restart the agent');

    expect(first.sendResize).toHaveBeenCalledWith(100, 30);
  });

  it('Reload asks first, then ends the agent and starts a new one', async () => {
    await openPanel({ team: 'alpha' });

    await click('Reload the Concierge');
    expect(endConcierge).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('Reload the Concierge?');

    const confirm = [...document.body.querySelectorAll('.reset-confirm-card button')]
      .find((b) => b.textContent?.trim() !== 'Cancel') as HTMLButtonElement;
    confirm.click();
    await flushPromises();
    await flushPromises();

    // The session is the person's, so neither the end nor the reattach names the team.
    expect(endConcierge).toHaveBeenCalledWith();
    expect(connectConcierge).toHaveBeenCalledTimes(2);
    expect(connectConcierge).toHaveBeenLastCalledWith(
      expect.objectContaining({ cols: expect.any(Number), rows: expect.any(Number) }),
      expect.anything(),
    );
  });
});

describe('the Agent picker in the header', () => {
  it('lifts its menu above the panel', async () => {
    const panel = await openPanel();

    expect(panel.findAllComponents({ name: 'QSelect' })[0]!.props('popupContentClass')).toBe('concierge-agent-menu');
  });

  /** `{ agent }` alone: the agent is the whole of the Concierge setting. */
  it('sends only the agent when switching, then restarts the Concierge', async () => {
    const panel = await openPanel();

    panel.findAllComponents({ name: 'QSelect' })[0]!.vm.$emit('update:modelValue', 'codex-interactive');
    await flushPromises();
    await flushPromises();

    expect(setConcierge).toHaveBeenCalledWith({ agent: 'codex-interactive' });
    expect(endConcierge).toHaveBeenCalled();
  });

  it('does not drag the window when the picker is pressed', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 100, top: 100, width: 400, height: 300 });
    const picker = shell().querySelector('.concierge-subtitle .q-field__native')
      ?? shell().querySelector('.concierge-subtitle .q-field__control')!;

    pointer('pointerdown', picker, 100, 100);
    pointer('pointermove', shell(), 160, 100);

    expect(display.left).toBe(100);
  });
});

describe('the settings gear', () => {
  /** There is no separate Concierge dialog: the gear opens Admin > Settings on its Concierge tab. */
  it('opens Settings on the Concierge tab', async () => {
    await openPanel();

    await click('Concierge settings');

    const settings = wrapper!.findComponent({ name: 'TenantSettingsDialog' });
    expect(settings.exists(), 'no Settings dialog in the panel').toBe(true);
    expect(settings.props('modelValue')).toBe(true);
    expect(settings.props('initialTab')).toBe('concierge');
  });
});

describe('dragging the window', () => {
  it('moves the window with the toolbar, and stops on pointerup', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 100, top: 100, width: 400, height: 300 });
    const bar = shell().querySelector('.concierge-bar')!;

    pointer('pointerdown', bar, 200, 200);
    pointer('pointermove', shell(), 230, 210);
    expect([display.left, display.top]).toEqual([130, 110]);

    pointer('pointerup', shell(), 230, 210);
    pointer('pointermove', shell(), 300, 300);
    expect([display.left, display.top]).toEqual([130, 110]);
  });

  /** A touch drag the browser takes over ends in pointercancel, never pointerup. */
  it('stops on pointercancel as it does on pointerup', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 100, top: 100 });

    pointer('pointerdown', shell().querySelector('.concierge-bar')!, 200, 200);
    pointer('pointercancel', shell(), 200, 200);
    pointer('pointermove', shell(), 260, 260);

    expect([display.left, display.top]).toEqual([100, 100]);
  });

  it('stops a resize on pointercancel too', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 0, top: 0, width: 500, height: 400 });

    pointer('pointerdown', shell().querySelector('.concierge-resize-e')!, 500, 200);
    pointer('pointercancel', shell(), 500, 200);
    pointer('pointermove', shell(), 560, 200);

    expect(display.width).toBe(500);
  });

  it('keeps the window inside the viewport', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 100, top: 100 });

    pointer('pointerdown', shell().querySelector('.concierge-bar')!, 200, 200);
    pointer('pointermove', shell(), 200 - 5000, 200 - 5000);

    expect(display.left).toBeGreaterThanOrEqual(0);
    expect(display.top).toBeGreaterThanOrEqual(0);
  });

  it('does not start a drag from a toolbar button', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 100, top: 100 });

    pointer('pointerdown', button('Concierge settings')!, 200, 200);
    pointer('pointermove', shell(), 260, 260);

    expect([display.left, display.top]).toEqual([100, 100]);
  });

  it('resizes from the south-east handle', async () => {
    await openPanel();
    const display = useTerminalDisplayStore();
    display.set({ left: 0, top: 0, width: 500, height: 400 });

    pointer('pointerdown', shell().querySelector('.concierge-resize-se')!, 500, 400);
    pointer('pointermove', shell(), 540, 430);

    expect([display.width, display.height]).toEqual([540, 430]);
  });
});
