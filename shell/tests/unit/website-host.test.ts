import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  PARTITION, scriptFor, WebsiteBridge, WebsiteHost, withinSite,
  type BridgeClient, type Clock, type PageWindow, type WindowFactory,
} from '../../src/main/website-host';

// Fake windows and a manual clock: no Electron, no network, no timers that really wait.

class ManualClock implements Clock {
  private t = 0;
  private next = 1;
  readonly timers = new Map<number, { at: number; fn: () => void }>();
  now(): number {
    return this.t;
  }
  /** Moves time on without firing timers (a slow op that has not timed out yet). */
  jump(ms: number): void {
    this.t += ms;
  }
  setTimeout(fn: () => void, ms: number): unknown {
    const id = this.next++;
    this.timers.set(id, { at: this.t + ms, fn });
    return id;
  }
  clearTimeout(h: unknown): void {
    this.timers.delete(h as number);
  }
  advance(ms: number): void {
    this.t += ms;
    for (const [id, t] of [...this.timers]) {
      if (t.at <= this.t) {
        this.timers.delete(id);
        t.fn();
      }
    }
  }
}

type Listener = (...args: any[]) => void;

class FakeWindow implements PageWindow {
  url = 'about:blank';
  title = '';
  closed = false;
  focused = 0;
  loads: string[] = [];
  scripts: string[] = [];
  listeners = new Map<string, Listener[]>();
  openHandler: ((d: { url: string }) => { action: 'deny' }) | null = null;
  /** what each executeJavaScript returns (default: an empty page) */
  scriptResult: (code: string) => unknown = () => ({ text: '', links: [] });
  /** what loading does (default: arrives and finishes) */
  onLoad: (url: string, w: FakeWindow) => void = (url, w) => {
    w.url = url;
    setTimeout(() => w.emit('did-finish-load'), 0);
  };
  png = { width: 2000, height: 1000 };
  resizedTo: number | null = null;
  constructor(readonly opts: { partition: string; show: boolean; title?: string }) {}
  loadURL(url: string): Promise<void> {
    this.loads.push(url);
    this.onLoad(url, this);
    return Promise.resolve();
  }
  executeJavaScript(code: string): Promise<unknown> {
    this.scripts.push(code);
    return Promise.resolve(this.scriptResult(code));
  }
  capturePage() {
    const self = this;
    const image = (width: number) => ({
      getSize: () => ({ width, height: 100 }),
      resize: (o: { width: number }) => {
        self.resizedTo = o.width;
        return image(o.width);
      },
      toPNG: () => Buffer.from(`png${width}`),
    });
    return Promise.resolve(image(this.png.width));
  }
  getURL() { return this.url; }
  getTitle() { return this.title; }
  on(event: string, l: Listener) {
    this.listeners.set(event, [...(this.listeners.get(event) ?? []), l]);
  }
  setWindowOpenHandler(h: (d: { url: string }) => { action: 'deny' }) { this.openHandler = h; }
  focus() { this.focused++; }
  destroy() { this.close(); }
  close() {
    if (this.closed) return;
    this.closed = true;
    this.emit('closed');
  }
  isDestroyed() { return this.closed; }
  emit(event: string, ...args: unknown[]) {
    for (const l of this.listeners.get(event) ?? []) l(...args);
  }
}

function setup(clear: (p: string) => Promise<void> = async () => {}) {
  const windows: FakeWindow[] = [];
  const factory: WindowFactory = (opts) => {
    const w = new FakeWindow(opts);
    if (next.onLoad) w.onLoad = next.onLoad;
    windows.push(w);
    return w;
  };
  const next: { onLoad?: FakeWindow['onLoad'] } = {};
  const calls: { method: string; params: any }[] = [];
  const client = { call: async (method: string, params?: Record<string, unknown>) => { calls.push({ method, params }); return { ok: true }; } };
  const clock = new ManualClock();
  const host = new WebsiteHost(factory, client, clock, clear);
  const replies = () => calls.filter((c) => c.method === 'hostBridge.reply').map((c) => c.params);
  return { host, windows, calls, replies, clock, next };
}

