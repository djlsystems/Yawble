// @vitest-environment happy-dom
//
// A WHOLE FOLDER AND A .ZIP INTO DOCUMENTS, from the explorer's toolbar. A folder is one request to
// `upload-folder` carrying each file with its path inside the folder (subfolders kept); a .zip is
// one request to `upload-zip`. Both ask about a clash first (`onClash: 'ask'`), put the clash to
// the person in the same dialog as Upload, and send again with the choice. An unsafe zip's refusal
// is shown with the server's own sentence.
//
// SEEN TO FAIL: with the dialog as it was (no folder or zip upload) every case fails; each was also
// reddened on its own - relativePath not sent, the choice not passed on (always `ask`), the zip
// sent to the folder route, and a refusal dropped instead of shown.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const q = vi.hoisted(() => ({
  screen: { lt: { sm: false } },
  platform: { is: { mac: false } },
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: q.notify, screen: q.screen, platform: q.platform }),
}));

import { documentsServer } from '../../test/documentsServer';
import { click, doubleClick, openExplorer, row, settle, toolbarButton, until } from '../../test/documentsExplorer';
import { resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  localStorage.clear();
  q.notify.mockReset();
  server = documentsServer();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function inReports() {
  await openExplorer('alpha');
  await doubleClick(row('reports'));
}

/** A file as a browser folder pick gives it: its path inside the picked folder on the File. */
function picked(relativePath: string): File {
  const file = new File(['x'], relativePath.slice(relativePath.lastIndexOf('/') + 1));
  Object.defineProperty(file, 'webkitRelativePath', { value: relativePath });
  return file;
}

async function choose(selector: string, files: File[]) {
  const input = document.body.querySelector<HTMLInputElement>(selector)!;
  Object.defineProperty(input, 'files', { value: files, configurable: true });
  input.dispatchEvent(new Event('change'));
  await settle();
}

const errorText = () => document.body.querySelector('.documents-error')?.textContent?.trim() ?? '';

describe('Upload a folder', () => {
  it('is on the toolbar and picks a folder, not files', async () => {
    await inReports();

    expect(toolbarButton('uploadFolder')).not.toBeNull();
    expect(document.body.querySelector('[data-upload-folder-input]')!.hasAttribute('webkitdirectory')).toBe(true);
  });

  it('sends every file with its relative path, in one request, into the folder on screen', async () => {
    await inReports();
    await choose('[data-upload-folder-input]', [
      picked('job-tracker/solution.json'),
      picked('job-tracker/members/scheduler.md'),
    ]);

    expect(server.callsTo('upload')).toHaveLength(0);
    expect(server.callsTo('upload-folder').map((call) => [call.folder, call.body])).toEqual([
      [
        'alpha',
        {
          file: ['solution.json', 'scheduler.md'],
          relativePath: ['job-tracker/solution.json', 'job-tracker/members/scheduler.md'],
          path: 'reports',
          onClash: 'ask',
        },
      ],
    ]);
  });
});

describe('Upload a .zip', () => {
  it('sends the zip to the zip route, into the folder on screen', async () => {
    await inReports();
    expect(toolbarButton('uploadZip')).not.toBeNull();
    await choose('[data-upload-zip-input]', [new File(['PK'], 'job-tracker.zip')]);

    expect(server.callsTo('upload')).toHaveLength(0);
    expect(server.callsTo('upload-zip').map((call) => [call.folder, call.body])).toEqual([
      ['alpha', { file: 'job-tracker.zip', path: 'reports', onClash: 'ask' }],
    ]);
  });

  it('shows the refusal of an unsafe zip in the server\'s words', async () => {
    await inReports();
    server.reply('upload-zip', 400, { error: 'The zip was not unpacked: evil/link is a link.' });
    await choose('[data-upload-zip-input]', [new File(['PK'], 'evil.zip')]);

    expect(errorText()).toBe('The zip was not unpacked: evil/link is a link.');
    expect(document.body.querySelector('.documents-error')!.getAttribute('role')).toBe('alert');
  });
});

describe('a clash, for a folder or a zip', () => {
  const clash = (name: string) => ({
    error: `${name} is already in reports. Choose Keep both, Replace or Skip.`,
    clashes: [{ from: name, to: `reports/${name}`, isFolder: true }],
  });

  it.each([
    ['upload-zip', '[data-upload-zip-input]', () => [new File(['PK'], 'job-tracker.zip')]],
    ['upload-folder', '[data-upload-folder-input]', () => [picked('job-tracker/solution.json')]],
  ] as const)('%s asks, then sends again with the person\'s choice', async (route, input, files) => {
    for (const choice of ['keep-both', 'replace', 'skip'] as const) {
      await inReports();
      server.calls.length = 0;
      server.reply(route, 409, clash('job-tracker'));
      await choose(input, files());

      await click(await until(() => document.body.querySelector(`.documents-clash [data-clash="${choice}"]`)));
      await settle();

      expect(server.callsTo(route).map((call) => (call.body as { onClash: string }).onClash)).toEqual(['ask', choice]);
      resetBody();
    }
  });

  it('Cancel sends nothing more', async () => {
    await inReports();
    server.reply('upload-zip', 409, clash('job-tracker'));
    await choose('[data-upload-zip-input]', [new File(['PK'], 'job-tracker.zip')]);

    await click(await until(() => document.body.querySelector('.documents-clash [data-clash="cancel"]')));
    await settle();

    expect(server.callsTo('upload-zip')).toHaveLength(1);
  });
});
