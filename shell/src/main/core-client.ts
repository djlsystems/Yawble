import net from 'node:net';
import { encodeFrame, FrameDecoder, FrameKind } from '../shared/frame';

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

export class CoreRpcError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
    // Electron hands the window only the error's text ("<name>: <message>"), so the code rides in the name:
    // the renderer's errorCode reads it back and errorText leaves it out.
    this.name = `CoreRpcError[${code}]`;
  }
}

/**
 * The shell's connection to the core over a Unix socket (Linux, macOS) or a named pipe (Windows,
 * address \\.\pipe\name). JSON requests carry an id; the core answers each with the same id.
 */
export class CoreClient {
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Set<(event: { event: string; data: unknown }) => void>();
  private nextId = 0;
  private closedError: Error | null = null;
  private readonly closeListeners = new Set<() => void>();

  private constructor(private readonly socket: net.Socket) {
    const decoder = new FrameDecoder();
    socket.on('data', (chunk: Buffer) => {
      for (const frame of decoder.push(chunk)) {
        if (frame.kind !== FrameKind.Json) continue;
        const message = JSON.parse(frame.payload.toString('utf8'));
        if (message.id === undefined && typeof message.event === 'string') {
          // A push (no id): terminal output and state.
          for (const listener of this.listeners) listener({ event: message.event, data: message.data });
          continue;
        }
        const waiter = this.pending.get(message.id);
        if (!waiter) continue;
        this.pending.delete(message.id);
        if (message.error) waiter.reject(new CoreRpcError(message.error.code, message.error.message));
        else waiter.resolve(message.result);
      }
    });
    const fail = (error: Error) => {
      this.closedError = error;
      for (const waiter of this.pending.values()) waiter.reject(error);
      this.pending.clear();
      for (const listener of [...this.closeListeners]) listener();
      this.closeListeners.clear();
    };
    socket.on('error', fail);
    socket.on('close', () => fail(new Error('the core closed the connection')));
  }

  static connect(address: string, token: string): Promise<CoreClient> {
    return new Promise((resolve, reject) => {
      const socket = net.connect({ path: address });
      socket.once('error', reject);
      socket.once('connect', async () => {
        socket.off('error', reject);
        const client = new CoreClient(socket);
        try {
          await client.call('auth', { token });
          resolve(client);
        } catch (error) {
          socket.destroy();
          reject(error);
        }
      });
    });
  }

  call<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T> {
    if (this.closedError) return Promise.reject(this.closedError);
    const id = ++this.nextId;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      const payload = Buffer.from(JSON.stringify({ id, method, params: params ?? null }), 'utf8');
      this.socket.write(encodeFrame(FrameKind.Json, payload));
    });
  }

  /** Pushes from the core (frames with no id). Returns the unsubscribe function. */
  onEvent(listener: (event: { event: string; data: unknown }) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Runs once when the connection is gone (now, if it already is). Returns the unsubscribe function. */
  onClose(listener: () => void): () => void {
    if (this.closedError) {
      listener();
      return () => {};
    }
    this.closeListeners.add(listener);
    return () => this.closeListeners.delete(listener);
  }

  close(): void {
    this.socket.end();
  }
}
