// The renderer asks through confirmDialog (confirm.ts), not window.confirm. A test that only needs an answer
// installs one here: every question is answered at once with `answer(message)`, and the stub records the messages as
// a vi.fn's calls (as the window.confirm spies did). Undo with the returned stop(), or stopConfirmStubs() in afterEach.
import { watch } from 'vue';
import { vi } from 'vitest';
import { confirmState } from '../../src/renderer/src/confirm';

const stops: (() => void)[] = [];

export function stubConfirm(answer: (message: string) => boolean) {
  const fn = vi.fn(answer);
  const stop = watch(confirmState, (d) => { if (d) d.resolve(fn(d.message)); }, { flush: 'sync' });
  stops.push(stop);
  return fn;
}

export function stopConfirmStubs(): void {
  while (stops.length) stops.pop()!();
  confirmState.value?.resolve(false);
}
