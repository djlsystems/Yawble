// A WEBSITE CONNECTION'S BROWSER RUN, ON A WORKER (CON 6). The worker - never the Team Connections
// process, which runs as the agent and holds no value - starts this as its own user with one request on
// stdin, holding the connection's profile lock while it runs, and reads one JSON answer from stdout:
//
//   { command: "verify" | "check" | "session" | "act",
//     connection: { name, fields: {...}, secrets: { password?, totpSecret?, token?, session? } },
//     profile, chromeRoot, path?, expect?, minutes?, steps?: [{ action, selector, value? }] }
//
//   -> { status, reason, captcha?, badPassword?, offHost?, unreachable?, refusal?,
//        report?, screenshot? (PNG, base64), handoff? (a session or a token), sessionExpires? }
//
// verify is the signed-in check (Test connection); check opens a page signed in and reports its text
// and a screenshot; session hands a short-lived session over; act clicks, fills or submits on a page,
// and only when the connection's May act on pages is on. Every command is held to the connection's
// site and other hosts (browser.mjs). Every value of the connection - and of every session the browser
// held - is taken out of reason and report; the handoff alone carries one, and the worker seals it.
// The commands, words and rules are the Web Credentials plugin's (commands.mjs, verify.mjs).
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import { Status, entryFrom, secretValues, stateSecretValues, resolveOnSite, hostAllowed } from './entries.mjs';
import { verify, ensureSignedIn, sessionExpiry } from './verify.mjs';
import { withEntryBrowser, pruneState } from './browser.mjs';
import { makeRedactor } from './redact.mjs';
import { click, type, pressEnter, pause } from './human.mjs';

export const MayActRefusal = (name) =>
  `${name}: acting on pages is off for this connection. A person turns on May act on pages in the connection's settings (Admin -> Connections) to allow click, fill and submit.`;

// The most of a page's text an answer carries.
const MaxText = 4000;

export async function run(request) {
  const entry = entryFrom(request.connection);
  const redactor = makeRedactor(secretValues(entry));
  const browserRun = {
    profile: request.profile,
    chromeRoot: request.chromeRoot,
    extraArgs: Array.isArray(request.extraArgs) ? request.extraArgs : [],
    sessionSeen: (held) => redactor.add(stateSecretValues(held)),
  };

  let answer;
  switch (request.command) {
    case 'verify':
      answer = await verify(entry, browserRun);
      break;
    case 'check':
      answer = await check(entry, browserRun, request.path, request.expect, null);
      break;
    case 'act':
      answer = entry.mayAct
        ? await check(entry, browserRun, request.path, null, Array.isArray(request.steps) ? request.steps : [])
        : { status: Status.verified, refusal: true, reason: MayActRefusal(entry.name) };
      break;
    case 'session':
      answer = await session(entry, browserRun, request.minutes);
      break;
    default:
      answer = { status: Status.failed, reason: `The website runner does not know the command "${request.command}".` };
  }

  // The words are redacted of every value; the handoff and the screenshot are left as they are.
  const out = { ...answer };
  for (const key of ['reason', 'report']) if (typeof out[key] === 'string') out[key] = redactor.redact(out[key]);
  delete out.state;
  return out;
}

