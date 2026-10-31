// The website runner against a local fake site, in the platform's own Google Chrome (the copy the
// sign-in page runs, under $HARNESS_SIGN_IN_CHROME or /data/google-chrome). No real site, no real
// account. Skipped on a machine with no Chrome or no Xvfb.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { run, MayActRefusal } from '../run.mjs';
import { installed, platformRoot } from '../chrome.mjs';
import { startFakeSite, PASSWORD, USERNAME, TOTP_SECRET, API_TOKEN } from './fakeSite.mjs';

process.env.WEB_CREDENTIALS_PACE ||= '0.2';
const skip = installed(platformRoot()).length === 0 || !existsSync('/usr/bin/Xvfb') ? 'no Google Chrome or Xvfb here' : false;

let site;
let totpSite;
const folders = [];
before(async () => {
  site = await startFakeSite();
  totpSite = await startFakeSite({ totp: true });
});
after(async () => {
  await site.close();
  await totpSite.close();
  for (const f of folders) rmSync(f, { recursive: true, force: true });
});

function profile() {
  const folder = mkdtempSync(join(tmpdir(), 'tc-website-'));
  folders.push(folder);
  return folder;
}

const connection = (fields, secrets = {}, name = 'Fake portal') => ({ name, fields: { siteUrl: site.origin, ...fields }, secrets });

test('a password connection signs in, and its check page is reported with text and a screenshot and no value', { skip }, async () => {
  const p = profile();
  const c = connection({ method: 'password', signInUrl: '/login', checkUrl: '/dashboard', checkText: 'Signed in', username: USERNAME }, { password: PASSWORD });
  const verified = await run({ command: 'verify', connection: c, profile: p });
  assert.equal(verified.status, 'Verified', verified.reason);

  const checked = await run({ command: 'check', connection: c, profile: p, path: '/leaky' });
  assert.equal(checked.status, 'Verified', checked.reason);
  assert.match(checked.report, /HTTP 200/);
  assert.match(checked.report, /text:/);
  assert.ok(checked.screenshot && Buffer.from(checked.screenshot, 'base64').subarray(1, 4).toString() === 'PNG');
  // The page prints its own session cookie and a token from local storage: neither comes back.
  assert.doesNotMatch(checked.report, /sess-[a-z0-9]+/);
  assert.doesNotMatch(checked.report, /ls-access-/);
  assert.doesNotMatch(JSON.stringify(checked).replace(checked.screenshot, ''), new RegExp(PASSWORD));
  assert.match(checked.report, /\[redacted\]/);
});

test('a refused password is Expired with badPassword; a CAPTCHA is Needs you saying Sign in', { skip }, async () => {
  const refused = await run({ command: 'verify', profile: profile(), connection: connection({ method: 'password', signInUrl: '/login', checkUrl: '/dashboard', username: USERNAME }, { password: 'wrong password' }) });
  assert.equal(refused.status, 'Expired');
  assert.equal(refused.badPassword, true);
  assert.doesNotMatch(refused.reason, /wrong password/);

  const captcha = await run({ command: 'verify', profile: profile(), connection: connection({ method: 'password', signInUrl: '/captcha-login', checkUrl: '/dashboard', username: USERNAME }, { password: PASSWORD }) });
  assert.equal(captcha.status, 'Needs you');
  assert.equal(captcha.captcha, true);
  assert.match(captcha.reason, /Sign in on the connection/);
});

test('a two-step password connection types its TOTP code', { skip }, async () => {
  const c = { name: 'Two-step', fields: { siteUrl: totpSite.origin, method: 'password', signInUrl: '/login', checkUrl: '/dashboard', username: USERNAME }, secrets: { password: PASSWORD, totpSecret: TOTP_SECRET } };
  const verified = await run({ command: 'verify', connection: c, profile: profile() });
  assert.equal(verified.status, 'Verified', verified.reason);
});

