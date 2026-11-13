/**
 * A team's question to the person, as Markdown to show. A question already written in Markdown (lines that are list
 * items or a table) is shown as it is. One written as a single paragraph with inline numbered items, "(1) ... (2) ...",
 * has them put in a table, one item per row: the text before the first item stays above the table, and the sentences
 * after the last item's first sentence go below it.
 */
export function questionMarkdown(text: string): string {
  const t = (text ?? '').trim();
  if (/\n\s*([-*+]|\d+[.)])\s+\S/.test(t) || /\n\s*\|.*\|/.test(t)) return t;

  const markers = [...t.matchAll(/\((\d{1,2})\)\s+/g)];
  // Only a run numbered 1, 2, 3... counts (so "(see 2)" or a lone "(1)" stays prose).
  const run: RegExpMatchArray[] = [];
  for (const m of markers) if (Number(m[1]) === run.length + 1) run.push(m);
  if (run.length < 2) return t;

  const intro = t.slice(0, run[0].index).trim();
  const items: string[] = [];
  let outro = '';
  run.forEach((m, i) => {
    const start = m.index! + m[0].length;
    let body = t.slice(start, i + 1 < run.length ? run[i + 1].index : undefined).trim();
    if (i === run.length - 1) {
      // The last item ends at its first sentence; what follows is the question's own text.
      const end = body.search(/[.?!]\s+(?=[A-Z])/);
      if (end >= 0) {
        outro = body.slice(end + 1).trim();
        body = body.slice(0, end + 1);
      }
    }
    items.push(body.replace(/[;,]\s*(and|or)?\s*$/i, '').trim());
  });

  const cell = (s: string) => s.replace(/\|/g, '\\|').replace(/\s+/g, ' ').trim();
  const split = items.map((s) => {
    const c = s.indexOf(':');
    return c > 0 && c <= 60 ? [s.slice(0, c), s.slice(c + 1)] : null;
  });
  const labelled = split.filter(Boolean).length >= Math.ceil(items.length * 0.6);
  const rows = items.map((s, i) => {
    const parts = labelled ? split[i] ?? [s, ''] : [s];
    return `| ${i + 1} | ${parts.map(cell).join(' | ')} |`;
  });
  const table = [labelled ? '| # | Item | Details |' : '| # | Item |', labelled ? '|---|---|---|' : '|---|---|', ...rows].join('\n');
  return [intro, table, outro].filter((p) => p.length > 0).join('\n\n');
}

/** Whether a question asks for the person's approval (offer an Approve button). */
export function asksForApproval(text: string): boolean {
  return /\b(approve|approval|ok to|okay to|go ahead|agree with|accept all|confirm|sign off|green.?light|may i|can i proceed|shall i)\b/i.test(text ?? '')
    && questionOptions(text).length === 0;
}

/**
 * The options of a question that asks the person to choose ("Which one? 1. ... 2. ..."), each with a short label for
 * its button; none when the question is not a choice. Numbered lines ("1. **Allow network access.** You allow...")
 * and inline runs ("(1) ... (2) ...") both count; the label is the bold part, or the item's first words.
 */
export function questionOptions(text: string): { n: number; label: string; text: string }[] {
  const t = (text ?? '').trim();
  // A choice says so: it asks which, how to go on, or to reply with a number. A plan put up for approval is not one.
  const choosing = /\b(which|choose|pick|one of|options?|either|prefer|do you want|how (should|do) (we|i) (proceed|go on|continue)|what should (we|i) do|reply( with)? \(?\d)/i.test(t);
  if (!choosing) return [];
  // Proposals put up for approval as a set ("Reply "approve" to accept all four, or say which to change") are not a
  // choice either, even though they say "which".
  if (/\b(reply|answer|say)\s+["'“]?(approve|yes|ok|agreed?)["'”]?\s+to\b|\b(agree|go along) with (these|the|all|this)\b|\baccept (all|both|these|them)\b/i.test(t)) return [];
  let items = [...t.matchAll(/^\s*(\d{1,2})[.)]\s+(.+)$/gm)].map((m) => ({ n: Number(m[1]), text: m[2].trim() }));
  if (items.length < 2) {
    items = [];
    const run = [...t.matchAll(/\((\d{1,2})\)\s+/g)];
    run.forEach((m, i) => {
      if (Number(m[1]) !== items.length + 1) return;
      const end = i + 1 < run.length ? run[i + 1].index : undefined;
      items.push({ n: Number(m[1]), text: t.slice(m.index! + m[0].length, end).trim().replace(/[;,]\s*(or)?\s*$/i, '') });
    });
  }
  if (items.length < 2 || items.length > 6 || items.some((x, i) => x.n !== i + 1)) return [];
  // Numbered QUESTIONS ("1. What is the URL? 2. Which is right?") each need an answer: they are not options to pick
  // one of. So are items the person is asked to fill in ("reply with the URL and your answers").
  if (items.filter((x) => x.text.includes('?')).length * 2 >= items.length) return [];
  if (/\breply with (the|your) (answers?|url|values?|details)\b|\byour answers to\b|\banswers? (to|for) (each|all|them)\b/i.test(t)) return [];
  return items.map((x) => {
    const bold = x.text.match(/\*\*(.+?)\*\*/)?.[1];
    const words = (bold ?? x.text.split(/[.:;—–-]\s/)[0]).replace(/[*_`]/g, '').trim();
    return { n: x.n, label: words.length > 48 ? words.slice(0, 47) + '…' : words.replace(/\.$/, ''), text: x.text };
  });
}
