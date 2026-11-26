import { shallowRef } from 'vue';
import { GIT_COMMITS_RESOLVE, GIT_COMMITS_RESOLVE_MAX, type CommitMatch, type CommitsResolved, type YawbleApi } from '../../shared/api';
import { LOCAL_MARK } from './localMark';

// A commit-shaped ID in text (commitView.ts isCommitId) is a link only once the core confirms it is a commit
// (git.commitsResolve). Until then, and for good when it is not one, it is plain text.
// Rendered markdown marks each candidate as <span data-commit-id> and the document's observer (installCommitResolve)
// collects them; CommitText.vue asks for its own. All of a screen's IDs go in one call (200 a call at most), and every
// answer is kept for the session.

/** The window API to ask (window.yawble), or the one a test passes in. */
let apiOverride: YawbleApi | null = null;
let delayMs = 25;

const cache = new Map<string, CommitMatch[]>();
const queued = new Set<string>();
const asking = new Set<string>();
/** IDs a call failed for: text for the rest of the session (no retry loop against a core that cannot answer). */
const failed = new Set<string>();
let timer: ReturnType<typeof setTimeout> | null = null;

/** Bumped whenever answers arrive: CommitText.vue reads it to render again. */
export const resolvedVersion = shallowRef(0);

/** Tests: the API to ask and the batching delay. */
export function configureCommitResolve(opts: { api?: YawbleApi | null; delayMs?: number }): void {
  if (opts.api !== undefined) apiOverride = opts.api;
  if (opts.delayMs !== undefined) delayMs = opts.delayMs;
}

/** Forgets every answer (tests; a session's answers otherwise last until the window closes). */
export function resetCommitResolve(): void {
  cache.clear(); queued.clear(); asking.clear(); failed.clear();
  if (timer) clearTimeout(timer);
  timer = null;
  resolvedVersion.value = 0;
}

/** What the core said about an ID: its commits ([] = not a commit), or undefined while not known yet. */
export function cachedMatches(id: string): CommitMatch[] | undefined {
  return cache.get(id.toLowerCase());
}

/** Queues IDs not yet known or asked; one call goes a moment after the last. */
export function requestResolve(ids: Iterable<string>): void {
  for (const raw of ids) {
    const id = raw.toLowerCase();
    if (cache.has(id) || asking.has(id) || failed.has(id)) continue;
    queued.add(id);
  }
  if (queued.size && !timer) timer = setTimeout(() => { timer = null; void flush(); }, delayMs);
}

/** Sends what is queued now (one call; above the core's cap of 200 IDs, one per 200) and waits for it. */
export async function flush(): Promise<void> {
  if (timer) { clearTimeout(timer); timer = null; }
  if (!queued.size) return;
  const ids = [...queued];
  queued.clear();
  for (const id of ids) asking.add(id);
  const api = apiOverride ?? (typeof window !== 'undefined' ? window.yawble : undefined);
  const chunks: string[][] = [];
  for (let i = 0; i < ids.length; i += GIT_COMMITS_RESOLVE_MAX) chunks.push(ids.slice(i, i + GIT_COMMITS_RESOLVE_MAX));
  await Promise.all(chunks.map(async (chunk) => {
    try {
      if (!api) throw new Error('no api');
      const answer = await api.call<CommitsResolved>(GIT_COMMITS_RESOLVE, { ids: chunk });
      for (const id of chunk) cache.set(id, answerMatches(answer, id));
    } catch {
      // Not answered (an older core, a dropped socket, a refusal): the IDs stay text for this session.
      for (const id of chunk) failed.add(id);
    } finally {
      for (const id of chunk) asking.delete(id);
    }
  }));
  resolvedVersion.value++;
  for (const l of listeners) l();
}

const listeners = new Set<() => void>();

/** The commits one ID names in a git.commitsResolve answer (results [{id, valid, matches}]); an ID the
 * answer leaves out, or a malformed entry, counts as no commit. */
export function answerMatches(answer: CommitsResolved | null | undefined, id: string): CommitMatch[] {
  const rows = Array.isArray(answer?.results) ? answer!.results : [];
  const row = rows.find((r) => typeof r?.id === 'string' && r.id.toLowerCase() === id.toLowerCase());
  if (!row || !Array.isArray(row.matches)) return [];
  return row.matches
    .filter((m) => !!m && typeof m.fullId === 'string' && typeof m.team === 'string')
    .map((m) => ({
      team: m.team,
      ...(typeof m.teamId === 'number' ? { teamId: m.teamId } : {}),
      repo: typeof m.repo === 'string' && m.repo ? m.repo : null,
      fullId: m.fullId.toLowerCase(),
      shortId: typeof m.shortId === 'string' ? m.shortId : m.fullId.slice(0, 12).toLowerCase(),
      title: typeof m.title === 'string' ? m.title : '',
      pushed: m.pushed !== false,
    }));
}

