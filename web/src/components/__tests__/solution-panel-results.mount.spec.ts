// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S RESULTS SECTION (B001J): each `panel.outputs` folder's files newest first, as
// the Host orders them, each with its download link; a folder with nothing yet says so; recent runs
// with their output, as text.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, sent, type Call } from '../../test/solutionFixtures';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';

let calls: Call[] = [];
let read: PanelShape;

beforeEach(() => {
  read = panelRead();
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(panelRoutes(() => read), calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function results() {
  await mountDialog(SolutionPanel, { team: 'job-tracker' });
  await settle();
  await openSection('results');
}

describe('the control panel: Results', () => {
  it('lists an output folder newest first with a download link per file', async () => {
    await results();

    const files = [...document.body.querySelectorAll('[data-output="Applications"] [data-output-file]')];
    expect(files.map((file) => file.getAttribute('data-output-file'))).toEqual(['Applications/acme.md', 'Applications/older.md']);

    const download = files[0]!.querySelector('a[data-download]')!;
    expect(download.getAttribute('href')).toBe('/api/teams/job-tracker/documents/content?path=Applications%2Facme.md');
    expect(download.getAttribute('download')).toBe('acme.md');
    expect(files[0]!.textContent).toContain('2.0 KB');
  });

  it('says an output folder with nothing in it yet', async () => {
    await results();
    expect(bodyFind('[data-output="Drafts"]')?.textContent).toContain('Nothing here yet.');
  });

  it('says when only the newest files are shown', async () => {
    read = panelRead({ outputs: [{ ...panelRead().outputs[0]!, more: true }] });
    await results();
    expect(bodyFind('[data-output="Applications"]')?.textContent).toContain('Showing the newest 2');
  });

  it('shows recent runs with their output as text', async () => {
    await results();

    const run = bodyFind('[data-run="41"]')!;
    expect(run.textContent).toContain('Scout');
    expect(run.textContent).toContain('completed');
    expect(run.querySelector('[data-run-output]')?.textContent).toBe('3 new postings <script>x</script>');
    expect(run.querySelector('script')).toBeNull();
    expect(run.querySelector('[data-run-transcript-toggle]')).toBeNull();
  });

  it("reads an agent member's run transcript when asked, as text, and hides it again", async () => {
    calls = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(
        fakeHost(
          [
            (call) =>
              call.url === '/api/teams/job-tracker/members/Manager/runs/40/transcript'
                ? new Response('08:00\tRead the posting <b>now</b>\n08:01\tHanded back\n', { status: 200 })
                : undefined,
            ...panelRoutes(() => read),
          ],
          calls,
        ),
      ),
    );
    await results();

    const run = bodyFind('[data-run="40"]')!;
    (run.querySelector('[data-run-transcript-toggle]') as HTMLElement).click();
    await settle();

    expect(sent(calls, 'GET', '/api/teams/job-tracker/members/Manager/runs/40/transcript')).toHaveLength(1);
    const transcript = bodyFind('[data-run="40"] [data-run-transcript]')!;
    expect(transcript.textContent).toContain('Read the posting <b>now</b>');
    expect(transcript.querySelector('b')).toBeNull();

    (bodyFind('[data-run="40"] [data-run-transcript-toggle]') as HTMLElement).click();
    await settle();
    expect(bodyFind('[data-run="40"] [data-run-transcript]')).toBeNull();
  });
});
