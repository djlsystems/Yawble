import type { AvailableBackend, CoreEvent, CoreInfo, YawbleApi } from '../../shared/api';

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

/**
 * The same window API as the desktop's preload bridge, over a WebSocket: the transport of a REMOTE view of an
 * instance. The page that serves this bundle also serves the socket, at "rpc" next to the page; whatever is behind it
 * relays the instance core's JSON-RPC unchanged: send {id, method, params}; receive {id, result} or {id, error:{code,
 * message}}, and pushes {event, data}. Nothing here names or assumes a particular server.
 */
export function createRemoteApi(socketUrl: string = defaultSocketUrl()): YawbleApi {
  const pending = new Map<number, Pending>();
  const listeners = new Set<(event: CoreEvent) => void>();
  const queue: string[] = [];
  let nextId = 0;
  let socket: WebSocket | null = null;
  let retry = 500;

  const connect = () => {
    const ws = new WebSocket(socketUrl);
    socket = ws;
    ws.onopen = () => {
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
      if (message.error) waiter.reject(new Error(message.error.message ?? message.error.code ?? 'error'));
      else waiter.resolve(message.result);
    };
    ws.onclose = (e) => {
      socket = null;
      const why = e.reason || 'the connection to the instance closed';
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
      const id = ++nextId;
      pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      const text = JSON.stringify({ id, method, params: params ? JSON.parse(JSON.stringify(params)) : null });
      if (socket?.readyState === WebSocket.OPEN) socket.send(text);
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
    // An agent's terminal: the same page in its own browser window (the page reads #terminal/<id>).
    openTerminal: async (memberId) => {
      const url = new URL(window.location.href);
      url.hash = `terminal/${memberId}`;
      window.open(url.toString(), `terminal-${memberId}`, 'width=980,height=620');
    },
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
