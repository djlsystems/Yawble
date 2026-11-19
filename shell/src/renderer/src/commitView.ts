import { shallowRef } from 'vue';
import type { CommitFound } from '../../shared/api';
import { LOCAL_MARK } from './localMark';
import { cachedMatches, commitChoice, pickMatches } from './commitResolve';

// B072: the one Commit view any commit ID in the app opens (hand-backs, the board's trail, the Monitor, the Tidy panel,
// the Git dialog). One at a time: opening another commit replaces it. teams: where to look, in order; the first team
// whose repository has the commit is shown (a hand-back in the Concierge does not say its team).

/** repo (B074): the repository a resolved link named, in teams[0] (absent: the team's main repository). */
export interface CommitTarget { teams: string[]; sha: string; repo?: string }

export const commitTarget = shallowRef<CommitTarget | null>(null);

export function openCommit(team: string | string[], sha: string, repo?: string | null): void {
  const teams = (Array.isArray(team) ? team : [team]).filter((t, i, all) => !!t && all.indexOf(t) === i);
  if (!teams.length || !isAskableId(sha)) return;
  commitTarget.value = { teams, sha: sha.toLowerCase(), ...(repo ? { repo } : {}) };
}

export function closeCommit(): void {
  commitTarget.value = null;
}

/**
 * The teams to look in when a commit ID is clicked: the team its place says (data-commit-team on it or an ancestor),
 * else the team on screen, else every team. Set by App.vue as the place changes.
 */
export const commitTeams = shallowRef<{ current: string | null; all: string[] }>({ current: null, all: [] });

/** The teams to try for a commit ID clicked at el: its place's team first, then the team on screen, then every team. */
export function teamsFor(el: Element | null): string[] {
  const own = (el?.closest('[data-commit-team]') as HTMLElement | null)?.dataset.commitTeam ?? null;
  const t = commitTeams.value;
  return [own, t.current, ...t.all].filter((x): x is string => !!x);
}

/**
 * Opens the Commit view from a click (or Enter) on any [data-commit] element: the commit links rendered markdown makes
 * (markdown.ts). Returns the remover.
 */
export function installCommitLinks(doc: Document = document): () => void {
  const open = (e: Event) => {
    const el = (e.target as Element | null)?.closest?.('[data-commit]') as HTMLElement | null;
    if (!el) return;
    if (e instanceof KeyboardEvent && e.key !== 'Enter' && e.key !== ' ') return;
    e.preventDefault();
    e.stopPropagation();
    const id = el.dataset.commit ?? '';
    const teams = teamsFor(el);
    // B074: an ID that names several commits opens the chooser (repository and title); one confirmed by
    // git.commitsResolve opens in the team and repository it was found in, then the usual order.
    if (el.dataset.commitChoose !== undefined) {
      const matches = pickMatches(cachedMatches(id) ?? [], teams);
      if (matches.length > 1) { commitChoice.value = { id, teams, matches }; return; }
      if (matches.length === 1) { openCommit([matches[0].team, ...teams], matches[0].fullId, matches[0].repo); return; }
    }
    const inTeam = el.dataset.commitIn;
    openCommit(inTeam ? [inTeam, ...teams] : teams, id, inTeam ? el.dataset.commitRepo : null);
  };
  doc.addEventListener('click', open, true);
  doc.addEventListener('keydown', open, true);
  return () => { doc.removeEventListener('click', open, true); doc.removeEventListener('keydown', open, true); };
}

/** What the Commit view's actions ask the app to do: open the team's Git dialog at main or at the commit's branch. */
export interface GitRequest { team: string; focus: 'compare' | 'branch'; branch: string | null; sha: string }
export const gitRequest = shallowRef<GitRequest | null>(null);

/** What git.commitShow takes: 4 to 64 hex digits (a full ID the app passes, short or long; the core refuses others). */
export function isAskableId(text: string): boolean {
  return /^[0-9a-f]{4,64}$/i.test(text);
}

/** A commit ID in text, as git prints one: 7 to 40 hex digits, with at least one digit and one letter (so "deadbeef" or a
 * number like 1234567 is not taken for one). */
export function isCommitId(text: string): boolean {
  return /^[0-9a-f]{7,40}$/i.test(text) && /[0-9]/.test(text) && /[a-f]/i.test(text);
}

