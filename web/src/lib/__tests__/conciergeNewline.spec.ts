import { describe, expect, it } from 'vitest';

import { conciergeNewline } from '../conciergeNewline';

/**
 * WHAT THIS CAN PROVE AND WHAT IT CANNOT.
 *
 * Whether a byte sequence makes a given CLI insert a newline is a fact about that CLI, and no unit
 * test can establish it. What these pin is the DECISION: that one sequence goes to every preset,
 * and that it is `ESC CR`.
 *
 * The evidence behind that is in `conciergeNewline`'s own comment - all four presets measured in a
 * browser, pressing Alt+Enter and reading both the socket and the CLI's input box. These tests
 * exist so the decision cannot drift without someone deleting an assertion that says why.
 */
describe('conciergeNewline', () => {
  it('is ESC CR — the encoding of Alt+Enter', () => {
    expect(conciergeNewline()).toBe('\x1b\r');
  });

  /**
   * THE REGRESSION TEST FOR THE SHAPE, NOT JUST THE VALUE.
   *
   * A bracketed paste (`ESC[200~ CR ESC[201~`), sent when the application has enabled
   * bracketed-paste mode, was measured SUBMITTING in `grok` and `copilot`: a line break between paste markers reads as "the user pasted an empty line", and a
   * reasonable line editor accepts it.
   *
   * A mode branch cannot be added without this failing, and if one is added it must be because
   * somebody measured a preset that needs it - not because the reasoning looked sound.
   */
  it('never sends a bracketed paste, which submits in at least two presets', () => {
    expect(conciergeNewline()).not.toContain('\x1b[200~');
    expect(conciergeNewline()).not.toContain('\x1b[201~');
  });

  /** LF was tried against `grok` and did nothing. CR is what Alt+Enter carries. */
  it('sends CR rather than LF', () => {
    expect(conciergeNewline()).toContain('\r');
    expect(conciergeNewline()).not.toContain('\n');
  });

  /** One answer for every preset. The function takes no argument precisely so it cannot vary. */
  it('takes no argument, so it cannot vary by preset or by mode', () => {
    expect(conciergeNewline.length).toBe(0);
    expect(conciergeNewline()).toBe(conciergeNewline());
  });
});
