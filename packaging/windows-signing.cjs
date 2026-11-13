// Windows code signing through Azure Artifact Signing, with signtool and Microsoft's Artifact Signing plug-in
// (Azure.CodeSigning.Dlib.dll). Used by build.mjs --sign for the staged payload, and by electron-builder (its
// signtoolOptions.sign hook) for the app's own files and the installer.
//
// The signer signs in through Azure (DefaultAzureCredential: `az login` is enough) and needs the "Artifact Signing
// Certificate Profile Signer" role on the account. Where the tools are:
//   YAWBLE_SIGNTOOL        signtool.exe (default: the newest x64 one in the Windows 10/11 SDK)
//   YAWBLE_SIGNING_DLIB    Azure.CodeSigning.Dlib.dll (default: where the Artifact Signing client tools install it)
// Which account and certificate profile: signing.json beside this file.
'use strict';
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const settings = JSON.parse(fs.readFileSync(path.join(__dirname, 'signing.json'), 'utf8'));

/** The file types Windows checks a signature on before running or loading them. */
const SIGNABLE = ['.exe', '.dll', '.node'];

function findSigntool() {
  if (process.env.YAWBLE_SIGNTOOL) return process.env.YAWBLE_SIGNTOOL;
  const kits = path.join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Windows Kits', '10', 'bin');
  const versions = fs.existsSync(kits) ? fs.readdirSync(kits).filter((v) => /^10\.\d+\.\d+\.\d+$/.test(v)) : [];
  versions.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  for (const v of versions.reverse()) {
    const candidate = path.join(kits, v, 'x64', 'signtool.exe');
    if (fs.existsSync(candidate)) return candidate;
  }
  throw new Error('signtool.exe not found: install the Windows SDK, or set YAWBLE_SIGNTOOL');
}

function findDlib() {
  const candidates = [
    process.env.YAWBLE_SIGNING_DLIB,
    path.join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'MicrosoftArtifactSigningClientTools', 'Azure.CodeSigning.Dlib.dll'),
  ].filter(Boolean);
  const found = candidates.find((c) => fs.existsSync(c));
  if (!found) throw new Error('Azure.CodeSigning.Dlib.dll not found: install the Artifact Signing client tools, or set YAWBLE_SIGNING_DLIB');
  return found;
}

let tools;
function toolsOnce() {
  if (tools) return tools;
  const metadata = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'yawble-sign-')), 'metadata.json');
  fs.writeFileSync(metadata, JSON.stringify({
    Endpoint: settings.endpoint, CodeSigningAccountName: settings.account, CertificateProfileName: settings.profile,
  }));
  tools = { signtool: findSigntool(), dlib: findDlib(), metadata };
  return tools;
}

/** Whether a file carries a valid Authenticode signature that chains to a trusted root (anyone's: ours or its maker's). */
function isSigned(file) {
  const r = spawnSync(toolsOnce().signtool, ['verify', '/pa', '/q', file], { stdio: 'ignore' });
  return r.status === 0;
}

/** The files under a folder that Windows checks a signature on. */
function signableFiles(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...signableFiles(full));
    else if (SIGNABLE.includes(path.extname(entry.name).toLowerCase())) out.push(full);
  }
  return out;
}

/**
 * Signs the given files with the DJL Systems certificate, SHA-256, timestamped (a signature stays valid after the
 * certificate's short life). Files that already carry a valid signature are left as they are, so Microsoft's own
 * runtime files keep Microsoft's. Signs in batches; a batch that fails (the timestamp server or the service did not
 * answer) is tried again, up to three times, for the files still unsigned. Throws if any file ends unsigned.
 */
function signFiles(files, log = console.log) {
  const { signtool, dlib, metadata } = toolsOnce();
  let todo = files.filter((f) => !isSigned(f));
  log(`signing: ${todo.length} of ${files.length} file(s) need a signature`);
  for (let attempt = 1; todo.length > 0 && attempt <= 3; attempt++) {
    for (let i = 0; i < todo.length; i += 40) {
      const batch = todo.slice(i, i + 40);
      spawnSync(signtool, [
        'sign', '/fd', 'SHA256', '/tr', settings.timestamp, '/td', 'SHA256', '/dlib', dlib, '/dmdf', metadata, ...batch,
      ], { stdio: ['ignore', 'ignore', 'inherit'] });
    }
    todo = todo.filter((f) => !isSigned(f));
    if (todo.length > 0 && attempt < 3) {
      log(`signing: ${todo.length} file(s) not signed yet, trying again`);
      spawnSync(process.execPath, ['-e', 'setTimeout(() => {}, 5000)']);
    }
  }
  if (todo.length > 0) throw new Error(`signing failed for ${todo.length} file(s):\n  ${todo.join('\n  ')}`);
}

/** Every signable file under the folders, signed or throws naming those that are not. */
function verifyAll(dirsOrFiles) {
  const files = dirsOrFiles.flatMap((p) => (fs.statSync(p).isDirectory() ? signableFiles(p) : [p]));
  const unsigned = files.filter((f) => !isSigned(f));
  if (unsigned.length > 0) throw new Error(`${unsigned.length} file(s) are not signed:\n  ${unsigned.join('\n  ')}`);
  return files.length;
}

/**
 * Files the hook could not sign. electron-builder logs a failed signature and carries on, and some of the files it signs
 * (the installer's own elevate.exe, which asks Windows for administrator rights) are packed inside the installer, out of
 * reach of verifyAll: build.mjs fails the build when this list is not empty.
 */
const failures = [];

/** electron-builder's signtoolOptions.sign hook: one file at a time, as electron-builder hands them over. */
async function electronBuilderSign(configuration) {
  try {
    signFiles([configuration.path], () => {});
  } catch (e) {
    failures.push(configuration.path);
    throw e;
  }
}

module.exports = { signFiles, signableFiles, verifyAll, isSigned, electronBuilderSign, failures, settings };
