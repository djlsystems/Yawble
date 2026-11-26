// Remote.html, built once by Vite from the renderer's source into out/e2e/renderer, and the fake
// core behind the page's "rpc" socket. Nothing listens on a port: this sandbox refuses loopback sockets (to the browser
// and to node), so the page is loaded from a made-up origin, http://yawble-remote.test/, whose requests Playwright
// fulfils from the built files, and its WebSocket is answered by Playwright's routeWebSocket from the fake core. No real
// core, no real programs, no network.
import { existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import type { BrowserContext, WebSocketRoute } from '@playwright/test';
import { createFakeCore } from './fake-core';

export const ORIGIN = 'http://yawble-remote.test';
const shellDir = path.resolve(__dirname, '..', '..', '..');
// Under out/ (ignored): Playwright empties test-results at the start of each run.
export const builtDir = path.join(shellDir, 'out', 'e2e', 'renderer');

/**
 * mkdirSync(dir, { recursive: true }) that survives another worker making the same folder at the same moment. On Windows
 * two workers' beforeAll racing on one new folder can get EPERM (or EEXIST/EBUSY) from the loser even though the folder
 * now exists (seen with two workers in team-spacing.spec.ts's beforeAll). So such an error counts as done once the folder is there;
 * otherwise it is tried again a few times, then thrown.
 */
export function ensureDir(dir: string, tries = 5) {
  for (let i = 1; ; i++) {
    try { mkdirSync(dir, { recursive: true }); return; } catch (e) {
      const code = (e as NodeJS.ErrnoException).code;
      const racy = code === 'EPERM' || code === 'EEXIST' || code === 'EBUSY' || code === 'EACCES';
      if (racy && existsSync(dir) && statSync(dir).isDirectory()) return;
      if (!racy || i >= tries) throw e;
      const until = Date.now() + 50 * i; while (Date.now() < until) { /* short wait, then again */ }
    }
  }
}

/**
 * The run this build belongs to: E2E_RUN_ID, set once per run by tests/e2e/global-setup.ts (it reaches every worker),
 * or the runner's pid (the workers' parent) when the specs run without that setup.
 */
const runId = () => process.env.E2E_RUN_ID || String(process.ppid);

// A cross-process lock per output folder: a directory beside it (out/e2e/renderer.lock), made with mkdir
// (atomic; EEXIST when taken) and holding the owner's pid. A lock whose owner has gone (a worker killed mid-build) is broken.
const LOCK_POLL_MS = 500;
const LOCK_STALE_MS = 20 * 60_000;
const lockPath = (out: string) => `${out}.lock`;

function alive(pid: number) {
  try { process.kill(pid, 0); return true; } catch (e) { return (e as NodeJS.ErrnoException).code === 'EPERM'; }
}

function tryLock(lock: string) {
  mkdirSync(path.dirname(lock), { recursive: true });
  try {
    mkdirSync(lock);
    writeFileSync(path.join(lock, 'owner'), String(process.pid));
    return true;
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code !== 'EEXIST') throw e;
  }
  let age: number;
  try { age = Date.now() - statSync(lock).mtimeMs; } catch { return false; } // released meanwhile: try again
  let owner = NaN;
  try { owner = Number(readFileSync(path.join(lock, 'owner'), 'utf8')); } catch { /* not written yet */ }
  const gone = owner ? owner !== process.pid && !alive(owner) : age > 60_000;
  if (gone || age > LOCK_STALE_MS) rmSync(lock, { recursive: true, force: true });
  return false;
}

function unlock(lock: string) { rmSync(lock, { recursive: true, force: true }); }

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/**
 * Builds a renderer (index.html and remote.html) once per run of the test runner, or never with E2E_SKIP_BUILD=1 when
 * a build is there. Safe from any number of workers: the first takes the folder's lock and builds; the others
 * wait for it, then find the run's mark (.built-by = runId()) and reuse the build, so no worker empties a folder another
 * is serving from. A worker restarted after a failure finds the mark too.
 */
export async function buildRenderer(out = builtDir) {
  const lock = lockPath(out);
  while (!tryLock(lock)) await sleep(LOCK_POLL_MS);
  try { await buildRendererLocked(out); } finally { unlock(lock); }
}

async function buildRendererLocked(out: string) {
  const mark = path.join(out, '.built-by');
  if (existsSync(path.join(out, 'remote.html'))) {
    if (process.env.E2E_SKIP_BUILD === '1') return;
    if (existsSync(mark) && readFileSync(mark, 'utf8') === runId()) return;
  }
  console.log(`[e2e] building ${path.relative(shellDir, out) || out} (worker pid ${process.pid})`);
  const { build } = await import('vite');
  await build({ configFile: path.join(shellDir, 'vite.config.mts'), logLevel: 'error', build: { outDir: out, emptyOutDir: true } });
  writeFileSync(mark, runId());
}

export function fakeRemote(dir = builtDir) {
  const builtDir = dir;
  const core = createFakeCore();
  const sockets = new Set<WebSocketRoute>();
  return {
    core,
    url: `${ORIGIN}/remote.html`,
    /** Routes a context's page loads to the built files and its socket to the fake core. */
    async attach(ctx: BrowserContext) {
      await ctx.route(`${ORIGIN}/**`, async (route) => {
        const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
        const file = path.resolve(builtDir, rel || 'remote.html');
        if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
        return route.fulfill({ path: file });
      });
      await ctx.routeWebSocket(/\/rpc$/, (ws) => {
        sockets.add(ws);
        ws.onClose(() => sockets.delete(ws));
        ws.onMessage((m) => { const out = core.answer(String(m)); if (out) ws.send(out); });
      });
    },
    /** Pushes an event to every open page, as the core's {event, data}. */
    push(event: string, data: unknown) { for (const ws of sockets) ws.send(JSON.stringify({ event, data })); },
  };
}
