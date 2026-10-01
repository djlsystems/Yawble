// @vitest-environment happy-dom
//
// THE CONCIERGE PANEL, MOUNTED CLOSED AND THEN OPENED. Mounted rather than read as text: a
// substring of `ConciergePanel.vue` can be present while the behaviour it names is broken.
//
// XTERM IS FAKED, AND ONLY XTERM. happy-dom has no canvas and no layout, so the real terminal can
// be opened (see `main-layout.mount.spec.ts`) but not observed: its options, its fit and its key
// handler are what this file asserts, so the fake records exactly those. Everything else - the
// panel, the display store, the socket wiring - is the real code.
import { productCli } from '../../presentation/product';
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
    focus = vi.fn();
    modes = { bracketedPasteMode: false };
    dataHandler: ((data: string) => void) | null = null;

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

    onData(handler: (data: string) => void) {
      this.dataHandler = handler;
    }

    /**
     * xterm's own rule, and the only part of its paste this file depends on: wrapped only in
     * bracketed-paste mode, then out through onData. `concierge-panel.xterm.mount.spec.ts` holds the
     * real terminal to it.
     */
    paste(text: string) {
      this.dataHandler?.(this.modes.bracketedPasteMode ? `\x1b[200~${text}\x1b[201~` : text);
    }

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

const { concierge, endConcierge, listCatalog, setConcierge, uploadConciergeAttachment, connectConcierge, sockets, notify, screen, platform } =
  vi.hoisted(() => {
    const sockets: { sendInput: ReturnType<typeof vi.fn>; sendResize: ReturnType<typeof vi.fn>; dispose: ReturnType<typeof vi.fn> }[] = [];
    return {
      concierge: vi.fn(),
      endConcierge: vi.fn(),
      listCatalog: vi.fn(),
      setConcierge: vi.fn(),
      uploadConciergeAttachment: vi.fn(),
      sockets,
      connectConcierge: vi.fn(() => {
        const socket = { sendInput: vi.fn(), sendResize: vi.fn(), dispose: vi.fn() };
        sockets.push(socket);
        return socket;
      }),
      notify: vi.fn(),
      screen: { lt: { sm: false } },
      platform: { has: { touch: false } },
    };
  });

vi.mock('@xterm/xterm', () => ({ Terminal: fake.FakeTerminal }));
vi.mock('@xterm/addon-fit', () => ({ FitAddon: fake.FakeFitAddon }));

