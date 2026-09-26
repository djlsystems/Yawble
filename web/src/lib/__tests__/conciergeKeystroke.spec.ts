import { describe, expect, it } from 'vitest';

import { conciergeKeyAction } from '../conciergeKeystroke';
import { conciergeNewline } from '../conciergeNewline';

/**
 * THESE TEST THE DECISION, NOT THE COMPONENT'S SOURCE AS TEXT.
 *
 * A check like `expect(source).toContain('event.shiftKey')` passes with the feature broken: with
 * the sequence wrong, the chord bound to the wrong key, or the handler returning true. The suite
 * can mount components; the answer is still to move the decision out of the component, which is
 * what `conciergeKeystroke.ts` is for, and then to test the decision.
 *
 * WHAT THESE STILL CANNOT PROVE, stated so nobody mistakes a green run for a working feature:
 * whether a given byte sequence makes a given CLI insert a newline is a fact about that CLI. Only
 * pressing the key against it can establish that. These tests pin the decision; the browser pins
 * the outcome.
 */
describe('conciergeKeyAction', () => {
  const anySequence = 'SEQ';

  it('sends the newline sequence for Shift+Enter', () => {
    const action = conciergeKeyAction(
      { type: 'keydown', key: 'Enter', shiftKey: true },
      anySequence,
    );

    expect(action.send).toBe(anySequence);
  });

  /**
   * THE REGRESSION TEST FOR THE STRAY-CR SUBMIT.
   *
   * Returning false from xterm's handler stops xterm handling the key and, in doing so, stops xterm
   * calling preventDefault - so the browser's default action delivers the key to the hidden
   * textarea, which emits a bare CR, and the line submits in the same keystroke. Both halves are
   * required.
   */
  it('swallows the key AND cancels the default action for Shift+Enter', () => {
    const action = conciergeKeyAction(
      { type: 'keydown', key: 'Enter', shiftKey: true },
      anySequence,
    );

    expect(action.swallow).toBe(true);
    expect(action.preventDefault).toBe(true);
  });

  it('leaves a bare Enter entirely alone, so it still submits', () => {
    const action = conciergeKeyAction(
      { type: 'keydown', key: 'Enter', shiftKey: false },
      anySequence,
    );

    expect(action.send).toBeNull();
    expect(action.swallow).toBe(false);
    expect(action.preventDefault).toBe(false);
  });

  it('ignores every key that is not Enter, however it is modified', () => {
    for (const key of ['a', 'Tab', 'Escape', 'ArrowUp', 'Backspace']) {
      const action = conciergeKeyAction({ type: 'keydown', key, shiftKey: true }, anySequence);

      expect(action.send, key).toBeNull();
      expect(action.swallow, key).toBe(false);
    }
  });

  /**
   * xterm reports keypress and keyup through the same handler. Acting on more than keydown would
   * send the sequence two or three times for one press - which reads as a double newline rather
   * than as this rule being missing.
   */
  it('acts on keydown only', () => {
    for (const type of ['keypress', 'keyup']) {
      const action = conciergeKeyAction({ type, key: 'Enter', shiftKey: true }, anySequence);

      expect(action.send, type).toBeNull();
      expect(action.swallow, type).toBe(false);
      expect(action.preventDefault, type).toBe(false);
    }
  });

  /**
   * The sequence is passed IN rather than computed here, so this module stays about the KEYSTROKE
   * and `conciergeNewline` stays the one place the bytes are decided. This pins that the decision
   * carries whichever answer it was handed rather than a constant of its own.
   */
  it('carries whatever conciergeNewline produced', () => {
    const sequence = conciergeNewline();
    const action = conciergeKeyAction({ type: 'keydown', key: 'Enter', shiftKey: true }, sequence);

    expect(action.send).toBe(sequence);
  });
});
