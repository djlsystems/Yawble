// B111 (card 497): folder asks, shell side. An agent asks for a folder outside its clone (read or read-write); the
// person approves (never wider than asked), approves read-only, or denies. The contract is DeveloperMarlo's (card 496,
// docs/folder-asks.md): the FolderAsk types are shared/api.ts's, re-exported here (card 507), never declared again.
import type { FolderAsk, FolderAskChoice } from '../../shared/api';

export type { FolderAsk, FolderAskChoice } from '../../shared/api';
export type FolderAccess = FolderAsk['access'];
export type FolderAskState = FolderAsk['state'];
export { FOLDER_ASKS_CHANGED } from '../../shared/api';

/** The member's own name from "Team/Name" (the whole string when it has no slash). */
export function folderAskMember(a: Pick<FolderAsk, 'member'>): string {
  const i = a.member.indexOf('/');
  return i >= 0 ? a.member.slice(i + 1) : a.member;
}

/**
 * Who asked, when it is not the member itself: the Manager asking for one of its members (docs/folder-asks.md: askedBy
 * is not member). Compared by name ("Team/Name" or a bare name: both are the same team), so null for the member's own
 * ask or when askedBy is missing.
 */
export function folderAskAskedBy(a: { member: string; askedBy?: string | null }): string | null {
  if (!a.askedBy) return null;
  const asker = folderAskMember({ member: a.askedBy });
  return asker && asker.toLowerCase() !== folderAskMember(a).toLowerCase() ? asker : null;
}

/** The asker's words for the card: "Manager" when that is its name, else "Name (Manager)". */
export const askerWords = (asker: string): string => (asker.toLowerCase() === 'manager' ? 'Manager' : `${asker} (Manager)`);

/**
 * The card's head line in plain text: "Team / Member asks read-write on Z:\p - reason", or, for the Manager's ask,
 * "Team / Manager asks, for Member, read-write on Z:\p - reason".
 */
export function folderAskHeadText(a: Pick<FolderAsk, 'team' | 'member' | 'access' | 'path'> & { reason?: string | null; askedBy?: string | null }): string {
  const asker = folderAskAskedBy(a);
  const who = asker ? `${askerWords(asker)} asks, for ${folderAskMember(a)},` : `${folderAskMember(a)} asks`;
  return `${a.team} / ${who} ${accessWords(a.access)} on ${a.path}${a.reason ? ` - ${a.reason}` : ''}`;
}

export const accessWords = (a: FolderAccess): string => (a === 'readwrite' ? 'read-write' : 'read-only');

/**
 * The answers the card offers, in order: Approve (as asked), Approve read-only only for a read-write ask, and Deny. Never
 * wider than asked: a read-only ask never offers read-write, whatever the core's choices say. The core's choices, when
 * it sends them, can only narrow this list.
 */
export function folderAskChoices(a: { access: FolderAccess; choices?: readonly FolderAskChoice[] | null }): FolderAskChoice[] {
  const base: FolderAskChoice[] = a.access === 'readwrite' ? ['readwrite', 'read', 'deny'] : ['read', 'deny'];
  return a.choices?.length ? base.filter((c) => a.choices!.includes(c)) : base;
}

/** The pending asks (what Needs you, the You node and the counts show). */
export const pendingFolderAsks = (list: readonly FolderAsk[]): FolderAsk[] => list.filter((a) => a && a.state === 'pending');

/** What an answer did, in words, for the line after it: "Approved read-only for X on Z:\p: applies from its next run." */
export function folderAnswerText(a: FolderAsk, choice: FolderAskChoice): string {
  const who = folderAskMember(a);
  // A deny tells the asker: the Manager, when it asked for the member (docs/folder-asks.md, Deny).
  if (choice === 'deny') return `Denied ${who} ${accessWords(a.access)} on ${a.path}; ${folderAskAskedBy(a) ? 'the Manager is' : 'the agent is'} told.`;
  return `Approved ${accessWords(choice)} for ${who} on ${a.path}: applies from its next run.`;
}