vi.mock('quasar', async (importOriginal) => {
  const { reactive: makeReactive } = await import('vue');
  const reactiveScreen = makeReactive(screen);
  return {
    ...(await importOriginal<Record<string, unknown>>()),
    useQuasar: () => ({ notify, screen: reactiveScreen, platform, dark: { isActive: false } }),
  };
});

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  endConcierge,
  listCatalog,
  setConcierge,
  uploadConciergeAttachment,
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
import { ActionRefused } from '../../api/client';
import { NoConciergeToReceive, NotAnImage } from '../../lib/conciergeAttachment';

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
  platform.has.touch = false;

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
  delete (navigator as { clipboard?: unknown }).clipboard;
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
      `claude is not signed in yet: sign in below, in this terminal, or store a key with ${productCli} secret set; or choose another agent in Tenant Settings.`,
    );
    expect(connectConcierge).toHaveBeenCalledTimes(1);
  });

  /**
   * The person signs in inside this terminal, so the notice must not outlive the sign-in. Only
   * setInterval is faked: the helpers' flushPromises needs the real setTimeout.
   */
  describe('after a sign-in in the terminal', () => {
    const notSignedIn = { ...effective, agent: 'claude', auth: { installed: true, signedIn: false, detail: null } };
    const signedIn = { ...effective, agent: 'claude', auth: { installed: true, signedIn: true, detail: null } };

    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    it('asks again and clears the notice once the agent is signed in, without restarting the session', async () => {
      concierge.mockResolvedValue({ agent: 'claude', effective: notSignedIn });
      await openPanel();
      expect(bannerText()).toContain('not signed in yet');

      // Still not signed in: the notice stays.
      vi.advanceTimersByTime(15_000);
      await flushPromises();
      expect(bannerText()).toContain('not signed in yet');

      concierge.mockResolvedValue({ agent: 'claude', effective: signedIn });
      vi.advanceTimersByTime(15_000);
      await flushPromises();
      expect(banner()).toBeNull();
      expect(connectConcierge).toHaveBeenCalledTimes(1);

      // Signed in is the end of it: no more asking.
      const asked = concierge.mock.calls.length;
      vi.advanceTimersByTime(60_000);
      await flushPromises();
      expect(concierge.mock.calls.length).toBe(asked);
    });

    it('stops asking when the panel is closed', async () => {
      concierge.mockResolvedValue({ agent: 'claude', effective: notSignedIn });
      await openPanel();
      await wrapper!.setProps({ modelValue: false });
      await flushPromises();

      const asked = concierge.mock.calls.length;
      vi.advanceTimersByTime(60_000);
      await flushPromises();
      expect(concierge.mock.calls.length).toBe(asked);
    });

    it('does not ask at all when the agent is already signed in', async () => {
      concierge.mockResolvedValue({ agent: 'claude', effective: signedIn });
      await openPanel();

      const asked = concierge.mock.calls.length;
      vi.advanceTimersByTime(60_000);
      await flushPromises();
      expect(concierge.mock.calls.length).toBe(asked);
    });
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

describe('attaching an image', () => {
  const Stored = '/data/concierge/attachments/20261001T120000Z-1.png';
  // Bare: the fake terminal starts, as a real one does, with bracketed-paste mode off.
  const pastedPath = Stored;
  const png = () => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'shot.png', { type: 'image/png' });

  /** Only what the panel reads off a DataTransfer, so the case states exactly what the browser had. */
  function transfer(files: File[], text?: string): DataTransfer {
    return {
      types: [...(text === undefined ? [] : ['text/plain']), ...(files.length ? ['Files'] : [])],
      files,
      items: files.map((file) => ({ kind: 'file', type: file.type, getAsFile: () => file })),
      getData: (type: string) => (type === 'text/plain' ? text ?? '' : ''),
    } as unknown as DataTransfer;
  }

  function paste(data: DataTransfer): ClipboardEvent {
    const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: data });
    // Onto xterm's own element, as a real paste lands - the host listens in the capture phase.
    shell().querySelector('.concierge-host .xterm')!.dispatchEvent(event);
    return event;
  }

  function drop(target: Element, data: DataTransfer): DragEvent {
    const event = new DragEvent('drop', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: data });
    target.dispatchEvent(event);
    return event;
  }

  function clipboardHolding(types: Record<string, Blob>) {
    const read = vi.fn(async () => [{ types: Object.keys(types), getType: async (type: string) => types[type]! }]);
    Object.defineProperty(navigator, 'clipboard', { value: { read }, configurable: true });
    return read;
  }

  function altV(key = 'v', code = 'KeyV'): { event: KeyboardEvent; passOn: boolean } {
    const event = new KeyboardEvent('keydown', { key, code, altKey: true, cancelable: true });
    return { event, passOn: terminal().keyHandler!(event) };
  }

  const sent = () => sockets.at(-1)!.sendInput.mock.calls.map(([data]) => data as string);
  const alert = () => shell().querySelector('[role="alert"]')?.textContent?.trim() ?? '';

  beforeEach(() => {
    uploadConciergeAttachment.mockResolvedValue({ path: Stored, size: 4, type: 'image/png' });
  });

  it('uploads an image paste and pastes the path it was stored at', async () => {
    await openPanel();
    const image = png();

    const event = paste(transfer([image]));
    await flushPromises();

    expect(event.defaultPrevented).toBe(true);
    expect(uploadConciergeAttachment).toHaveBeenCalledWith(image);
    expect(sent()).toEqual([pastedPath]);
  });

  it('leaves a paste carrying text to the terminal, even with a picture beside it', async () => {
    await openPanel();

    const event = paste(transfer([png()], 'A1\tB1'));
    await flushPromises();

    expect(event.defaultPrevented).toBe(false);
    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
  });

  it('uploads the clipboard image on Alt+V and sends no key', async () => {
    await openPanel();
    const read = clipboardHolding({ 'image/png': new Blob([new Uint8Array([1])], { type: 'image/png' }) });

    const { event, passOn } = altV();
    await flushPromises();

    expect([passOn, event.defaultPrevented]).toEqual([false, true]);
    expect(read).toHaveBeenCalledOnce();
    expect((uploadConciergeAttachment.mock.calls[0]![0] as File).type).toBe('image/png');
    expect(sent()).toEqual([pastedPath]);
  });

  it('sends Alt+V on unchanged when the clipboard holds text', async () => {
    await openPanel();
    clipboardHolding({ 'text/plain': new Blob(['hello'], { type: 'text/plain' }) });

    altV();
    await flushPromises();

    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
    // ESC v: what xterm sends for Alt+V with Alt as Meta.
    expect(sent()).toEqual(['\x1bv']);
  });

  it('sends Alt+V on unchanged where the browser has no clipboard.read, or refuses it', async () => {
    await openPanel();
    Object.defineProperty(navigator, 'clipboard', { value: {}, configurable: true });

    altV();
    await flushPromises();

    Object.defineProperty(navigator, 'clipboard', {
      value: { read: () => Promise.reject(new DOMException('denied', 'NotAllowedError')) },
      configurable: true,
    });

    altV();
    await flushPromises();

    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
    expect(sent()).toEqual(['\x1bv', '\x1bv']);
  });

  it('leaves Alt+K on a Dvorak layout to xterm, though it is the key where QWERTY has V', async () => {
    await openPanel();
    const read = clipboardHolding({ 'image/png': new Blob([new Uint8Array([1])], { type: 'image/png' }) });

    const { event, passOn } = altV('k', 'KeyV');
    await flushPromises();

    // Passed on and not cancelled, so xterm sends its own ESC k (the real-terminal spec shows it).
    expect([passOn, event.defaultPrevented]).toEqual([true, false]);
    expect(read).not.toHaveBeenCalled();
    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
    expect(sent()).toEqual([]);
  });

  it('still takes a Mac Option+V, which types √, and forwards the √ when there is no image', async () => {
    const platform = vi.spyOn(navigator, 'platform', 'get').mockReturnValue('MacIntel');
    await openPanel();
    clipboardHolding({ 'image/png': new Blob([new Uint8Array([1])], { type: 'image/png' }) });

    const taken = altV('√', 'KeyV');
    await flushPromises();

    expect([taken.passOn, taken.event.defaultPrevented]).toEqual([false, true]);
    expect(uploadConciergeAttachment).toHaveBeenCalledOnce();

    clipboardHolding({ 'text/plain': new Blob(['hello'], { type: 'text/plain' }) });
    altV('√', 'KeyV');
    await flushPromises();

    expect(sent()).toEqual([pastedPath, '√']);
    platform.mockRestore();
  });

  it('uploads dropped images and pastes their paths', async () => {
    await openPanel();
    const image = png();

    const event = drop(shell().querySelector('.concierge-host')!, transfer([image]));
    await flushPromises();

    expect(event.defaultPrevented).toBe(true);
    expect(uploadConciergeAttachment).toHaveBeenCalledWith(image);
    expect(sent()).toEqual([pastedPath]);
  });

  it('opens a picker from the Attach image button and uploads what is picked', async () => {
    await openPanel();
    const picker = shell().querySelector('input[type="file"]') as HTMLInputElement;
    const opened = vi.spyOn(picker, 'click').mockImplementation(() => {});

    await click('Attach image');
    expect(opened).toHaveBeenCalledOnce();
    expect(picker.accept).toBe('image/png,image/jpeg,image/gif,image/webp');

    const image = png();
    Object.defineProperty(picker, 'files', { value: [image], configurable: true });
    picker.dispatchEvent(new Event('change'));
    await flushPromises();

    expect(uploadConciergeAttachment).toHaveBeenCalledWith(image);
    expect(sent()).toEqual([pastedPath]);
  });

  it('says why a refused upload failed, in the server\'s words, and inserts nothing', async () => {
    await openPanel();
    uploadConciergeAttachment.mockRejectedValue(new Error('That file is not a PNG, JPEG, GIF or WebP image.'));

    paste(transfer([png()]));
    await flushPromises();

    expect(alert()).toContain('That file is not a PNG, JPEG, GIF or WebP image.');
    expect(sent()).toEqual([]);
  });

  it('shows the 409 sentence when no CLI is running to receive the image, and inserts nothing', async () => {
    await openPanel();
    // The route's own body for no CLI running, verbatim.
    const sentence = 'The image was not attached: the Concierge is not running. Start it, then attach the image again.';
    uploadConciergeAttachment.mockRejectedValue(
      Object.assign(new ActionRefused(sentence, { error: sentence }), { status: 409 }),
    );

    drop(shell().querySelector('.concierge-host')!, transfer([png()]));
    await flushPromises();

    expect(alert()).toContain(sentence);
    expect(sent()).toEqual([]);
  });

  it('gives a 409 that carried no sentence one of its own, and inserts nothing into the compose bar', async () => {
    platform.has.touch = true;
    await openPanel();
    const field = shell().querySelector('input[aria-label="Compose a line for the console"]') as HTMLInputElement;
    uploadConciergeAttachment.mockRejectedValue(Object.assign(new Error('409 Conflict'), { status: 409 }));

    drop(shell().querySelector('.concierge-host')!, transfer([png()]));
    await flushPromises();

    expect(alert()).toContain(NoConciergeToReceive);
    expect(field.value).toBe('');
    expect(sent()).toEqual([]);
  });

  it('says the same sentence for a drop with no image on the compose bar as on the shell', async () => {
    platform.has.touch = true;
    await openPanel();
    const notes = new File(['hi'], 'notes.txt', { type: 'text/plain' });

    const onBar = drop(shell().querySelector('.concierge-compose-wrap')!, transfer([notes]));
    await flushPromises();
    const fromBar = alert();
    await click('Dismiss');

    drop(shell().querySelector('.concierge-host')!, transfer([notes]));
    await flushPromises();

    expect(onBar.defaultPrevented).toBe(true);
    expect(fromBar).toContain(NotAnImage);
    expect(alert()).toBe(fromBar);
    expect(uploadConciergeAttachment).not.toHaveBeenCalled();
  });

  it('on a touch device puts the path into the compose bar at the caret, not into the terminal', async () => {
    platform.has.touch = true;
    await openPanel();
    const field = shell().querySelector('input[aria-label="Compose a line for the console"]') as HTMLInputElement;
    field.value = 'look at  please';
    field.dispatchEvent(new Event('input'));
    await flushPromises();
    field.setSelectionRange(8, 8);

    drop(shell().querySelector('.concierge-host')!, transfer([png()]));
    await flushPromises();

    expect(field.value).toBe(`look at ${Stored} please`);
    expect(sent()).toEqual([]);
  });

  it('uploads an image pasted into the compose bar', async () => {
    platform.has.touch = true;
    await openPanel();
    const field = shell().querySelector('input[aria-label="Compose a line for the console"]') as HTMLInputElement;

    const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: transfer([png()]) });
    field.dispatchEvent(event);
    await flushPromises();

    expect(event.defaultPrevented).toBe(true);
    expect(field.value).toBe(Stored);
  });
});
