// @vitest-environment happy-dom
// Card 393: a terminal link asks through confirmDialog (confirm.ts), never the browser's confirm(), and opens only
// http/https addresses, through the app's external-link path (window.open(url, '_blank')).
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';
import { confirmState } from '../../src/renderer/src/confirm';
import { activateTerminalLink, isWebLink, linkQuestion, terminalLinkHandler } from '../../src/renderer/src/terminalLinks';
import { stopConfirmStubs, stubConfirm } from './confirm-stub';

let lastOptions: Record<string, any> | null = null;
vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    cols = 80;
    rows = 24;
    options: Record<string, unknown>;
    constructor(o: Record<string, unknown>) { this.options = { ...o }; lastOptions = o; }
    open() {}
    write() {}
    reset() {}
    focus() {}
    dispose() {}
    loadAddon() {}
    onData() { return { dispose() {} }; }
    onResize() { return { dispose() {} }; }
  },
}));
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }));
(globalThis as any).ResizeObserver ??= class { observe() {} disconnect() {} };

const URL_ = 'https://example.com/docs?q=1';
let windowConfirm: ReturnType<typeof vi.fn>;
let windowOpen: ReturnType<typeof vi.fn>;

beforeEach(() => {
  windowConfirm = vi.fn(() => true);
  windowOpen = vi.fn(() => null);
  vi.stubGlobal('confirm', windowConfirm);
  window.confirm = windowConfirm as any;
  window.open = windowOpen as any;
});
afterEach(() => {
  stopConfirmStubs();
  vi.unstubAllGlobals();
});

describe('terminal links (card 393)', () => {
  it('asks through confirmDialog with the URL, and opens it on OK', async () => {
    const asked = stubConfirm(() => true);
    const open = vi.fn();
    expect(await activateTerminalLink(URL_, open)).toBe(true);
    expect(asked).toHaveBeenCalledTimes(1);
    expect(asked.mock.calls[0][0]).toBe(linkQuestion(URL_));
    expect(asked.mock.calls[0][0]).toContain(URL_);
    expect(open).toHaveBeenCalledWith(URL_);
    expect(windowConfirm).not.toHaveBeenCalled();
  });

  it('names the OK button Open', async () => {
    const p = activateTerminalLink(URL_, vi.fn());
    expect(confirmState.value?.okLabel).toBe('Open');
    expect(confirmState.value?.danger).toBe(false);
    confirmState.value!.resolve(false);
    expect(await p).toBe(false);
  });

  it('does not open on Cancel', async () => {
    const asked = stubConfirm(() => false);
    const open = vi.fn();
    expect(await activateTerminalLink(URL_, open)).toBe(false);
    expect(asked).toHaveBeenCalledTimes(1);
    expect(open).not.toHaveBeenCalled();
    expect(windowConfirm).not.toHaveBeenCalled();
  });

  it('refuses other schemes without asking', async () => {
    const asked = stubConfirm(() => true);
    const open = vi.fn();
    for (const uri of ['javascript:alert(1)', 'file:///C:/Windows/win.ini', 'data:text/html,<b>x</b>', 'mailto:a@b.c',
      'vbscript:x', 'ftp://example.com/', '//example.com/', 'example.com', '']) {
      expect(isWebLink(uri)).toBe(false);
      expect(await activateTerminalLink(uri, open)).toBe(false);
    }
    expect(asked).not.toHaveBeenCalled();
    expect(open).not.toHaveBeenCalled();
    expect(isWebLink('http://localhost:5173/x')).toBe(true);
    expect(isWebLink('HTTPS://EXAMPLE.COM')).toBe(true);
  });

  it('the handler opens through window.open(url, "_blank") on OK and never calls window.confirm', async () => {
    stubConfirm(() => true);
    terminalLinkHandler.activate(new MouseEvent('click'), URL_, undefined as any);
    await flushPromises();
    expect(windowOpen).toHaveBeenCalledTimes(1);
    expect(windowOpen.mock.calls[0][0]).toBe(URL_);
    expect(windowOpen.mock.calls[0][1]).toBe('_blank');
    expect(windowConfirm).not.toHaveBeenCalled();
  });

  it('the handler opens nothing on Cancel', async () => {
    stubConfirm(() => false);
    terminalLinkHandler.activate(new MouseEvent('click'), URL_, undefined as any);
    await flushPromises();
    expect(windowOpen).not.toHaveBeenCalled();
    expect(windowConfirm).not.toHaveBeenCalled();
  });

  it('TerminalView gives its xterm Terminal this link handler, with non-http schemes left off', async () => {
    const api: YawbleApi = {
      getInfo: async () => { throw new Error('no'); },
      getAvailableBackends: async () => [],
      call: async (method: string) => (method === 'terminal.attach' ? { replay: '', end: 0, session: null } : {}) as any,
      onEvent: () => () => {},
      openTerminal: async () => {},
      pickFolder: async () => null,
    };
    const TerminalView = (await import('../../src/renderer/src/components/TerminalView.vue')).default;
    const w = mount(TerminalView, { props: { channel: 'member:1', api } });
    await flushPromises();
    expect(lastOptions?.linkHandler).toBe(terminalLinkHandler);
    expect(lastOptions?.linkHandler.allowNonHttpProtocols).toBeFalsy();
    w.unmount();
  });
});
