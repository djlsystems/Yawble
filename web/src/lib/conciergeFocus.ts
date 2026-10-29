/**
 * THE CONCIERGE AND A DIALOG OPEN BEHIND IT: WHO HAS THE KEYBOARD.
 *
 * The panel is teleported to `<body>` and drawn above every dialog (z-index 7000), but a Quasar
 * dialog with a backdrop also listens for `focusin` on `<body>` and, whenever focus lands outside
 * it, pulls focus back into itself. So with Backlog, Marketplace, Documents or Git open, a click on
 * the terminal focused xterm's textarea for an instant and the dialog took it straight back: what
 * the person typed went to the dialog, never to the Concierge. Quasar also closes the top dialog on
 * an Escape `keyup` heard on `window`, so an Escape meant for the agent closed the dialog behind.
 *
 * THE RULES, all three of them the panel's:
 *
 * 1. Focus that lands inside the panel stays there. The panel stops its own `focusin` before it
 *    reaches `<body>`, so the dialog's trap never hears about it (`keepsFocus`).
 * 2. An Escape typed inside the panel is the panel's - the terminal's, to interrupt the agent - and
 *    is not also the dialog's (`keepsEscape`).
 * 3. Closing the panel hands focus back to what had it before, or else to the dialog on top, so it
 *    is never left on a removed node or on `<body>` (`focusAfterConcierge`).
 *
 * Clicking back into the dialog needs nothing here: Quasar's dialog inner is `tabindex="-1"`, the
 * click focuses it, and from then on its own Escape and Tab handling run exactly as before.
 */

/** A `focusin` inside the panel, stopped before the dialog's trap on `<body>` can see it. */
export function keepsFocus(event: FocusEvent): void {
  event.stopPropagation();
}

/** An Escape `keyup` inside the panel, stopped before Quasar's dialog listener on `window`. */
export function keepsEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape') event.stopPropagation();
}

/** The dialog on top, as Quasar draws it: its focusable inner box, last in the document. */
export function topDialog(doc: Document = document): HTMLElement | null {
  const inners = doc.querySelectorAll<HTMLElement>('.q-dialog .q-dialog__inner');
  return inners.length > 0 ? inners[inners.length - 1]! : null;
}

/**
 * Where focus goes once the panel closes: what had it before the panel opened, while that is still
 * in the page and not part of the panel, otherwise the top dialog, otherwise nowhere in particular.
 */
export function focusAfterConcierge(previous: Element | null, doc: Document = document): HTMLElement | null {
  if (
    previous instanceof HTMLElement &&
    previous !== doc.body &&
    previous.isConnected &&
    previous.closest('.concierge-shell') === null
  ) {
    return previous;
  }

  return topDialog(doc);
}
