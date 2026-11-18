// B075 (card 447): "Open on GitHub/GitLab/…" links. The core builds every url (git_web_link, card 446) from the
// repository's provider or link pattern and answers null when there is no link (a commit not on origin, a host it does not
// know with no pattern); the shell only asks and shows the ones it got.
import type { GitProvider, GitWebLinkQuery, RepoProviderInfo, YawbleApi } from '../../shared/api';
import { GIT_WEB_LINK } from '../../shared/api';

export const PROVIDER_LABELS: Record<GitProvider, string> = {
  github: 'GitHub', gitlab: 'GitLab', bitbucket: 'Bitbucket', azuredevops: 'Azure DevOps', gitea: 'Gitea', none: 'plain git (no provider)',
};

/** The provider in effect: the person's override, else the detected one. */
export function effectiveProvider(r: RepoProviderInfo | null | undefined): GitProvider | null {
  return r?.provider ?? r?.providerDetected ?? null;
}

/** The link's text: "Open on GitLab"; "Open in the browser" for a link pattern on a host with no provider. */
export function openOnText(p: GitProvider | null): string {
  return p && p !== 'none' ? `Open on ${PROVIDER_LABELS[p]}` : 'Open in the browser';
}

/** A stable key for one query (to keep the answers in a map). */
export function linkKey(q: GitWebLinkQuery): string {
  return [q.kind, q.commit, q.branch, q.file, q.from, q.to, q.pr].map((x) => x ?? '').join('|');
}

/** Asks the core for each query's url; a query with no url (null, or the core not answering) is left out. */
export async function fetchWebLinks(api: YawbleApi, team: { id: number; name: string }, repo: string | null, queries: GitWebLinkQuery[]): Promise<Record<string, string>> {
  const out: Record<string, string> = {};
  const seen = new Set<string>();
  await Promise.all(queries.map(async (q) => {
    const key = linkKey(q);
    if (seen.has(key)) return;
    seen.add(key);
    try {
      const r = await api.call<{ url: string | null }>(GIT_WEB_LINK, { teamId: team.id, team: team.name, ...(repo ? { repo } : {}), ...q });
      if (typeof r?.url === 'string' && /^https?:\/\//i.test(r.url)) out[key] = r.url;
    } catch { /* an older core without git_web_link: no links */ }
  }));
  return out;
}