test('a token connection sends its header to its own hosts only, and a refused token is Expired', { skip }, async () => {
  const ok = await run({ command: 'verify', profile: profile(), connection: connection({ method: 'token', checkUrl: '/api/me', checkText: 'ok signed in' }, { token: API_TOKEN }) });
  assert.equal(ok.status, 'Verified', ok.reason);
  const refused = await run({ command: 'verify', profile: profile(), connection: connection({ method: 'token', checkUrl: '/api/me' }, { token: 'tok_not_the_one' }) });
  assert.equal(refused.status, 'Expired');
  assert.doesNotMatch(refused.reason, /tok_not_the_one/);
});

test('an interactive connection uses its saved session, and an expired one says Sign in', { skip }, async () => {
  const live = JSON.stringify({ v: 2, storageState: site.liveSession(), identity: null });
  const ok = await run({ command: 'check', profile: profile(), path: '/dashboard', connection: connection({ method: 'interactive', checkUrl: '/dashboard', checkText: 'Signed in' }, { session: live }) });
  assert.equal(ok.status, 'Verified', ok.reason);
  assert.match(ok.report, /Dashboard/);

  const dead = JSON.stringify(site.deadSession());
  const expired = await run({ command: 'verify', profile: profile(), connection: connection({ method: 'interactive', checkUrl: '/dashboard', checkText: 'Signed in' }, { session: dead }) });
  assert.equal(expired.status, 'Expired');
  assert.match(expired.reason, /Sign in on the connection/);
});

test('a path off the connection\'s hosts is refused, and a redirect off them is stopped in the browser', { skip }, async () => {
  const c = connection({ method: 'interactive', checkUrl: '/dashboard' }, { session: JSON.stringify(site.liveSession()) });
  const refused = await run({ command: 'check', profile: profile(), connection: c, path: `http://127.0.0.2:${site.port}/elsewhere` });
  assert.equal(refused.refusal, true);
  assert.match(refused.reason, /not one of the connection's hosts/);
  const redirected = await run({ command: 'check', profile: profile(), connection: c, path: '/redir' });
  assert.equal(redirected.offHost, true);
  assert.match(redirected.reason, /127\.0\.0\.2/);
});

test('act is refused while May act on pages is off; on, it fills and submits, held to the hosts', { skip }, async () => {
  const fields = { method: 'password', signInUrl: '/login', checkUrl: '/dashboard', username: USERNAME };
  const steps = [{ action: 'fill', selector: '#note', value: 'hello from a member' }, { action: 'click', selector: '#save' }];
  const off = await run({ command: 'act', profile: profile(), connection: connection(fields, { password: PASSWORD }), path: '/notes', steps });
  assert.equal(off.refusal, true);
  assert.equal(off.reason, MayActRefusal('Fake portal'));
  assert.equal(site.requests.filter((r) => r.path === '/notes').length, 0);

  const p = profile();
  const on = await run({ command: 'act', profile: p, connection: connection({ ...fields, mayAct: 'true' }, { password: PASSWORD }), path: '/notes', steps });
  assert.equal(on.status, 'Verified', on.reason);
  assert.match(on.report, /Saved note: hello from a member/);
  assert.ok(on.screenshot);

  const leave = await run({ command: 'act', profile: p, connection: connection({ ...fields, mayAct: 'true' }, { password: PASSWORD }), path: '/notes', steps: [{ action: 'click', selector: '#off' }] });
  assert.equal(leave.offHost, true);
  assert.match(leave.reason, /127\.0\.0\.2/);
});

test('session hands over a pruned storage state for a password connection, and a token with its header', { skip }, async () => {
  const s = await run({ command: 'session', profile: profile(), minutes: 5, connection: connection({ method: 'password', signInUrl: '/login', checkUrl: '/dashboard', username: USERNAME }, { password: PASSWORD }) });
  assert.equal(s.status, 'Verified', s.reason);
  assert.equal(s.handoff.kind, 'storageState');
  assert.ok(s.handoff.storageState.cookies.some((c) => c.name === 'sess'));
  assert.deepEqual(s.handoff.hosts, ['127.0.0.1']);

  const t = await run({ command: 'session', profile: profile(), connection: connection({ method: 'token', tokenHeader: 'X-Api-Key' }, { token: API_TOKEN }) });
  assert.deepEqual([t.handoff.kind, t.handoff.header, t.handoff.value], ['token', 'X-Api-Key', API_TOKEN]);
});