/**
 * The commits an ID can mean where it is shown, teams in order (commitView.ts teamsFor: its place's team, the team on
 * screen, every team). own: the team the place itself names (data-commit-team, CommitText's team), never the screen's
 * or a fallback. When own has it, only own's count (a hand-back means its own team's commit); else every team's,
 * nearest team first, so an ID in two teams with no team of its place opens the chooser. The same commit twice is once.
 */
export function pickMatches(all: CommitMatch[], teams: string[], own?: string | null): CommitMatch[] {
  const seen = new Set<string>();
  const uniq = all.filter((m) => { const k = `${m.team}\n${m.repo ?? ''}\n${m.fullId}`; if (seen.has(k)) return false; seen.add(k); return true; });
  const mine = own ? uniq.filter((m) => m.team === own) : [];
  const list = mine.length ? mine : uniq;
  const rank = (m: CommitMatch) => { const i = teams.indexOf(m.team); return i < 0 ? teams.length : i; };
  return list.map((m, i) => ({ m, i })).sort((x, y) => rank(x.m) - rank(y.m) || x.i - y.i).map((x) => x.m);
}

/** A confirmed link's hover: the commit's title, and the Commit view's own "not pushed" words when origin lacks it. */
export function commitTitle(m: CommitMatch): string {
  const head = m.title || `Commit ${m.fullId.slice(0, 12)}`;
  return m.pushed ? head : `${head}\n${LOCAL_MARK.long}`;
}

/** An ambiguous link's hover. */
export function chooseTitle(id: string, matches: CommitMatch[]): string {
  return `${matches.length} commits start with ${id}: click to choose one`;
}

/** The repository as the chooser names it: the team's main repository is "main". */
export const repoName = (m: CommitMatch) => m.repo || 'main';

// ---- The chooser an ambiguous ID opens (CommitChooser.vue, mounted once in App.vue).

export interface CommitChoice { id: string; teams: string[]; matches: CommitMatch[] }
export const commitChoice = shallowRef<CommitChoice | null>(null);
export function closeChoice(): void { commitChoice.value = null; }

// ---- Rendered markdown: <span data-commit-id> candidates upgraded in place.

/** Upgrades every settled candidate under root from what is known, and queues the rest. teamsOf: commitView.teamsFor. */
export function upgradeCandidates(root: ParentNode, teamsOf: (el: Element) => string[]): void {
  const pending: string[] = [];
  for (const el of root.querySelectorAll<HTMLElement>('[data-commit-id]:not([data-commit-state])')) {
    const id = el.dataset.commitId ?? '';
    const all = cachedMatches(id);
    if (!all) { if (failed.has(id.toLowerCase())) el.dataset.commitState = 'text'; else pending.push(id); continue; }
    settle(el, id, pickMatches(all, teamsOf(el), placeTeam(el)));
  }
  if (pending.length) requestResolve(pending);
}

/** The team el's place names itself (the nearest data-commit-team), or null: the screen's team does not count. */
export function placeTeam(el: Element | null): string | null {
  return (el?.closest('[data-commit-team]') as HTMLElement | null)?.dataset.commitTeam || null;
}

function settle(el: HTMLElement, id: string, matches: CommitMatch[]): void {
  if (!matches.length) { el.dataset.commitState = 'text'; return; }
  el.dataset.commitState = matches.length === 1 ? 'link' : 'choose';
  el.classList.add('commit-link');
  el.setAttribute('role', 'button');
  el.setAttribute('tabindex', '0');
  el.dataset.test = 'commit-link';
  if (matches.length === 1) {
    const m = matches[0];
    el.dataset.commit = m.fullId;
    el.dataset.commitIn = m.team;
    if (m.repo) el.dataset.commitRepo = m.repo;
    el.title = commitTitle(m);
  } else {
    el.dataset.commit = id;
    el.dataset.commitChoose = '';
    el.title = chooseTitle(id, matches);
  }
}

/**
 * Watches the document for rendered candidates (any v-html of markdown.ts output, wherever it is) and upgrades them as
 * answers come. Returns the remover.
 */
export function installCommitResolve(teamsOf: (el: Element) => string[], doc: Document = document): () => void {
  const scan = () => upgradeCandidates(doc, teamsOf);
  const observer = new MutationObserver((records) => {
    if (records.some((r) => [...r.addedNodes].some((n) => n.nodeType === 1))) scan();
  });
  observer.observe(doc.body ?? doc.documentElement, { childList: true, subtree: true });
  listeners.add(scan);
  scan();
  return () => { observer.disconnect(); listeners.delete(scan); };
}
