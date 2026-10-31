// A WEBSITE CONNECTION AS THE BROWSER USES IT: its fields (every value but a masked one) and its masked
// values (password, TOTP secret, token, saved session), as the worker hands them over for one run. The
// same entry, words and rules as a Web Credentials entries row (moved from it, CON 6).

export const Methods = ['password', 'token', 'interactive'];

export const Status = {
  verified: 'Verified',
  expired: 'Expired',
  needsYou: 'Needs you',
  failed: 'Failed',
  never: 'Never checked',
};

const secretColumns = ['password', 'totpSecret', 'token', 'session'];

// lower-cased key -> value, so a row's secrets are found whatever case the settings spell it in.
function byLowerKey(object) {
  const out = new Map();
  if (object && typeof object === 'object') {
    for (const [k, v] of Object.entries(object)) out.set(k.toLowerCase(), v);
  }
  return out;
}

function text(value) {
  return typeof value === 'string' ? value.trim() : '';
}

const bareHost =
  /^(?:(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:\p{L}{2,63}|xn--[A-Za-z0-9-]{1,59})|localhost|(?:[0-9]{1,3}\.){3}[0-9]{1,3})\.?(?::[0-9]{1,5})?$/iu;

// A Site URL, Sign-in URL or check URL written as a bare address (mysite.com, www.mysite.com/login)
// with https:// before it; anything else exactly as written - any scheme (http stays http,
// javascript: stays and is refused as before), a path (/login, dashboard) and text that is not an
// address. Yawble's "format": "url" does the same on a save; this covers a row written before it.
export function withScheme(value) {
  const raw = text(value);
  if (!raw) return raw;
  if (/^[a-z][a-z0-9+.-]*:/i.test(raw) && !/^[^:/?#]+:[0-9]+(?:[/?#]|$)/.test(raw)) return raw;
  return bareHost.test(raw.split(/[/?#]/)[0]) ? `https://${raw}` : raw;
}

// The hosts an entry may use: its Site URL's, then each in `hosts` (names or URLs, separated by
// commas or spaces), lower case, without port. Answers [] for a Site URL that is not http(s).
export function entryHosts(siteUrl, hosts) {
  const out = [];
  const add = (h) => {
    const host = h.toLowerCase().replace(/\.$/, '');
    if (host && !out.includes(host)) out.push(host);
  };
  let site;
  try {
    site = new URL(siteUrl);
  } catch {
    return [];
  }
  if (site.protocol !== 'https:' && site.protocol !== 'http:') return [];
  add(site.hostname);
  for (const part of text(hosts).split(/[\s,]+/)) {
    if (!part) continue;
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(part)) {
      try {
        add(new URL(part).hostname);
      } catch {
        // not a URL: skipped
      }
    } else {
      add(part.replace(/\/.*$/, '').replace(/:\d+$/, ''));
    }
  }
  return out;
}

export function hostAllowed(entry, url) {
  let u;
  try {
    u = typeof url === 'string' ? new URL(url) : url;
  } catch {
    return false;
  }
  if (u.protocol !== 'https:' && u.protocol !== 'http:') return false;
  return entry.hosts.includes(u.hostname.toLowerCase());
}

// Resolves a path or URL on the entry's site: a path (/dashboard) is taken against the Site URL; a
// full URL must be on the entry's hosts. Answers { url } or { refusal }.
export function resolveOnSite(entry, pathOrUrl, what) {
  const raw = text(pathOrUrl);
  if (!raw) return { url: new URL(entry.siteUrl) };
  let url;
  try {
    url = /^[a-z][a-z0-9+.-]*:/i.test(raw) ? new URL(raw) : new URL(raw.startsWith('/') ? raw : '/' + raw, entry.siteUrl);
  } catch {
    return { refusal: `${entry.name}: ${what} "${raw}" is not a path or URL.` };
  }
  if (!hostAllowed(entry, url)) {
    return { refusal: `${entry.name}: ${what} is on ${url.hostname || url.protocol}, which is not one of the connection's hosts (${entry.hosts.join(', ')}).` };
  }
  return { url };
}

// The connection's entry: { name, siteUrl, hosts, method, ..., secrets, session, written }.
export function entryFrom(connection) {
  const fields = connection?.fields && typeof connection.fields === 'object' ? connection.fields : {};
  const given = connection?.secrets && typeof connection.secrets === 'object' ? connection.secrets : {};
  const name = text(connection?.name) || 'This website';
  const secrets = {};
  for (const column of secretColumns) {
    const v = given[column];
    if (typeof v === 'string' && v !== '') secrets[column] = v;
  }
  const siteUrl = withScheme(fields.siteUrl);
  return {
    name,
    siteUrl,
    hosts: entryHosts(siteUrl, fields.hosts),
    method: Methods.includes(fields.method) ? fields.method : '',
    signInUrl: withScheme(fields.signInUrl),
    checkUrl: withScheme(fields.checkUrl),
    checkText: text(fields.checkText),
    username: text(fields.username),
    tokenHeader: text(fields.tokenHeader) || 'Authorization',
    selectors: {
      username: text(fields.usernameSelector),
      password: text(fields.passwordSelector),
      submit: text(fields.submitSelector),
      totp: text(fields.totpSelector),
    },
    // The browser's languages and time zone when no saved session names them (profile.mjs).
    settings: { languages: text(fields.languages), timeZone: text(fields.timeZone) },
    mayAct: fields.mayAct === 'true' || fields.mayAct === true,
    secrets,
    // The saved session, parsed: { storageState, identity, text }, or null when there is none or it
    // could not be read (secrets.session is then set but this is null).
    session: readSession(secrets.session),
    // What a person writes, for noticing an edit: every field.
    written: Object.fromEntries(Object.keys(fields).sort().map((k) => [k, fields[k]])),
  };
}

// Every value of the connection, for this run's own redaction (the worker and the Host redact too): a
// string is redacted wherever it appears, a { token } only as a whole token (see stateSecretValues).
// The username is the connection's too, and is taken out of what comes back with the rest.
export function secretValues(entry) {
  const values = new Set();
  for (const v of Object.values(entry.secrets)) if (typeof v === 'string' && v.length >= 4) values.add(v);
  if (entry.username.length >= 4) values.add(entry.username);
  const out = [...values];
  // A session's cookie and storage values are credentials of their own.
  if (entry.session) out.push(...stateSecretValues(entry.session.storageState));
  return out;
}

// Whole values that are settings, not secrets: never redacted, compared case-insensitively.
const NOT_SECRET = new Set(['true', 'false', 'null', 'none', 'undefined', 'yes', 'no', 'on', 'off', 'dark', 'light', 'auto', 'default']);

// The values of a storage state that are credentials, part by part, as the platform redacts a saved
// session: each whole cookie value and local-storage value of 4 characters or more, and each string
// of 8 or more inside a local-storage value that is JSON. A value of 4-7 characters comes back as
// { token } - redacted only as a whole token, not inside a longer word - and one of 8 or more as a
// string, redacted wherever it appears. 1-3 characters and settings words (true, dark...) never.
// Whatever the row's method, so a page that prints its own cookie does not put it in an answer.
export function stateSecretValues(state) {
  const out = new Map();
  const one = (v, whole) => {
    if (NOT_SECRET.has(v.toLowerCase())) return;
    if (v.length >= 8) out.set(v, v);
    else if (whole && v.length >= 4 && !out.has(v)) out.set(v, { token: v });
  };
  // Each value and its URL-decoded form ('+' as a space), as the platform's SecretParts.
  const add = (v, whole = true) => {
    if (typeof v !== 'string') return;
    one(v, whole);
    const decoded = urlDecoded(v);
    if (decoded !== v) one(decoded, whole);
  };
  const inside = (v) => {
    if (typeof v === 'string') add(v, false);
    else if (Array.isArray(v)) v.forEach(inside);
    else if (v && typeof v === 'object') Object.values(v).forEach(inside);
  };
  for (const c of state?.cookies ?? []) add(c.value);
  for (const o of state?.origins ?? []) {
    for (const s of o.localStorage ?? []) {
      add(s.value);
      try {
        inside(JSON.parse(s.value));
      } catch {
        // not JSON: the whole value is enough
      }
    }
  }
  return [...out.values()];
}

// Uri.UnescapeDataString of v with '+' as a space: each run of %XX decoded, one that is not UTF-8 left.
function urlDecoded(v) {
  return v.replace(/\+/g, ' ').replace(/(?:%[0-9A-Fa-f]{2})+/g, (m) => {
    try {
      return decodeURIComponent(m);
    } catch {
      return m;
    }
  });
}

// A saved session as the platform's sign-in page keeps it: the v2 envelope
// { "v": 2, "storageState": {...}, "identity": {...} }, or a bare storage state saved before the
// envelope (v1). Answers { storageState, identity (null for v1), text } or null when it is unreadable.
export function readSession(text) {
  if (typeof text !== 'string' || text === '') return null;
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  if (!value || typeof value !== 'object') return null;
  const isState = (s) => s && typeof s === 'object' && (Array.isArray(s.cookies) || Array.isArray(s.origins));
  const tidy = (s) => ({ cookies: Array.isArray(s.cookies) ? s.cookies : [], origins: Array.isArray(s.origins) ? s.origins : [] });
  if (value.v === 2 && isState(value.storageState)) {
    const identity = value.identity && typeof value.identity === 'object' ? value.identity : null;
    return { storageState: tidy(value.storageState), identity, text };
  }
  if (isState(value)) return { storageState: tidy(value), identity: null, text };
  return null;
}