// Lower case only, as git prints them: "Deadbeef1" or "CAFE123" in a sentence is a word, not a commit. Not after a
// slash, dot, # or dash (a path, a URL, a colour), and not before ".ext" (a file name); a sentence's full stop is fine.
const ID = /(^|[^0-9A-Za-z_./\\#-])([0-9a-f]{7,40})(?![0-9A-Za-z_]|[.\-/\\][0-9A-Za-z_])/g;

export interface CommitPart { text: string; sha?: string }

/** Plain text split into text and commit IDs (in order). A hex run inside a word, a path or a URL stays text. */
export function commitParts(text: string): CommitPart[] {
  const parts: CommitPart[] = [];
  let at = 0;
  for (const m of text.matchAll(ID)) {
    const sha = m[2];
    if (!isCommitId(sha)) continue;
    const start = (m.index ?? 0) + m[1].length;
    if (start > at) parts.push({ text: text.slice(at, start) });
    parts.push({ text: sha, sha: sha.toLowerCase() });
    at = start + sha.length;
  }
  if (at < text.length) parts.push({ text: text.slice(at) });
  return parts;
}

// ---- The view's words (pure, so each state is tested without mounting).

export const spaced = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2');

/** "Merged DeveloperMarlo's branch into team/x, 4 commits". Either branch may be unknown (null): left out, never "null". */
export function mergeLine(c: CommitFound): string | null {
  if (!c.isMerge) return null;
  const m = c.merge;
  if (!m) return `A merge of ${c.parents.length} parents.`;
  const from = m.fromMember ? `${m.fromMember}'s branch` : m.fromBranch || 'another branch';
  const into = m.intoBranch ? ` into ${m.intoBranch}` : '';
  return `Merged ${from}${into}, ${m.commitCount} commit${m.commitCount === 1 ? '' : 's'}`;
}

/** Where the commit is, in plain words, most important first. */
export function whereLines(c: CommitFound): { key: string; text: string; tone: 'ok' | 'warn' | 'plain' }[] {
  const w = c.where;
  const out: { key: string; text: string; tone: 'ok' | 'warn' | 'plain' }[] = [];
  const teamBranch = w.branches.find((b) => b.startsWith('team/'));
  if (w.inMain) out.push({ key: 'main', text: 'In main', tone: 'ok' });
  else if (w.onlyOnMember) out.push({ key: 'member', text: `Only on ${w.onlyOnMember}'s branch`, tone: 'warn' });
  else if (teamBranch) out.push({ key: 'team', text: 'In the team branch only, not in main yet', tone: 'warn' });
  else if (w.branches.length) out.push({ key: 'branches', text: `On ${w.branches.join(', ')}, not in main yet`, tone: 'warn' });
  else if (w.otherBranches?.length) out.push({ key: 'other', text: `On ${w.otherBranches.join(', ')} (not a team or member branch)`, tone: 'warn' });
  else if (w.deletedFrom) out.push({ key: 'none', text: `On no branch: it was on ${w.deletedFrom}, which was deleted`, tone: 'warn' });
  else out.push({ key: 'none', text: 'On no branch (a branch that held it was deleted)', tone: 'warn' });
  out.push(w.pushed ? { key: 'pushed', text: 'On origin', tone: 'ok' } : { key: 'unpushed', text: LOCAL_MARK.long, tone: 'warn' });
  if (w.tags.length) out.push({ key: 'tags', text: `Included in release ${w.tags.join(', ')}`, tone: 'plain' });
  return out;
}

/** The branch "Show its branch" opens: the member's, else the team branch, else the first that holds it. */
export function itsBranch(c: CommitFound): string | null {
  const b = c.where.branches;
  return b.find((x) => x.startsWith('members/')) ?? b.find((x) => x.startsWith('team/')) ?? b.find((x) => x !== 'main') ?? b[0] ?? null;
}

const STATUS_WORDS: Record<string, string> = { added: 'added', deleted: 'deleted', renamed: 'renamed', copied: 'copied', modified: 'changed', typechange: 'type changed' };
export const fileStatusWord = (s: string) => STATUS_WORDS[s] ?? s;

/** "a@x.com" style person line; the committer only when someone else committed it. */
export function differentCommitter(c: CommitFound): boolean {
  const a = c.author;
  const k = c.committer;
  return !!k && (k.name !== a.name || k.email !== a.email);
}
