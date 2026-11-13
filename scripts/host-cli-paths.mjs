#!/usr/bin/env node
// Prints where this computer's agent CLIs are installed, the way a person's machine has them: every PATH hit (launcher),
// what it really is after symlinks (realpath), the text of a Windows .cmd/.ps1 shim or a script wrapper, and the
// CLI's own --version with its self-update off. It shows the install layouts the host-CLI detection must handle
// (run it on each machine you check, e.g. Windows and macOS); it decides nothing.
//
//   node scripts/host-cli-paths.mjs [--require claude,grok]
//
// --require: exit 1 if a named CLI is not on PATH or its --version fails. Portable: no which/readlink -f/where.
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, realpathSync, statSync } from 'node:fs';
import path from 'node:path';

const argv = process.argv.slice(2);
const required = argv[0] === '--require' ? (argv[1] ?? '').split(',').filter(Boolean) : [];
const win = process.platform === 'win32';
const exts = win ? ['', ...(process.env.PATHEXT ?? '.COM;.EXE;.BAT;.CMD').split(';').map((e) => e.toLowerCase()), '.ps1'] : [''];
const dirs = (process.env.PATH ?? '').split(path.delimiter).filter(Boolean);
// Self-update off, as the app runs them.
const env = { ...process.env, DISABLE_AUTOUPDATER: '1', GROK_DISABLE_AUTOUPDATER: '1' };

function hits(name) {
  const seen = new Set();
  const out = [];
  for (const dir of dirs) {
    for (const ext of exts) {
      const p = path.join(dir, name + ext);
      const key = win ? p.toLowerCase() : p;
      if (seen.has(key)) continue;
      seen.add(key);
      try { if (statSync(p).isFile()) out.push(p); } catch { /* not here */ }
    }
  }
  return out;
}

function isText(p) {
  const head = readFileSync(p).subarray(0, 512);
  return !head.includes(0) && head.length > 0;
}

let failed = false;
for (const cli of ['claude', 'grok']) {
  console.log(`== ${cli}`);
  const found = hits(cli);
  if (found.length === 0) {
    console.log('  not on PATH');
    if (required.includes(cli)) { console.log(`::error title=${cli} not installed::${cli} is not on PATH`); failed = true; }
    continue;
  }
  for (const p of found) {
    let real = p;
    try { real = realpathSync.native(p); } catch { /* broken link */ }
    const size = existsSync(real) ? statSync(real).size : -1;
    console.log(`  launcher  ${p}`);
    console.log(`  realpath  ${real}  (${size} bytes)`);
    if (size > 0 && size < 64 * 1024 && isText(real)) {
      console.log('  wrapper text:');
      for (const line of readFileSync(real, 'utf8').split(/\r?\n/).filter((l) => l.trim()).slice(0, 25)) console.log(`    | ${line}`);
    }
  }
  // Run what a person's shell would run: the first PATH hit, via the shell on Windows so .cmd shims work.
  const r = spawnSync(win ? `"${found[0]}"` : found[0], ['--version'], { env, encoding: 'utf8', timeout: 60_000, shell: win });
  const version = `${r.stdout ?? ''}${r.stderr ?? ''}`.trim().split(/\r?\n/)[0];
  console.log(`  --version exit ${r.status}: ${version}`);
  if (required.includes(cli) && r.status !== 0) { console.log(`::error title=${cli} --version failed::${r.error?.message ?? version}`); failed = true; }
}
process.exit(failed ? 1 : 0);
