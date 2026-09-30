/**
 * A NUMBER BOX DOES NOT CHANGE ON THE MOUSE WHEEL. A browser steps a focused `<input type="number">`
 * when the wheel turns over it, so a person scrolling a form past the box they just typed in changes
 * the value without seeing it - JobTracker's `salaryMax` was saved as -2 that way (B001W). One
 * listener on the document, installed once at boot (`boot/numberWheel.ts`), covers every number box
 * in every form, present and future; no form opts in.
 *
 * Only a FOCUSED number box is held: an unfocused one does not step, and the page scrolls past it as
 * usual.
 */
export function onNumberWheel(event: WheelEvent): void {
  const target = event.target;
  if (!(target instanceof HTMLInputElement) || target.type !== 'number') return;
  if (target.ownerDocument.activeElement !== target) return;

  event.preventDefault();
}

/** Installs the guard on `doc`; returns the function that removes it. */
export function guardNumberWheel(doc: Document): () => void {
  // `passive: false`: a passive listener may not prevent the step. Capture, so nothing below can stop it first.
  const options: AddEventListenerOptions = { capture: true, passive: false };
  doc.addEventListener('wheel', onNumberWheel, options);
  return () => doc.removeEventListener('wheel', onNumberWheel, options);
}
