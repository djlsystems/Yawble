// EVERY VALUE OF THE CONNECTION TAKEN OUT of what a website run answers, before it leaves the runner:
// the worker and the Host redact again with the platform's ValueRedactor; this is the runner's own net
// under them, with the same rule. Moved from the Web Credentials plugin's out.mjs (CON 6).

// Where a whole token may start, as the platform's ValueRedactor: after no letter or digit, or right
// after an escape that ends in one - \u0022 as JSON writes a quote, \n, %22 - so an escaped copy is
// caught. After it, an escape starts with \ or %, which is no letter or digit already.
const TOKEN_START = '(?:(?<![\\p{L}\\p{N}])|(?<=\\\\u[0-9A-Fa-f]{4})|(?<=\\\\[nrtbf])|(?<=%[0-9A-Fa-f]{2}))';

// A value, and its body inside a JSON string as .NET's default encoder writes it (+ < > & ' " ` and
// every non-ASCII character as \uXXXX), as the relaxed encoder does (only " \ and controls), and
// the relaxed one with / as \/ (PHP, some Java encoders).
export function spellings(value) {
  const control = { '\b': '\\b', '\f': '\\f', '\n': '\\n', '\r': '\\r', '\t': '\\t' };
  const u = (c) => '\\u' + c.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0');
  const body = (strict) =>
    value
      .split('')
      .map((c) => {
        if (c === '\\') return '\\\\';
        if (control[c]) return control[c];
        const code = c.charCodeAt(0);
        if (code < 0x20) return u(c);
        if (c === '"') return strict ? u(c) : '\\"';
        if (strict && (code >= 0x7f || '+<>&\'`'.includes(c))) return u(c);
        return c;
      })
      .join('');
  const relaxed = body(false);
  return [...new Set([value, body(true), relaxed, relaxed.replaceAll('/', '\\/')])];
}

// A redactor of `list`: each item a string (redacted wherever it appears) or { token } (only as a whole
// token, where no letter or digit touches it), each also in every JSON spelling. Values shorter than 4
// are never redacted. `add` takes more, learned during the run (the session a sign-in obtains).
export function makeRedactor(list = []) {
  let values = [];
  let given = [];
  const set = (items) => {
    given = [...items];
    const byValue = new Map();
    for (const x of items) {
      const item = typeof x === 'string' ? { v: x, whole: false } : x && typeof x.token === 'string' ? { v: x.token, whole: true } : null;
      if (!item || item.v.length < 4) continue;
      for (const v of spellings(item.v)) {
        const key = v.toLowerCase();
        const seen = byValue.get(key);
        byValue.set(key, { v, whole: (seen ? seen.whole : true) && item.whole });
      }
    }
    // Longest first; of two the same length, the one redacted anywhere first.
    values = [...byValue.values()].sort((a, b) => b.v.length - a.v.length || a.whole - b.whole);
  };
  set(list);
  const redact = (s) => {
    if (typeof s !== 'string') return s;
    let out = s;
    for (const { v, whole } of values) {
      const escaped = v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const pattern = whole ? `${TOKEN_START}${escaped}(?![\\p{L}\\p{N}])` : escaped;
      out = out.replace(new RegExp(pattern, 'giu'), '[redacted]');
    }
    return out;
  };
  return { redact, add: (more) => set([...given, ...more]) };
}
