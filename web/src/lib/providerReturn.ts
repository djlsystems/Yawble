import type { CallbackOutcome } from './connections';

/**
 * A WEB SIGN-IN STARTED FROM A SLOT, IN ANOTHER TAB. Google's sign-in leaves for the provider and
 * comes back through the Host's callback to the Console. Started from a slot - in a member's
 * settings, the solution wizard, the control panel - leaving would lose the very form the account is
 * for, so the provider is opened in a new tab instead, and this tab waits.
 *
 * The Host's return names only the outcome and the connection, not the sign-in it belongs to, so the
 * tab carries that itself: each sign-in has a TAG. The link opens the Console at
 * `#/console?signin=<tag>`; that tab keeps the tag in its own session storage (it outlives the trip
 * to the provider, in that tab only), asks the waiting tab for the provider's address and goes. Back
 * from the provider, it says what came back under the tag, and the waiting tab takes only its own.
 *
 * Only the outcome travels back: `connected` and the connection's id, or `refused` and the Host's
 * reason - never a code, a state or a token. A return no tab takes is shown where it lands.
 */
const Channel = 'connections.return';
const TabKey = 'connections.tab';

type Message =
  | { kind: 'ask'; tag: string }
  | { kind: 'go'; tag: string; url: string }
  | { kind: 'back'; tag: string; outcome: CallbackOutcome }
  | { kind: 'taken'; tag: string };

/** A new sign-in's tag: random, and made without a secure context (a LAN address is not one). */
export function newSigninTag(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

/** Where the link for a sign-in opens: the Console, which goes on to the provider. */
export function signinTabAddress(tag: string): string {
  return `#/console?signin=${encodeURIComponent(tag)}`;
}

/**
 * Waits on the sign-in tagged `tag`: tells its tab the provider's address, and hands on what came
 * back. Listens until the returned function is called.
 */
export function awaitProviderReturn(tag: string, url: string, onReturn: (outcome: CallbackOutcome) => void): () => void {
  const channel = new BroadcastChannel(Channel);
  channel.onmessage = (event: MessageEvent<Message>) => {
    const message = event.data;
    if (!message || message.tag !== tag) return;
    if (message.kind === 'ask') channel.postMessage({ kind: 'go', tag, url } satisfies Message);
    if (message.kind === 'back') {
      channel.postMessage({ kind: 'taken', tag } satisfies Message);
      onReturn(message.outcome);
    }
  };

  return () => channel.close();
}

/** Answers on the channel within `wait` ms, or null. */
function exchange<T>(send: Message, answer: (message: Message) => T | null, wait: number): Promise<T | null> {
  return new Promise((resolve) => {
    const channel = new BroadcastChannel(Channel);
    const done = (value: T | null) => {
      clearTimeout(timer);
      channel.close();
      resolve(value);
    };
    const timer = setTimeout(() => done(null), wait);
    channel.onmessage = (event: MessageEvent<Message>) => {
      const value = event.data ? answer(event.data) : null;
      if (value !== null) done(value);
    };
    channel.postMessage(send);
  });
}

/**
 * In the tab the link opened: keeps the tag for the way back and asks the waiting tab where to go.
 * Null when no tab is waiting on that sign-in any more.
 */
export async function leaveForProvider(tag: string, wait = 5000): Promise<string | null> {
  sessionStorage.setItem(TabKey, tag);
  const url = await exchange({ kind: 'ask', tag }, (message) => (message.kind === 'go' && message.tag === tag ? message.url : null), wait);
  if (url === null) sessionStorage.removeItem(TabKey);
  return url;
}

/**
 * In the tab the provider returned to: tells the tab waiting on this tab's sign-in what came back.
 * True when it took it, and this tab has nothing more to show.
 */
export async function announceProviderReturn(outcome: CallbackOutcome, wait = 1000): Promise<boolean> {
  const tag = sessionStorage.getItem(TabKey);
  if (tag === null) return false;
  sessionStorage.removeItem(TabKey);

  const taken = await exchange({ kind: 'back', tag, outcome }, (message) => (message.kind === 'taken' && message.tag === tag ? true : null), wait);
  return taken === true;
}
