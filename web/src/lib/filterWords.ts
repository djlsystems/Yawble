/**
 * THE FILTER BOXES' RULE. Plugins, Agents and Marketplace's tabs each narrow their tiles by free
 * text the same way: the text split into lower-case words, and a tile shown when every word is
 * found somewhere in what the tile says. Written once here; each dialog decides what its tiles say.
 */

/** The filter's words: lower case, split on spaces, blanks dropped. Cleared (null) is no words. */
export const filterWords = (text: string | null | undefined) =>
  (text ?? '').trim().toLowerCase().split(/\s+/).filter((word) => word !== '');

/** Whether every word is found in one of `fields`, case-insensitively. No words matches anything. */
export function matchesWords(words: readonly string[], ...fields: readonly (string | null | undefined)[]): boolean {
  if (words.length === 0) return true;
  const text = fields.map((field) => field ?? '').join(' ').toLowerCase();
  return words.every((word) => text.includes(word));
}
