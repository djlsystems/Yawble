#!/usr/bin/env node
// Installers. One command per OS, run on that OS:
//
//   node packaging/build.mjs [--rid linux-x64|osx-arm64|win-x64] [--skip-stage] [--sign]
//
// --sign (Windows): every .exe and .dll that ships, and the installer, signed with the DJL Systems certificate through
// Azure Artifact Signing (windows-signing.cjs, signing.json); files already signed by their maker keep that signature.
// The build fails if any file ends unsigned. Output: Yawble-Setup-<version>-<arch>.exe and SHA256SUMS.
//
// 1. Stage the .NET payload for the RID into packaging/stage/<rid>/:
//      core/  yawble-core, self-contained (no .NET install needed), with the sandbox's native lib (and wxc-exec.exe on Windows)
//      bin/   yawble-bridge, yawble-grok-auth and yawble-guardcheck (the guardrail check's probe), native AOT (the core's RunLayout.AppBinDir),
//             and yawble, the companion client (Claude Code's MCP server and a CLI; README "Using Yawble from Claude Code")
// 2. Build the shell (`npm run build` in shell/, so shell/out matches this checkout) and package it with Electron and that payload under resources/:
//      Windows  NSIS setup .exe, per-user, no admin, with an uninstaller in Settings > Apps
//      macOS    .app in a .dmg and a .zip, arm64 only, ad-hoc signed (mac-adhoc-sign.cjs)
//      Linux    .tar.gz with install.sh / uninstall.sh and install-apparmor.sh (root, once)
// Output: artifacts/installers/<rid>/. Without --sign nothing is signed; macOS is not notarized. No agent CLI is bundled, pinned or fetched:
// the app detects the person's own installed Claude Code and Grok and runs them from where they are installed.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..');
const require = createRequire(import.meta.url);

const hostRid = `${{ win32: 'win', darwin: 'osx', linux: 'linux' }[process.platform]}-${process.arch === 'arm64' ? 'arm64' : 'x64'}`;
const argv = process.argv.slice(2);
let rid = hostRid;
let skipStage = false;
let sign = false;
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--rid') rid = argv[++i];
  else if (argv[i] === '--skip-stage') skipStage = true;
  else if (argv[i] === '--sign') sign = true;
  else throw new Error(`unknown argument ${argv[i]}`);
}
const supported = ['linux-x64', 'linux-arm64', 'win-x64', 'win-arm64', 'osx-arm64'];
if (!supported.includes(rid)) {
  throw new Error(`${rid} is not packaged: ${rid === 'osx-x64' ? 'Microsoft.Mxc.Sdk 1.0.1 ships no osx-x64 native library' : `use one of ${supported.join(', ')}`}`);
}
if (rid.split('-')[0] !== hostRid.split('-')[0]) throw new Error(`build ${rid} on its own OS (this is ${hostRid})`);
const os = rid.split('-')[0];
if (sign && os !== 'win') throw new Error('--sign signs Windows builds; macOS and Linux are not signed here');
const signing = sign ? require('./windows-signing.cjs') : null;
const exe = os === 'win' ? '.exe' : '';
const stage = path.join(here, 'stage', rid);
const out = path.join(repo, 'artifacts', 'installers', rid);