// SIGNED IN, OPEN THE PAGE, OPTIONALLY ACT ON IT, AND REPORT IT: the Web Credentials `check`, with
// its sign-in on the same page for a password connection, and act's steps after it.
async function check(entry, run, path, expect, steps) {
  if (entry.method === 'interactive' && !entry.session) {
    return { status: Status.needsYou, reason: `${entry.name} needs you: sign in once with Sign in on the connection.` };
  }
  if (entry.method === 'token' && !entry.secrets.token) {
    return { status: Status.needsYou, reason: `${entry.name} needs you: it has no token. Edit the connection and set the Token.` };
  }
  const shown = path || entry.checkUrl || '/';
  const target = resolveOnSite(entry, shown, 'the path');
  if (target.refusal) return { status: Status.verified, refusal: true, reason: target.refusal };
  for (const step of steps || []) {
    if (!['click', 'fill', 'submit'].includes(step?.action) || typeof step.selector !== 'string' || step.selector.trim() === '') {
      return { status: Status.verified, refusal: true, reason: `${entry.name}: each step is click "<selector>", fill "<selector>" "<text>" or submit "<selector>".` };
    }
  }

  return withEntryBrowser(entry, run, async (page, context, guard) => {
    if (entry.method === 'password') {
      // Sign in on this very page (unless the profile still is), so the path below is opened by the
      // signed-in browser. A sign-in here is confirmed as any verify is.
      const outcome = await ensureSignedIn(page, entry, guard, true);
      if (!outcome.ok) return { ...outcome, reason: `${entry.name}: the sign-in FAILED, so ${shown} was not opened. ${outcome.reason}` };
    }

    let response;
    try {
      response = await page.goto(target.url.toString(), { waitUntil: 'domcontentloaded', timeout: 20000 });
    } catch (e) {
      if (guard.blocked.length) return offHost(entry, `${shown} redirected to ${guard.blocked[0]}, off the connection's hosts; the browser did not go there.`);
      return { status: Status.failed, unreachable: true, reason: `${entry.name}: ${shown} could not be opened (${String(e.message).split('\n')[0]}).` };
    }
    const landed = new URL(page.url());
    if (!hostAllowed(entry, landed)) {
      return offHost(entry, `${shown} redirected to ${guard.blocked[0] || landed.hostname}, off the connection's hosts; the browser did not go there.`);
    }
    await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => {});

    // A page that is not signed in any more (the sign-in page, a 401) is an expired session.
    const status = response ? response.status() : 0;
    if (entry.method !== 'password' && (status === 401 || status === 403)) {
      return { status: Status.expired, reason: `${entry.name} is expired: ${shown} answered ${status}. Sign in again with Sign in on the connection.` };
    }

    const done = [];
    for (const step of steps || []) {
      const element = await page.$(step.selector).catch(() => null);
      if (!element || !(await element.isVisible().catch(() => false))) {
        return { status: Status.verified, refusal: true, reason: `${entry.name}: nothing on ${landed.pathname} matches ${step.selector}, so ${step.action} was not done${done.length ? ` (done before it: ${done.join(', ')})` : ''}.` };
      }
      const navigated = page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 8000 }).catch(() => {});
      if (step.action === 'click') await click(page, element);
      else if (step.action === 'fill') await type(page, element, String(step.value ?? ''));
      else {
        const tag = await element.evaluate((el) => el.tagName.toLowerCase()).catch(() => '');
        if (tag === 'form') await element.evaluate((form) => form.requestSubmit());
        else if (tag === 'input' || tag === 'textarea' || tag === 'select') {
          await click(page, element);
          await pressEnter(page);
        } else await click(page, element);
      }
      if (step.action !== 'fill') await navigated;
      await pause(150, 350);
      done.push(`${step.action} ${step.selector}`);
      if (guard.blocked.length || !hostAllowed(entry, new URL(page.url()))) {
        return offHost(entry, `${step.action} ${step.selector} tried to go to ${guard.blocked[0] || new URL(page.url()).hostname}, off the connection's hosts; the browser stopped it.`);
      }
    }
    if (done.length) await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => {});

    // A page that tried to leave after it loaded (a meta refresh, a script) was stopped in the
    // browser; what is on screen now is Chrome's own error page, not the site's.
    if (guard.blocked.length || !hostAllowed(entry, new URL(page.url()))) {
      return offHost(entry, `${shown} loaded, then tried to go to ${guard.blocked[0] || new URL(page.url()).hostname}, off the connection's hosts; the browser stopped it, so there is no page to report.`);
    }

    const now = new URL(page.url());
    const title = await page.title().catch(() => '');
    const heading = await page.$eval('h1', (el) => el.textContent.trim()).catch(() => '');
    const body = (await page.evaluate(() => (document.body ? document.body.innerText : '')).catch(() => '')).trim();
    const html = expect ? await page.content().catch(() => '') : '';
    const found = expect ? html.includes(expect) || body.includes(expect) : null;
    const screenshot = await page.screenshot({ fullPage: false, type: 'png' }).catch(() => null);
    const held = await context.storageState().catch(() => null);

    const lines = [
      `${entry.name} ${shown}:`,
      `  HTTP ${status}, ${now.origin}${now.pathname}`,
      `  title: ${title || '(none)'}`,
      `  heading: ${heading || '(none)'}`,
    ];
    if (done.length) lines.push(`  done: ${done.join('; ')}`);
    if (expect !== null && expect !== undefined) lines.push(`  expected text ${found ? 'found' : 'NOT found'}: "${expect}"`);
    for (const host of guard.framesBlocked || []) lines.push(`  a frame from ${host} was blocked: off the connection's hosts`);
    lines.push('  text:');
    const text = body.length > MaxText ? `${body.slice(0, MaxText)}…` : body;
    lines.push(...(text ? text.split('\n').map((l) => `    ${l}`) : ['    (none)']));

    return {
      status: Status.verified,
      reason: `${entry.name} is verified.`,
      sessionExpires: sessionExpiry(held),
      report: lines.join('\n'),
      screenshot: screenshot ? screenshot.toString('base64') : null,
    };
  }).catch((e) => ({ status: Status.failed, reason: `${entry.name}: ${String(e.message).split('\n')[0]}` }));
}

