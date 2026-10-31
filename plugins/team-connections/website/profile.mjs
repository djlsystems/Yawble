// THE IDENTITY A WEBSITE CONNECTION'S BROWSER SHOWS, kept beside its profile. The connection has one
// lasting Chrome profile on a worker, the one Sign in uses (<profile>/chrome), reused on every run, so a
// site sees the same browser coming back; <profile>/website.json keeps the identity chosen for it and a
// salted digest of the saved session last applied (never a value). Moved from the Web Credentials
// plugin (CON 6), where a profile was a plugin member's row's.
//
// The identity is the saved session's (a v2 envelope from the platform's sign-in page) when there is
// one, so this browser is the browser the person signed in with; otherwise the connection's languages
// and time zone (under Advanced), en-US and UTC when they are empty or unusable, on an ordinary
// 1920x1080 desktop screen. Once chosen it is kept for the connection.
import { mkdirSync, readFileSync, writeFileSync, renameSync, rmSync, readlinkSync, existsSync, chmodSync } from 'node:fs';
import { join } from 'node:path';
import { scryptSync } from 'node:crypto';

export const DefaultScreen = { width: 1920, height: 1080 };

export function loadMeta(profile) {
  try {
    return JSON.parse(readFileSync(join(profile, 'website.json'), 'utf8'));
  } catch {
    return {};
  }
}

export function saveMeta(profile, meta) {
  const file = join(profile, 'website.json');
  writeFileSync(file + '.tmp', JSON.stringify(meta, null, 2), { mode: 0o600 });
  chmodSync(file + '.tmp', 0o600);
  renameSync(file + '.tmp', file);
}

// A salted digest of a session's text: notices that the person signed in again without keeping it.
export function sessionDigest(salt, text) {
  return scryptSync(String(text), Buffer.from(salt, 'hex'), 32, { N: 1 << 14 }).toString('hex');
}

const LanguageTag = /^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$/;

// A well-formed tag in its canonical case (en-gb -> en-GB), or null.
function canonicalTag(tag) {
  try {
    const canonical = Intl.getCanonicalLocales(tag);
    return canonical.length === 1 ? canonical[0] : null;
  } catch {
    return null;
  }
}

// The settings' (or a session's) languages: each well-formed tag once, in its canonical case, so an
// older install's en-gb is en-GB and not dropped. Time zones need no such step: Intl takes an older
// name (Asia/Calcutta, US/Eastern) or another case as the zone it stands for (timeZoneFrom).
export function languagesFrom(value) {
  const list = Array.isArray(value) ? value : String(value || '').split(/[\s,;]+/);
  const kept = [];
  for (const l of list) {
    const given = String(l).trim();
    const tag = given.length <= 35 && LanguageTag.test(given) ? canonicalTag(given) : null;
    if (tag && !kept.some((k) => k.toLowerCase() === tag.toLowerCase())) kept.push(tag);
  }
  return kept.slice(0, 10);
}

// The settings' languages as Chrome's own preference holds them for a person who chose them: each
// language with a region followed by its base language when the list lacks it (en-US -> en-US, en),
// as Chrome's default for its own language is.
export function withBaseLanguages(languages) {
  const baseOf = (tag) => tag.split('-')[0].toLowerCase();
  const has = (tag) => languages.some((l) => l.toLowerCase() === tag);
  const out = [];
  languages.forEach((tag, i) => {
    out.push(tag);
    const base = baseOf(tag);
    if (!has(base) && !languages.slice(i + 1).some((l) => baseOf(l) === base)) out.push(base);
  });
  return out.slice(0, 10);
}

export const DefaultLanguages = ['en-US', 'en'];
export const DefaultTimeZone = 'UTC';

export function timeZoneFrom(value) {
  const zone = String(value || '').trim();
  if (!zone || zone.length > 64) return null;
  try {
    return new Intl.DateTimeFormat('en-US', { timeZone: zone }).resolvedOptions().timeZone;
  } catch {
    return null;
  }
}

const size = (v, min, max) => (Number.isInteger(v) && v >= min && v <= max ? v : null);

// The identity a row's browser launches with. `session` is the parsed saved session (or null),
// `settings` the plugin's own { languages, timeZone }, `kept` the row's identity from an earlier run.
export function identityFor({ session, settings = {}, kept }) {
  const from = session?.identity ?? kept ?? {};
  const chosen = languagesFrom(settings.languages);
  const languages = languagesFrom(from.languages).length ? languagesFrom(from.languages) : chosen.length ? withBaseLanguages(chosen) : DefaultLanguages;
  const timezoneId = timeZoneFrom(from.timezoneId) || timeZoneFrom(settings.timeZone) || DefaultTimeZone;
  const screen = {
    width: size(from.screen?.width, 800, 7680) ?? DefaultScreen.width,
    height: size(from.screen?.height, 600, 4320) ?? DefaultScreen.height,
  };
  // No fixed viewport when none was handed over: the window fills the screen, as a maximised one does.
  const viewport =
    size(from.viewport?.width, 320, screen.width) && size(from.viewport?.height, 240, screen.height)
      ? { width: from.viewport.width, height: from.viewport.height }
      : null;
  const scale = typeof from.deviceScaleFactor === 'number' && from.deviceScaleFactor >= 1 && from.deviceScaleFactor <= 3 ? from.deviceScaleFactor : 1;
  return {
    browser: 'chrome',
    version: typeof from.version === 'string' && /^\d+(\.\d+){1,3}$/.test(from.version) ? from.version : null,
    locale: languages[0],
    languages,
    timezoneId,
    screen,
    viewport,
    deviceScaleFactor: scale,
  };
}

// Chrome's own preferences for the row: the languages (navigator.languages and Accept-Language) and a
// clean last exit, so no "restore pages?" bubble follows a run that was stopped.
export function preparePreferences(dir, languages) {
  const folder = join(dir, 'Default');
  mkdirSync(folder, { recursive: true, mode: 0o700 });
  const file = join(folder, 'Preferences');
  let prefs = {};
  if (existsSync(file)) {
    try {
      prefs = JSON.parse(readFileSync(file, 'utf8')) || {};
    } catch {
      prefs = {};
    }
  }
  prefs.intl = { ...(prefs.intl || {}), accept_languages: languages.join(','), selected_languages: languages.join(',') };
  prefs.profile = { ...(prefs.profile || {}), exit_type: 'Normal', exited_cleanly: true };
  writeFileSync(file, JSON.stringify(prefs), { mode: 0o600 });
}

// Chrome's single-instance lock (a link to "<host>-<pid>"): a live browser on the profile means the row
// is in use, so the run says so rather than share it; a lock left by a browser that is gone is removed.
export function profileBusy(dir) {
  let target;
  try {
    target = readlinkSync(join(dir, 'SingletonLock'));
  } catch {
    return false;
  }
  const pid = Number(target.split('-').pop());
  if (Number.isInteger(pid) && pid > 0) {
    try {
      process.kill(pid, 0);
      return true;
    } catch {
      // gone
    }
  }
  for (const lock of ['SingletonLock', 'SingletonCookie', 'SingletonSocket']) rmSync(join(dir, lock), { force: true });
  return false;
}
