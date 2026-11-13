// Shared helpers for the toolchain fetch scripts. Node 22, no dependencies.
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { chmod, mkdir, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const defaultToolchainRoot = path.join(repoRoot, '.toolchain');

/** The platform key used by every manifest: linux-x64, osx-arm64, win-x64, ... */
export function currentPlatform() {
  const os = { linux: 'linux', darwin: 'osx', win32: 'win' }[process.platform];
  const arch = { x64: 'x64', arm64: 'arm64' }[process.arch];
  if (!os || !arch) throw new Error(`unsupported platform ${process.platform}-${process.arch}`);
  return `${os}-${arch}`;
}

export function parseArgs(argv) {
  const out = { platforms: [], root: defaultToolchainRoot, only: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--platform') out.platforms.push(argv[++i]);
    else if (a === '--root') out.root = path.resolve(argv[++i]);
    else if (a === '--only') out.only.push(argv[++i]);
    else if (a === '--all-platforms') out.all = true;
    else throw new Error(`unknown argument ${a}`);
  }
  if (out.platforms.length === 0 && !out.all) out.platforms.push(currentPlatform());
  return out;
}

export async function sha256File(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

export function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex');
}

/** Writes bytes to dest only after their sha256 matches; never leaves a partial or unverified file. */
export async function writeVerified(dest, bytes, expectedSha, executable) {
  const actual = sha256(bytes);
  if (actual !== expectedSha) {
    throw new Error(`sha256 mismatch for ${dest}: expected ${expectedSha}, got ${actual}`);
  }
  await mkdir(path.dirname(dest), { recursive: true });
  const tmp = `${dest}.partial-${process.pid}`;
  await writeFile(tmp, bytes);
  if (executable) await chmod(tmp, 0o755);
  await rm(dest, { force: true });
  await rename(tmp, dest);
}

export async function download(url, init) {
  const res = await fetch(url, { redirect: 'follow', ...init });
  if (!res.ok && res.status !== 206) throw new Error(`GET ${url}: HTTP ${res.status}`);
  return Buffer.from(await res.arrayBuffer());
}