const PUBLIC = 'persist:website-public-7';
// a website login's partition as the core names it: "persist:website-" + its store key, Guid.NewGuid().ToString("N")
const LOGIN = 'persist:website-3f2b8c1e9d7a4b6c8e0f1a2b3c4d5e6f';
const DASHED = 'persist:website-3f2b8c1e-9d7a-4b6c-8e0f-1a2b3c4d5e6f'; // a Guid's "D" form: never what the core sends
const SITE = 'https://example.com/';
let seq = 0;
const req = (op: string, over: Record<string, unknown> = {}) => ({
  id: `r${++seq}`, op, partition: PUBLIC, pageKey: 'k1', siteUrl: SITE, args: {}, ...over,
});
const flush = () => new Promise<void>((r) => setTimeout(r, 5));

describe('withinSite', () => {
  // The same table as the core's WithinSiteRules (WebsiteStoreTests.cs).
  const table: [string, string, boolean][] = [
    ['https://example.com/a', 'https://example.com/', true],
    ['https://www.example.com/a', 'https://example.com/', true],
    ['https://example.com/a', 'https://www.example.com/', true],
    ['https://app.example.com/', 'https://example.com/', true],
    ['https://a.b.example.com/', 'https://www.example.com/', true],
    ['https://EXAMPLE.com/', 'https://example.com/', true],
    ['https://evil-example.com/', 'https://example.com/', false],
    ['https://example.com.evil.net/', 'https://example.com/', false],
    ['https://other.com/', 'https://example.com/', false],
    ['https://user@example.com/', 'https://example.com/', false],
    ['https://user:pw@example.com/', 'https://example.com/', false],
    ['http://example.com/', 'https://example.com/', false],
    ['http://example.com/', 'http://example.com/', true],
    ['https://example.com/', 'http://example.com/', true],
    ['ftp://example.com/', 'https://example.com/', false],
    ['file:///c:/x', 'https://example.com/', false],
    ['https://anything.com/', 'https://www.com/', false],
    ['https://www.com/', 'https://www.com/', true],
    ['https://example.com:8443/', 'https://example.com/', true],
    ['https://bücher.example/', 'https://xn--bcher-kva.example/', true],
    ['https://xn--bcher-kva.example/', 'https://bücher.example/', true],
    ['/relative', 'https://example.com/', false],
  ];
  it('WithinSiteMatchesTheCore', () => {
    for (const [url, site, want] of table) expect(withinSite(url, site), `${url} in ${site}`).toBe(want);
  });
});

