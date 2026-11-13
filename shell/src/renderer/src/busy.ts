import { reactive } from 'vue';

/**
 * The "action in progress" state of a view's buttons: run(key, fn) marks `key` pending while fn's promise runs and
 * clears it on success or error (fn's own try/catch shows the error). A second run of a key that is still pending
 * does nothing (a double click), and resolves to undefined. is(key) is for :disabled and BusyLabel's :busy.
 *
 * Several buttons on one thing (Approve / Decline on an ask) share a key, so all of them are held while one runs;
 * the optional `action` says which one was clicked, so only that one shows the spinner: is(key, action).
 */
export function useBusy() {
  const pending = reactive(new Map<string, string>());
  async function run<T>(key: string, fn: () => Promise<T>, action = ''): Promise<T | undefined> {
    if (pending.has(key)) return undefined;
    pending.set(key, action);
    try { return await fn(); } finally { pending.delete(key); }
  }
  return {
    run,
    /** `key` is pending (with `action`: and that action is the one running). */
    is: (key: string, action?: string) => pending.has(key) && (action === undefined || pending.get(key) === action),
    /** Any action pending (to hold the other buttons of the same thing). */
    any: () => pending.size > 0,
  };
}
export type Busy = ReturnType<typeof useBusy>;
