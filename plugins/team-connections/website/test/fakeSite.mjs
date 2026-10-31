// Copied from the Web Credentials plugin's tests, with pages to act on (CON 6).
// A local fake site the tests sign in to. No real account, no network beyond loopback. Two hosts:
// the primary (127.0.0.1) and an "other" host (127.0.0.2) a sign-in or check can wrongly land on.
// It has: a password form, a two-step (TOTP) step, a token API, a session that can be made to
// expire, a CAPTCHA page, a page that redirects off-host, and a password it keeps rejecting
// (for the lockout guard). And a probe page that reports what a site can see of the browser, and
// the per-key timing of a sign-in's typing.
import { createServer } from 'node:http';
import { totp } from '../totp.mjs';

export const PASSWORD = 'correct horse';
export const USERNAME = 'admin@example.com';
export const TOTP_SECRET = 'JBSWY3DPEHPK3PXP';
export const API_TOKEN = 'tok_example_123456';

function page(body) {
  return `<!doctype html><html><head><title>Fake</title></head><body>${body}</body></html>`;
}

function cookies(req) {
  const out = {};
  for (const part of (req.headers.cookie || '').split(';')) {
    const [k, v] = part.split('=');
    if (k) out[k.trim()] = (v || '').trim();
  }
  return out;
}

