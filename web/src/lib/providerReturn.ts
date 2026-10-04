import type { CallbackOutcome } from './connections';

/**
 * A WEB SIGN-IN STARTED FROM A SLOT, IN ANOTHER TAB. Google's sign-in leaves for the provider and
 * comes back through the Host's callback to the Console. Started from a slot - in a member's
 * settings, the solution wizard, the control panel - leaving would lose the very form the account is
 * for, so the provider is opened in a new tab instead, and this tab waits.
 *
 * The tab the provider returns to says what came back on a channel the waiting tab listens on, then
 * closes itself. Only the outcome travels: `connected` and the connection's id, or `refused` and the
 * Host's reason - never a code, a state or a token. A note in local storage says a tab is waiting,
 * so a return nobody waits for is shown where it lands, as before.
 */
const Channel = 'connections.return';
const WaitingKey = 'connections.waiting';

/** Listens for the provider's return until the answered function is called. */
export function awaitProviderReturn(onReturn: (outcome: CallbackOutcome) => void): () => void {
  localStorage.setItem(WaitingKey, '1');
  const channel = new BroadcastChannel(Channel);
  channel.onmessage = (event: MessageEvent<CallbackOutcome>) => onReturn(event.data);

  return () => {
    channel.close();
    localStorage.removeItem(WaitingKey);
  };
}

/**
 * Tells the waiting tab what the provider's return carried. True when a tab was waiting for it,
 * and this one has nothing more to show.
 */
export function announceProviderReturn(outcome: CallbackOutcome): boolean {
  if (localStorage.getItem(WaitingKey) === null) return false;
  localStorage.removeItem(WaitingKey);

  const channel = new BroadcastChannel(Channel);
  channel.postMessage(outcome);
  channel.close();
  return true;
}
