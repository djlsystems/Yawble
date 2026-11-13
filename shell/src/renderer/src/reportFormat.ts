/**
 * An agent's report as the card shows it. The core prefixes what a report is ("handed back: ", "declared complete: ",
 * "blocked: ", "needs decision: "); that becomes a label. A long report written as one line (older agents were asked for
 * "one short sentence" and crammed everything in) gets its first sentence as its own paragraph, the summary; a report
 * that already has line breaks is left exactly as written.
 */
export type ReportKind = 'handback' | 'complete' | 'blocked' | 'decision';
export interface Report { kind: ReportKind | null; label: string | null; body: string }

const Prefixes: [string, ReportKind, string][] = [
  ['handed back: ', 'handback', 'Handed back'],
  ['declared complete: ', 'complete', 'Declared complete'],
  ['blocked: ', 'blocked', 'Blocked'],
  ['needs decision: ', 'decision', 'Needs decision'],
];

export function splitReport(text: string): Report {
  const t = text.trim();
  for (const [prefix, kind, label] of Prefixes)
    if (t.startsWith(prefix)) return { kind, label, body: summaryFirst(t.slice(prefix.length).trim()) };
  return { kind: null, label: null, body: summaryFirst(t) };
}

/** A one-line report longer than a sentence: a paragraph break after its first sentence (within the first 300 chars). */
function summaryFirst(body: string): string {
  if (body.includes('\n') || body.length < 100) return body;
  const end = /[.!?](?=\s+[A-Z0-9(`*"'#\[])/g;
  for (let m = end.exec(body); m; m = end.exec(body)) {
    const i = m.index + 1;
    if (i < 20) continue;
    if (i > 300) break;
    return body.slice(0, i) + '\n\n' + body.slice(i).trimStart();
  }
  return body;
}