describe('WebsiteHost', () => {
  it('OpenRepliesWithTitleTextAndLinks', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: 'https://example.com/a' } }));
    const w = t.windows[0];
    expect(w.opts).toMatchObject({ partition: PUBLIC, show: false });
    w.title = 'Hello';
    // second call reuses the page
    w.scriptResult = () => ({
      text: 'body text',
      links: [
        { text: 'Docs', href: 'https://example.com/docs' },
        { text: 'Elsewhere', href: 'https://other.com/' },
      ],
    });
    await t.host.handle(req('open', { args: { url: 'https://example.com/b' } }));
    expect(t.windows).toHaveLength(1);
    const r = t.replies()[1];
    expect(r).toMatchObject({ ok: true, url: 'https://example.com/b', title: 'Hello' });
    expect(r.text).toContain('body text');
    expect(r.text).toContain('Docs → https://example.com/docs');
    expect(r.text).not.toContain('other.com');
  });

  it('RedirectOutsideTheSiteIsStopped', async () => {
    const t = setup();
    const p = t.host.handle(req('open', { args: { url: 'https://example.com/a' } }));
    const w = t.windows[0];
    w.onLoad = (_u, win) => {
      setTimeout(() => {
        let prevented = false;
        win.emit('will-redirect', { preventDefault: () => { prevented = true; } }, 'https://login.idp.net/auth');
        expect(prevented).toBe(true);
      }, 0);
    };
    await p;
    expect(t.replies()[0]).toMatchObject({ ok: true, stoppedAt: 'https://login.idp.net/auth' });
  });

  it('RedirectInsideTheSiteIsLeftAlone', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: 'https://example.com/a' } }));
    let prevented = false;
    t.windows[0].emit('will-navigate', { preventDefault: () => { prevented = true; } }, 'https://app.example.com/x');
    expect(prevented).toBe(false);
  });

  it('WindowOpenOutsideIsDenied', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: 'https://example.com/a' } }));
    const w = t.windows[0];
    expect(w.openHandler!({ url: 'https://other.com/' })).toEqual({ action: 'deny' });
    expect(w.loads).toEqual(['https://example.com/a']);
    expect(w.openHandler!({ url: 'https://example.com/popup' })).toEqual({ action: 'deny' });
    expect(w.loads).toEqual(['https://example.com/a', 'https://example.com/popup']);
  });

  it('SelectorIsData', async () => {
    const hostile = `a"b\`c</script>\${x}'`;
    for (const op of ['read', 'click', 'fill', 'submit']) {
      const script = scriptFor(op, { selector: hostile, text: hostile });
      const literal = JSON.stringify(hostile);
      expect(script).toContain(literal);
      const without = script.split(literal).join('');
      expect(without).not.toContain('</script>');
      expect(without).not.toContain('${x}');
      expect(without).not.toContain('`c');
    }
    const t = setup();
    await t.host.handle(req('open', { args: { url: 'https://example.com/' } }));
    t.windows[0].scriptResult = () => ({ found: true, text: 'ok' });
    await t.host.handle(req('read', { args: { selector: hostile } }));
    expect(t.windows[0].scripts.at(-1)).toContain(JSON.stringify(hostile));
  });

  it('ReadSaysWhenNothingMatches', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: 'https://example.com/' } }));
    t.windows[0].scriptResult = () => ({ found: false });
    await t.host.handle(req('read', { args: { selector: '#nope' } }));
    expect(t.replies().at(-1)).toMatchObject({ ok: false, error: 'nothing on the page matches #nope' });
  });

  it('ReadWithNoPageOpenSaysSo', async () => {
    const t = setup();
    await t.host.handle(req('read'));
    expect(t.replies()[0].ok).toBe(false);
    expect(t.replies()[0].error).toMatch(/open/);
  });

  it('PagesArePerTeam', async () => {
    const t = setup();
    await t.host.handle(req('open', { pageKey: 'team1', args: { url: SITE } }));
    await t.host.handle(req('open', { pageKey: 'team2', args: { url: SITE } }));
    await t.host.handle(req('open', { pageKey: 'team1', args: { url: SITE } }));
    expect(t.windows).toHaveLength(2);
  });

  it('CallsOnOnePageAreSerial', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    let running = 0;
    let maxRunning = 0;
    const order: string[] = [];
    w.executeJavaScript = async (code: string) => {
      running++;
      maxRunning = Math.max(maxRunning, running);
      order.push(code.includes('"first"') ? 'first' : 'second');
      if (code.includes('"first"')) await gate;
      running--;
      return { found: true, text: 'x' };
    };
    const a = t.host.handle(req('read', { args: { selector: 'first' } }));
    const b = t.host.handle(req('read', { args: { selector: 'second' } }));
    await flush();
    expect(order).toEqual(['first']);
    release();
    await Promise.all([a, b]);
    expect(order).toEqual(['first', 'second']);
    expect(maxRunning).toBe(1);
  });

  it('IdlePageCloses', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    t.clock.advance(4 * 60 * 1000);
    expect(w.closed).toBe(false);
    t.clock.advance(61 * 1000);
    expect(w.closed).toBe(true);
    await t.host.handle(req('open', { args: { url: SITE } }));
    expect(t.windows).toHaveLength(2);
  });

  it('HugePageIsCut', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].scriptResult = () => ({ text: 'x'.repeat(300000), links: [] });
    await t.host.handle(req('open', { args: { url: SITE } }));
    expect(t.replies()[1].text.length).toBeLessThanOrEqual(200100);
    expect(t.replies()[1].text.length).toBeGreaterThanOrEqual(200000);
  });

  it('ScreenshotIsResizedAndEncoded', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].title = 'Home';
    await t.host.handle(req('screenshot'));
    const r = t.replies()[1];
    expect(r).toMatchObject({ ok: true, url: SITE, title: 'Home' });
    expect(t.windows[0].resizedTo).toBe(1280);
    expect(Buffer.from(r.png, 'base64').toString()).toBe('png1280');
  });

  it('FillSetsTheValue', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].scriptResult = () => ({ found: true });
    await t.host.handle(req('fill', { args: { selector: '#q', text: 'hi' } }));
    expect(t.replies()[1]).toMatchObject({ ok: true, text: 'filled', url: SITE });
    expect(t.windows[0].scripts.at(-1)).toContain('"hi"');
  });

  it('ClickWaitsForTheNavigationItStarts', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    w.scriptResult = (code) => {
      if (code.includes('.click()')) {
        setTimeout(() => { w.url = 'https://example.com/next'; w.title = 'Next'; w.emit('did-start-loading'); w.emit('did-finish-load'); }, 0);
        return { found: true };
      }
      return { text: 'next page', links: [] };
    };
    await t.host.handle(req('click', { args: { selector: 'a.next' } }));
    expect(t.replies()[1]).toMatchObject({ ok: true, url: 'https://example.com/next', title: 'Next' });
    expect(t.replies()[1].text).toContain('next page');
  });

  it('ClickThatLeavesTheSiteIsStopped', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    w.scriptResult = (code) => {
      if (code.includes('.click()')) {
        setTimeout(() => w.emit('will-navigate', { preventDefault() {} }, 'https://ads.example.net/'), 0);
        return { found: true };
      }
      return { text: '', links: [] };
    };
    await t.host.handle(req('click', { args: { selector: 'a.ext' } }));
    expect(t.replies()[1]).toMatchObject({ ok: true, stoppedAt: 'https://ads.example.net/' });
  });

  it('ClickWithNoNavigationAnswersAfterAShortWait', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].scriptResult = (code) => (code.includes('.click()') ? { found: true } : { text: 'same', links: [] });
    const p = t.host.handle(req('click', { args: { selector: 'button' } }));
    await flush();
    t.clock.advance(1000);
    await p;
    expect(t.replies()[1]).toMatchObject({ ok: true });
    expect(t.replies()[1].text).toContain('same');
  });

  it('CheckLoadsTheSiteHiddenAndClosesIt', async () => {
    const t = setup();
    await t.host.handle(req('check', { partition: LOGIN }));
    const w = t.windows[0];
    expect(w.opts.show).toBe(false);
    expect(w.closed).toBe(true);
    expect(t.replies()[0]).toMatchObject({ ok: true, url: SITE });
    expect(t.replies()[0].stoppedAt).toBeUndefined();
  });

  it('CheckThatLeavesTheSiteSaysWhere', async () => {
    const t = setup();
    t.next.onLoad = (_u, win) => setTimeout(() => win.emit('will-redirect', { preventDefault() {} }, 'https://idp.net/login'), 0);
    await t.host.handle(req('check', { partition: LOGIN }));
    expect(t.replies()[0]).toMatchObject({ ok: true, stoppedAt: 'https://idp.net/login' });
  });

  it('SignInWindowClosingReportsSignedIn', async () => {
    const t = setup();
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 42 } }));
    const w = t.windows[0];
    expect(w.opts.show).toBe(true);
    expect(w.opts.title).toBe('Sign in to example.com — close this window when you are signed in');
    expect(t.replies()[0].ok).toBe(true);
    // a second sign-in focuses the same window
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 42 } }));
    expect(t.windows).toHaveLength(1);
    expect(w.focused).toBe(1);
    // not held to the site
    let prevented = false;
    w.emit('will-navigate', { preventDefault: () => { prevented = true; } }, 'https://idp.net/login');
    expect(prevented).toBe(false);
    // window.open navigates the same window
    expect(w.openHandler!({ url: 'https://idp.net/popup' })).toEqual({ action: 'deny' });
    expect(w.loads).toContain('https://idp.net/popup');
    w.close();
    await flush();
    const signed = t.calls.filter((c) => c.method === 'hostBridge.signedIn');
    expect(signed).toHaveLength(1);
    expect(signed[0].params).toEqual({ connectionId: 42 });
  });

  it('SignInCloseClosesTheWindowAndReportsOnce', async () => {
    const t = setup();
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 5 } }));
    await t.host.handle(req('signInClose', { partition: LOGIN, args: { connectionId: 5 } }));
    await flush();
    expect(t.windows[0].closed).toBe(true);
    expect(t.calls.filter((c) => c.method === 'hostBridge.signedIn')).toHaveLength(1);
    expect(t.replies()[1].ok).toBe(true);
  });

  it('ForgetClearsThePartition', async () => {
    const cleared: string[] = [];
    const t = setup(async (p) => { cleared.push(p); });
    await t.host.handle(req('open', { partition: LOGIN, args: { url: SITE } }));
    await t.host.handle(req('open', { partition: PUBLIC, pageKey: 'other', args: { url: SITE } }));
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 9 } }));
    await t.host.handle(req('forget', { partition: LOGIN }));
    expect(cleared).toEqual([LOGIN]);
    const [page, other, signIn] = t.windows;
    expect(page.closed).toBe(true);
    expect(signIn.closed).toBe(true);
    expect(other.closed).toBe(false);
    await flush();
    // forgetting is not a sign-in: nothing is reported as signed in
    expect(t.calls.filter((c) => c.method === 'hostBridge.signedIn')).toHaveLength(0);
    expect(t.replies().at(-1).ok).toBe(true);
  });

  it('TheCoresPartitionNamesAreAccepted', async () => {
    // cross-layer: the core keys a login's session by Guid.NewGuid().ToString("N") (ConnectionService), and names a public Website's by its id
    const core = readFileSync(resolve(__dirname, '../../../core/src/Yawble.Core/Connections/ConnectionService.cs'), 'utf8');
    expect(core).toContain('Guid.NewGuid().ToString("N")');
    const website = readFileSync(resolve(__dirname, '../../../core/src/Yawble.Core/Integrations/IntegrationService.Website.cs'), 'utf8');
    expect(website).toContain('WebsitePartitionPrefix = "persist:website-"');
    expect(website).toContain('WebsitePartitionPrefix + "public-" + apiId');
    const nForm = '3f2b8c1e9d7a4b6c8e0f1a2b3c4d5e6f'; // what Guid.ToString("N") prints: 32 lower-case hex digits
    expect(PARTITION.test('persist:website-' + nForm)).toBe(true);
    expect(PARTITION.test('persist:website-public-12')).toBe(true);
    const t = setup();
    await t.host.handle(req('signIn', { partition: 'persist:website-' + nForm, args: { connectionId: 1 } }));
    expect(t.replies()[0].ok).toBe(true);
    expect(t.windows[0].opts.partition).toBe('persist:website-' + nForm);
  });

  it('QueuedClickPastItsDeadlineIsSkipped', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const ran: string[] = [];
    w.executeJavaScript = async (code: string) => {
      ran.push(code.includes('"slow"') ? 'slow' : code.includes('el.click()') ? 'click' : 'other');
      if (code.includes('"slow"')) await gate;
      return { found: true, text: 'x' };
    };
    const slow = t.host.handle(req('read', { args: { selector: 'slow' }, deadline: t.clock.now() + 45_000 }));
    const click = t.host.handle(req('click', { args: { selector: '#buy' }, deadline: t.clock.now() + 45_000 }));
    await flush();
    t.clock.jump(46_000); // the core has given up on both
    release();
    await Promise.all([slow, click]);
    expect(ran).toEqual(['slow']); // the click never ran
    const replies = t.replies();
    expect(replies[2]).toMatchObject({ ok: false });
    expect(replies[2].error).toMatch(/too late/);
  });

  it('ADeadlineCapsTheLoad', async () => {
    const t = setup();
    t.next.onLoad = () => {}; // never finishes
    const p = t.host.handle(req('open', { args: { url: SITE }, deadline: t.clock.now() + 8_000 }));
    await flush();
    t.clock.advance(8_000); // well before the 25 s load limit
    await p;
    expect(t.replies()[0].ok).toBe(false);
    expect(t.replies()[0].error).toMatch(/too long/);
  });

  it('ARequestAlreadyPastItsDeadlineOpensNothing', async () => {
    const t = setup();
    t.clock.jump(100_000);
    await t.host.handle(req('open', { args: { url: SITE }, deadline: 50_000 }));
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 2 }, deadline: 50_000 }));
    expect(t.windows).toHaveLength(0);
    for (const r of t.replies()) expect(r.error).toMatch(/too late/);
    // forgetting is cleanup: it runs whatever the deadline
    const cleared: string[] = [];
    const u = setup(async (p) => { cleared.push(p); });
    u.clock.jump(100_000);
    await u.host.handle(req('forget', { partition: LOGIN, deadline: 50_000 }));
    expect(cleared).toEqual([LOGIN]);
  });

  it('ForgetClosesPublicPagesAndAWebsitesPages', async () => {
    const cleared: string[] = [];
    const t = setup(async (p) => { cleared.push(p); });
    await t.host.handle(req('open', { partition: PUBLIC, pageKey: '7:1:Alpha/Ada', args: { url: SITE } }));
    await t.host.handle(req('open', { partition: LOGIN, pageKey: '8:1:Alpha/Ada', args: { url: SITE } }));
    await t.host.handle(req('open', { partition: LOGIN, pageKey: '9:1:Alpha/Ada', args: { url: SITE } }));
    await t.host.handle(req('forget', { partition: PUBLIC }));
    expect(t.windows.map((w) => w.closed)).toEqual([true, false, false]);
    await t.host.handle(req('forget', { partition: 'persist:website-public-8', args: { pagesOf: '8:' } }));
    expect(t.windows.map((w) => w.closed)).toEqual([true, true, false]);
    expect(cleared).toEqual([PUBLIC, 'persist:website-public-8']);
  });

  it('BadPartitionIsRefused', async () => {
    const t = setup();
    const bads = ['persist:other', 'website-public-1', 'persist:website-public-x', 'persist:website-123', '', 'persist:website-public-7/../x',
      DASHED, 'persist:website-3F2B8C1E9D7A4B6C8E0F1A2B3C4D5E6F', 'persist:website-3f2b8c1e9d7a4b6c8e0f1a2b3c4d5e6f00'];
    for (const bad of bads) {
      await t.host.handle(req('open', { partition: bad, args: { url: SITE } }));
    }
    expect(t.windows).toHaveLength(0);
    expect(t.replies()).toHaveLength(bads.length);
    for (const r of t.replies()) expect(r.ok).toBe(false);
  });

  it('EveryRequestGetsOneReply', async () => {
    const t = setup();
    const ids: string[] = [];
    const go = async (op: string, over: Record<string, unknown> = {}) => {
      const r = req(op, over);
      ids.push(r.id);
      await t.host.handle(r);
    };
    await go('open', { args: { url: SITE } });
    t.windows[0].executeJavaScript = () => Promise.reject(new Error('boom\n    at secret (file.js:1)'));
    await go('read');
    await go('nonsense');
    await go('forget', { partition: 'bad' });
    await go('signIn', { partition: LOGIN, args: {} }); // no connection id
    const replies = t.replies();
    expect(replies.map((r) => r.id)).toEqual(ids);
    expect(replies[1]).toMatchObject({ ok: false, error: 'boom' });
    expect(replies[1].error).not.toContain('secret');
    expect(replies[2].ok).toBe(false);
  });

  it('ALoadFailureIsAnError', async () => {
    const t = setup();
    const p = t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].onLoad = (_u, win) => setTimeout(() => win.emit('did-fail-load', 'ERR_NAME_NOT_RESOLVED', -105), 0);
    await p;
    expect(t.replies()[0]).toMatchObject({ ok: false });
    expect(t.replies()[0].error).toContain('ERR_NAME_NOT_RESOLVED');
  });

  it('ALoadThatNeverEndsTimesOut', async () => {
    const t = setup();
    const p = t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].onLoad = () => {};
    await flush();
    t.clock.advance(31000);
    await p;
    expect(t.replies()[0].ok).toBe(false);
    expect(t.replies()[0].error).toMatch(/too long/);
  });
});

