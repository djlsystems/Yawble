/**
 * An agent run's terminal output, for a person to read. A headless run prints one JSON event per line (Claude Code's
 * stream-json; other CLIs' JSON events are read generically); each becomes an entry: what the agent said, what it
 * thought, each tool it called and what came back, how the run ended. A line that is not JSON is kept as text.
 */
export type EntryKind = 'start' | 'text' | 'thinking' | 'tool' | 'result' | 'end' | 'note' | 'plain';

export interface Entry {
  kind: EntryKind;
  /** One line: who or what ("PowerShell", "bus: tell", "Run finished"). */
  title: string;
  /** The body: Markdown for 'text', plain text otherwise (already shortened). */
  body: string;
  error?: boolean;
}

const MaxResultLines = 14;
const MaxResultChars = 2000;

export function parseTranscript(text: string): Entry[] {
  const entries: Entry[] = [];
  const plain: string[] = [];
  const flushPlain = () => {
    const t = plain.join('\n').trim();
    if (t) entries.push({ kind: 'plain', title: '', body: t });
    plain.length = 0;
  };
  for (const line of (text ?? '').split('\n')) {
    const t = line.trim();
    if (t.startsWith('{') && t.endsWith('}')) {
      let event: any;
      try { event = JSON.parse(t); } catch { event = null; }
      if (event && typeof event === 'object') {
        flushPlain();
        entries.push(...fromEvent(event));
        continue;
      }
    }
    plain.push(line);
  }
  flushPlain();
  return merge(entries);
}

/** Consecutive text from the same message reads as one block. */
function merge(entries: Entry[]): Entry[] {
  const out: Entry[] = [];
  for (const e of entries) {
    const last = out[out.length - 1];
    if (last && e.kind === 'text' && last.kind === 'text') last.body += '\n\n' + e.body;
    else out.push({ ...e });
  }
  return out;
}

function fromEvent(e: any): Entry[] {
  // ---- Claude Code stream-json
  if (e.type === 'system' && e.subtype === 'init') {
    return [{ kind: 'start', title: 'Session started', body: [e.model && `model ${e.model}`, e.cwd && `in ${e.cwd}`].filter(Boolean).join(' · ') }];
  }
  if (e.type === 'system') return []; // hooks, status, compaction: not for reading
  if (e.type === 'assistant' && Array.isArray(e.message?.content)) return e.message.content.flatMap(fromAssistantPart);
  if (e.type === 'user' && Array.isArray(e.message?.content)) return e.message.content.flatMap(fromUserPart);
  if (e.type === 'result') {
    const secs = typeof e.duration_ms === 'number' ? `${Math.round(e.duration_ms / 1000)} s` : null;
    const cost = typeof e.total_cost_usd === 'number' ? `$${e.total_cost_usd.toFixed(2)}` : null;
    const turns = typeof e.num_turns === 'number' ? `${e.num_turns} turns` : null;
    return [{
      kind: 'end', title: e.is_error ? 'Run ended with an error' : 'Run finished', error: !!e.is_error,
      body: [secs, turns, cost].filter(Boolean).join(' · '),
    }];
  }
  if (e.type === 'tool_progress' || e.type === 'stream_event') return [];
  // ---- anything else (Codex, Grok, Copilot, the app's own notes): its text if it has one
  const text = firstText(e);
  if (text) return [{ kind: 'note', title: label(e), body: shorten(text) }];
  return [];
}

function fromAssistantPart(p: any): Entry[] {
  if (p?.type === 'text' && p.text?.trim()) return [{ kind: 'text', title: '', body: p.text.trim() }];
  if (p?.type === 'thinking' && (p.thinking ?? '').trim()) return [{ kind: 'thinking', title: 'Thinking', body: p.thinking.trim() }];
  if (p?.type === 'tool_use') return [{ kind: 'tool', title: toolName(p.name), body: toolSummary(p.name, p.input) }];
  return [];
}

function fromUserPart(p: any): Entry[] {
  if (p?.type !== 'tool_result') return [];
  const content = typeof p.content === 'string' ? p.content
    : Array.isArray(p.content) ? p.content.map((c: any) => (c?.type === 'text' ? c.text : c?.type === 'image' ? '[image]' : '')).join('\n') : '';
  return [{ kind: 'result', title: p.is_error ? 'Error' : 'Result', body: shorten(content), error: !!p.is_error }];
}

/** "mcp__bus__tell" reads "bus: tell"; built-in tools keep their names. */
export function toolName(name: string): string {
  const m = /^mcp__([^_]+(?:_[^_]+)*)__(.+)$/.exec(name ?? '');
  return m ? `${m[1]}: ${m[2]}` : name ?? 'tool';
}

/** The one thing a person wants to see of a tool call: the command, the file, the pattern, the message. */
export function toolSummary(name: string, input: any): string {
  if (!input || typeof input !== 'object') return '';
  const pick = input.command ?? input.file_path ?? input.path ?? input.pattern ?? input.url ?? input.query
    ?? input.instruction ?? input.text ?? input.question ?? input.status ?? input.delivered ?? input.reason
    ?? input.description ?? input.prompt ?? input.name;
  const s = typeof pick === 'string' ? pick : JSON.stringify(input);
  const extra = input.member ? ` → ${input.member}` : '';
  return shorten(s, 6, 600) + extra;
}

function firstText(e: any): string | null {
  for (const k of ['text', 'message', 'content', 'output', 'msg']) {
    const v = e?.[k] ?? e?.item?.[k] ?? e?.data?.[k];
    if (typeof v === 'string' && v.trim()) return v.trim();
  }
  return null;
}

function label(e: any): string {
  return [e?.type, e?.item?.type, e?.subtype].filter((x) => typeof x === 'string').join(' · ') || 'event';
}

export function shorten(s: string, maxLines = MaxResultLines, maxChars = MaxResultChars): string {
  let t = (s ?? '').replace(/\r/g, '').trim();
  const lines = t.split('\n');
  let cut = false;
  if (lines.length > maxLines) { t = lines.slice(0, maxLines).join('\n'); cut = true; }
  if (t.length > maxChars) { t = t.slice(0, maxChars); cut = true; }
  return cut ? `${t}\n… (${lines.length} lines; switch to Raw for all of it)` : t;
}
