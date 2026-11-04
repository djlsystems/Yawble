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

/** Value words, which never sit near an estimate or a spend figure: value, ROI, return, savings. */
export const ValueWords = /\b(value|roi|return|returns|savings)\b/i;

/**
 * THE SPEND PAGE'S LINT. There, **Actual Spend** is a real figure - confirmed vendor expenses - and the
 * page itself is titled Spend; every other spend word, an estimate titled Cost alone, `agent h`, and any
 * value word is misnamed. `titles` are the page's headings and tile titles, checked for Cost alone.
 */
export function misnamedOnSpend(texts: readonly (string | null | undefined)[], titles: readonly string[] = []): string[] {
  const cleaned = texts.map((t) => (t ?? '').replace(/\s+/g, ' ').trim());
  const words = cleaned.filter((t) => {
    const rest = t === 'Spend' ? '' : t.replace(/Actual Spend(?![a-z])/g, '');
    return SpendWord.test(rest) || AgentHShort.test(rest) || ValueWords.test(rest);
  });
  const costAlone = titles.map((t) => t.replace(/\s+/g, ' ').trim()).filter((t) => CostAlone.test(t));
  return [...words, ...costAlone];
}
