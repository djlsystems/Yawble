import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { isUiMethod } from '../../src/shared/api';

// Electron main refuses any core method the window calls that is not under UI_METHOD_PREFIXES. A screen that calls a
// method outside the list fails at run time only (in the main process's log), so every literal call in the renderer
// is checked here.
const renderer = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'renderer');
// These reach the core through their own channels (Channels.getInfo / getAvailableBackends), not Channels.call.
const ownChannel = new Set(['core.getInfo', 'sandbox.getAvailableBackends']);

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) return sources(p);
    return /\.(ts|vue)$/.test(e.name) ? [p] : [];
  });
}

describe('the core methods the window calls', () => {
  const called = new Set<string>();
  for (const file of sources(renderer)) {
    for (const m of readFileSync(file, 'utf8').matchAll(/\bcall\b[^(;]{0,80}\(\s*'([a-zA-Z]+\.[a-zA-Z.]+)'/g)) called.add(m[1]);
  }

  it('finds the calls', () => {
    expect(called.size).toBeGreaterThan(50);
    expect(called).toContain('network.asks');
    expect(called).toContain('tools.asks');
  });

  it('are all methods Electron main lets the window call', () => {
    const refused = [...called].filter((m) => !ownChannel.has(m) && !isUiMethod(m)).sort();
    expect(refused).toEqual([]);
  });
});
