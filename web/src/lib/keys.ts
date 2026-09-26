/**
 * The keys a soft keyboard does not have.
 *
 * Without these an agent TUI cannot be driven from a phone at all: no interrupt, no completion, no
 * history. This is the whole difference between a console that is visible on a phone and one that
 * can be used from it.
 */
export const ControlKeys = {
  Esc: '\x1b',
  Tab: '\t',

  // CSI sequences, not characters. A TUI reads history and moves the cursor with these.
  Up: '\x1b[A',
  Down: '\x1b[B',
  Right: '\x1b[C',
  Left: '\x1b[D',
} as const;

/**
 * A composed line as the bytes a terminal expects: the text, then a carriage return.
 *
 * CR and not LF. Enter on a real keyboard sends \r, and a TUI reading its prompt is waiting for that
 * — \n is a different key and prompts that distinguish them will simply not submit.
 *
 * Returns null for nothing worth sending, so a stray tap on Send cannot enter a bare newline at the
 * agent's prompt. Whitespace is trimmed off the ENDS only: dictation habitually leaves a trailing
 * space, while the spaces between words are the message.
 */
export function composedLine(text: string): string | null {
  const trimmed = text.trim();

  return trimmed.length > 0 ? `${trimmed}\r` : null;
}

/**
 * Ctrl-<letter> as the byte a terminal expects: the letter's position in the alphabet.
 *
 * C is the third letter, so Ctrl-C is 0x03 — the interrupt, and the single most important key on
 * the bar. A non-letter has no control form and is passed through unchanged rather than being
 * mangled into a random byte.
 */
export function ctrlSequence(character: string): string {
  const position = character.toLowerCase().charCodeAt(0) - 96; // 'a' is 97

  return position >= 1 && position <= 26 ? String.fromCharCode(position) : character;
}
