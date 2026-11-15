import type { NetworkAsk } from '../../shared/api';

/**
 * One item in Needs you: one member's waiting network asks that share a registrable domain (the core's `domain`), e.g.
 * crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com. An ask without a domain is an item alone. Allow on the
 * item answers every listed host (network.answer with ids).
 */
export interface NetworkAskGroup {
  key: string;
  /** The asks, oldest first; the first one is the item's own (its id is sent as id). */
  asks: NetworkAsk[];
  hosts: string[];
  /** The domain allow offered for the item ("*.digicert.com"): the first of its asks that offers one; null when none does. */
  domainPattern: string | null;
}

const keyOf = (a: NetworkAsk) => `${a.teamId}\u0000${a.member}\u0000${a.domain || a.host}`;

/** Groups the waiting asks by team, member and registrable domain, in the order each item's first ask came. */
export function groupAsks(asks: NetworkAsk[]): NetworkAskGroup[] {
  const groups = new Map<string, NetworkAskGroup>();
  for (const a of [...asks].sort((x, y) => x.id - y.id)) {
    const key = keyOf(a);
    let g = groups.get(key);
    if (!g) groups.set(key, (g = { key, asks: [], hosts: [], domainPattern: null }));
    g.asks.push(a);
    if (!g.hosts.includes(a.host)) g.hosts.push(a.host);
    g.domainPattern ??= a.domainPattern ?? null;
  }
  return [...groups.values()];
}

/** The item's hosts as one line: "api.nuget.org", or "crl3.digicert.com, ocsp.digicert.com, crl4.digicert.com (3 hosts)". */
export function hostsLabel(hosts: string[]): string {
  return hosts.length === 1 ? hosts[0] : `${hosts.join(', ')} (${hosts.length} hosts)`;
}
