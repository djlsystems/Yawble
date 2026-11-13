// Adding typed text to a list (Settings > Guardrails' list editors): commas or new lines split one paste into several
// entries, each is trimmed, blanks are dropped and an entry already in the list is ignored. Returns the same array
// when nothing was added.
export function addEntries(list: string[], text: string): string[] {
  const items = text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s);
  const next = [...list];
  for (const i of items) if (!next.includes(i)) next.push(i);
  return next.length === list.length ? list : next;
}

/** Whether typed text would add anything (not only blanks or separators). */
export const hasEntries = (text: string): boolean => text.split(/[\n,]/).some((s) => s.trim() !== '');