describe('WebsiteBridge', () => {
  function fakeClient(name: string) {
    const calls: { method: string; params: any }[] = [];
    let onEvent: ((e: { event: string; data: unknown }) => void) | null = null;
    const closers: (() => void)[] = [];
    const client: BridgeClient & { name: string; calls: typeof calls; push: (e: string, d: unknown) => void; drop: () => void } = {
      name, calls,
      call: async (method, params) => { calls.push({ method, params }); return { ok: true }; },
      onEvent: (l) => { onEvent = l; return () => { onEvent = null; }; },
      onClose: (l) => { closers.push(l); return () => {}; },
      close: () => {},
      push: (event, data) => onEvent?.({ event, data }),
      drop: () => closers.forEach((c) => c()),
    };
    return client;
  }

  it('BridgeUsesItsOwnConnection', async () => {
    const mainWindowClient = fakeClient('main');
    const own = fakeClient('bridge');
    const clock = new ManualClock();
    const windows: FakeWindow[] = [];
    let connects = 0;
    const bridge = new WebsiteBridge(async () => { connects++; return own; }, (o) => { const w = new FakeWindow(o); windows.push(w); return w; }, async () => {}, clock);
    bridge.start();
    await flush();
    expect(connects).toBe(1); // one connection of its own
    expect(own.calls[0]).toEqual({ method: 'hostBridge.hello', params: {} });
    own.push('website.request', req('open', { args: { url: SITE } }));
    await flush();
    await flush();
    const reply = own.calls.find((c) => c.method === 'hostBridge.reply');
    expect(reply?.params).toMatchObject({ ok: true, url: SITE });
    expect(mainWindowClient.calls).toHaveLength(0);
    // other events are ignored
    own.push('terminal.output', {});
    bridge.stop();
  });

  it('ReconnectsAfterADropWithBackoff', async () => {
    const clients = [fakeClient('a'), fakeClient('b')];
    let n = 0;
    const clock = new ManualClock();
    const bridge = new WebsiteBridge(async () => {
      const c = clients[n++];
      if (!c) throw new Error('core gone');
      return c;
    }, (o) => new FakeWindow(o), async () => {}, clock);
    bridge.start();
    await flush();
    expect(n).toBe(1);
    clients[0].drop();
    await flush();
    clock.advance(1000);
    await flush();
    expect(n).toBe(2);
    expect(clients[1].calls[0].method).toBe('hostBridge.hello');
    // the next connect fails: the wait doubles
    clients[1].drop();
    await flush();
    clock.advance(1000);
    await flush();
    expect(n).toBe(3);
    clock.advance(1000);
    await flush();
    expect(n).toBe(3);
    clock.advance(1000);
    await flush();
    expect(n).toBe(4);
    bridge.stop();
  });
});

