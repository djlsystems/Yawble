/**
 * WHAT KIND OF STEP A LIVE-VIEW LINE IS, read off the shape the server gives every line:
 * `Bash: <command>`, `Read <path>`, `MCP: <tool>`, `<Tool>: <short input>`, a tool result's first
 * line and size, `User: <first line>`, `Attachment: <name or first line>`, or the agent's own text.
 * The stream stays plain text; this only decides how a line is shown.
 *
 * A TOOL IS RECOGNISED BY NAME, NOT BY SHAPE. `Note: ...` in the agent's prose has the same shape as
 * `Grep: ...`, so only the tools named here get the tool styling; an unknown one reads as text,
 * which is harmless.
 */
export type LiveLineKind = 'tool' | 'result' | 'text' | 'user' | 'aside';

export interface LiveLine {
  /** When the event happened, or null when the server sent no time for it. */
  at: Date | null;
  kind: LiveLineKind;
  /** The chip beside the line: the tool's name, `User`, `Note`, or empty for the agent's text. */
  label: string;
  /** The line without its label. */
  text: string;
}

const Tools = new Set([
  'Bash', 'MCP', 'Write', 'Edit', 'MultiEdit', 'NotebookEdit', 'Grep', 'Glob', 'LS', 'Skill',
  'Task', 'Agent', 'TodoWrite', 'ToolSearch', 'WebFetch', 'WebSearch', 'BashOutput', 'KillShell',
]);

/** A tool result: `<first line> (<n> bytes)`, the size always last. */
const ResultSize = / \(\d+ bytes\)$/;

/**
 * One line off the stream: `<when>` TAB `<line>`, `<when>` being the event's time in UTC or empty.
 * A line with no tab, or whose first field is not a time, is all line and has no time.
 */
export function parseLiveLine(raw: string): LiveLine {
  const tab = raw.indexOf('\t');
  if (tab >= 0) {
    const when = raw.slice(0, tab);
    const at = when === '' ? null : new Date(when);
    if (at === null || !Number.isNaN(at.getTime())) return { at, ...classifyLiveLine(raw.slice(tab + 1)) };
  }

  return { at: null, ...classifyLiveLine(raw) };
}

/** `13:34:41` in the viewer's own time zone. */
export function liveLineTime(at: Date): string {
  return at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}

export function classifyLiveLine(line: string): Omit<LiveLine, 'at'> {
  // `Read <path>` has no colon, so the path is what tells it from "Read the spec first." in prose:
  // an absolute path, or one word with a slash in it.
  if (/^Read (\/|~|\S*\/\S*$)/.test(line)) return { kind: 'tool', label: 'Read', text: line.slice(5) };
  if (line.startsWith('User: ')) return { kind: 'user', label: 'User', text: line.slice(6) };
  if (line.startsWith('Attachment: ')) return { kind: 'aside', label: 'Note', text: line.slice(12) };

  const colon = line.indexOf(': ');
  if (colon > 0 && Tools.has(line.slice(0, colon))) {
    return { kind: 'tool', label: line.slice(0, colon), text: line.slice(colon + 2) };
  }

  if (ResultSize.test(line)) return { kind: 'result', label: '', text: line };
  return { kind: 'text', label: '', text: line };
}
