import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';

/**
 * CONNECTIONS, READ FOR A PERSON: the redirect URI to register, which connections a plugin's slot
 * can take, what a slot still needs from one, and the sentences the Host says about it. Kept out of
 * the components so the Connections dialog and the binding picker read a connection the same way,
 * and testable without a mount.
 *
 * THE HOST IS STILL THE CHECK. It refuses a binding of the wrong provider or without a scope, and
 * blocks a run whose required slot is unbound; the sentences here are its sentences, shown before
 * the save so a person is not sent round the loop to find out.
 */

/** The path the provider sends the browser back to, on the Host. */
export const CallbackPath = '/api/connections/callback';

/**
 * The redirect URI to register at the provider for the address the person is using now: exactly
 * what the Host builds from the request when `start` is sent without one.
 */
export function redirectUriFor(origin: string): string {
  return `${origin.replace(/\/+$/, '')}${CallbackPath}`;
}

const Loopback = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * WHY A PROVIDER WILL REFUSE THE REDIRECT URI FOR THIS ADDRESS, or null when it will take it.
 *
 * Google and Microsoft never call the redirect URI themselves - they send the browser to it - so it
 * need not be reachable from the internet. What they check is its SPELLING, when it is registered:
 * `http` only for localhost, never a raw IP address other than loopback, and an `https` name that
 * ends in a real top-level domain. A person on a LAN address (`http://192.168.1.20:8080`) would
 * otherwise copy the URI shown, paste it at the provider, and only then be refused. `localhost`
 * works on the machine running the container, where the same page is one address away.
 *
 * `cli` is the operator CLI's name, passed in because the brand lives in the presentation layer.
 */
export function redirectUriWarning(origin: string, cli: string): string | null {
  let url: URL;

  try {
    url = new URL(origin);
  } catch {
    return null;
  }

  const host = url.hostname.toLowerCase();

  if (Loopback.has(host)) return null;

  const port = url.port ? `:${url.port}` : '';
  const fix =
    ` Open this page at http://localhost${port} on the machine running the container, where the `
    + `providers accept http, and register that address instead; or connect from that machine with \`${cli} connect\`.`;

  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host) || host.startsWith('[')) {
    return `Google and Microsoft refuse a redirect URI on an IP address such as ${host}.${fix}`;
  }

  if (url.protocol === 'http:') {
    return `Google and Microsoft accept a redirect URI over http only for localhost, and this address is ${host}.${fix}`;
  }

  if (!host.includes('.') || /\.(local|lan|internal|home|localdomain)$/.test(host)) {
    return `Google refuses a redirect URI whose name does not end in a public top-level domain, such as ${host}.${fix}`;
  }

  return null;
}

/**
 * One line of help per built-in provider, for when the Host sends none. The Host's own `help` wins:
 * it knows the client type it expects.
 */
const builtInHelp: Record<string, string> = {
  google:
    'Google: a "Desktop app" client accepts http://127.0.0.1:<port> without registering it; a "Web application" client needs the redirect URI above registered exactly.',
  microsoft:
    'Microsoft: in the app registration add the redirect URI above under the "Web" platform (for the CLI, http://localhost there too), and create a client secret (copy its Value).',
};

export function providerHelp(provider: Pick<ConnectionProvider, 'id' | 'kind' | 'help'>): string {
  if (provider.help && provider.help.trim() !== '') return provider.help;
  return builtInHelp[provider.id] ?? 'Register the redirect URI above as an allowed redirect for this client.';
}

/** A provider's display name, from the list when it is there, else its id. */
export function providerName(id: string, providers: ConnectionProvider[]): string {
  const known = providers.find((provider) => provider.id === id);
  if (known) return known.name;
  if (id === 'google') return 'Google';
  if (id === 'microsoft') return 'Microsoft';
  return id;
}

/** Whether a slot takes connections of this connection's provider. `custom` takes any custom one. */
export function slotAllows(slot: ConnectionSlot, connection: Pick<Connection, 'provider' | 'providerKind'>): boolean {
  return (
    slot.providers.includes(connection.provider) ||
    (connection.providerKind === 'custom' && slot.providers.includes('custom'))
  );
}