describe('WebsiteHost fix round', () => {
  it('OutsidePopupIsToldToTheAgent', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    w.openHandler!({ url: 'https://other.com/x?token=secret#frag' });
    w.scriptResult = () => ({ found: true, text: 'still here' });
    await t.host.handle(req('read'));
    expect(t.replies()[1].stoppedAt).toBe('https://other.com/x');
    expect(JSON.stringify(t.replies()[1])).not.toContain('secret');
    await t.host.handle(req('read'));
    expect(t.replies()[2].stoppedAt).toBeUndefined(); // told once
  });

  it('SubframeRedirectDoesNotStopThePage', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    let prevented = false;
    t.windows[0].emit('will-redirect', { preventDefault: () => { prevented = true; } }, 'https://ads.net/x', false);
    expect(prevented).toBe(false);
    t.windows[0].emit('will-redirect', { preventDefault: () => { prevented = true; } }, 'https://ads.net/x', true);
    expect(prevented).toBe(true);
  });

  it('StoppedAtLosesQueryAndFragment', async () => {
    const t = setup();
    t.next.onLoad = (_u, win) => setTimeout(() => win.emit('will-redirect', { preventDefault() {} }, 'https://u:p@idp.net/cb?code=abc&state=x#t', true), 0);
    await t.host.handle(req('check', { partition: LOGIN }));
    expect(t.replies()[0].stoppedAt).toBe('https://idp.net/cb');
  });

  it('CheckUrlLosesQueryAndFragment', async () => {
    const t = setup();
    t.next.onLoad = (_url, w) => { w.url = 'https://example.com/home?sid=1#x'; setTimeout(() => w.emit('did-finish-load'), 0); };
    await t.host.handle(req('check', { partition: LOGIN }));
    expect(t.replies()[0].url).toBe('https://example.com/home');
  });

  it('ANeverAnsweringScriptEndsThePageWithOneError', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    w.executeJavaScript = () => new Promise(() => {});
    const p = t.host.handle(req('read'));
    await flush();
    t.clock.advance(20_000);
    await p;
    expect(t.replies()).toHaveLength(2);
    expect(t.replies()[1]).toMatchObject({ ok: false });
    expect(t.replies()[1].error).toMatch(/did not answer/);
    expect(w.closed).toBe(true);
    await t.host.handle(req('open', { args: { url: SITE } }));
    expect(t.windows).toHaveLength(2);
    expect(t.replies()[2].ok).toBe(true);
  });

  it('ACrashedPageFailsTheCallInFlight', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    const w = t.windows[0];
    w.executeJavaScript = () => new Promise(() => {});
    const p = t.host.handle(req('read'));
    await flush();
    w.emit('render-process-gone');
    await p;
    expect(t.replies()[1]).toMatchObject({ ok: false });
    expect(t.replies()[1].error).toMatch(/crashed/);
    expect(w.closed).toBe(true);
  });

  it('ClickAndFillNeedASelector', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    await t.host.handle(req('click'));
    await t.host.handle(req('fill', { args: { text: 'x' } }));
    expect(t.replies()[1].ok).toBe(false);
    expect(t.replies()[1].error).toMatch(/selector/);
    expect(t.replies()[2].ok).toBe(false);
    expect(t.windows[0].scripts.some((c) => c.includes('el.click()'))).toBe(false);
  });

  it('AnEmptyCaptureIsAnError', async () => {
    const t = setup();
    await t.host.handle(req('open', { args: { url: SITE } }));
    t.windows[0].png = { width: 0, height: 0 };
    await t.host.handle(req('screenshot'));
    expect(t.replies()[1]).toMatchObject({ ok: false });
    expect(t.replies()[1].error).toMatch(/captured/);
  });

  it('SignInRepliesBeforeThePageLoads', async () => {
    const t = setup();
    t.next.onLoad = () => {}; // never finishes
    await t.host.handle(req('signIn', { partition: LOGIN, args: { connectionId: 3 } }));
    expect(t.replies()[0].ok).toBe(true);
  });
});

