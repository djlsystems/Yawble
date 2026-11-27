/**
 * The unsent text of each Concierge chat, kept while the window is open: the message box is rebuilt each time the
 * person comes back to the Concierge from another page, and shows the text they left in it. One draft per chat, and
 * one for the new chat that has no id yet.
 */
export const NEW_CHAT_DRAFT = 'new';

const drafts = new Map<string, string>();

export function draftOf(key: string): string {
  return drafts.get(key) ?? '';
}

export function keepDraft(key: string, text: string): void {
  if (text) drafts.set(key, text);
  else drafts.delete(key);
}

export function forgetDrafts(): void {
  drafts.clear();
}
