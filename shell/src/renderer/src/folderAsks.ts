// B111 (card 497): folder asks, shell side. An agent asks for a folder outside its clone (read or read-write); the
// person approves (never wider than asked), approves read-only, or denies. The contract is DeveloperMarlo's (card 496,
// docs/folder-asks.md, the FolderAsk types in shared/api.ts). Until his branch lands the shapes are declared here, as
// the card gives them; once it lands, these types become re-exports of shared/api.ts (FolderAsk, FolderAskChoice).

export type FolderAccess = 'read' | 'readwrite';
export type FolderAskState = 'pending' | 'approved' | 'denied' | 'closed';
/** folder.answer's choice: approve as asked (read-write), approve read-only, or deny. */
export type FolderAskChoice = 'readwrite' | 'read' | 'deny';

/** One folder ask (folder.asks). member is "Team/Name". Everything but the ids is agent-influenced text: plain text only. */
export interface FolderAsk {
  id: number;
  team: string;
  member: string;
  askedBy?: string | null;
  path: string;
  access: FolderAccess;
  reason: string | null;
  exists: boolean;
  state: FolderAskState;
  grantedAccess?: FolderAccess | null;
  decidedBy?: string | null;
  decidedAt?: string | null;
  workflow?: number | null;
  choices?: FolderAskChoice[] | null;
}

export const FOLDER_ASKS_CHANGED = 'folder.asksChanged';

/** The member's own name from "Team/Name" (the whole string when it has no slash). */
export function folderAskMember(a: Pick<FolderAsk, 'member'>): string {
  const i = a.member.indexOf('/');
  return i >= 0 ? a.member.slice(i + 1) : a.member;
}

export const accessWords = (a: FolderAccess): string => (a === 'readwrite' ? 'read-write' : 'read-only');

/**
 * The answers the card offers, in order: Approve (as asked), Approve read-only only for a read-write ask, and Deny. Never
 * wider than asked: a read-only ask never offers read-write, whatever the core's choices say. The core's choices, when
 * it sends them, can only narrow this list.
 */
export function folderAskChoices(a: Pick<FolderAsk, 'access' | 'choices'>): FolderAskChoice[] {
  const base: FolderAskChoice[] = a.access === 'readwrite' ? ['readwrite', 'read', 'deny'] : ['read', 'deny'];
  return a.choices?.length ? base.filter((c) => a.choices!.includes(c)) : base;
}

/** The pending asks (what Needs you, the You node and the counts show). */
export const pendingFolderAsks = (list: readonly FolderAsk[]): FolderAsk[] => list.filter((a) => a && a.state === 'pending');

/** What an answer did, in words, for the line after it: "Approved read-only for X on Z:\p: applies from its next run." */
export function folderAnswerText(a: FolderAsk, choice: FolderAskChoice): string {
  const who = folderAskMember(a);
  if (choice === 'deny') return `Denied ${who} ${accessWords(a.access)} on ${a.path}; the agent is told.`;
  return `Approved ${accessWords(choice)} for ${who} on ${a.path}: applies from its next run.`;
}