describe('WebsiteBridge fix round', () => {
  function client() {
    const calls: string[] = [];
    const closers: (() => void)[] = [];
    let closed = 0;
    let handler: ((e: { event: string; data: unknown }) => void) | null = null;
    return {
      calls,
      get closed() { return closed; },
      get listening() { return handler !== null; },
      call: async (m: string): Promise<unknown> => { calls.push(m); return {}; },
      onEvent: (l: any) => { handler = l; return () => { handler = null; }; },
      onClose: (l: () => void) => { closers.push(l); return () => {}; },
      close: () => { closed++; },
    };
  }
  const mk = (c: ReturnType<typeof client>) =>
    new WebsiteBridge(async () => c as any, (o) => new FakeWindow(o), async () => {}, new ManualClock());

  it('FailedHelloClosesTheClient', async () => {
    const c = client();
    c.call = async () => { throw new Error('no'); };
    const b = mk(c);
    b.start();
    await flush();
    expect(c.closed).toBe(1);
    b.stop();
  });

  it('ListensBeforeHello', async () => {
    const c = client();
    let listeningAtHello = false;
    c.call = async () => { listeningAtHello = c.listening; return {}; };
    const b = mk(c);
    b.start();
    await flush();
    expect(listeningAtHello).toBe(true);
    b.stop();
  });

  it('StopDuringHelloClosesTheClient', async () => {
    const c = client();
    let release!: () => void;
    c.call = () => new Promise<unknown>((r) => { release = () => r({}); });
    const b = mk(c);
    b.start();
    await flush();
    b.stop();
    release();
    await flush();
    expect(c.closed).toBeGreaterThanOrEqual(1);
  });
});
