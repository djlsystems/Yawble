// RFC 6238 time-based one-time codes (SHA-1, 30 s, 6 digits): what an authenticator app shows for
// the person's own TOTP secret.
import { createHmac } from 'node:crypto';

export function base32(secret) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const clean = secret.toUpperCase().replace(/[\s=-]/g, '');
  let bits = '';
  for (const c of clean) {
    const i = alphabet.indexOf(c);
    if (i < 0) throw new Error('The TOTP secret is not base32.');
    bits += i.toString(2).padStart(5, '0');
  }
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

export function totp(secret, now = Date.now(), step = 30, digits = 6) {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 1000 / step)));
  const mac = createHmac('sha1', base32(secret)).update(counter).digest();
  const offset = mac[mac.length - 1] & 0x0f;
  const code = (mac.readUInt32BE(offset) & 0x7fffffff) % 10 ** digits;
  return String(code).padStart(digits, '0');
}