/** The connections a slot's picker lists: those of an allowed provider, by name. */
export function connectionsForSlot(slot: ConnectionSlot, connections: Connection[]): Connection[] {
  return connections.filter((connection) => slotAllows(slot, connection)).sort((a, b) => a.name.localeCompare(b.name));
}

/** The scopes a slot asks of a connection of this provider: its own key, else `custom` for a custom one. */
export function slotScopes(slot: ConnectionSlot, connection: Pick<Connection, 'provider' | 'providerKind'>): string[] {
  return (
    slot.scopes[connection.provider] ??
    (connection.providerKind === 'custom' ? slot.scopes.custom : undefined) ??
    []
  );
}

/** What the slot asks for that the connection was not granted, in the slot's order. */
export function missingScopes(slot: ConnectionSlot, connection: Connection): string[] {
  const granted = new Set(connection.scopes);
  return slotScopes(slot, connection).filter((scope) => !granted.has(scope));
}

const quoted = (scopes: string[]) => scopes.map((scope) => `\`${scope}\``).join(', ');

/**
 * The Host's refusal of a binding without a scope the slot needs, which offers Reconnect. The
 * connection's own name and account are in it, so a person knows which one to reconnect.
 */
export function scopeRefusal(slotName: string, connection: Connection, missing: string[]): string {
  const what = missing.length === 1 ? `the scope ${quoted(missing)}` : `the scopes ${quoted(missing)}`;
  const those = missing.length === 1 ? 'that scope' : 'those scopes';

  return (
    `Connection ${named(connection)} was not granted ${what} that slot \`${slotName}\` needs. ` +
    `Reconnect it from Admin → Connections with ${those}, then bind it again.`
  );
}

/** "'Work mail' (person@example.com)", or just "'person@example.com'" when the name IS the account: the Host's `Named`. */
export function named(connection: Pick<Connection, 'name' | 'account'>): string {
  return connection.name === connection.account ? `'${connection.name}'` : `'${connection.name}' (${connection.account})`;
}

/** The Host's sentence for a required slot left unbound: the member's runs are blocked with it. */
export function unboundRefusal(slotName: string): string {
  return `This member has no connection bound for the plugin's required slot \`${slotName}\`. A person binds one in the member's settings.`;
}

/** The sentence for a binding naming a connection that no longer exists. */
export function goneRefusal(slotName: string): string {
  return `The connection bound for slot \`${slotName}\` no longer exists. A person binds another in the member's settings.`;
}

/**
 * "needs a Google or Microsoft connection": the Host's summary when it sent one, else the same
 * sentence built from the provider names.
 */
export function slotSummary(slot: ConnectionSlot, providers: ConnectionProvider[] = []): string {
  if (slot.summary && slot.summary.trim() !== '') return slot.summary;

  const names = slot.providers.map((id) => (id === 'custom' ? 'custom' : providerName(id, providers)));
  const list = names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')} or ${names[names.length - 1]}`;

  return `needs a ${list} connection`;
}

/** The bindings to send: each slot with a connection chosen. An unbound slot is left out. */
export function bindingsBody(
  slots: Record<string, ConnectionSlot> | undefined,
  bound: Record<string, string>,
): Record<string, string> {
  return Object.fromEntries(
    Object.keys(slots ?? {}).flatMap((name) => ((bound[name] ?? '') !== '' ? [[name, bound[name]!]] : [])),
  );
}

/** Each slot's stored binding, or empty until a person picks one. */
export function initialBindings(
  slots: Record<string, ConnectionSlot> | undefined,
  stored: Record<string, string> = {},
): Record<string, string> {
  return Object.fromEntries(Object.keys(slots ?? {}).map((name) => [name, stored[name] ?? '']));
}

/** Scopes as a person types them: separated by commas, spaces or new lines, each once. */
export function parseScopes(text: string): string[] {
  return [...new Set(text.split(/[\s,]+/).map((scope) => scope.trim()).filter((scope) => scope !== ''))];
}

/** "ok", or "needs reconnect" - in words, never in colour alone. */
export function statusLabel(connection: Pick<Connection, 'status'>): string {
  return connection.status === 'ok' ? 'ok' : 'needs reconnect';
}

