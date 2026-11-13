import { spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdtempSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';

export interface CoreLaunch {
  /** The executable to run, and any leading arguments (e.g. dotnet + the core dll in development). */
  command: string;
  args: string[];
}

export interface RunningCore {
  child: ChildProcess;
  address: string;
  token: string;
}

/**
 * Where the core is. YAWBLE_CORE_PATH wins (a .dll runs under `dotnet`, anything else directly); a packaged
 * app has it under resources/core; in development it is the Debug build output in this repository.
 */
export function locateCore(repoRoot: string, resourcesPath: string | undefined, env = process.env): CoreLaunch {
  const exe = process.platform === 'win32' ? 'yawble-core.exe' : 'yawble-core';
  const candidates = [
    env.YAWBLE_CORE_PATH,
    resourcesPath ? path.join(resourcesPath, 'core', exe) : undefined,
    path.join(repoRoot, 'core', 'src', 'Yawble.Core', 'bin', 'Debug', 'net10.0', 'yawble-core.dll'),
  ].filter((p): p is string => !!p);
  const found = candidates.find((p) => existsSync(p));
  if (!found) throw new Error(`the core was not found; build it (dotnet build) or set YAWBLE_CORE_PATH. Looked in: ${candidates.join(', ')}`);
  return found.endsWith('.dll') ? { command: 'dotnet', args: [found] } : { command: found, args: [] };
}

/** A fresh per-launch address: a socket in a new 0700 folder, or a random pipe name on Windows. */
export function newIpcAddress(): string {
  const id = randomBytes(8).toString('hex');
  if (process.platform === 'win32') return `\\\\.\\pipe\\yawble-${id}`;
  // Short base path: Unix socket paths are limited to ~104 bytes on macOS.
  const base = process.env.XDG_RUNTIME_DIR && existsSync(process.env.XDG_RUNTIME_DIR) ? process.env.XDG_RUNTIME_DIR : os.tmpdir();
  const dir = mkdtempSync(path.join(base, 'yawble-')); // created 0700
  return path.join(dir, 'core.sock');
}

/** The end of what the core wrote on stderr, bounded, so a core that stops before READY can say why. */
export class StderrTail {
  private buf = '';
  constructor(private readonly max = 8192) {}
  push(chunk: string): void {
    this.buf += chunk;
    if (this.buf.length > this.max) this.buf = this.buf.slice(this.buf.length - this.max);
  }
  text(): string { return this.buf; }
}

/**
 * The error for a core that exited before READY. Its "fatal: " lines are the reason meant for the person (a restore that
 * left no database in place, say); without one, the last line it wrote; without any, just the exit code.
 */
export function earlyExitError(code: number | null, stderr: string): Error {
  const lines = stderr.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);
  const fatal = lines.filter((l) => l.startsWith('fatal: ')).map((l) => l.slice('fatal: '.length));
  const reason = fatal.length > 0 ? fatal.join(' ') : lines.at(-1);
  return new Error(reason ? `the core exited with code ${code} before READY: ${reason}` : `the core exited with code ${code} before READY`);
}

/** Starts the core sidecar and resolves once it prints its READY line. */
export function startCore(launch: CoreLaunch, opts: { dataDir: string; installDir?: string; timeoutMs?: number }): Promise<RunningCore> {
  const token = randomBytes(32).toString('hex');
  const address = newIpcAddress();
  const args = [...launch.args, '--ipc', address, '--data-dir', opts.dataDir];
  if (opts.installDir) args.push('--install-dir', opts.installDir);
  const child = spawn(launch.command, args, {
    // stdin stays open: the core exits when it closes, so it never outlives the shell.
    stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, YAWBLE_IPC_TOKEN: token, DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
    windowsHide: true,
  });
  const tail = new StderrTail();
  child.stderr?.on('data', (d: Buffer) => { tail.push(d.toString()); process.stderr.write(`[core] ${d}`); });
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('the core did not report READY in time')), opts.timeoutMs ?? 30_000);
    const lines = readline.createInterface({ input: child.stdout! });
    lines.on('line', (line) => {
      if (line.startsWith('YAWBLE-READY ')) {
        clearTimeout(timer);
        resolve({ child, address: line.slice('YAWBLE-READY '.length), token });
      } else {
        process.stdout.write(`[core] ${line}\n`);
      }
    });
    child.once('error', (e) => { clearTimeout(timer); reject(e); });
    child.once('exit', (code) => { clearTimeout(timer); reject(earlyExitError(code, tail.text())); });
  });
}