// A page the member opened or acted on that led off the connection's hosts: stopped in the browser and
// refused, not the connection's fault (a sign-in that leaves them is: verify.mjs says Needs you).
function offHost(entry, what) {
  return { status: Status.verified, refusal: true, offHost: true, reason: `${entry.name}: ${what} Ask a person to add the host to the connection's Other hosts if it belongs to the site.` };
}

// A SHORT-LIVED SESSION FOR THE MEMBER THAT ASKED, as Web Credentials hands one over: a token and its
// header for a token connection; for an interactive one its saved session; for a password one the
// session of a sign-in made now. Pruned to the connection's hosts. The worker seals it; the Host
// writes it into the asking member's workspace and deletes it when it expires.
async function session(entry, run, minutes) {
  const span = Math.max(1, Math.min(120, parseInt(minutes, 10) || 30));
  const expiresAt = new Date(Date.now() + span * 60000).toISOString();

  if (entry.method === 'token') {
    const token = entry.secrets.token;
    if (!token) return { status: Status.needsYou, reason: `${entry.name} needs you: it has no token. Edit the connection and set the Token.` };
    const header = entry.tokenHeader || 'Authorization';
    const value = header.toLowerCase() === 'authorization' ? `Bearer ${token}` : token;
    return { status: Status.verified, reason: `${entry.name} is verified.`, handoff: { kind: 'token', header, value, hosts: entry.hosts, expiresAt } };
  }

  let state;
  if (entry.method === 'interactive') {
    if (!entry.session) return { status: Status.needsYou, reason: `${entry.name} needs you: sign in once with Sign in on the connection.` };
    state = pruneState(entry.session.storageState, entry.hosts);
  } else {
    const result = await verify(entry, run);
    if (result.status !== Status.verified || !result.state) {
      return { ...result, reason: `${entry.name}: no session was handed over: the sign-in failed. ${result.reason}` };
    }
    state = pruneState(result.state, entry.hosts);
  }
  return {
    status: Status.verified,
    reason: `${entry.name} is verified.`,
    sessionExpires: sessionExpiry(state),
    handoff: { kind: 'storageState', storageState: state, hosts: entry.hosts, expiresAt },
  };
}

async function main() {
  let request;
  try {
    request = JSON.parse(readFileSync(0, 'utf8'));
  } catch {
    process.stdout.write(JSON.stringify({ status: Status.failed, reason: 'The website runner could not read its request.' }) + '\n');
    process.exit(1);
  }
  let answer;
  try {
    answer = await run(request);
  } catch (e) {
    // The message's first line only, redacted with the rest by the worker: never a stack.
    answer = { status: Status.failed, reason: `The website run failed: ${String(e?.message ?? e).split('\n')[0]}` };
  }
  process.stdout.write(JSON.stringify(answer) + '\n');
}

if (process.argv[1] && basename(process.argv[1]) === 'run.mjs') {
  main();
}
