#!/usr/bin/env node
// Bundles wxc-exec.exe from the SAME microsoft/mxc release as the NuGet SDK (sandbox.lock.json). The NuGet package
// carries only mxc_ffi, not wxc-exec.exe, and Windows runs agents as
// `wxc-exec.exe --config <file>` under a core-owned ConPTY.
//
// The release asset is a ~540 MB zip, so by default only the one member is read, with HTTP range
// requests, and checked against the sha256 pinned in sandbox.lock.json. --verify-asset downloads the whole
// zip instead and checks the asset's own sha256 first (use it in release pipelines).
//   node toolchain/fetch-wxc-exec.mjs                        # win-x64 and win-arm64 into .toolchain/
//   node toolchain/fetch-wxc-exec.mjs --platform win-x64 --root <dir>
//   node toolchain/fetch-wxc-exec.mjs --print-sha            # print member sums, write nothing
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { inflateRawSync } from 'node:zlib';
import { download, repoRoot, sha256, writeVerified, defaultToolchainRoot } from './lib.mjs';

const lock = JSON.parse(await readFile(path.join(repoRoot, 'sandbox.lock.json'), 'utf8'));
const argv = process.argv.slice(2);
const opt = { platforms: [], root: defaultToolchainRoot, verifyAsset: false, printSha: false };
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--platform') opt.platforms.push(argv[++i]);
  else if (argv[i] === '--root') opt.root = path.resolve(argv[++i]);
  else if (argv[i] === '--verify-asset') opt.verifyAsset = true;
  else if (argv[i] === '--print-sha') opt.printSha = true;
  else throw new Error(`unknown argument ${argv[i]}`);
}
if (opt.platforms.length === 0) opt.platforms = Object.keys(lock.wxcExec.files);

const { githubRepo, tag, version } = lock.release;
const url = `https://github.com/${githubRepo}/releases/download/${tag}/${lock.wxcExec.asset}`;

/** A zip reader over either a whole buffer or HTTP ranges. */
async function openZip() {
  if (opt.verifyAsset) {
    console.log(`fetch   ${url} (whole asset)`);
    const whole = await download(url);
    const actual = sha256(whole);
    if (actual !== lock.wxcExec.assetSha256) {
      throw new Error(`asset sha256 mismatch: expected ${lock.wxcExec.assetSha256}, got ${actual}`);
    }
    return { size: whole.length, read: async (start, len) => whole.subarray(start, start + len) };
  }
  const head = await fetch(url, { method: 'HEAD', redirect: 'follow' });
  if (!head.ok) throw new Error(`HEAD ${url}: HTTP ${head.status}`);
  const finalUrl = head.url;
  const size = Number(head.headers.get('content-length'));
  return {
    size,
    read: async (start, len) => {
      const buf = await download(finalUrl, { headers: { Range: `bytes=${start}-${start + len - 1}` } });
      if (buf.length !== len) throw new Error(`range ${start}+${len} returned ${buf.length} bytes`);
      return buf;
    },
  };
}

function findEocd(tail) {
  for (let i = tail.length - 22; i >= 0; i--) if (tail.readUInt32LE(i) === 0x06054b50) return i;
  throw new Error('zip end-of-central-directory record not found');
}

async function centralDirectory(zip) {
  const tailLen = Math.min(zip.size, 65536 + 22);
  const tail = await zip.read(zip.size - tailLen, tailLen);
  const e = findEocd(tail);
  let count = tail.readUInt16LE(e + 10);
  let cdSize = tail.readUInt32LE(e + 12);
  let cdOffset = tail.readUInt32LE(e + 16);
  if (cdOffset === 0xffffffff || cdSize === 0xffffffff || count === 0xffff) {
    const loc = e - 20; // zip64 end-of-central-directory locator
    if (tail.readUInt32LE(loc) !== 0x07064b50) throw new Error('zip64 locator missing');
    const z64 = await zip.read(Number(tail.readBigUInt64LE(loc + 8)), 56);
    count = Number(z64.readBigUInt64LE(32));
    cdSize = Number(z64.readBigUInt64LE(40));
    cdOffset = Number(z64.readBigUInt64LE(48));
  }
  const cd = await zip.read(cdOffset, cdSize);
  const entries = new Map();
  for (let p = 0, n = 0; n < count; n++) {
    if (cd.readUInt32LE(p) !== 0x02014b50) throw new Error('bad central directory entry');
    const method = cd.readUInt16LE(p + 10);
    let csize = cd.readUInt32LE(p + 20);
    let usize = cd.readUInt32LE(p + 24);
    const nameLen = cd.readUInt16LE(p + 28);
    const extraLen = cd.readUInt16LE(p + 30);
    const commentLen = cd.readUInt16LE(p + 32);
    let offset = cd.readUInt32LE(p + 42);
    const name = cd.toString('utf8', p + 46, p + 46 + nameLen);
    // zip64 extended information extra field
    for (let x = p + 46 + nameLen; x < p + 46 + nameLen + extraLen; ) {
      const id = cd.readUInt16LE(x), len = cd.readUInt16LE(x + 2);
      if (id === 0x0001) {
        let q = x + 4;
        if (usize === 0xffffffff) { usize = Number(cd.readBigUInt64LE(q)); q += 8; }
        if (csize === 0xffffffff) { csize = Number(cd.readBigUInt64LE(q)); q += 8; }
        if (offset === 0xffffffff) { offset = Number(cd.readBigUInt64LE(q)); q += 8; }
      }
      x += 4 + len;
    }
    if (!entries.has(name)) entries.set(name, { method, csize, usize, offset });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}

async function extract(zip, entry) {
  const local = await zip.read(entry.offset, 30);
  if (local.readUInt32LE(0) !== 0x04034b50) throw new Error('bad local file header');
  const start = entry.offset + 30 + local.readUInt16LE(26) + local.readUInt16LE(28);
  const data = await zip.read(start, entry.csize);
  const out = entry.method === 0 ? data : entry.method === 8 ? inflateRawSync(data) : null;
  if (!out) throw new Error(`unsupported zip method ${entry.method}`);
  if (out.length !== entry.usize) throw new Error('extracted size does not match the zip directory');
  return out;
}

const zip = await openZip();
const entries = await centralDirectory(zip);
let failed = false;
for (const platform of opt.platforms) {
  const pin = lock.wxcExec.files[platform];
  try {
    if (!pin) throw new Error(`no wxc-exec.exe pinned for ${platform}`);
    const entry = entries.get(pin.member);
    if (!entry) throw new Error(`${pin.member} is not in ${lock.wxcExec.asset} of ${tag}`);
    const bytes = await extract(zip, entry);
    if (opt.printSha) { console.log(`${platform} ${pin.member} ${sha256(bytes)}`); continue; }
    const dest = path.join(opt.root, platform, 'sandbox', version, 'wxc-exec.exe');
    await writeVerified(dest, bytes, pin.sha256, true);
    console.log(`ok      wxc-exec.exe ${platform} (sandbox ${tag}) ${dest}`);
  } catch (error) {
    failed = true;
    console.error(`FAILED  wxc-exec.exe ${platform}: ${error.message}`);
  }
}
process.exit(failed ? 1 : 0);
