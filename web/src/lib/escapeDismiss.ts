/**
 * Whether an Escape keypress should close the Concierge panel.
 *
 * A pure function in `lib/` because that is still the cleanest testable place for this logic in
 * this project: `ConciergePanel` can be mounted now, and logic left in the component is still logic
 * a test only reaches through the whole panel.
 *
 * TWO RULES, AND THE SECOND IS THE ONE THAT IS EASY TO LOSE.
 *
 * 1. **Escape belongs to the TERMINAL while it has focus.** Interrupting a runaway agent matters
 *    more than closing a panel, and Escape is how a person does that - in a TUI, in vim, in
 *    anything the agent has launched. So focus inside the terminal host means the panel ignores it.
 *
 * 2. **A dialog open ABOVE the panel takes Escape first, and the panel must not also act on it.**
 *    The panel listens on `document`, and a Quasar dialog teleports to `<body>` - so its focused
 *    element is NOT inside the terminal host, and rule 1 alone would let one Escape close the
 *    dialog *and* the panel behind it. That became reachable the moment those dialogs stopped
 *    carrying `persistent`: while they blocked Escape entirely there was no keypress to share.
 */
export interface EscapeContext {
  /** Focus is inside the terminal host element. */
  focusInTerminal: boolean;

  /** Any Quasar dialog is currently mounted above the panel. */
  dialogOpen: boolean;
}

export function dismissesPanelOnEscape(context: EscapeContext): boolean {
  if (context.focusInTerminal) return false;
  if (context.dialogOpen) return false;

  return true;
}

/**
 * Is a Quasar dialog on screen?
 *
 * Asks the DOM rather than tracking a count, because the panel does not own every dialog that can
 * appear over it - the Settings dialog and the reload confirmation are its own, but a dialog
 * opened from anywhere else lands in the same place. A query answers for all of them and cannot
 * drift from the truth the way a counter incremented in one component would.
 *
 * `.q-dialog` is Quasar's own root class on the teleported node. Narrower than checking
 * `document.body` for a scroll-lock class, which is also set by menus and popups that should NOT
 * swallow Escape.
 */
export function aDialogIsOpen(doc: Document = document): boolean {
  return doc.querySelector('.q-dialog') !== null;
}
