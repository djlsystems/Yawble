/**
 * WHAT THE CONCIERGE'S TERMINAL DOES WITH A KEYSTROKE, IN A PLACE A SPEC CAN REACH.
 *
 * `conciergeNewline` beside this file already decided WHICH BYTES Shift+Enter sends. The rest of
 * the decision - which chord counts, whether to swallow the key, and whether the browser's default
 * action has to be cancelled - lives here rather than in a closure inside `ConciergePanel.vue`,
 * because the decision is better tested as a pure function.
 *
 * THE FAILURE THIS PREVENTS, MEASURED AGAINST A REAL CONCIERGE. Without the cancellation, one
 * Shift+Enter press puts TWO messages on the socket: the escape sequence, and then a bare CR. The
 * CR is what makes the line submit -
 * `\x1b\r` for a terminal with bracketed paste off, `\x1b[200~\r\x1b[201~` with it on, and in both
 * cases a `\r` behind it. The CLI never gets a chance to treat the sequence as a newline, because a
 * submit arrives in the same keystroke.
 *
 * WHY `return false` WAS NOT ENOUGH, WHICH IS THE WHOLE POINT OF THIS FILE. Returning false from
 * xterm's `attachCustomKeyEventHandler` stops xterm HANDLING the event - and in doing so stops
 * xterm calling `preventDefault()` on it. The browser's default action then proceeds and delivers
 * the key to xterm's hidden textarea, whose own input path emits the CR. So the handler has to
 * cancel the default itself. Confirmed live: with a capture-phase `preventDefault()` installed, the
 * socket carried the escape sequence ALONE and nothing was submitted.
 *
 * BOTH HALVES ARE LOAD-BEARING AND THEY ARE NOT THE SAME HALF.
 *  - `preventDefault` stops the browser delivering the key to the textarea - the stray CR.
 *  - `swallow` (the handler returning false) stops xterm ALSO acting on the key itself.
 * Dropping either one reinstates a bug.
 */

/** What a keystroke reduces to, for the only decision this module makes. */
export interface ConciergeKeyEvent {
  readonly type: string;
  readonly key: string;
  readonly shiftKey: boolean;
}

/** What the panel must do about a keystroke. */
export interface ConciergeKeyAction {
  /**
   * Bytes to write to the PTY, or null to send nothing.
   *
   * Null is not the same as an empty string: an empty string would be a write of no bytes, which is
   * a thing this could plausibly want to do one day and is not what is meant here.
   */
  readonly send: string | null;

  /** Whether xterm must be stopped from handling this key itself. */
  readonly swallow: boolean;

  /** Whether the browser's default action must be cancelled - see the note above. */
  readonly preventDefault: boolean;
}

const PASS_THROUGH: ConciergeKeyAction = {
  send: null,
  swallow: false,
  preventDefault: false,
};

/**
 * Decide what to do with one keystroke.
 *
 * @param event the keystroke, reduced to the three fields that matter.
 * @param newlineSequence what Shift+Enter should send, from `conciergeNewline`. Passed in rather
 *   than computed here because it depends on the terminal's CURRENT bracketed-paste mode, which an
 *   application turns on and off as its line editor comes and goes - so it must be read at the
 *   keystroke and cannot be a constant this module holds.
 */
export function conciergeKeyAction(
  event: ConciergeKeyEvent,
  newlineSequence: string,
): ConciergeKeyAction {
  // KEYDOWN ONLY. xterm reports keypress and keyup through the same handler, and acting on more
  // than one of them would send the sequence two or three times for one press.
  if (event.type !== 'keydown') return PASS_THROUGH;

  if (event.key === 'Enter' && event.shiftKey) {
    return { send: newlineSequence, swallow: true, preventDefault: true };
  }

  // EVERYTHING ELSE PASSES THROUGH UNTOUCHED, INCLUDING A BARE ENTER. A bare Enter must still
  // submit, and it must do so by xterm's own ordinary path rather than by anything here - which is
  // why this returns the pass-through value rather than describing what Enter does.
  return PASS_THROUGH;
}
