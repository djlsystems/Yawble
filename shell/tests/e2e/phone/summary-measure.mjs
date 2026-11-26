// Prints test-results/phone-page-measure.ndjson (written by phone-remote.spec.ts's page-measure block), one
// line per page and size: px from the top of the window of the first content row and the named rows, the inner
// vertical scrollers, sideways scroll, and every field under 16 px / 44 px.   node tests/e2e/phone/summary-measure.mjs
import { readFileSync } from 'node:fs';
import path from 'node:path';

const file = path.resolve(import.meta.dirname, '..', '..', '..', 'test-results', 'phone-page-measure.ndjson');
const rows = readFileSync(file, 'utf8').trim().split('\n').map((l) => JSON.parse(l));
rows.sort((a, b) => (a.size + a.screen).localeCompare(b.size + b.screen));
const span = (v) => (v ? `${v.top}-${v.bottom}` : '-');
for (const r of rows) {
  const named = Object.entries(r.rows ?? {}).map(([k, v]) => `${k}=${span(v)}`).join(' ');
  const bad = r.inputs.filter((i) => i.font < 16 || i.h < 44).map((i) => `${i.what} ${i.font}px/${i.h}px`);
  const fields = r.inputs.length ? `fields=${r.inputs.length} min ${Math.min(...r.inputs.map((i) => i.font))}px/${Math.min(...r.inputs.map((i) => i.h))}px` : 'fields=0';
  console.log([r.size, r.screen.padEnd(30), `first=${r.first ? span(r.first) : 'none'}`, named,
    `scrollers=${r.scrollers.length}${r.scrollers.length > 1 ? ` [${r.scrollers.join(' | ')}]` : ''}`, `sideways=${r.overflow}`, fields,
    bad.length ? `UNDER: ${bad.join('; ')}` : '', r.back ? `back=${JSON.stringify(r.back)}` : '', r.pane ? `pane=${JSON.stringify(r.pane)}` : '',
    r.memberAfterScroll ? `memberScrolled=${span(r.memberAfterScroll)}` : '', r.openedAt ? `openedAt=${r.openedAt}` : ''].filter(Boolean).join(' '));
}
