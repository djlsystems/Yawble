// B037 verify: remote.html, built once by Vite from the renderer's source into out/b037/renderer, and the fake
// core behind the page's "rpc" socket. Nothing listens on a port: this sandbox refuses loopback sockets (to the browser
// and to node), so the page is loaded from a made-up origin, http://yawble-remote.test/, whose requests Playwright
// fulfils from the built files, and its WebSocket is answered by Playwright's routeWebSocket from the fake core. No real
// core, no real programs, no network.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import type { BrowserContext, WebSocketRoute } from '@playwright/test';
import { createFakeCore } from './fake-core';

/**
 * The main commit the 1280 px desktop layout is compared with (phone-remote.spec.ts). To move the base, change this
 * one line: the next run extracts that commit's shell into out/b037/main and rebuilds out/b037/renderer-base.
 * Was b726212 (cards 169-276), then f3a3c3d (cards 294-312, nav order be4a82a and the rest of main up to it); 43048e1 since
 * card 313 (the Sidebar team counts as pills).
 */
export const BASE_COMMIT = '43048e1';

export const ORIGIN = 'http://yawble-remote.test';
const shellDir = path.resolve(__dirname, '..', '..', '..');
// Under out/ (ignored): Playwright empties test-results at the start of each run.
export const builtDir = path.join(shellDir, 'out', 'b037', 'renderer');
const baseTreeDir = path.join(shellDir, 'out', 'b037', 'main');
/**
 * The base's shell for the 1280 px comparison: main BASE_COMMIT, extracted with `git archive BASE_COMMIT shell code-browser`
 * (extractBaseTree). B037_BASE_DIR picks another base tree instead (its shell/ and its built renderer/); then nothing is extracted.
 */
export const baseShellDir = process.env.B037_BASE_DIR ? path.resolve(process.env.B037_BASE_DIR, 'shell') : path.join(baseTreeDir, 'shell');
export const baseBuiltDir = process.env.B037_BASE_DIR ? path.resolve(process.env.B037_BASE_DIR, 'renderer') : path.join(shellDir, 'out', 'b037', 'renderer-base');

/**
 * Extracts BASE_COMMIT's shell/ and code-browser/ into out/b037/main (`git archive BASE_COMMIT shell code-browser`),
 * unless out/b037/main/.commit already names it. A new tree drops the old base build, so it is built again even with
 * B037_SKIP_BUILD=1. The tree sits under shell/, so its imports (vue, highlight.js, ...) resolve from shell/node_modules.
 */
export function extractBaseTree() {
  if (process.env.B037_BASE_DIR) return;
  const stamp = path.join(baseTreeDir, '.commit');
  if (existsSync(path.join(baseShellDir, 'src', 'renderer', 'remote.html')) && existsSync(stamp) && readFileSync(stamp, 'utf8').trim() === BASE_COMMIT) return;
  const repo = path.resolve(shellDir, '..');
  execFileSync('git', ['rev-parse', '--verify', '--quiet', `${BASE_COMMIT}^{commit}`], { cwd: repo, stdio: 'ignore' });
  rmSync(baseTreeDir, { recursive: true, force: true });
  rmSync(baseBuiltDir, { recursive: true, force: true });
  mkdirSync(baseTreeDir, { recursive: true });
  const tar = path.join(shellDir, 'out', 'b037', 'main.tar');
  execFileSync('git', ['archive', '--format=tar', '-o', tar, BASE_COMMIT, 'shell', 'code-browser'], { cwd: repo });
  execFileSync('tar', ['-xf', tar, '-C', baseTreeDir]);
  rmSync(tar, { force: true });
  writeFileSync(stamp, BASE_COMMIT);
}

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
  // shell's vite.config.mts at BASE_COMMIT: root, base, the Vue plugin, the Code browser alias, dedupe, the build-version
  // define (as with no scripts/ folder: 0.0.0+dev.unknown), the two pages; unchanged from b726212 to f3a3c3d and to 43048e1).
  // Check them again when BASE_COMMIT moves: `git diff <old> <new> -- shell/vite.config.mts`.
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
