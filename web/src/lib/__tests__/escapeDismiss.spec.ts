import { describe, expect, it } from 'vitest';

import { dismissesPanelOnEscape } from '../escapeDismiss';

describe('dismissesPanelOnEscape', () => {
  it('closes the panel when focus is outside the terminal and no dialog is open', () => {
    expect(dismissesPanelOnEscape({ focusInTerminal: false, dialogOpen: false })).toBe(true);
  });

  it('leaves Escape to the terminal while the terminal has focus', () => {
    // Interrupting a runaway agent matters more than closing a panel, and Escape is how a person
    // does that - in a TUI, in vim, in whatever the agent launched.
    expect(dismissesPanelOnEscape({ focusInTerminal: true, dialogOpen: false })).toBe(false);
  });

  it('does not close the panel underneath a dialog that is taking the same Escape', () => {
    // The panel listens on `document` and a Quasar dialog teleports to `<body>`, so the dialog's
    // focused element is NOT inside the terminal host. Without this rule one Escape would close the
    // dialog AND the panel behind it - reachable only since those dialogs stopped carrying
    // `persistent`, which had been blocking Escape entirely.
    expect(dismissesPanelOnEscape({ focusInTerminal: false, dialogOpen: true })).toBe(false);
  });

  it('is false when both hold, so neither rule depends on the other', () => {
    expect(dismissesPanelOnEscape({ focusInTerminal: true, dialogOpen: true })).toBe(false);
  });
});
