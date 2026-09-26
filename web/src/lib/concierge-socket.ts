/**
 * The close reason the server sends when a newer client took the terminal.
 *
 * A PTY has one size and nothing here re-renders per client, so only one device can drive a session;
 * attaching detaches whoever was there. This string must stay in step with
 * `PtyWebSocket.EvictedReason` — if it drifts, the takeover silently reads as the agent having died.
 */
export const EvictedReason = 'attached in another tab or device';

export interface ConciergeSocket {
  /** A keystroke. Sent as a BINARY frame — see the note on framing below. */
  sendInput: (data: string) => void;

  /** Terminal geometry. Sent as a TEXT frame carrying control JSON. */
  sendResize: (cols: number, rows: number) => void;

  /** Detaches. The session behind it keeps running. */
  dispose: () => void;
}

export interface ConciergeHandlers {
  onOpen: () => void;
  onData: (bytes: Uint8Array) => void;
  onClose: (reason: string) => void;
}

/**
 * The console's own raw WebSocket.
 *
 * NOT SignalR: SignalR is message-oriented and a PTY is a bidirectional byte
 * stream with binary frames. Nothing here knows about containers,
 * messages or correlation — that is what makes it a console rather than a member.
 *
 * **Framing is load-bearing.** Keystrokes go as BINARY frames; TEXT frames are
 * reserved for control JSON like resize. Sending input as text would hand the
 * child a control message instead of a keypress, and sending control as binary
 * would type JSON into the agent.
 */
export function connectConcierge(
  size: { cols: number; rows: number },
  handlers: ConciergeHandlers,
): ConciergeSocket {
  // One Concierge per PERSON, so the route names no team.
  const url = new URL('/api/concierge/ws', window.location.href);
  url.protocol = url.protocol.replace('http', 'ws');

  // The geometry travels ON the connect, not in a resize frame afterwards. The child prints its
  // banner the moment it spawns, and a terminal transcript has its width baked into it — so a size
  // that arrives a moment later means that banner was drawn for a screen nobody has, and replaying
  // it here is scrambled rather than merely narrow.
  url.searchParams.set('cols', String(size.cols));
  url.searchParams.set('rows', String(size.rows));

  const socket = new WebSocket(url);
  socket.binaryType = 'arraybuffer';

  const encoder = new TextEncoder();

  socket.addEventListener('open', () => handlers.onOpen());

  socket.addEventListener('message', (event: MessageEvent<ArrayBuffer>) =>
    handlers.onData(new Uint8Array(event.data)),
  );

  socket.addEventListener('close', (event) => handlers.onClose(event.reason));

  return {
    sendInput: (data) => {
      if (socket.readyState === WebSocket.OPEN) socket.send(encoder.encode(data));
    },

    sendResize: (cols, rows) => {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ type: 'resize', cols, rows }));
      }
    },

    // Closing DETACHES. The console session keeps running server-side, which is
    // why there is no DELETE here and no close-that-kills on the chrome.
    dispose: () => socket.close(),
  };
}
