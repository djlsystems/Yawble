// The shell's half of "website login": the core asks (website.request pushes on a bridge connection), the shell
// drives a browser window on this computer and answers (hostBridge.reply). No `electron` import here: index.ts gives the
// real window factory and the session clearing, tests give fakes.

/** Same answers as the core's IntegrationService.WithinSite (shared test table). */
export function withinSite(url: string, site: string): boolean {
  let u: URL;
  let s: URL;
  try {
    u = new URL(url);
    s = new URL(site);
  } catch {
    return false;
  }
  if (u.username.length > 0 || u.password.length > 0) return false;
  const schemeOk = u.protocol === 'https:' || (u.protocol === 'http:' && s.protocol === 'http:');
  if (!schemeOk) return false;
  let b = s.hostname.toLowerCase();
  // "www." is dropped only when a dot remains ("www.com" must not cover all of .com)
  if (b.startsWith('www.') && b.slice(4).includes('.')) b = b.slice(4);
  const h = u.hostname.toLowerCase();
  return b.length > 0 && (h === b || h.endsWith('.' + b));
}

/** An address without its query, fragment and user info: identity-provider redirects carry codes and state in them. */
export function stripUrl(url: string): string {
  try {
    const u = new URL(url);
    u.search = '';
    u.hash = '';
    u.username = '';
    u.password = '';
    return u.toString();
  } catch {
    return url.split(/[?#]/)[0];
  }
}

export interface PageImage {
  getSize(): { width: number; height: number };
  resize(options: { width: number }): PageImage;
  toPNG(): Uint8Array;
}

/**
 * The part of a browser window the host uses. Events (the adapter in index.ts maps Electron's onto these):
 * 'will-navigate' / 'will-redirect' (event, url, isMainFrame), 'render-process-gone', 'did-start-loading', 'did-finish-load', 'did-fail-load' (description, code),
 * 'closed'.
 */
export interface PageWindow {
  loadURL(url: string): Promise<unknown>;
  executeJavaScript(code: string): Promise<unknown>;
  capturePage(): Promise<PageImage>;
  getURL(): string;
  getTitle(): string;
  on(event: string, listener: (...args: any[]) => void): void;
  setWindowOpenHandler(handler: (details: { url: string }) => { action: 'deny' }): void;
  focus(): void;
  close(): void;
  /** Closes at once, whatever the page says (hidden pages). */
  destroy(): void;
  isDestroyed(): boolean;
}

export type WindowFactory = (opts: { partition: string; show: boolean; title?: string }) => PageWindow;

export interface Clock {
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
  /** Unix ms, compared with a request's deadline (the core's clock on the same computer). Default: Date.now(). */
  now?(): number;
}

const realClock: Clock = {
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
  now: () => Date.now(),
};

export interface WebsiteRequest {
  id: string;
  op: string;
  partition: string;
  pageKey: string;
  siteUrl: string;
  args: Record<string, unknown>;
  /** Unix ms: when the core stops waiting. An op that would start after it is skipped, and each step is kept under the time left. */
  deadline?: number;
}

interface Reply {
  ok: boolean;
  error?: string;
  url?: string;
  title?: string;
  text?: string;
  png?: string;
  stoppedAt?: string;
}

// the core's names: "public-" + a Website's id, or a website login's store key (a Guid in "N" form: 32 lower-case hex digits)
export const PARTITION = /^persist:website-(public-\d+|[0-9a-f]{32})$/;
const MAX_TEXT = 200_000;
const MAX_LINKS = 100;
// the core waits 45 s for an answer: the worst case of any op (a load, then a read of the page) stays under it
const OPEN_TIMEOUT_MS = 25_000;
const ACTION_TIMEOUT_MS = 10_000;
const ACTION_GRACE_MS = 600; // a click that starts no navigation in this time is done
const CALL_TIMEOUT_MS = 10_000; // any one call into a window
const DEADLINE_MARGIN_MS = 500; // the reply still has to reach the core
const TOO_LATE = 'too late: the app stopped waiting for this action before it could start; try again';
const IDLE_MS = 5 * 60 * 1000;
const MAX_SHOT_WIDTH = 1280;

/**
 * The script for one op. The selector and text are data: they go in through JSON.stringify and nowhere else, and the agent
 * never supplies script.
 */
export function scriptFor(op: string, args: Record<string, unknown>): string {
  const sel = JSON.stringify(typeof args.selector === 'string' ? args.selector : null);
  const text = JSON.stringify(typeof args.text === 'string' ? args.text : '');
  switch (op) {
    case 'page':
      return `(() => {
  const text = document.body ? String(document.body.innerText || '').slice(0, ${MAX_TEXT + 1}) : '';
  const links = Array.from(document.querySelectorAll('a[href]')).slice(0, 1000).map((a) => ({ text: String(a.innerText || a.textContent || '').trim().slice(0, 80), href: a.href }));
  return { text, links };
})()`;
    case 'read':
      return `(() => {
  const s = ${sel};
  if (s === null) return { found: true, text: document.body ? String(document.body.innerText || '').slice(0, ${MAX_TEXT + 1}) : '' };
  const el = document.querySelector(s);
  if (!el) return { found: false };
  return { found: true, text: String(el.innerText !== undefined ? el.innerText : el.textContent || '').slice(0, ${MAX_TEXT + 1}) };
})()`;
    case 'click':
      return `(() => {
  const el = document.querySelector(${sel});
  if (!el) return { found: false };
  el.click();
  return { found: true };
})()`;
    case 'fill':
      return `(() => {
  const el = document.querySelector(${sel});
  if (!el) return { found: false };
  const value = ${text};
  if (el.focus) el.focus();
  const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
  if (setter && setter.set) setter.set.call(el, value); else el.value = value;
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
  return { found: true };
})()`;
    case 'submit':
      return `(() => {
  const s = ${sel};
  let form = null;
  if (s !== null) {
    const el = document.querySelector(s);
    if (!el) return { found: false };
    form = el.tagName === 'FORM' ? el : (el.form || el.closest('form'));
    if (!form) return { error: 'that element is not inside a form' };
  } else {
    if (document.forms.length === 0) return { error: 'there is no form on the page' };
    if (document.forms.length > 1) return { error: 'there are several forms on the page; say which one with a selector' };
    form = document.forms[0];
  }
  form.requestSubmit();
  return { found: true };
})()`;
    default:
      throw new Error(`unknown script ${op}`);
  }
}

type NavEvent = { t: 'gone' } | { t: 'start' } | { t: 'finish' } | { t: 'fail'; desc: string } | { t: 'stop'; url: string };
type NavResult = { kind: 'finish' } | { kind: 'stop'; url: string } | { kind: 'none' } | { kind: 'timeout' } | { kind: 'gone' } | { kind: 'fail'; desc: string };

interface Page {
  win: PageWindow;
  partition: string;
  siteUrl: string;
  queue: Promise<unknown>;
  hooks: Set<(e: NavEvent) => void>;
  idle: unknown;
  /** an outside window.open that was denied; told to the agent with the next reply */
  popupStop?: string;
  stoppedAt?: string;
  /** the deadline of the op running on the page */
  deadline?: number;
}

function errorMessage(e: unknown): string {
  // the first line only: never a stack
  const m = e instanceof Error ? e.message : String(e);
  return m.split('\n')[0].trim() || 'something went wrong';
}

export class WebsiteHost {
  private readonly pages = new Map<string, Page>();
  private readonly signIns = new Map<string, { win: PageWindow; connectionId: number; partition: string; reported: boolean }>();

  constructor(
    private readonly factory: WindowFactory,
    private readonly client: { call(method: string, params?: Record<string, unknown>): Promise<unknown> },
    private readonly clock: Clock = realClock,
    private readonly clearPartition: (partition: string) => Promise<void> = () => Promise.reject(new Error('this app cannot clear a session')),
  ) {}

  /** Answers the request with exactly one hostBridge.reply, whatever happens. */
  async handle(request: WebsiteRequest): Promise<void> {
    if (!request || typeof request.id !== 'string' || request.id.length === 0) return; // nobody to answer
    let reply: Reply;
    try {
      reply = await this.dispatch(request);
    } catch (e) {
      reply = { ok: false, error: errorMessage(e) };
    }
    try {
      await this.client.call('hostBridge.reply', { id: request.id, ...reply });
    } catch {
      // the bridge is gone; the core's request times out or fails on its own
    }
  }

  /** Closes every window (the app is quitting, or the bridge was stopped). */
  closeAll(): void {
    for (const page of [...this.pages.values()]) this.dropPage(page);
    for (const s of [...this.signIns.values()]) {
      s.reported = true;
      this.safeClose(s.win, true);
    }
    this.signIns.clear();
  }

  private now(): number {
    return this.clock.now ? this.clock.now() : Date.now();
  }

  private static deadlineOf(r: WebsiteRequest): number | undefined {
    return typeof r.deadline === 'number' && Number.isFinite(r.deadline) ? r.deadline : undefined;
  }

  /** `ms`, or less when the deadline is nearer; throws "too late" when nothing is left. */
  private within(ms: number, deadline: number | undefined): number {
    if (deadline === undefined) return ms;
    const left = deadline - this.now() - DEADLINE_MARGIN_MS;
    if (left <= 0) throw new Error(TOO_LATE);
    return Math.min(ms, left);
  }

  private async dispatch(r: WebsiteRequest): Promise<Reply> {
    if (typeof r.partition !== 'string' || !PARTITION.test(r.partition)) throw new Error('that session name is not one the app uses');
    const args = r.args && typeof r.args === 'object' ? r.args : {};
    // forgetting is cleanup and always runs; anything else the core no longer waits for is skipped
    if (r.op !== 'forget') this.within(1, WebsiteHost.deadlineOf(r));
    switch (r.op) {
      case 'signIn': return this.signIn(r, args);
      case 'signInClose': return this.signInClose(r, args);
      case 'forget': return this.forget(r.partition, typeof args.pagesOf === 'string' ? args.pagesOf : undefined);
      case 'check': return this.check(r);
      case 'open': case 'read': case 'screenshot': case 'click': case 'fill': case 'submit':
        return this.onPage(r, args);
      default:
        throw new Error(`unknown action ${String(r.op)}`);
    }
  }

  // ---- pages: one per pageKey, calls on it one at a time, closed when idle ----

  private onPage(r: WebsiteRequest, args: Record<string, unknown>): Promise<Reply> {
    let page = this.pages.get(r.pageKey);
    if (page && (page.partition !== r.partition || page.win.isDestroyed())) {
      this.dropPage(page);
      page = undefined;
    }
    if (!page) {
      if (r.op !== 'open') return Promise.reject(new Error('no page is open; open one first'));
      page = this.makePage(r.partition, r.siteUrl, false);
      this.pages.set(r.pageKey, page);
    }
    const p = page;
    p.siteUrl = r.siteUrl;
    this.clock.clearTimeout(p.idle);
    const run = async (): Promise<Reply> => {
      this.clock.clearTimeout(p.idle);
      try {
        // queued behind a slow op: once the core has stopped waiting, never click or type for an agent already told it failed
        const deadline = WebsiteHost.deadlineOf(r);
        this.within(1, deadline);
        if (p.win.isDestroyed()) throw new Error('the page was closed; open it again');
        p.stoppedAt = undefined;
        p.deadline = deadline;
        const reply = await this.run(p, r.op, args);
        if (p.popupStop) {
          reply.stoppedAt ??= p.popupStop;
          p.popupStop = undefined;
        }
        return reply;
      } finally {
        p.deadline = undefined;
        this.armIdle(r.pageKey, p);
      }
    };
    const result = p.queue.then(run, run);
    p.queue = result.catch(() => undefined);
    return result;
  }

  private armIdle(key: string, page: Page): void {
    this.clock.clearTimeout(page.idle);
    page.idle = this.clock.setTimeout(() => {
      if (this.pages.get(key) === page) this.pages.delete(key);
      this.safeClose(page.win, true);
    }, IDLE_MS);
  }

  private dropPage(page: Page): void {
    this.clock.clearTimeout(page.idle);
    for (const [k, p] of this.pages) if (p === page) this.pages.delete(k);
    this.safeClose(page.win, true);
  }

  private safeClose(win: PageWindow, hard = false): void {
    try {
      if (win.isDestroyed()) return;
      if (hard) win.destroy();
      else win.close();
    } catch {
      // already gone
    }
  }

  private makePage(partition: string, siteUrl: string, show: boolean): Page {
    const win = this.factory({ partition, show });
    const page: Page = { win, partition, siteUrl, queue: Promise.resolve(), hooks: new Set(), idle: undefined };
    const emit = (e: NavEvent) => { for (const h of [...page.hooks]) h(e); };
    const held = (event: { preventDefault(): void }, url: string, isMainFrame?: boolean) => {
      if (isMainFrame === false) return; // a frame inside the page is not the page
      if (withinSite(url, page.siteUrl)) return;
      event.preventDefault();
      page.stoppedAt = stripUrl(url);
      emit({ t: 'stop', url: page.stoppedAt });
    };
    win.on('render-process-gone', () => {
      emit({ t: 'gone' });
      this.dropPage(page);
    });
    win.on('will-navigate', held);
    win.on('will-redirect', held);
    win.on('did-start-loading', () => emit({ t: 'start' }));
    win.on('did-finish-load', () => emit({ t: 'finish' }));
    win.on('did-fail-load', (desc: string, code: number) => {
      if (code === -3) return; // ERR_ABORTED: we stopped it, or a newer navigation replaced it
      emit({ t: 'fail', desc: String(desc) });
    });
    win.setWindowOpenHandler(({ url }) => {
      // never a new window; an address inside the site loads in this one
      if (withinSite(url, page.siteUrl)) void Promise.resolve(win.loadURL(url)).catch(() => undefined);
      else page.popupStop = stripUrl(url);
      return { action: 'deny' };
    });
    return page;
  }

  private waitNav(page: Page, kick: () => Promise<unknown> | void, o: { timeoutMs: number; graceMs?: number }): Promise<NavResult> {
    return new Promise<NavResult>((resolve, reject) => {
      let started = o.graceMs === undefined;
      let done = false;
      const timers: unknown[] = [];
      const end = (r: NavResult) => {
        if (done) return;
        done = true;
        page.hooks.delete(hook);
        for (const t of timers) this.clock.clearTimeout(t);
        resolve(r);
      };
      const hook = (e: NavEvent) => {
        if (e.t === 'gone') end({ kind: 'gone' });
        else if (e.t === 'stop') end({ kind: 'stop', url: e.url });
        else if (e.t === 'start') started = true;
        else if (e.t === 'finish' && started) end({ kind: 'finish' });
        else if (e.t === 'fail' && started) end({ kind: 'fail', desc: e.desc });
      };
      page.hooks.add(hook);
      let limit: number;
      try {
        limit = this.within(o.timeoutMs, page.deadline);
      } catch (e) {
        done = true;
        page.hooks.delete(hook);
        reject(e);
        return;
      }
      timers.push(this.clock.setTimeout(() => end({ kind: 'timeout' }), limit));
      if (o.graceMs !== undefined) timers.push(this.clock.setTimeout(() => { if (!started) end({ kind: 'none' }); }, o.graceMs));
      try {
        Promise.resolve(kick()).catch((e) => {
          if (done) return;
          done = true;
          page.hooks.delete(hook);
          for (const t of timers) this.clock.clearTimeout(t);
          reject(e);
        });
      } catch (e) {
        done = true;
        page.hooks.delete(hook);
        for (const t of timers) this.clock.clearTimeout(t);
        reject(e);
      }
    });
  }

  /** A call into the window that never answers (or a page that crashes) ends the page and fails the op. */
  private guard<T>(page: Page, call: () => Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      let done = false;
      let timer: unknown;
      const hook = (e: NavEvent) => { if (e.t === 'gone') fail(new Error('the page crashed; open it again')); };
      const cleanup = () => { done = true; page.hooks.delete(hook); this.clock.clearTimeout(timer); };
      const fail = (err: Error) => {
        if (done) return;
        cleanup();
        this.dropPage(page);
        reject(err);
      };
      let limit: number;
      try {
        limit = this.within(CALL_TIMEOUT_MS, page.deadline);
      } catch (e) {
        reject(e);
        return;
      }
      page.hooks.add(hook);
      timer = this.clock.setTimeout(() => fail(new Error('the page did not answer in time; it was closed')), limit);
      let started: Promise<T>;
      try {
        started = Promise.resolve(call());
      } catch (e) {
        fail(e instanceof Error ? e : new Error(String(e)));
        return;
      }
      started.then(
        (v) => { if (done) return; cleanup(); resolve(v); },
        (e) => { if (done) return; cleanup(); reject(e); },
      );
    });
  }

  private async run(page: Page, op: string, args: Record<string, unknown>): Promise<Reply> {
    switch (op) {
      case 'open': {
        const url = typeof args.url === 'string' ? args.url : '';
        if (!withinSite(url, page.siteUrl)) return { ok: true, stoppedAt: url };
        const nav = await this.waitNav(page, () => this.load(page.win, url), { timeoutMs: OPEN_TIMEOUT_MS });
        return this.afterNav(page, nav, 'open');
      }
      case 'read': {
        const r = await this.script(page, 'read', args);
        return { ok: true, url: this.shownUrl(page), title: page.win.getTitle(), text: String(r.text ?? '').slice(0, MAX_TEXT) };
      }
      case 'screenshot': {
        let image = await this.guard(page, () => page.win.capturePage());
        if (image.getSize().width === 0) throw new Error('the page could not be captured (it may be empty or hidden)');
        if (image.getSize().width > MAX_SHOT_WIDTH) image = image.resize({ width: MAX_SHOT_WIDTH });
        const png = Buffer.from(image.toPNG());
        if (png.length === 0) throw new Error('the page could not be captured (it may be empty or hidden)');
        return { ok: true, url: this.shownUrl(page), title: page.win.getTitle(), png: png.toString('base64') };
      }
      case 'click':
      case 'submit': {
        const nav = await this.waitNav(page, () => this.script(page, op, args), { timeoutMs: ACTION_TIMEOUT_MS, graceMs: ACTION_GRACE_MS });
        return this.afterNav(page, nav, op);
      }
      case 'fill':
        await this.script(page, 'fill', args);
        return { ok: true, url: this.shownUrl(page), title: page.win.getTitle(), text: 'filled' };
      default:
        throw new Error(`unknown action ${op}`);
    }
  }

  private load(win: PageWindow, url: string): Promise<unknown> {
    return Promise.resolve(win.loadURL(url)).catch(() => undefined); // failures arrive as did-fail-load
  }

  private async script(page: Page, op: string, args: Record<string, unknown>): Promise<Record<string, unknown>> {
    if ((op === 'click' || op === 'fill') && (typeof args.selector !== 'string' || args.selector.length === 0)) {
      throw new Error(`say which element to ${op} with a selector`);
    }
    if (op === 'fill' && typeof args.text !== 'string') throw new Error('say what to type');
    const raw = await this.guard(page, () => page.win.executeJavaScript(scriptFor(op, args)));
    const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
    if (typeof r.error === 'string') throw new Error(r.error);
    if (r.found === false) throw new Error(`nothing on the page matches ${String(args.selector)}`);
    return r;
  }

  private async afterNav(page: Page, nav: NavResult, op: string): Promise<Reply> {
    if (nav.kind === 'stop') return { ok: true, stoppedAt: nav.url };
    if (nav.kind === 'gone') throw new Error('the page crashed; open it again');
    if (nav.kind === 'fail') throw new Error(`the page did not load (${nav.desc})`);
    if (nav.kind === 'timeout' && op === 'open') {
      this.dropPage(page);
      throw new Error('the page took too long to load');
    }
    const now = page.win.getURL();
    if (now && now !== 'about:blank' && !withinSite(now, page.siteUrl)) return { ok: true, stoppedAt: stripUrl(now) };
    return await this.snapshot(page);
  }

  private async snapshot(page: Page): Promise<Reply> {
    const raw = (await this.guard(page, () => page.win.executeJavaScript(scriptFor('page', {})))) as { text?: unknown; links?: unknown } | null;
    let text = String(raw?.text ?? '').slice(0, MAX_TEXT);
    const links: string[] = [];
    if (Array.isArray(raw?.links)) {
      for (const l of raw.links as { text?: unknown; href?: unknown }[]) {
        if (links.length >= MAX_LINKS) break;
        const href = String(l?.href ?? '');
        if (!withinSite(href, page.siteUrl)) continue;
        links.push(`${String(l.text ?? '').replace(/\s+/g, ' ').trim()} → ${href.slice(0, 300)}`);
      }
    }
    if (links.length > 0) text += `\n\nLinks on this page:\n${links.join('\n')}`;
    return { ok: true, url: this.shownUrl(page), title: page.win.getTitle(), text };
  }

  /** The page's address as the agent sees it: as is inside the site, else without query and fragment. */
  private shownUrl(page: Page): string {
    const u = page.win.getURL();
    return withinSite(u, page.siteUrl) ? u : stripUrl(u);
  }

  // ---- check, sign-in, forget ----

  private async check(r: WebsiteRequest): Promise<Reply> {
    const page = this.makePage(r.partition, r.siteUrl, false);
    page.deadline = WebsiteHost.deadlineOf(r);
    try {
      const nav = await this.waitNav(page, () => this.load(page.win, r.siteUrl), { timeoutMs: OPEN_TIMEOUT_MS });
      if (nav.kind === 'stop') return { ok: true, stoppedAt: nav.url };
      if (nav.kind === 'gone') throw new Error('the page crashed');
      if (nav.kind === 'fail') throw new Error(`the page did not load (${nav.desc})`);
      if (nav.kind === 'timeout') throw new Error('the page took too long to load');
      const url = stripUrl(page.win.getURL());
      if (!withinSite(url, r.siteUrl)) return { ok: true, stoppedAt: url };
      return { ok: true, url, title: page.win.getTitle() };
    } finally {
      this.safeClose(page.win, true);
    }
  }

  private connectionId(args: Record<string, unknown>): number {
    const id = args.connectionId;
    if (typeof id !== 'number' || !Number.isSafeInteger(id) || id <= 0) throw new Error('a connection id is needed');
    return id;
  }

  private async signIn(r: WebsiteRequest, args: Record<string, unknown>): Promise<Reply> {
    const connectionId = this.connectionId(args);
    const key = String(connectionId);
    const existing = this.signIns.get(key);
    if (existing && !existing.win.isDestroyed()) {
      existing.win.focus();
      return { ok: true };
    }
    let host = r.siteUrl;
    try {
      host = new URL(r.siteUrl).hostname;
    } catch {
      throw new Error('the site address is not valid');
    }
    const win = this.factory({ partition: r.partition, show: true, title: `Sign in to ${host} — close this window when you are signed in` });
    const entry = { win, connectionId, partition: r.partition, reported: false };
    this.signIns.set(key, entry);
    // single sign-on goes through other hosts, so this window is not held to the site; a new window opens in this one
    win.setWindowOpenHandler(({ url }) => {
      if (/^https?:\/\//i.test(url)) void Promise.resolve(win.loadURL(url)).catch(() => undefined);
      return { action: 'deny' };
    });
    win.on('closed', () => {
      if (this.signIns.get(key) === entry) this.signIns.delete(key);
      if (entry.reported) return;
      entry.reported = true;
      void this.client.call('hostBridge.signedIn', { connectionId }).catch(() => undefined);
    });
    void this.load(win, r.siteUrl); // the window is open; the person watches it load
    return { ok: true };
  }

  private async signInClose(_r: WebsiteRequest, args: Record<string, unknown>): Promise<Reply> {
    const entry = this.signIns.get(String(this.connectionId(args)));
    if (entry) this.safeClose(entry.win); // its 'closed' reports signedIn
    return { ok: true };
  }

  /** Clears a session and closes its pages (public ones too), and the pages whose key starts with `pagesOf` (a removed Website's, whatever session they use). */
  private async forget(partition: string, pagesOf?: string): Promise<Reply> {
    for (const [key, page] of [...this.pages]) {
      if (page.partition === partition || (pagesOf !== undefined && pagesOf.length > 0 && key.startsWith(pagesOf))) this.dropPage(page);
    }
    for (const [key, s] of [...this.signIns]) {
      if (s.partition !== partition) continue;
      s.reported = true; // forgetting is not a sign-in
      this.signIns.delete(key);
      this.safeClose(s.win, true);
    }
    await this.clearPartition(partition);
    return { ok: true };
  }
}

/** The bridge connection's client: the subset of CoreClient the bridge uses. */
export interface BridgeClient {
  call(method: string, params?: Record<string, unknown>): Promise<unknown>;
  onEvent(listener: (event: { event: string; data: unknown }) => void): () => void;
  onClose(listener: () => void): () => void;
  close(): void;
}

/**
 * Keeps the website host connected to the core on its own connection (never the main window's, so a busy window cannot
 * block the replies): connects, says hostBridge.hello, runs website.request pushes, and reconnects with a bounded backoff.
 */
export class WebsiteBridge {
  readonly host: WebsiteHost;
  private current: BridgeClient | null = null;
  private stopped = false;
  private sleeping: { handle: unknown; wake: () => void } | null = null;

  constructor(
    private readonly connect: () => Promise<BridgeClient>,
    factory: WindowFactory,
    clearPartition: (partition: string) => Promise<void>,
    private readonly clock: Clock = realClock,
    private readonly backoff = { initialMs: 1000, maxMs: 30_000 },
  ) {
    this.host = new WebsiteHost(factory, { call: (m, p) => this.call(m, p) }, clock, clearPartition);
  }

  private call(method: string, params?: Record<string, unknown>): Promise<unknown> {
    if (!this.current) return Promise.reject(new Error('the bridge to the core is not connected'));
    return this.current.call(method, params);
  }

  start(): void {
    void this.loop();
  }

  stop(): void {
    this.stopped = true;
    this.host.closeAll();
    this.current?.close();
    if (this.sleeping) {
      this.clock.clearTimeout(this.sleeping.handle);
      this.sleeping.wake();
    }
  }

  private async loop(): Promise<void> {
    let delay = this.backoff.initialMs;
    while (!this.stopped) {
      let client: BridgeClient | null = null;
      try {
        client = await this.connect();
        if (this.stopped) {
          client.close();
          return;
        }
        // listen before hello, so a request pushed right after it is not missed
        const unsubscribe = client.onEvent((e) => {
          if (e.event === 'website.request') void this.host.handle(e.data as WebsiteRequest);
        });
        const closed = new Promise<void>((resolve) => client!.onClose(resolve));
        try {
          await client.call('hostBridge.hello', {});
        } catch (e) {
          unsubscribe();
          throw e;
        }
        if (this.stopped) {
          unsubscribe();
          client.close();
          return;
        }
        this.current = client;
        delay = this.backoff.initialMs;
        await closed;
        unsubscribe();
        this.current = null;
      } catch {
        this.current = null;
        try { client?.close(); } catch { /* already closed */ }
      }
      if (this.stopped) return;
      await new Promise<void>((resolve) => {
        this.sleeping = { handle: this.clock.setTimeout(resolve, delay), wake: resolve };
      });
      this.sleeping = null;
      delay = Math.min(delay * 2, this.backoff.maxMs);
    }
  }
}
