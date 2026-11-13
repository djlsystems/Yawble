// electron-builder afterPack hook. macOS only: with no Developer ID (unsigned dev build), ad-hoc sign every
// Mach-O we add under Contents/Resources, then the whole bundle. Apple silicon refuses to run unsigned code,
// and repackaging Electron (new Info.plist, new name) breaks the signature it shipped with.
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

function isMachO(file) {
  const fd = fs.openSync(file, 'r');
  try {
    const b = Buffer.alloc(4);
    if (fs.readSync(fd, b, 0, 4, 0) < 4) return false;
    const magic = b.readUInt32BE(0);
    return [0xfeedfacf, 0xcffaedfe, 0xfeedface, 0xcefaedfe, 0xcafebabe].includes(magic);
  } finally {
    fs.closeSync(fd);
  }
}

function* walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) yield* walk(p);
    else if (e.isFile()) yield p;
  }
}

exports.default = async function adhocSign(context) {
  if (context.electronPlatformName !== 'darwin') return;
  const app = path.join(context.appOutDir, `${context.packager.appInfo.productFilename}.app`);
  const sign = (target, ...extra) => execFileSync('codesign', ['--force', '--sign', '-', ...extra, target], { stdio: 'inherit' });
  for (const sub of ['core', 'bin']) {
    for (const f of walk(path.join(app, 'Contents', 'Resources', sub))) if (isMachO(f)) sign(f);
  }
  sign(app, '--deep');
  execFileSync('codesign', ['--verify', '--deep', '--strict', '--verbose=2', app], { stdio: 'inherit' });
};
