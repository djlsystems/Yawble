/**
 * THE SIGN-IN PAGE'S SOCKET: a member's table row signed in to by the person, in a headless Chromium
 * the platform runs (see `SignInEndpoints`). The screen arrives as binary JPEG frames and notices as
 * JSON text; the person's mouse and keys go back as JSON text, `{"type":"done"}` to keep the session
 * and close, `{"type":"cancel"}` to close. The session itself never crosses this socket: a save
 * answers only the hosts kept and the providers among them.
 */

/** What the socket tells the page. */
export type SignInMessage =
  | { kind: 'started'; width: number; height: number; site: string }
  | { kind: 'page'; url?: string; title?: string }
  | { kind: 'refused'; host: string }
  | { kind: 'provider'; host: string }
  | { kind: 'saved'; hosts: string[]; providers: string[] }
  | { kind: 'error'; error: string }
  | { kind: 'ended'; reason: string };

export interface SignInHandlers {
  onMessage: (message: SignInMessage) => void;
  onFrame: (jpeg: Blob) => void;
  onClose: () => void;
}

export interface SignInSocket {
  send: (input: object) => void;
  done: () => void;
  cancel: () => void;
  dispose: () => void;
}

/** The socket's address for a member's row: `/api/teams/<team>/members/<member>/sign-in/<field>/<row>`. */
export function signInSocketUrl(team: string, member: string, field: string, row: string, href: string): URL {
  const path = [team, 'members', member, 'sign-in', field, row].map(encodeURIComponent).join('/');
  const url = new URL(`/api/teams/${path}`, href);
  url.protocol = url.protocol.replace('http', 'ws');
  return url;
}

export function connectSignIn(
  where: { team: string; member: string; field: string; row: string },
  handlers: SignInHandlers,
): SignInSocket {
  const socket = new WebSocket(signInSocketUrl(where.team, where.member, where.field, where.row, window.location.href));
  socket.binaryType = 'blob';

  socket.addEventListener('message', (event: MessageEvent<Blob | string>) => {
    if (typeof event.data !== 'string') {
      handlers.onFrame(event.data);
      return;
    }

    try {
      handlers.onMessage(JSON.parse(event.data) as SignInMessage);
    } catch {
      // Not a notice: nothing to show.
    }
  });
  socket.addEventListener('close', () => handlers.onClose());

  const send = (input: object) => {
    if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(input));
  };

  return {
    send,
    done: () => send({ type: 'done' }),
    cancel: () => send({ type: 'cancel' }),
    dispose: () => socket.close(),
  };
}

/** CDP's modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8. */
export function modifiers(event: Pick<KeyboardEvent, 'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey'>): number {
  return (event.altKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) | (event.metaKey ? 4 : 0) | (event.shiftKey ? 8 : 0);
}

/** A point on the shown screen, in the browser's own CSS pixels: the image is scaled to fit. */
export function pointAt(
  clientX: number,
  clientY: number,
  rect: Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>,
  size: { width: number; height: number },
): { x: number; y: number } {
  const x = rect.width > 0 ? ((clientX - rect.left) * size.width) / rect.width : 0;
  const y = rect.height > 0 ? ((clientY - rect.top) * size.height) / rect.height : 0;
  return { x: Math.round(Math.min(Math.max(x, 0), size.width)), y: Math.round(Math.min(Math.max(y, 0), size.height)) };
}

const Buttons = ['left', 'middle', 'right'] as const;

/** A mouse event as the socket takes it. */
export function mouseInput(
  kind: 'mousePressed' | 'mouseReleased' | 'mouseMoved',
  event: Pick<MouseEvent, 'clientX' | 'clientY' | 'button' | 'detail' | 'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey'>,
  rect: Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>,
  size: { width: number; height: number },
): object {
  const at = pointAt(event.clientX, event.clientY, rect, size);
  return {
    type: 'mouse',
    event: kind,
    ...at,
    button: kind === 'mouseMoved' ? 'none' : (Buttons[event.button] ?? 'left'),
    clickCount: kind === 'mouseMoved' ? 0 : Math.max(event.detail, 1),
    modifiers: modifiers(event),
  };
}

/** A key as the socket takes it: a key that types carries its text, unless Ctrl or Meta is held. */
export function keyInput(
  kind: 'keyDown' | 'keyUp',
  event: Pick<KeyboardEvent, 'key' | 'code' | 'keyCode' | 'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey'>,
): object {
  const types = kind === 'keyDown' && !event.ctrlKey && !event.metaKey && (event.key.length === 1 || event.key === 'Enter');
  return {
    type: 'key',
    event: kind,
    key: event.key,
    code: event.code,
    keyCode: event.keyCode,
    ...(types ? { text: event.key === 'Enter' ? '\r' : event.key } : {}),
    modifiers: modifiers(event),
  };
}
