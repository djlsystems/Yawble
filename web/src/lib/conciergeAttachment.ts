/**
 * AN IMAGE GIVEN TO THE CONCIERGE, AS A FILE PATH IT CAN OPEN.
 *
 * An agent CLI's own image paste reads the clipboard of the machine it runs on - the container,
 * which has none - and the browser terminal forwards only text. So the PAGE catches the image,
 * uploads it to the Concierge's own folder, and types the path the Host answers with into the
 * prompt as a paste - through xterm's own `paste`, so it is bracketed exactly when the CLI asked for
 * bracketed paste (DECSET 2004) and bare when it did not. A CLI that attaches images by path attaches it; any other gets a file it can
 * open. Nothing here names a CLI, and no CLI's own image key is relied on.
 *
 * THE FOUR WAYS IN, AND WHERE EACH WORKS:
 *  - a paste event carrying an image (Ctrl+V / Cmd+V): every browser.
 *  - Alt+V, read through `navigator.clipboard.read()`: Chromium (Chrome, Edge) after the person
 *    grants clipboard permission. Firefox exposes `clipboard.read` only from 125 on, and asks with a
 *    paste prompt per read; Safari exposes it but only inside a user gesture and with its own
 *    "Paste" button, and older versions lack it. Wherever it is missing or refused, Alt+V reaches
 *    the CLI exactly as xterm would have sent it, and the paste event or the Attach image button
 *    is the path.
 *  - a drop of image files: every desktop browser.
 *  - the Attach image button, a file picker: every browser, and the only one on most phones.
 *
 * THE BYTES ARE NEVER RENDERED. A File goes from the event to the upload and nowhere else: no
 * preview, no object URL, no <img>. That is what keeps the CSP untouched.
 */

/** What the picker offers. The Host checks the content; this is only what the dialog lists. */
export const AttachableImageTypes = 'image/png,image/jpeg,image/gif,image/webp';

/**
 * What a drop holding no image is told, on the shell and on the compose bar alike: one sentence,
 * so the same mistake reads the same wherever the person made it.
 */
export const NotAnImage = 'Only an image can be attached: PNG, JPEG, GIF or WebP.';

/** A 409 whose body carried no sentence of its own. The Host refuses that way when no CLI runs. */
export const NoConciergeToReceive = 'No Concierge is running to receive the image. Start it, then attach again.';

/**
 * The sentence a refused upload is shown with: the body's `error`, else its `message`, else - for a
 * 409 that said nothing - `NoConciergeToReceive`, else whatever the failure said.
 */
export function refusedUploadSentence(cause: unknown): string {
  const failure = cause as { status?: unknown; body?: Record<string, unknown> } | null;
  const body = failure?.body;

  for (const field of ['error', 'message'] as const) {
    const said = body?.[field];
    if (typeof said === 'string' && said.trim()) return said;
  }

  if (failure?.status === 409) return NoConciergeToReceive;
  return cause instanceof Error ? cause.message : String(cause);
}

function isImage(type: string): boolean {
  return type.startsWith('image/');
}

/** The image files a drop carries. Anything else in it is left out, not refused. */
export function droppedImages(data: DataTransfer | null): File[] {
  if (!data) return [];
  return Array.from(data.files).filter((file) => isImage(file.type));
}

/** Whether a drag is carrying files at all, which is all a dragover can see. */
export function dragCarriesFiles(data: DataTransfer | null): boolean {
  return !!data && Array.from(data.types).includes('Files');
}

/**
 * The image a paste carries, or none when it should stay a text paste.
 *
 * TEXT WINS WHEN BOTH ARE THERE. A screenshot is an image alone; copying cells from a spreadsheet
 * or a slide puts plain text AND a picture of it on the clipboard, and the person meant the text.
 * Uploading the picture there would replace a paste that works today with one nobody asked for.
 */
export function pastedImage(data: DataTransfer | null): File | null {
  if (!data) return null;
  if (Array.from(data.types).includes('text/plain')) return null;

  for (const item of Array.from(data.items)) {
    if (item.kind !== 'file' || !isImage(item.type)) continue;
    const file = item.getAsFile();
    if (file) return file;
  }

  return Array.from(data.files).find((file) => isImage(file.type)) ?? null;
}

/** A keystroke, reduced to what the Alt+V decision reads. */
export interface ImageChordEvent {
  readonly type: string;
  readonly key: string;
  readonly code: string;
  readonly altKey: boolean;
  readonly shiftKey: boolean;
  readonly ctrlKey: boolean;
  readonly metaKey: boolean;
}

const AsciiLetter = /^[A-Za-z]$/;

/**
 * Alt+V, on keydown only, BY `key`: the V the layout types, not the key where QWERTY has its V. On
 * Dvorak that key types k, and Alt+K is the CLI's, not the page's.
 *
 * `code` decides only when `key` is not an ASCII letter: a Mac's Option+V composes "√", and a
 * Cyrillic layout types "м", and in both the V position is the only V there is.
 */
export function isImagePasteChord(event: ImageChordEvent): boolean {
  if (event.type !== 'keydown' || !event.altKey || event.ctrlKey || event.metaKey) return false;
  if (AsciiLetter.test(event.key)) return event.key === 'v' || event.key === 'V';
  return event.code === 'KeyV';
}

/** The platforms xterm treats as a Mac, where Option composes a character rather than acting as Meta. */
export function xtermSeesMac(platform: string): boolean {
  return ['Macintosh', 'MacIntel', 'MacPPC', 'Mac68K'].includes(platform);
}

/**
 * THE BYTES XTERM WOULD HAVE SENT FOR THAT ALT+V, for when the clipboard holds no image.
 *
 * Off a Mac, xterm sends Alt+<letter> as ESC and the letter of the key - V - lower case unless
 * Shift is held, whatever the layout typed. On a Mac it sends nothing for the keydown: Option
 * composes a character ("√"), the browser types it, and that character is what the CLI received -
 * so that is what is forwarded. A dead key composes nothing yet, and nothing is forwarded.
 */
export function imagePasteChordBytes(event: Pick<ImageChordEvent, 'key' | 'shiftKey'>, onMac: boolean): string {
  if (!onMac) return `\x1b${event.shiftKey ? 'V' : 'v'}`;
  return [...event.key].length === 1 ? event.key : '';
}

/** The part of `navigator.clipboard` this reads; absent on browsers that lack it. */
export interface ImageClipboard {
  read?: () => Promise<readonly { types: readonly string[]; getType: (type: string) => Promise<Blob> }[]>;
}

/**
 * The image on the system clipboard, or null for no image, no API, or no permission - the three
 * are the same answer to the caller, which forwards the key. Never throws.
 */
export async function clipboardImage(clipboard: ImageClipboard | undefined): Promise<File | null> {
  if (!clipboard || typeof clipboard.read !== 'function') return null;

  try {
    for (const item of await clipboard.read()) {
      // Text wins here too, for the reason `pastedImage` gives.
      if (item.types.includes('text/plain')) return null;

      const type = item.types.find(isImage);
      if (!type) continue;

      const blob = await item.getType(type);
      return new File([blob], `clipboard.${type.slice('image/'.length)}`, { type });
    }
  } catch {
    // Denied, or no focus, or an unreadable item. Not an error to show: the key goes through.
  }

  return null;
}