// opts: { captcha, offsite, rejectPassword } flip the awkward behaviours on.
export function startFakeSite(opts = {}) {
  const bindHost = opts.host || '127.0.0.1';
  const sessions = new Set(); // live session tokens
  let counter = 0;
  const probes = []; // what each visit to /probe saw: { headers, report }
  const keyTimes = []; // per sign-in POST: the page-observed key times of each field
  const requests = []; // every request the site received: { method, path }
  let password = PASSWORD; // the site's password now; a test can change it
  let lastSession = null; // the newest session cookie value the site gave out

  const handler = (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    requests.push({ method: req.method, path: url.pathname });
    const send = (status, body, headers = {}) => {
      res.writeHead(status, { 'content-type': 'text/html; charset=utf-8', ...headers });
      res.end(body);
    };

    // The token API: a bearer header signed in, anything else 401.
    if (url.pathname === '/api/me') {
      const auth = req.headers.authorization || '';
      if (auth === `Bearer ${API_TOKEN}`) return send(200, page('<h1>Me</h1>ok signed in'));
      return send(401, page('<h1>Unauthorized</h1>'));
    }

    // THE PROBE: what a site can see. The page's script reports the browser's own view (navigator,
    // client hints with their high-entropy values, WebGL, screen, viewport, time zone, and a mark left
    // in local storage by an earlier visit); the server keeps the headers the page was asked with and
    // the ones the report came with (after Accept-CH, the high-entropy Sec-CH-UA-* headers).
    if (url.pathname === '/probe') {
      const visit = { headers: { ...req.headers }, cookieMark: cookies(req).mark || null, report: null, reportHeaders: null };
      probes.push(visit);
      const n = probes.length;
      return send(200, page(`<h1>Probe</h1><p id="who">Signed in</p><script>
        (async () => {
          const r = {};
          r.userAgent = navigator.userAgent;
          r.webdriver = navigator.webdriver;
          r.pluginsLength = navigator.plugins.length;
          r.languages = navigator.languages;
          r.language = navigator.language;
          r.timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
          r.screen = { width: screen.width, height: screen.height, availWidth: screen.availWidth, availHeight: screen.availHeight, colorDepth: screen.colorDepth };
          r.viewport = { width: innerWidth, height: innerHeight };
          r.outer = { width: outerWidth, height: outerHeight };
          r.devicePixelRatio = devicePixelRatio;
          r.hardwareConcurrency = navigator.hardwareConcurrency;
          r.chromeObject = typeof window.chrome === 'object';
          try {
            const gl = document.createElement('canvas').getContext('webgl');
            const ext = gl && gl.getExtension('WEBGL_debug_renderer_info');
            r.webgl = gl ? { vendor: gl.getParameter(ext ? ext.UNMASKED_VENDOR_WEBGL : gl.VENDOR), renderer: gl.getParameter(ext ? ext.UNMASKED_RENDERER_WEBGL : gl.RENDERER) } : null;
          } catch (e) { r.webgl = null; }
          if (navigator.userAgentData) {
            r.uaData = { brands: navigator.userAgentData.brands, mobile: navigator.userAgentData.mobile, platform: navigator.userAgentData.platform };
            r.uaHigh = await navigator.userAgentData.getHighEntropyValues(['architecture', 'bitness', 'model', 'platformVersion', 'fullVersionList', 'uaFullVersion']);
          }
          r.markBefore = localStorage.getItem('probe-mark');
          if (!r.markBefore) localStorage.setItem('probe-mark', 'visit-${n}');
          await fetch('/probe-report?n=${n}', { method: 'POST', body: JSON.stringify(r) });
          document.title = 'probe-done';
        })();
      </script>`), {
        'accept-ch': 'Sec-CH-UA-Full-Version-List, Sec-CH-UA-Platform-Version, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness',
        'set-cookie': `mark=${cookies(req).mark || 'visit-' + n}; Path=/; Max-Age=86400`,
      });
    }
    if (url.pathname === '/probe-report' && req.method === 'POST') {
      let data = '';
      req.on('data', (c) => (data += c));
      req.on('end', () => {
        const visit = probes[Number(url.searchParams.get('n')) - 1];
        if (visit) {
          visit.report = JSON.parse(data);
          visit.reportHeaders = { ...req.headers };
        }
        send(204, '');
      });
      return;
    }

    // The signed-in check page.
    if (url.pathname === '/dashboard') {
      const c = cookies(req);
      if (c.sess && sessions.has(c.sess)) return send(200, page('<h1>Dashboard</h1><p id="who">Signed in</p>'));
      return send(302, '', { location: '/login' });
    }

    if (url.pathname === '/captcha-login') {
      return send(200, page('<h1>Sign in</h1><div class="g-recaptcha">Please prove you are human (captcha)</div>'));
    }

    if (url.pathname === '/offsite-login') {
      return send(302, '', { location: (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/elsewhere' });
    }

    // A signed-in page that redirects off the site's host.
    if (url.pathname === '/redir') {
      return send(302, '', { location: (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/landing' });
    }

    // Signed-in pages that leave for the other host after they load: a meta refresh, and an iframe.
    if (url.pathname === '/meta-off') {
      const to = (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/landing';
      return send(200, `<!doctype html><html><head><title>Leaving</title><meta http-equiv="refresh" content="0; url=${to}"></head><body><h1>Leaving</h1></body></html>`);
    }
    if (url.pathname === '/js-off') {
      const to = (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/landing';
      return send(200, `<!doctype html><html><head><title>Leaving</title></head><body><h1>Leaving</h1><script>location.href = ${JSON.stringify(to)};</script></body></html>`);
    }
    if (url.pathname === '/frame-off') {
      const to = (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/framed';
      return send(200, page(`<h1>Framed</h1><iframe src="${to}"></iframe>`));
    }

    // A page that prints its own session: the cookie in its title and heading, and a JSON value it
    // keeps in local storage in its paragraph.
    if (url.pathname === '/leaky') {
      const sess = cookies(req).sess || '';
      // More cookies the page sets and prints: a 7-character one (redacted at 4+), a 3-character one
      // (too short to redact), consent=true (a settings word, never redacted, nor inside "trueness")
      // and a 5-character one printed only inside a longer word (not a whole token, so not redacted)
      // and after an escape, as JSON (\u0022) and a URL (%22) write a quote (a whole token, redacted).
      // A JSON local-storage value holds "undefined", a settings word that is never redacted.
      return send(200, `<!doctype html><html><head><title>Session ${sess}</title></head><body><h1>Cookie ${sess}</h1><p id="ls"></p><p id="short"></p><script>
        document.cookie = 'code=code123; Path=/';
        document.cookie = 'tiny=xq7; Path=/';
        document.cookie = 'consent=true; Path=/';
        document.cookie = 'pin=k7q2z; Path=/';
        document.getElementById('short').textContent = 'Code ' + 'code123' + ' tiny ' + 'xq7' + ' consent true trueness word abck7q2z9';
        document.title = document.title + ' code123 consent true trueness word abck7q2z9';
        document.title += ' json \\\\u0022k7q2z\\\\u0022 url %22k7q2z%22';
        localStorage.setItem('prefs', JSON.stringify({ theme: 'undefined', mode: 'undefined' }));
        document.title += ' prefs ' + JSON.parse(localStorage.getItem('prefs')).theme;
        localStorage.setItem('auth', JSON.stringify({ accessToken: 'ls-access-${sess}-token', n: 1 }));
        document.getElementById('ls').textContent = JSON.parse(localStorage.getItem('auth')).accessToken;
        document.title = document.title + ' ' + JSON.parse(localStorage.getItem('auth')).accessToken;
      </script></body></html>`);
    }

    // A signed-in page to act on: a form whose submit shows what was filled, and a link off the site.
    if (url.pathname === '/notes') {
      const c = cookies(req);
      if (!(c.sess && sessions.has(c.sess))) return send(302, '', { location: '/login' });
      if (req.method === 'POST') {
        return readBody(req, (form) => send(200, page(`<h1>Saved</h1><p id="saved">Saved note: ${form.note}</p>`)));
      }
      const off = (opts.offsiteOrigin || `http://127.0.0.2:${url.port}`) + '/elsewhere';
      return send(200, page(`<h1>Notes</h1><form method="post" action="/notes" id="f"><input name="note" id="note"/><button id="save" type="submit">Save</button></form><a id="off" href="${off}">Leave</a>`));
    }

    if (url.pathname === '/elsewhere') {
      return send(200, page('<h1>Another host</h1>'));
    }

    // The password form and its POST.
    if (url.pathname === '/login') {
      if (req.method !== 'POST') {
        const captcha = opts.captcha ? '<div class="h-captcha">captcha</div>' : '';
        return send(200, page(`<h1>Sign in</h1>${captcha}<form method="post" action="/login">
          <input type="email" name="email" />
          <input type="password" name="password" />
          <input type="hidden" name="keys" />
          <button type="submit">Log in</button></form>${keyScript}`));
      }
      return readBody(req, (form) => {
        if (form.keys) keyTimes.push(JSON.parse(form.keys));
        if (opts.rejectPassword || form.password !== password || form.email !== USERNAME) {
          return send(200, page('<h1>Sign in</h1><p class="error">Wrong username or password</p><form method="post" action="/login"><input type="email" name="email"/><input type="password" name="password"/><button type="submit">Log in</button></form>'));
        }
        if (opts.totp) {
          const pending = 'pending' + ++counter;
          sessions.add('half:' + pending);
          return send(302, '', { location: '/totp', 'set-cookie': `half=${pending}; Path=/` });
        }
        const token = 'sess-' + Math.random().toString(36).slice(2, 12) + ++counter;
        sessions.add(token);
        lastSession = token;
        return send(302, '', { location: '/dashboard', 'set-cookie': `sess=${token}; Path=/` });
      });
    }

    if (url.pathname === '/totp') {
      if (req.method !== 'POST') {
        return send(200, page('<h1>Two-step</h1><form method="post" action="/totp"><input autocomplete="one-time-code" name="code"/><button type="submit">Verify</button></form>'));
      }
      return readBody(req, (form) => {
        const expected = totp(TOTP_SECRET);
        if (form.code !== expected) {
          return send(200, page('<h1>Two-step</h1><p class="error">Wrong code</p><form method="post" action="/totp"><input autocomplete="one-time-code" name="code"/><button type="submit">Verify</button></form>'));
        }
        const token = 'sess' + ++counter;
        sessions.add(token);
        return send(302, '', { location: '/dashboard', 'set-cookie': `sess=${token}; Path=/` });
      });
    }

    if (url.pathname === '/') return send(200, page('<h1>Home</h1>'));
    return send(404, page('<h1>Not found</h1>'));
  };

  const server = createServer(handler);
  return new Promise((resolve) => {
    server.listen(0, bindHost, () => {
      const port = server.address().port;
      resolve({
        host: bindHost,
        port,
        origin: `http://${bindHost}:${port}`,
        // A valid storageState for the interactive method: a known live session cookie.
        liveSession(host = '127.0.0.1') {
          const token = 'sess-live-' + ++counter;
          sessions.add(token);
          return { cookies: [{ name: 'sess', value: token, domain: host, path: '/', expires: Math.floor(Date.now() / 1000) + 3600, httpOnly: false, secure: false, sameSite: 'Lax' }], origins: [] };
        },
        deadSession(host = '127.0.0.1') {
          return { cookies: [{ name: 'sess', value: 'gone', domain: host, path: '/', expires: -1, httpOnly: false, secure: false, sameSite: 'Lax' }], origins: [] };
        },
        probes,
        keyTimes,
        requests,
        // Sign-in POSTs the site has received so far.
        signInPosts: () => requests.filter((r) => r.method === 'POST' && r.path === '/login').length,
        lastSession: () => lastSession,
        // The person changes the password on the site, and the site ends every session.
        changePassword(next) {
          password = next;
          sessions.clear();
        },
        close: () => new Promise((r) => server.close(r)),
      });
    });
  });
}

// Records, per field, when each key went down (performance.now()), and how each field was filled: a
// value that appears with no keys behind it was filled, not typed.
const keyScript = `<script>
  const times = {};
  for (const el of document.querySelectorAll('input[name]')) {
    el.addEventListener('keydown', (e) => { (times[el.name] ??= []).push(performance.now()); });
  }
  document.querySelector('form').addEventListener('submit', () => {
    document.querySelector('input[name=keys]').value = JSON.stringify(times);
  });
</script>`;

function readBody(req, cb) {
  let data = '';
  req.on('data', (c) => (data += c));
  req.on('end', () => {
    const form = {};
    for (const pair of data.split('&')) {
      const [k, v] = pair.split('=');
      if (k) form[decodeURIComponent(k)] = decodeURIComponent((v || '').replace(/\+/g, ' '));
    }
    cb(form);
  });
}
