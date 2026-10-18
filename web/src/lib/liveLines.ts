/**
 * WHAT KIND OF STEP A LIVE-VIEW LINE IS, as the Host says: every line comes as
 * `<when>` TAB `<kind>` TAB `<line>`, the kind one of `tool`, `result`, `text`, `user` or `note`, or
 * `raw` (a transcript line the Host could not parse) or `notice` (the Host's own sentence at the end
 * of a stream), both shown as text. The Host classifies each line by one rule, the same one its
 * recorded agent steps use, so the view and the Monitor never disagree; this only splits the line
 * into the chip and the words beside it.
 *
 * The line keeps the shape the server renders: `Bash: <command>`, `Read <path>`, `MCP: <tool>`,
 * `<Tool>: <short input>`, a tool result's first line and size, `User: <first line>`,
 * `Attachment: <name or first line>`, or the agent's own text. A line with no kind the Host names
 * reads as text, which is harmless.
 */
export type LiveLineKind = 'tool' | 'result' | 'text' | 'user' | 'note';

const Kinds: readonly LiveLineKind[] = ['tool', 'result', 'text', 'user', 'note'];
/** Kinds the Host sends that are no step: shown as plain text. */
const AsText: readonly string[] = ['raw', 'notice'];

export interface LiveLine {
  /** When the event happened, or null when the server sent no time for it. */
  at: Date | null;
  kind: LiveLineKind;
  /** The chip beside the line: the tool's name, `User`, `Note`, or empty for the agent's text. */
  label: string;
  /** The line without its label. */
  text: string;
}

/**
 * One line off the stream: `<when>` TAB `<kind>` TAB `<line>`, `<when>` being the event's time in
 * UTC or empty. A first field that is not a time, or no tab at all, is all line, with no time and no
 * kind; a second field that is not a kind is part of the line.
 */
export function parseLiveLine(raw: string): LiveLine {
  const tab = raw.indexOf('\t');
  if (tab >= 0) {
    const when = raw.slice(0, tab);
    const at = when === '' ? null : new Date(when);
    if (at === null || !Number.isNaN(at.getTime())) {
      const rest = raw.slice(tab + 1);
      const second = rest.indexOf('\t');
      const kind = second >= 0 ? rest.slice(0, second) : null;
      if (kind !== null && Kinds.includes(kind as LiveLineKind)) return { at, ...splitLiveLine(kind as LiveLineKind, rest.slice(second + 1)) };
      if (kind !== null && AsText.includes(kind)) return { at, ...splitLiveLine('text', rest.slice(second + 1)) };
      return { at, ...splitLiveLine('text', rest) };
    }
  }

  return { at: null, ...splitLiveLine('text', raw) };
}

/** `13:34:41` in the viewer's own time zone. */
export function liveLineTime(at: Date): string {
  return at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}

/** A line of the kind the Host gave it, as its chip and its words. */
export function splitLiveLine(kind: LiveLineKind, line: string): Omit<LiveLine, 'at'> {
  switch (kind) {
    case 'tool': {
      // `Read <path>` has no colon; every other tool is `<Tool>: <input>`.
      if (line.startsWith('Read ')) return { kind, label: 'Read', text: line.slice(5) };
      const colon = line.indexOf(': ');
      return colon > 0 ? { kind, label: line.slice(0, colon), text: line.slice(colon + 2) } : { kind, label: '', text: line };
    }
    case 'user':
      return { kind, label: 'User', text: line.startsWith('User: ') ? line.slice(6) : line };
    case 'note':
      return { kind, label: 'Note', text: line.startsWith('Attachment: ') ? line.slice(12) : line };
    default:
      return { kind, label: '', text: line };
  }
}
