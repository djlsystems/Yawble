// Folder asks, shell side. An agent asks for a folder outside its clone (read or read-write), or the Concierge asks
// for the person; the person approves (never wider than asked), approves read-only, or denies, and says whom an approval
// covers: the member, its whole team, or all teams. The contract is the core's (docs/folder-asks.md): the FolderAsk
// types are shared/api.ts's, re-exported here, never declared again.
import { FOLDER_ASK_CONCIERGE, type FolderAsk, type FolderAskChoice, type FolderAskScope } from '../../shared/api';

export type { FolderAsk, FolderAskChoice, FolderAskScope } from '../../shared/api';
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
 * Whether the Concierge raised the ask for the person: askedBy is exactly "Concierge". A member is always "Team/Name",
 * so a member that happens to be named Concierge is never taken for it.
 */
export const folderAskFromConcierge = (a: { askedBy?: string | null }): boolean => a.askedBy === FOLDER_ASK_CONCIERGE;

/**
 * Who asked, in the card's words, when it is not the member itself: "The Concierge", "Manager" or "Name (Manager)";
 * null for the member's own ask.
 */
export function folderAskAskerWords(a: { member: string; askedBy?: string | null }): string | null {
  if (folderAskFromConcierge(a)) return 'The Concierge';
  const asker = folderAskAskedBy(a);
  return asker ? askerWords(asker) : null;
}

/**
 * The card's head line in plain text: "Team / Member asks read-write on Z:\p - reason", or, for the Manager's ask,
 * "Team / Manager asks, for Member, read-write on Z:\p - reason", or "Team / The Concierge asks, for Member, ...".
 */
export function folderAskHeadText(a: Pick<FolderAsk, 'team' | 'member' | 'access' | 'path'> & { reason?: string | null; askedBy?: string | null }): string {
  const asker = folderAskAskerWords(a);
  const who = asker ? `${asker} asks, for ${folderAskMember(a)},` : `${folderAskMember(a)} asks`;
  return `${a.team} / ${who} ${accessWords(a.access)} on ${a.path}${a.reason ? ` - ${a.reason}` : ''}`;
}

/**
 * Whom an approval may cover, in the card's order: this member first (the default), then the whole team, then all
 * teams. Only what the core offers (scopes); a core that sends none grants the member only.
 */
export function folderAskScopes(a: { scopes?: readonly FolderAskScope[] | null }): FolderAskScope[] {
  const all: FolderAskScope[] = ['member', 'team', 'everywhere'];
  const offered = all.filter((s) => a.scopes?.includes(s));
  return offered.includes('member') ? offered : ['member'];
}

/** The scope's choice in the card: "Only Clifford", "Everyone on Smoke-sessions", "All teams". */
export function folderScopeLabel(a: Pick<FolderAsk, 'team' | 'member'>, scope: FolderAskScope): string {
  return scope === 'everywhere' ? 'All teams' : scope === 'team' ? `Everyone on ${a.team}` : `Only ${folderAskMember(a)}`;
}

/** Whom a scope covers, in a sentence: "Clifford", "every member of Smoke-sessions", "every agent of every team". */
export function folderScopeWho(a: Pick<FolderAsk, 'team' | 'member'>, scope: FolderAskScope): string {
  return scope === 'everywhere' ? 'every agent of every team' : scope === 'team' ? `every member of ${a.team}` : folderAskMember(a);
}

/**
 * Exactly what an approval with this access and scope grants, for the card's line and its buttons' titles:
 * "Gives every member of Smoke-sessions read-only access to Z:\p, from their next runs. Members hired later get it too. ..."
 */
export function folderGrantText(a: Pick<FolderAsk, 'team' | 'member' | 'path'>, access: FolderAccess, scope: FolderAskScope): string {
  const gives = `Gives ${folderScopeWho(a, scope)} ${accessWords(access)} access to ${a.path}`;
  if (scope === 'everywhere') return `${gives}, from their next runs. It goes into the global policy (Settings > Guardrails), for teams made later too.`;
  if (scope === 'team') return `${gives}, from their next runs. Members hired later get it too. It is listed in the team's Settings > Folders.`;
  return `${gives}, from its next run. It is listed on the member's Policy tab.`;
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

/**
 * What an answer did, in words, for the line after it: "Approved read-only for X on Z:\p: applies from its next run.",
 * or for a wider scope "Approved read-only for every member of T on Z:\p: applies from their next runs."
 */
export function folderAnswerText(a: FolderAsk, choice: FolderAskChoice, scope: FolderAskScope = 'member'): string {
  const who = folderAskMember(a);
  // A deny tells the asker: the Manager, when it asked for the member (docs/folder-asks.md, Deny); nobody for the Concierge's ask.
  if (choice === 'deny')
    return `Denied ${who} ${accessWords(a.access)} on ${a.path}${folderAskFromConcierge(a) ? '.' : `; ${folderAskAskedBy(a) ? 'the Manager is' : 'the agent is'} told.`}`;
  return `Approved ${accessWords(choice)} for ${folderScopeWho(a, scope)} on ${a.path}: applies from ${scope === 'member' ? 'its next run' : 'their next runs'}.`;
}
