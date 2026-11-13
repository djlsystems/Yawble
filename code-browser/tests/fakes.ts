import type { CodeBrowserApi, CodeEntry, CodeFile } from '../src/types';

/** A fake core: answers code.* calls from handlers, records every call, and pushes events on demand. */
export function fakeCore(handlers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const listeners = new Set<(e: { event: string; data: any }) => void>();
  const api: CodeBrowserApi = {
    async call(method, params) {
      calls.push({ method, params });
      const h = handlers[method];
      if (!h) throw new Error(`unexpected ${method}`);
      return (await h(params)) as any;
    },
    onEvent(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  return {
    api, calls, handlers,
    push: (event: string, data: unknown) => { for (const l of listeners) l({ event, data }); },
    listeners,
    called: (method: string) => calls.filter((c) => c.method === method),
  };
}

export const NOW = Date.parse('2026-11-12T12:00:00Z');
export const ago = (ms: number) => new Date(NOW - ms).toISOString();

export function entry(path: string, over: Partial<CodeEntry> = {}): CodeEntry {
  return { name: path.split('/').pop()!, path, kind: 'file', size: 120, mtime: ago(3 * 3600_000), marker: null, hidden: false, ...over };
}

export function textFile(path: string, content: string, over: Partial<CodeFile> = {}): CodeFile {
  return { path, type: 'text', size: content.length, content, lines: [], removed: [], linesUnknown: false, ...over };
}

/** n numbered lines of code. */
export const numbered = (n: number) => Array.from({ length: n }, (_, i) => `const v${i + 1} = ${i + 1};`).join('\n');
