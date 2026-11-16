// B037 verify: remote.html, built once by Vite from the renderer's source into out/b037/renderer, and the fake
// core behind the page's "rpc" socket. Nothing listens on a port: this sandbox refuses loopback sockets (to the browser
// and to node), so the page is loaded from a made-up origin, http://yawble-remote.test/, whose requests Playwright
// fulfils from the built files, and its WebSocket is answered by Playwright's routeWebSocket from the fake core. No real
// core, no real programs, no network.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import type { BrowserContext, WebSocketRoute } from '@playwright/test';
import { createFakeCore } from './fake-core';

export const ORIGIN = 'http://yawble-remote.test';
const shellDir = path.resolve(__dirname, '..', '..', '..');
// Under out/ (ignored): Playwright empties test-results at the start of each run.
export const builtDir = path.join(shellDir, 'out', 'b037', 'renderer');
/** The base's shell (main b726212), extracted with `git archive b726212 shell code-browser` for the 1280 px comparison. */
export const baseShellDir = process.env.B037_BASE_DIR ? path.resolve(process.env.B037_BASE_DIR, 'shell') : path.join(shellDir, 'out', 'b037', 'main', 'shell');
export const baseBuiltDir = process.env.B037_BASE_DIR ? path.resolve(process.env.B037_BASE_DIR, 'renderer') : path.join(shellDir, 'out', 'b037', 'renderer-base');

/**
 * Builds a renderer (index.html and remote.html) once per run of the test runner (a worker restarted after a failure
 * finds the runner's mark and reuses it), or never with B037_SKIP_BUILD=1 when a build is there.
 */
export async function buildRenderer(from = shellDir, out = builtDir) {
  const mark = path.join(out, '.built-by');
  if (existsSync(path.join(out, 'remote.html'))) {
    if (process.env.B037_SKIP_BUILD === '1') return;
    if (existsSync(mark) && readFileSync(mark, 'utf8') === String(process.ppid)) return;
  }
  const { build } = await import('vite');
  if (from === shellDir) {
    await build({ configFile: path.join(from, 'vite.config.mts'), logLevel: 'error', build: { outDir: out, emptyOutDir: true } });
    writeFileSync(mark, String(process.ppid));
    return;
  }
  // The base's tree: its vite.config.mts is not loadable from under test-results, so the same settings inline (the
  // shell's vite.config.mts at b726212: root, the Vue plugin, the Code browser alias, dedupe, the two pages).
  const vue = (await import('@vitejs/plugin-vue')).default;
  const renderer = path.join(from, 'src', 'renderer');
  await build({
    configFile: false, root: renderer, base: './', logLevel: 'error', plugins: [vue()],
    resolve: { alias: { '@yawble/code-browser': path.resolve(from, '../code-browser/src/index.ts') }, dedupe: ['vue', 'highlight.js'] },
    define: { __YAWBLE_BUILD__: JSON.stringify({ version: '0.0.0+dev.unknown', commit: 'unknown', dirty: false, released: false }) },
    build: { outDir: out, emptyOutDir: true, rollupOptions: { input: { index: path.join(renderer, 'index.html'), remote: path.join(renderer, 'remote.html') } } },
  });
  writeFileSync(mark, String(process.ppid));
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
