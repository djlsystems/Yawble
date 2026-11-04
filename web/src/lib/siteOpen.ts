import { openSiteCapability } from '../api/sites';

/**
 * OPENING A SITE FROM THE APP, without the new tab having to carry the session cookie.
 *
 * A site's entry, `/sites/<team>/<site>/`, is the one site address the cookie authorizes, and the
 * cookie is `SameSite=Strict`. A new tab's first request is where the browser decides whether to send
 * it; when it does not, the entry sends the tab to the front door, which is not the site (W27510). So
 * the app asks the Host for the capability over its own signed-in request and opens THAT path, which
 * serves the page with no cookie at all. The entry stays what a typed address or a bookmark uses.
 *
 * What the tab is sent to is checked first: only this site's own capability path, same origin. An
 * answer of any other shape is refused rather than followed.
 */

/** A site's entry, as `siteEntry` in `router/guard.ts` accepts it: two plain segments and a query. */
const EntryParts = /^\/sites\/([A-Za-z0-9_-][A-Za-z0-9_.-]*)\/([A-Za-z0-9_-][A-Za-z0-9_.-]*)\/(\?[^#\\]*)?$/;

/** A capability token: the Host's data-protection output, base64url. */
const Token = /^[A-Za-z0-9_-]+$/;

export const NotThisSite = 'The Host answered with an address that is not this site.';

/** The team, site and query an entry names, or null when it is not exactly an entry. */
export function entryParts(entry: string): { team: string; site: string; query: string } | null {
  const match = EntryParts.exec(entry);
  return match ? { team: match[1]!, site: match[2]!, query: match[3] ?? '' } : null;
}

/** `url` when it is this site's own capability path, else null. */
export function capabilityPath(team: string, site: string, url: unknown): string | null {
  if (typeof url !== 'string') return null;
  const prefix = `/sites/${encodeURIComponent(team)}/${encodeURIComponent(site)}/_c/`;
  if (!url.startsWith(prefix) || !url.endsWith('/')) return null;
  return Token.test(url.slice(prefix.length, -1)) ? url : null;
}

/** The capability path to open the site at, the entry's query carried unchanged. Throws the Host's refusal. */
export async function capabilityFor(team: string, site: string, query = ''): Promise<string> {
  const { url } = await openSiteCapability(team, site);
  const path = capabilityPath(team, site, url);
  if (path === null) throw new Error(NotThisSite);
  return `${path}${query}`;
}

/** The capability path for an entry (`siteEntry`'s answer), its query carried. */
export async function capabilityForEntry(entry: string): Promise<string> {
  const parts = entryParts(entry);
  if (parts === null) throw new Error(NotThisSite);
  return capabilityFor(parts.team, parts.site, parts.query);
}

/** What a new tab needs; apart so a test can stand in for the browser. */
export interface TabOpener {
  open: (url: string, target: string, features?: string) => Window | null;
  location: { href: string };
}

/**
 * Opens the site in a new tab, from a person's click. The tab is opened FIRST, in the click itself,
 * so no popup blocker takes it for an unasked window; its `opener` is cut at once, as `noopener`
 * would. Then it is sent to the capability path, or - when the Host refuses - to the entry, which
 * answers its own sentence (not published, no such site). A browser that refuses the tab gets the
 * entry the old way.
 */
export async function openSiteTab(
  site: { team: string; name: string; url: string },
  from: TabOpener = window,
): Promise<void> {
  const tab = from.open('about:blank', '_blank');
  if (tab === null) {
    from.open(site.url, '_blank', 'noopener');
    return;
  }

  tab.opener = null;

  let path: string;
  try {
    path = await capabilityFor(site.team, site.name);
  } catch {
    path = site.url;
  }

  tab.location.replace(new URL(path, from.location.href).href);
}
