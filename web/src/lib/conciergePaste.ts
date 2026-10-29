/**
 * PASTED TEXT, MADE SAFE FOR THE AGENT'S PROMPT BEFORE XTERM SENDS IT.
 *
 * On Fedora a paste into the Concierge needed two Enters: the first only made the agent's CLI say it
 * had removed an invalid character, the second sent. Nothing between the page and the CLI says
 * that - the Worker's PTY bridge forwards the bytes as they are - so the CLI is rejecting
 * something the clipboard carried. Linux clipboards hand over text from other terminals and from
 * web pages with characters a prompt cannot hold: lone CRs, a whole bracketed paste already wrapped
 * in its own `ESC[200~ ... ESC[201~` (pasted out of another terminal), NUL and other C0/C1
 * controls, no-break spaces and zero-width characters.
 *
 * WHAT IS KEPT: every newline (CRLF, CR, U+2028 and U+2029 become one LF, which xterm then sends
 * as the CR it always sends for a line break), tabs, and all other Unicode text - including ZWJ
 * and ZWNJ, which hold emoji sequences and several scripts together.
 *
 * WHAT GOES: bracketed-paste markers and any other escape introducer, C0 controls but tab and LF,
 * DEL, C1 controls, ZWSP, WORD JOINER, the BOM and soft hyphen. No-break spaces become spaces.
 */
const BracketMarkers = /\x1b\[20[01]~/g;
const LineBreaks = /\r\n|\r|\u2028|\u2029/g;
const NoBreakSpaces = /[\u00a0\u2007\u202f]/g;
// eslint-disable-next-line no-control-regex
const Unwanted = /[\x00-\x08\x0b-\x1f\x7f-\x9f\u00ad\u200b\u2060\ufeff]/g;

export function normalisePaste(text: string): string {
  return text
    .replace(BracketMarkers, '')
    .replace(LineBreaks, '\n')
    .replace(NoBreakSpaces, ' ')
    .replace(Unwanted, '');
}
