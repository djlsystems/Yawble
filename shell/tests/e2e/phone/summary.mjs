// Prints test-results/b037-audit.ndjson (written by phone-remote.spec.ts) as one block per screen: overflow, off-screen
// and small controls.   node tests/e2e/phone/summary.mjs [screen-filter]
import { readFileSync } from 'node:fs';
const r = readFileSync(new URL('../../../test-results/b037-audit.ndjson', import.meta.url), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));
const only = process.argv[2];
for (const x of r) {
  if (only && !x.screen.includes(only)) continue;
  const a = x.audit;
  if ('bannersH' in a) { console.log(`== ${x.screen} ${x.size} banners=${Math.round(a.bannersH)} px`); continue; }
  console.log(`== ${x.screen} ${x.size} overflow=${a.overflow} scrollWidth=${a.scrollWidth} controls=${a.controls} off=${a.offScreen.length} small=${a.small.length}${a.overflow ? ` wide: ${a.wide.join('; ')}` : ''}`);
  for (const c of a.offScreen) console.log('  OFF  ', c.what, JSON.stringify(c.label), `${c.x},${c.y}`, `${c.w}x${c.h}`, c.inScroller ? '(in a sideways scroller)' : '');
  for (const c of a.small) console.log('  SMALL', c.what, JSON.stringify(c.label), `${c.x},${c.y}`, `${c.w}x${c.h}`);
}
