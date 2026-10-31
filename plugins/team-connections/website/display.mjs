// ONE BROWSER'S SCREEN: its own virtual X display (Xvfb, which the image has), so Chrome runs headed
// as on a person's desktop. No TCP, and a fresh MIT-MAGIC-COOKIE-1 in a 0600 file in the plugin's own
// folder, so no other process in the container can connect to the display and watch the sign-in.
import { spawn } from 'node:child_process';
import { writeFileSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';

const Xvfb = process.env.WEB_CREDENTIALS_XVFB || '/usr/bin/Xvfb';

// An Xauthority file with one cookie for any display (FamilyWild): the number is only known once Xvfb
// has picked it.
function writeAuthority(path, cookie) {
  const field = (bytes) => Buffer.concat([Buffer.from([bytes.length >> 8, bytes.length & 0xff]), bytes]);
  const body = Buffer.concat([Buffer.from([0xff, 0xff]), field(Buffer.alloc(0)), field(Buffer.alloc(0)), field(Buffer.from('MIT-MAGIC-COOKIE-1')), field(cookie)]);
  writeFileSync(path, body, { mode: 0o600 });
}

// Starts a width x height screen; answers { display, authority, close() } once it takes connections.
export async function startDisplay(folder, width, height) {
  if (!existsSync(Xvfb)) throw new Error('this machine has no Xvfb, so the browser has no screen to run on');
  const authority = join(folder, `Xauthority-${process.pid}`);
  writeAuthority(authority, randomBytes(16));
  const child = spawn(Xvfb, ['-displayfd', '1', '-screen', '0', `${width}x${height}x24`, '-nolisten', 'tcp', '-auth', authority, '-nocursor'], {
    stdio: ['ignore', 'pipe', 'ignore'],
    env: { PATH: '/usr/local/bin:/usr/bin:/bin' },
  });
  const close = async () => {
    if (child.exitCode === null) {
      child.kill('SIGTERM');
      await new Promise((r) => {
        const t = setTimeout(r, 3000);
        child.once('exit', () => {
          clearTimeout(t);
          r();
        });
      });
    }
    rmSync(authority, { force: true });
  };
  try {
    const number = await new Promise((resolve, reject) => {
      let text = '';
      const timer = setTimeout(() => reject(new Error('Xvfb did not start a display')), 15000);
      child.stdout.on('data', (d) => {
        text += d;
        const m = text.match(/^(\d+)\n/);
        if (m) {
          clearTimeout(timer);
          resolve(m[1]);
        }
      });
      child.once('exit', () => {
        clearTimeout(timer);
        reject(new Error('Xvfb exited before it started a display'));
      });
    });
    return { display: ':' + number, authority, close };
  } catch (e) {
    await close();
    throw e;
  }
}
