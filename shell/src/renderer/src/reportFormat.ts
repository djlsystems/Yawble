/**
 * An agent's report as the card shows it. The core prefixes what a report is ("handed back: ", "declared complete: ",
 * "blocked: ", "needs decision: "); that becomes a label. A long report written as one line (older agents were asked for
 * "one short sentence" and crammed everything in) gets its first sentence as its own paragraph, the summary; a report
 * that already has line breaks is left exactly as written.
 */
export type ReportKind = 'handback' | 'complete' | 'blocked' | 'decision' | 'waiting';
export interface Report { kind: ReportKind | null; label: string | null; body: string }

const Prefixes: [string, ReportKind, string][] = [
  ['handed back: ', 'handback', 'Handed back'],
  ['declared complete: ', 'complete', 'Declared complete'],
  ['blocked: ', 'blocked', 'Blocked'],
  ['needs decision: ', 'decision', 'Needs decision'],
  ['waiting on you: ', 'waiting', 'Waiting on you'],
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
  const end = /[.!?](?=\s+[A-Z0-9(`*"'#[])/g;
  for (let m = end.exec(body); m; m = end.exec(body)) {
    const i = m.index + 1;
    if (i < 20) continue;
    if (i > 300) break;
    return body.slice(0, i) + '\n\n' + body.slice(i).trimStart();
  }
  return body;
}

/** The tile's report when the member waits on the person. */
export const WaitingLabel = 'Waiting on you';

/**
 * A member's report as its tile shows it: while it has an open decision (decisionId, or attention "decision"),
 * the chip says "Waiting on you" whatever it said before ("Declared complete" / "Nothing worth keeping" from an earlier
 * deliverable_complete is not its state any more); the body is its question's words when its last report was that
 * question, else a line that points to the decision card. Otherwise splitReport.
 */
export function memberReport(card: { statusLine?: string | null; attention?: string | null; decisionId?: number | null }): Report & { decisionId: number | null } {
  const said = splitReport(card.statusLine ?? '');
  const waiting = card.decisionId != null || card.attention === 'decision';
  if (!waiting) return { ...said, decisionId: null };
  const body = said.kind === 'decision' || said.kind === 'waiting' ? said.body : 'A decision is waiting for your answer on the team page.';
  return { kind: 'waiting', label: WaitingLabel, body, decisionId: card.decisionId ?? null };
}