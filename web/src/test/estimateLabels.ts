/**
 * WHAT THE COST SURFACES MAY CALL THEIR FIGURE. Agent hours times the instance's current rate is an
 * estimate of what execution cost - never money paid - so the Teams list, its summary tiles, the team
 * page's tile and the dialog it opens never title it Spend, Spent or Cost alone, and never say
 * `agent h` for Agent Hours. The per-workflow token budget is a different figure and keeps its words.
 */

/** Spend or spent, as a word, in any case. */
export const SpendWord = /\bspen[dt]\b/i;

/** A title that is the bare word Cost (`Cost`, `Cost · 8 weeks`, `Cost - last 8 weeks`). */
export const CostAlone = /^\s*Cost\b/;

/** The old short form of Agent Hours. */
export const AgentHShort = /\bagent h\b/i;

/** Every text that names the estimate wrongly: a spend word, `agent h`, or a title of Cost alone. */
export function misnamed(texts: readonly (string | null | undefined)[], { titles = false } = {}): string[] {
  return texts
    .map((t) => (t ?? '').replace(/\s+/g, ' ').trim())
    .filter((t) => SpendWord.test(t) || AgentHShort.test(t) || (titles && CostAlone.test(t)));
}

/** What a person reads off an element: its text, and every `aria-label` and `title` inside it. */
export function readOff(root: Element | null | undefined): string[] {
  if (!root) return [];
  const attributes = [root, ...root.querySelectorAll('[aria-label], [title]')]
    .flatMap((e) => [e.getAttribute('aria-label'), e.getAttribute('title')])
    .filter((t): t is string => t !== null);
  return [root.textContent ?? '', ...attributes];
}