/** "alpha / Inbox (mail)" for each member using it. */
export function usedByLabels(connection: Pick<Connection, 'usedBy'>): string[] {
  return connection.usedBy.map((use) => `${use.team} / ${use.label || use.member} (${use.slot})`);
}

/**
 * What the provider's round trip came back with: the Host's callback answers a redirect to
 * `/console?connection=connected|reconnected|refused[&id=…][&reason=…]`. Null when the query carries
 * none of it.
 */
export type CallbackOutcome =
  | { outcome: 'connected' | 'reconnected'; id: string | null }
  | { outcome: 'refused'; reason: string };

export function callbackOutcome(query: Record<string, unknown>): CallbackOutcome | null {
  const first = (value: unknown) => (Array.isArray(value) ? value[0] : value);
  const outcome = first(query.connection);

  if (outcome === 'connected' || outcome === 'reconnected') {
    const id = first(query.id);
    return { outcome, id: typeof id === 'string' ? id : null };
  }
  if (outcome === 'refused') {
    const reason = first(query.reason);
    return { outcome, reason: typeof reason === 'string' && reason !== '' ? reason : 'The Host gave no reason.' };
  }
  return null;
}

/**
 * THE PROVIDER'S RETURN, MOVED INTO THE HASH. The Host's callback redirects to
 * `/console?connection=…`, but the router is in hash mode and reads only the hash: left alone it
 * resolves `/` with an empty query and the person lands on the front page with no notice. The
 * address to replace it with carries the same query in `#/console?…` and no search, so a reload
 * does not say it again. Null when the search carries no `connection`.
 */
export function providerReturnAddress(location: Pick<Location, 'pathname' | 'search'>): string | null {
  const params = new URLSearchParams(location.search);
  if (!params.has('connection')) return null;

  const base = location.pathname.replace(/console\/?$/, '');
  return `${base}#/console?${params.toString()}`;
}

/** Rewrites the address in place, before the hash router reads it. True when it did. */
export function landProviderReturn(target: Pick<Window, 'location' | 'history'> = window): boolean {
  const address = providerReturnAddress(target.location);
  if (address === null) return false;

  target.history.replaceState(target.history.state, '', address);
  return true;
}

/** A timestamp for a person: the local date and time, or "never". */
export function when(iso: string | null): string {
  if (!iso) return 'never';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

/**
 * A setup guide's deep link for the project the person named. A link holds `{projectId}` where the
 * provider's console takes one; with none given, the query parameter that would carry it is left
 * out, so the link opens on whatever project the console has chosen.
 */
export function guideLink(link: string, projectId: string): string {
  const project = projectId.trim();
  if (project !== '') return link.replaceAll('{projectId}', encodeURIComponent(project));

  const [address, query] = link.split('?', 2) as [string, string | undefined];
  const kept = (query ?? '').split('&').filter((pair) => pair !== '' && !pair.includes('{projectId}'));
  return `${address.replaceAll('{projectId}', '')}${kept.length > 0 ? `?${kept.join('&')}` : ''}`;
}

/**
 * Why a Google client ID will not do, or null when it has the shape Google gives one: the ID of a
 * client ends in `.apps.googleusercontent.com`. Caught before saving, because the provider only
 * says so after the person has been sent to sign in.
 */
export function googleClientIdProblem(clientId: string): string | null {
  const id = clientId.trim();
  if (id === '' || /^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$/.test(id)) return null;
  return 'A Google client ID ends in .apps.googleusercontent.com. Copy it from the client you created, not the project number or name.';
}

/**
 * THE GUIDED SIGN-IN'S WAY BACK. The browser leaves the Console for the provider and returns to it
 * through the Host's callback; this note in the tab's session storage is how the Connections dialog
 * knows the round trip began in Add connection, and opens it again at its last step. Taken once.
 */
const GuidedKey = 'connections.guided';

export function rememberGuidedConnect(provider: string, storage: Storage = sessionStorage): void {
  storage.setItem(GuidedKey, provider);
}

export function takeGuidedConnect(storage: Storage = sessionStorage): string | null {
  const provider = storage.getItem(GuidedKey);
  storage.removeItem(GuidedKey);
  return provider;
}