function run(cmd, args, opts = {}) {
  console.log(`> ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit', cwd: repo, ...opts });
  if (r.error) throw r.error;
  if (r.status !== 0) throw new Error(`${cmd} exited with ${r.status}`);
}

function mustExist(file, why) {
  if (!existsSync(file)) throw new Error(`missing ${path.relative(repo, file)}: ${why}`);
}

// Native AOT on Windows finds the C++ linker through vswhere.exe, which lives in the Visual Studio Installer folder; a shell
// such as Git Bash does not put that folder on PATH, and the AOT publish (bin/) then fails. Add it when vswhere is missing.
function withVswhereOnPath() {
  const dirs = (process.env.PATH ?? '').split(path.delimiter).filter(Boolean);
  if (dirs.some((d) => existsSync(path.join(d, 'vswhere.exe')))) return;
  const installer = path.join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Microsoft Visual Studio', 'Installer');
  if (!existsSync(path.join(installer, 'vswhere.exe'))) return; // the publish says what is missing
  console.log(`> adding ${installer} to PATH (vswhere, for Native AOT)`);
  process.env.PATH = [...dirs, installer].join(path.delimiter);
}

// --- 1. the .NET payload ---------------------------------------------------------------------------------
if (!skipStage) {
  if (os === 'win') withVswhereOnPath();
  rmSync(stage, { recursive: true, force: true });
  if (os === 'win') {
    // The Windows core publish needs wxc-exec.exe from the same sandbox release (core csproj, YAWBLE001).
    const sandboxVersion = JSON.parse(readFileSync(path.join(repo, 'sandbox.lock.json'), 'utf8')).release.version;
    if (!existsSync(path.join(repo, '.toolchain', rid, 'sandbox', sandboxVersion, 'wxc-exec.exe'))) {
      run(process.execPath, [path.join(repo, 'toolchain', 'fetch-wxc-exec.mjs'), '--platform', rid]);
    }
  }
  const publish = (project, dir, ...extra) =>
    run('dotnet', ['publish', project, '-c', 'Release', '-r', rid, '-o', path.join(stage, dir), '-p:DebugType=none', '-p:DebugSymbols=false', ...extra]);
  // Self-contained: the person installing needs no .NET runtime. Not single-file: the sandbox's native lib is
  // loaded from beside the core and wxc-exec.exe is run from there (RpcDispatcher.WxcExec).
  publish('core/src/Yawble.Core', 'core', '--self-contained', 'true');
  publish('bridge/src/Yawble.Bridge', 'bin');
  publish('bridge/src/Yawble.GrokAuth', 'bin');
  publish('bridge/src/Yawble.GuardCheck', 'bin');
  publish('bridge/src/Yawble.Run', 'bin');
  // yawble-companion (AssemblyName yawble-companion, Native AOT): the core names <install>/bin/yawble-companion in companion.status.
  publish('core/tools/Yawble.Companion', 'bin');
  // Windows: the dotnet wrapper, first on the sandbox's PATH (builds in one MSBuild process; SandboxRequestCompiler.DotnetShimFolder).
  if (os === 'win') publish('bridge/src/Yawble.DotnetShim', path.join('bin', 'dotnet-shim'));
  // Native AOT leaves .dbg/.dSYM symbol files next to the binaries; they do not ship.
  for (const dir of [path.join(stage, 'bin'), path.join(stage, 'bin', 'dotnet-shim')].filter((d) => existsSync(d))) {
    for (const f of readdirSync(dir)) {
      if (f.endsWith('.dbg') || f.endsWith('.dSYM') || f.endsWith('.pdb')) rmSync(path.join(dir, f), { recursive: true, force: true });
    }
  }
}
const sandboxNative = { win: 'mxc_ffi.dll', osx: 'libmxc_ffi.dylib', linux: 'libmxc_ffi.so' }[os];
mustExist(path.join(stage, 'core', `yawble-core${exe}`), 'the core did not publish');
mustExist(path.join(stage, 'core', sandboxNative), `The sandbox's native library for ${rid} is not in Microsoft.Mxc.Sdk`);
if (os === 'win') mustExist(path.join(stage, 'core', 'wxc-exec.exe'), 'run node toolchain/fetch-wxc-exec.mjs --platform ' + rid);
mustExist(path.join(stage, 'bin', `yawble-bridge${exe}`), 'the bridge did not publish');
mustExist(path.join(stage, 'bin', `yawble-grok-auth${exe}`), 'yawble-grok-auth did not publish');
mustExist(path.join(stage, 'bin', `yawble-run${exe}`), 'yawble-run did not publish');
mustExist(path.join(stage, 'bin', `yawble-companion${exe}`), 'yawble-companion did not publish');
if (os === 'win') mustExist(path.join(stage, 'bin', 'dotnet-shim', 'dotnet.exe'), 'the dotnet wrapper did not publish');
// GuardrailService: the Guardrails tab is off ("the probe is not installed") without <install>/bin/yawble-guardcheck.
mustExist(path.join(stage, 'bin', `yawble-guardcheck${exe}`), 'yawble-guardcheck did not publish');
// roles/ (RoleTextDir) is granted to every agent run, and the sandbox refuses a request that grants a missing path (with a
// misleading slirp4netns/nsenter error on Linux), so it ships with a README: an empty folder is not packaged.
// There is no toolchain/ folder: the app runs the person's OWN installed Claude Code and Grok from their install
// folders (detected at run time, granted read-only), and never ships, pins or fetches an agent CLI.
mkdirSync(path.join(stage, 'roles'), { recursive: true });
writeFileSync(path.join(stage, 'roles', 'README.txt'), 'Role text for agents. This folder is granted to every agent run, so it must exist.\n');
rmSync(path.join(stage, 'toolchain'), { recursive: true, force: true }); // a stage left by an older build
// The staged core runs on its own (self-contained), before anything is packaged.
run(path.join(stage, 'core', `yawble-core${exe}`), ['--version']);
// Signed in place, so electron-builder copies signed files; those its maker signed (the .NET runtime, the sandbox) stay so.
if (signing) signing.signFiles(signing.signableFiles(stage));

// --- 2. Electron packaging -------------------------------------------------------------------------------
const shell = path.join(repo, 'shell');
mustExist(path.join(shell, 'node_modules', 'electron', 'package.json'), 'install the shell first: (cd shell && npm ci)');
// Always rebuilt: a shell/out left from an earlier checkout would ship an old window with this checkout's core.
// npm is npm.cmd on Windows, which Node starts only through a shell: there the whole fixed command is one string, since
// arguments beside shell: true are concatenated unescaped (Node's DEP0190).
if (process.platform === 'win32') run('npm run build', [], { cwd: shell, shell: true });
else run('npm', ['run', 'build'], { cwd: shell });
mustExist(path.join(shell, 'out', 'main', 'main', 'index.js'), 'the shell build wrote no out/main/main/index.js');
const electronVersion = JSON.parse(readFileSync(path.join(shell, 'node_modules', 'electron', 'package.json'), 'utf8')).version;
const { readBuildVersion } = await import('../scripts/build-version.mjs');
const buildVersion = readBuildVersion(repo).version;

const builder = require('electron-builder');
const archName = rid.endsWith('arm64') ? 'arm64' : 'x64';
const arch = builder.Arch[archName];
const name = sign ? `Yawble-Setup-${buildVersion}-${archName}` : `yawble-${buildVersion}-${rid}-unsigned`;
// The installed app's version where Windows shows it (Settings > Apps, inventory tools): electron-builder writes the
// uninstall entry from package.json's version, which is not the build's (yyyy.mm.dd.N is not semver); this NSIS hook
// runs after that entry is written and puts the build's version in it.
const nsisInclude = path.join(stage, 'installer-version.nsh');
writeFileSync(nsisInclude, [
  '!macro customInstall',
  `  WriteRegStr SHELL_CONTEXT "\${UNINSTALL_REGISTRY_KEY}" "DisplayVersion" "${buildVersion.replace(/[^0-9A-Za-z.+-]/g, '')}"`,
  '!macroend',
  '',
].join('\r\n'));
const config = {
  appId: 'com.djlsystems.yawble',
  productName: 'Yawble',
  // yawble.exe's file version (Windows) and the bundle version (macOS): the build's numbers, without +dev.<commit>.
  buildVersion: buildVersion.split('+')[0],
  // Windows and Linux: yawble(.exe), which the AppArmor profile and launcher name. Not on macOS, where
  // electron-builder would also rename the bundle; the Mac app stays "Yawble.app".
  ...(os === 'osx' ? {} : { executableName: 'yawble' }),
  electronVersion,
  directories: { app: shell, output: out },
  // The shell is already compiled (tsc + vite) into out/; its one runtime dependency (vue) is bundled by vite.
  files: ['out/**', 'resources/**', 'package.json', '!out/**/*.map', '!node_modules/**'],
  npmRebuild: false,
  nodeGypRebuild: false,
  asar: true,
  // resources/ is the core's install dir (RunLayout): core/, bin/ and roles/. No agent CLIs: the person's own are used.
  extraResources: [
    { from: path.join(stage, 'core'), to: 'core' },
    { from: path.join(stage, 'bin'), to: 'bin' },
    { from: path.join(stage, 'roles'), to: 'roles' },
  ],
  publish: null,
  win: {
    target: [{ target: 'nsis', arch: [archName] }],
    // yawble.exe, the installer and the uninstaller carry the Yawble icon (the taskbar and Start show it), not Electron's.
    icon: path.join(shell, 'resources', 'icon.ico'),
    signAndEditExecutable: true,
    ...(sign
      ? {
          // The app's executable, its DLLs and the installer and uninstaller, through the same signing (sign-hook.cjs).
          signtoolOptions: { sign: path.join(here, 'sign-hook.cjs'), signingHashAlgorithms: ['sha256'], publisherName: signing.settings.publisher },
          signExts: ['.dll', '.node'],
          forceCodeSigning: true,
        }
      : {
          // Unsigned: no certificate is configured, and signing must not be attempted with whatever the machine has.
          forceCodeSigning: false,
        }),
  },
  nsis: {
    artifactName: sign ? `${name}.\${ext}` : `${name}-setup.\${ext}`,
    oneClick: false,
    // The person chooses: "Only for me" (the default: %LOCALAPPDATA%\Programs\Yawble, no administrator prompt) or "Anyone
    // who uses this computer" (%ProgramFiles%\Yawble, with Windows' permission prompt). IT deploys silently with
    // /S /allusers (docs/deploying.md); the app's updater installs in the scope it finds (shell install-scope.ts).
    perMachine: false,
    allowElevation: true,
    selectPerMachineByDefault: false,
    allowToChangeInstallationDirectory: true,
    // The Start Menu and desktop shortcuts. Windows titles the app's notifications with this name (the shortcut
    // carries appId, which the shell sets as its AppUserModelID). productName stays: it decides the data folder.
    shortcutName: 'Yawble Desktop',
    // Settings > Apps lists the name and the version (DisplayVersion, from nsisInclude) apart.
    uninstallDisplayName: 'Yawble',
    include: nsisInclude,
    deleteAppDataOnUninstall: false,
  },
  mac: {
    target: [{ target: 'dmg', arch: [archName] }, { target: 'zip', arch: [archName] }],
    artifactName: `${name}.\${ext}`,
    category: 'public.app-category.developer-tools',
    identity: null, // no Developer ID: ad-hoc signed in afterPack instead, which Apple silicon requires to run at all
    hardenedRuntime: false,
    gatekeeperAssess: false,
    notarize: false,
  },
  dmg: { artifactName: `${name}.\${ext}`, writeUpdateInfo: false },
  afterPack: path.join(here, 'mac-adhoc-sign.cjs'),
  linux: {
    target: [{ target: 'tar.gz', arch: [archName] }],
    artifactName: `${name}.\${ext}`,
    category: 'Development',
    // install.sh (no root), uninstall.sh, and install-apparmor.sh (root, once) beside the executable.
    extraFiles: [{ from: path.join(here, 'linux'), to: '.', filter: ['*.sh', '*.desktop.in'] }],
  },
};
const platform = { win: builder.Platform.WINDOWS, osx: builder.Platform.MAC, linux: builder.Platform.LINUX }[os];
rmSync(out, { recursive: true, force: true });
const files = await builder.build({ targets: platform.createTarget(null, arch), config, publish: 'never', projectDir: here });
const installers = files.filter((f) => !f.endsWith('.blockmap') && !f.endsWith('.yml'));
if (signing) {
  if (signing.failures.length > 0) throw new Error(`signing failed for ${signing.failures.length} file(s) packed into the installer: ${signing.failures.join(', ')}`);
  // electron-builder logs a file it could not sign and carries on; every file that ships is checked here instead.
  const checked = signing.verifyAll([path.join(out, archName === 'x64' ? 'win-unpacked' : `win-${archName}-unpacked`), ...installers]);
  console.log(`\nsigned: all ${checked} .exe and .dll files and the installer carry a valid signature`);
  const { createHash } = await import('node:crypto');
  const sums = installers.map((f) => `${createHash('sha256').update(readFileSync(f)).digest('hex')}  ${path.basename(f)}`).join('\n') + '\n';
  writeFileSync(path.join(out, 'SHA256SUMS'), sums);
}
console.log('\ninstallers:');
for (const f of installers) {
  console.log(`  ${path.relative(repo, f)}  ${(statSync(f).size / 1048576).toFixed(1)} MiB`);
}
