import type { AvailableBackend, CoreEvent, CoreInfo, YawbleApi } from '../../shared/api';
import { openMemberTerminalById } from './memberTerminal';

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

/**
 * The same window API as the desktop's preload bridge, over a WebSocket: the transport of a REMOTE view of an
 * instance. The page that serves this bundle also serves the socket, at "rpc" next to the page; whatever is behind it
 * relays the instance core's JSON-RPC unchanged: send {id, method, params}; receive {id, result} or {id, error:{code,
 * message}}, and pushes {event, data}. Nothing here names or assumes a particular server.
 *
 * A call is sent at once or it fails; it is never sent after its caller heard that it failed. The calls the screens
 * make while the page's first connection is still opening wait for it; when a connection closes, every waiting call
 * fails and is dropped, and until the next one is open a new call fails at once (the screens ask again by themselves).
 */
export function createRemoteApi(socketUrl: string = defaultSocketUrl()): YawbleApi {
  const pending = new Map<number, Pending>();
  const listeners = new Set<(event: CoreEvent) => void>();
  const queue: string[] = [];
  let nextId = 0;
  let socket: WebSocket | null = null;
  let retry = 500;
  // True only while the page's first connection is opening: the one time a call waits for a socket.
  let starting = true;
  const CLOSED = 'the connection to the instance closed';

  const connect = () => {
    const ws = new WebSocket(socketUrl);
    socket = ws;
    ws.onopen = () => {
      starting = false;
      retry = 500;
      for (const text of queue.splice(0)) ws.send(text);
      for (const l of listeners) l({ event: 'remote.connected', data: null });
    };
    ws.onmessage = (m) => {
      const message = JSON.parse(String(m.data));
      if (message.id === undefined || message.id === null) {
        if (typeof message.event === 'string') for (const l of listeners) l({ event: message.event, data: message.data });
        return;
      }
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      // The code rides on the Error (as the desktop's CoreRpcError): remote_refused is the Cloud Link's own refusal.
      if (message.error) waiter.reject(Object.assign(new Error(message.error.message ?? message.error.code ?? 'error'), { code: message.error.code }));
      else waiter.resolve(message.result);
    };
    ws.onclose = (e) => {
      socket = null;
      starting = false;
      const why = e.reason || CLOSED;
      // Every call that waits has failed for its caller, the ones not sent yet too: none of them is sent later.
      queue.length = 0;
      for (const w of pending.values()) w.reject(new Error(why));
      pending.clear();
      for (const l of listeners) l({ event: 'remote.disconnected', data: { reason: why } });
      setTimeout(connect, retry);
      retry = Math.min(retry * 2, 10_000);
    };
  };
  connect();

  const call = <T>(method: string, params?: Record<string, unknown>): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const open = socket?.readyState === WebSocket.OPEN;
      if (!open && !starting) { reject(new Error(CLOSED)); return; }
      const id = ++nextId;
      pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      const text = JSON.stringify({ id, method, params: params ? JSON.parse(JSON.stringify(params)) : null });
      if (open) socket!.send(text);
      else queue.push(text);
    });

  return {
    remote: true,
    getInfo: () => call<CoreInfo>('core.getInfo'),
    getAvailableBackends: () => call<AvailableBackend[]>('sandbox.getAvailableBackends'),
    call,
    onEvent: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    // An agent's terminal: in this page, on its team's page in that member's pane (memberTerminal.ts), not in a
    // browser window of its own. (A #terminal/<id> link still opens the bare terminal page, as before.)
    openTerminal: async (memberId) => { await openMemberTerminalById(memberId); },
    // No OS folder picker in a browser: the person types the folder's path on the instance.
    pickFolder: async () => null,
  };
}

function defaultSocketUrl(): string {
  const url = new URL('rpc', window.location.href);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.hash = '';
  return url.toString();
}
