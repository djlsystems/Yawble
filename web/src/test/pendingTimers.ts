/**
 * NO TIMER OUTLIVES ITS SPEC FILE. Quasar's transitions finish on a `setTimeout`; a dialog a spec
 * left open schedules one, the file ends, its happy-dom environment is torn down, and the timer
 * then fires into a later file with `document` gone. Vitest reports that as an unhandled error
 * against whichever file happened to be running, and the suite exits 1 with every test green.
 *
 * Each timer set while a file runs is remembered until it fires or is cleared, and whatever is
 * still pending when the file's last test is done is cleared. Fake timers are unaffected: they
 * replace these globals while installed and put them back after.
 */
import { afterAll } from 'vitest';

type Handle = ReturnType<typeof setTimeout>;

const pending = new Set<Handle>();
const realSetTimeout = globalThis.setTimeout;
const realClearTimeout = globalThis.clearTimeout;
const realSetInterval = globalThis.setInterval;
const realClearInterval = globalThis.clearInterval;

function tracked<T extends (...args: never[]) => unknown>(real: T, replacement: T): T {
  // Keeps `setTimeout[util.promisify.custom]` and anything else hung on the original.
  Object.defineProperties(replacement, Object.getOwnPropertyDescriptors(real));
  return replacement;
}

globalThis.setTimeout = tracked(realSetTimeout, ((handler: (...args: unknown[]) => void, ms?: number, ...args: unknown[]) => {
  const handle: Handle = realSetTimeout(
    (...a: unknown[]) => {
      pending.delete(handle);
      if (typeof handler === 'function') handler(...a);
    },
    ms,
    ...args,
  );
  pending.add(handle);
  return handle;
}) as unknown as typeof setTimeout);

globalThis.clearTimeout = tracked(realClearTimeout, ((handle?: Handle) => {
  if (handle !== undefined) pending.delete(handle);
  realClearTimeout(handle);
}) as unknown as typeof clearTimeout);

globalThis.setInterval = tracked(realSetInterval, ((handler: (...args: unknown[]) => void, ms?: number, ...args: unknown[]) => {
  const handle: Handle = realSetInterval(handler, ms, ...args);
  pending.add(handle);
  return handle;
}) as unknown as typeof setInterval);

globalThis.clearInterval = tracked(realClearInterval, ((handle?: Handle) => {
  if (handle !== undefined) pending.delete(handle);
  realClearInterval(handle);
}) as unknown as typeof clearInterval);

afterAll(() => {
  // clearTimeout and clearInterval share one id space in Node and in happy-dom.
  for (const handle of pending) realClearTimeout(handle);
  pending.clear();
});
