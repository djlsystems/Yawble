/**
 * WHAT SHIFT+ENTER SENDS A CONCIERGE: `ESC CR`, FOR EVERY PRESET, MEASURED.
 *
 * This is Alt+Enter as a terminal encodes it, and it is what every CLI that can drive a Concierge
 * actually reads. `grok` says so on its own status bar - *Alt+Enter : newline* - and the other three
 * behave the same way.
 *
 * MEASURED against a live host, by pressing Alt+Enter in the panel and watching both
 * the bytes on the socket and the CLI's own input box:
 *
 *   preset    sent        result
 *   claude    ESC CR      two lines
 *   codex     ESC CR      two lines
 *   copilot   ESC CR      two lines
 *   grok      ESC CR      two lines
 *
 * THERE IS NO BRACKETED-PASTE MODE BRANCH. It is tempting to send `ESC[200~ CR ESC[201~` when the
 * application has enabled bracketed-paste mode, on the theory that `ESC CR` is claude's convention
 * alone. It is not: all four read it, and the bracketed form is actively WORSE for them - a line
 * break between paste markers says *the user pasted an empty line*, and a reasonable line editor
 * SUBMITS that. Measured: `grok` and `copilot` both submit on the bracketed form and both insert a
 * newline on `ESC CR`. What a CLI accepts here is not a function of what it has advertised about
 * pasting.
 *
 * DO NOT INTRODUCE A MODE BRANCH without measuring the preset it is for.
 *
 * NO ARGUMENT, DELIBERATELY. A parameter nothing varies on is an invitation to make it vary.
 */
export function conciergeNewline(): string {
  // CR, NOT LF. This is Alt+Enter's encoding - ESC followed by the same byte a bare Enter sends -
  // and it is the pair every one of these CLIs was measured reading. LF does nothing in `grok`.
  return '\x1b\r';
}
