// @vitest-environment happy-dom
//
// THE CLASH CHOICE. A move or copy the server answers 409 with `clashes` did nothing; the person
// is asked about each clash - Keep both, Replace or Skip, "Do this for the rest", or Cancel - and
// the request is sent again with `onClash` on each clashed item. A partial 409 shows the server's
// sentence and what was done.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const q = vi.hoisted(() => ({
  screen: { lt: { sm: false } },
  platform: { is: { mac: false } },
  notify: vi.fn(),
  copy: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: q.notify, screen: q.screen, platform: q.platform }),
  copyToClipboard: q.copy,
}));

import { documentsServer } from '../../test/documentsServer';
import { click, doubleClick, openExplorer, press, row, settle, until } from '../../test/documentsExplorer';
import { bodyText, resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

const clashes = [
  { from: 'reports/2026', to: '2026', isFolder: true },
  { from: 'reports/a.md', to: 'a.md', isFolder: false },
  { from: 'reports/b.pdf', to: 'b.pdf', isFolder: false },
];

beforeEach(() => {
  localStorage.clear();
  server = documentsServer();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

/** Copies three items of Alpha › reports, then pastes them into Alpha, where they clash. */
async function pasteWithClashes(clashing = clashes) {
  server.reply('copy', 409, { error: `${clashing.length} of these are already in Alpha.`, clashes: clashing });

  await openExplorer('alpha');
  await doubleClick(row('reports'));
  await click(row('2026'));
  await click(row('b.pdf'), { shiftKey: true });
  await press('c', { ctrlKey: true });
  await press('Backspace');
  await press('v', { ctrlKey: true });
  await until(() => document.body.querySelector('.documents-clash'));
}

const clashButton = (choice: string) => document.body.querySelector<HTMLElement>(`.documents-clash [data-clash="${choice}"]`)!;
const lastCopy = () => server.callsTo('copy').at(-1)!.body as { items: { path: string; onClash?: string }[] };

async function answer(choice: string) {
  await click(clashButton(choice));
  await settle();
}

describe('the clash dialog', () => {
  it('opens on a 409 with clashes, naming the clash and the folder', async () => {
    await pasteWithClashes();

    expect(document.body.querySelector('.documents-clash')!.textContent).toContain('2026 is already in Alpha.');
    expect(document.body.querySelector('.documents-clash')!.textContent).toContain('It is a folder.');
    expect(document.body.querySelector('.documents-clash')!.textContent).toContain('Do this for the rest (2)');
  });

  it.each([
    ['keep-both'],
    ['replace'],
    ['skip'],
  ])('%s resends with that onClash on the clashed item', async (choice) => {
    await pasteWithClashes([clashes[2]!]);
    await answer(choice);

    expect(server.callsTo('copy')).toHaveLength(2);
    expect(lastCopy().items).toEqual([
      { path: 'reports/2026' },
      { path: 'reports/a.md' },
      { path: 'reports/b.pdf', onClash: choice },
    ]);
  });

  it('asks about each in turn, and "do this for the rest" answers every remaining clash', async () => {
    await pasteWithClashes();
    await answer('skip');
    expect(server.callsTo('copy')).toHaveLength(1);

    const rest = document.body.querySelector<HTMLElement>('.documents-clash .q-checkbox')!;
    await click(rest);
    await answer('keep-both');

    expect(server.callsTo('copy')).toHaveLength(2);
    expect(Object.fromEntries(lastCopy().items.map((item) => [item.path, item.onClash]))).toEqual({
      'reports/2026': 'skip',
      'reports/a.md': 'keep-both',
      'reports/b.pdf': 'keep-both',
    });
  });

  it('Cancel sends nothing more', async () => {
    await pasteWithClashes();
    await answer('cancel');

    expect(server.callsTo('copy')).toHaveLength(1);
    expect(document.body.querySelector('.documents-clash')).toBeNull();
  });

  it('a partial 409 shows the sentence and marks what was done', async () => {
    server.reply('copy', 409, {
      error: 'The copy did not finish: 1 of 2 copied. Not copied: reports/b.pdf (permission denied).',
      results: [
        { from: 'reports/a.md', to: 'a.md', outcome: 'done' },
        { from: 'reports/b.pdf', to: 'b.pdf', outcome: 'failed', reason: 'permission denied' },
      ],
    });

    await openExplorer('alpha');
    await doubleClick(row('reports'));
    await click(row('a.md'));
    await click(row('b.pdf'), { ctrlKey: true });
    await press('c', { ctrlKey: true });
    await press('Backspace');
    await press('v', { ctrlKey: true });

    expect(bodyText()).toContain('The copy did not finish: 1 of 2 copied. Not copied: reports/b.pdf (permission denied).');
    expect(document.body.querySelector('.documents-error')!.textContent).toContain('Done: a.md');
  });
});
