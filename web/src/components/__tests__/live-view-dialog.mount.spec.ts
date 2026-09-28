// @vitest-environment happy-dom
//
// The live view of a member's run: lines appended verbatim as they arrive, one striped row per step, the view follows the
// bottom until the person scrolls up, the end of the run is said and the lines kept, and a member
// with nothing to watch shows the server's sentence rather than a spinner.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import LiveViewDialog from '../LiveViewDialog.vue';
import { asMemberId, asTeamId } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

/** A text/plain body the spec feeds by hand, so "as it arrives" is something a case can step through. */
function liveStream() {
  const encoder = new TextEncoder();
  let control!: ReadableStreamDefaultController<Uint8Array>;
  const body = new ReadableStream<Uint8Array>({ start: (c) => { control = c; } });
  const response = new Response(body, {
    status: 200,
    headers: { 'content-type': 'text/plain; charset=utf-8' },
  });

  return {
    response,
    push: async (text: string) => {
      control.enqueue(encoder.encode(text));
      await flushPromises();
      await flushPromises();
    },
    end: async () => {
      control.close();
      await flushPromises();
      await flushPromises();
    },
  };
}

const props = { team: asTeamId('alpha'), member: asMemberId('Builder'), name: 'Builder Bo' };

/**
 * happy-dom does no layout, so a scroller's geometry is stated: `content` px of lines in a 100px
 * window. `scrollTop` is kept as a plain field so what the component writes is what a case reads.
 */
function giveGeometry(el: HTMLElement, content: () => number) {
  let top = 0;
  Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => 100 });
  Object.defineProperty(el, 'scrollHeight', { configurable: true, get: content });
  Object.defineProperty(el, 'scrollTop', {
    configurable: true,
    get: () => top,
    set: (v: number) => { top = Math.max(0, Math.min(v, content() - 100)); },
  });
}

function scroller() {
  const el = bodyFind('.live-view-lines');
  if (!el) throw new Error('no line view rendered');
  return el;
}

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

/**
 * The API, mocked by path: `/live` answers `live`, `/runs` answers the page for its `before` cursor
 * (an empty page unless the case gives one), and `/runs/{seq}/transcript` answers from `transcripts`.
 */
interface Api {
  /** The `/live` answer; `'pending'` holds the request open, as the Host does while it finds the transcript. */
  live?: Response | 'pending';
  pages?: Record<string, unknown>;
  transcripts?: Record<number, () => Response>;
}

function stubApi(api: Api) {
  const fetch = vi.fn((input: string) => {
    const url = new URL(input, 'http://host');
    const transcript = /\/runs\/(\d+)\/transcript$/.exec(url.pathname);
    if (transcript) {
      const answer = api.transcripts?.[Number(transcript[1])];
      return Promise.resolve(answer ? answer() : new Response('No such run.', { status: 404 }));
    }
    if (url.pathname.endsWith('/runs')) {
      const page = api.pages?.[url.searchParams.get('before') ?? ''] ?? { runs: [], nextBefore: null };
      return Promise.resolve(jsonResponse(page));
    }
    if (url.pathname.endsWith('/live') && api.live === 'pending') return new Promise<Response>(() => {});
    if (url.pathname.endsWith('/live') && api.live) return Promise.resolve(api.live);
    return Promise.resolve(new Response('Builder is not running.', { status: 404 }));
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

async function openWith(response: Response) {
  const fetch = stubApi({ live: response });
  const wrapper = await mountDialog(LiveViewDialog, props);
  return { wrapper, fetch };
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('the live view dialog', () => {
  it('asks the live route for this member', async () => {
    const { fetch } = await openWith(liveStream().response);

    expect(fetch).toHaveBeenCalledWith('/api/teams/alpha/members/Builder/live', expect.anything());
  });

  it('renders a fixture of lines verbatim, including one split across chunks', async () => {
    const live = liveStream();
    await openWith(live.response);

    await live.push('$ dotnet build\n  Restoring   packages…\nBuild succ');
    await live.push('eeded.\n');

    const rendered = [...document.body.querySelectorAll('.live-view-line')].map((n) => n.textContent);
    expect(rendered).toEqual(['$ dotnet build', '  Restoring   packages…', 'Build succeeded.']);
  });

  it('shows each step as its own row, a tool call with its name as a chip', async () => {
    const live = liveStream();
    await openWith(live.response);

    await live.push('Checking the suite.\nBash: npm test\nTests passed (12 bytes)\n');

    const rows = [...document.body.querySelectorAll<HTMLElement>('.live-view-line')];
    expect(rows.map((row) => row.dataset.kind)).toEqual(['text', 'tool', 'result']);
    expect(rows[1]!.querySelector('.live-view-label')!.textContent).toBe('Bash');
    expect(rows[1]!.querySelector('.live-view-text')!.textContent).toBe('npm test');
  });

  it('shows each step at the time the server stamped on it, and a step with none without one', async () => {
    const live = liveStream();
    await openWith(live.response);

    const at = new Date(2026, 8, 26, 9, 34, 41);
    await live.push(`${at.toISOString()}\tBash: npm test\n\tgarbage\n`);

    const rows = [...document.body.querySelectorAll<HTMLElement>('.live-view-line')];
    expect(rows[0]!.querySelector('.live-view-time')!.textContent).toBe('09:34:41');
    expect(rows[0]!.querySelector('.live-view-text')!.textContent).toBe('npm test');
    expect(rows[1]!.querySelector('.live-view-time')).toBeNull();
    expect(rows[1]!.textContent).toBe('garbage');
  });

  it("hides the agent's attachments, says how many, and shows them when asked", async () => {
    const live = liveStream();
    await openWith(live.response);

    await live.push('Attachment: <total_tokens>1 tokens left</total_tokens>\nWorking.\nAttachment: environment\n');

    expect(document.body.querySelectorAll('.live-view-line')).toHaveLength(1);
    expect(bodyText()).toContain('Notes (2 hidden)');

    (bodyFind('.live-view-notes') as HTMLElement).click();
    await flushPromises();

    const rows = [...document.body.querySelectorAll<HTMLElement>('.live-view-line')];
    expect(rows.map((row) => row.dataset.kind)).toEqual(['aside', 'text', 'aside']);
    expect(rows[2]!.querySelector('.live-view-text')!.textContent).toBe('environment');
  });

  it('stays at the bottom as lines arrive', async () => {
    const live = liveStream();
    await openWith(live.response);
    await live.push('first\n');

    const el = scroller();
    giveGeometry(el, () => 20 * document.body.querySelectorAll('.live-view-line').length);

    for (let i = 0; i < 10; i++) await live.push(`line ${i}\n`);

    expect(el.scrollHeight).toBe(220);
    expect(el.scrollTop).toBe(120);
  });

  it('does not pull a person down who scrolled up, and follows again once they are back', async () => {
    const live = liveStream();
    await openWith(live.response);
    await live.push('first\n');

    const el = scroller();
    giveGeometry(el, () => 20 * document.body.querySelectorAll('.live-view-line').length);
    for (let i = 0; i < 10; i++) await live.push(`line ${i}\n`);

    el.scrollTop = 40;
    el.dispatchEvent(new Event('scroll'));
    await live.push('more\n');
    await live.push('and more\n');

    expect(el.scrollTop).toBe(40);
    expect(bodyText()).toContain('Scroll to the bottom to follow again');

    el.scrollTop = el.scrollHeight;
    el.dispatchEvent(new Event('scroll'));
    await live.push('after returning\n');

    expect(el.scrollTop).toBe(el.scrollHeight - 100);
  });

  it('says the run ended and keeps the lines', async () => {
    const live = liveStream();
    await openWith(live.response);

    await live.push('one\ntwo\n');
    await live.end();

    expect(bodyText()).toContain('The run ended.');
    expect(document.body.querySelectorAll('.live-view-line')).toHaveLength(2);
  });

  it('shows the 404 sentence when the member is not running', async () => {
    await openWith(new Response('Builder is not running.', { status: 404 }));

    expect(bodyText()).toContain('Builder is not running.');
    expect(bodyFind('.q-spinner')).toBeNull();
  });

  it('shows the reason when the preset has no live view', async () => {
    await openWith(
      new Response(JSON.stringify({ live: false, reason: 'The echo preset has no live view.' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );

    expect(bodyText()).toContain('The echo preset has no live view.');
    expect(bodyFind('.q-spinner')).toBeNull();
    expect(bodyFind('.live-view-lines')).toBeNull();
  });

  it('says it is waiting for the agent while the Host is still finding the transcript', async () => {
    stubApi({ live: 'pending' });
    await mountDialog(LiveViewDialog, props);

    expect(bodyText()).toContain('Waiting for the agent to start its session.');
    expect(bodyText()).not.toContain('Not running');
  });

  it('says there is no live view when the Host gives an empty reason, rather than a blank box', async () => {
    await openWith(jsonResponse({ live: false, reason: '' }));

    expect(bodyFind('.live-view-reason')!.textContent!.trim()).toBe('This member has no live view.');
  });
});

const run = (seq: number, over: Record<string, unknown> = {}) => ({
  seq,
  workflow: 400 + seq,
  startedAt: new Date(2026, 8, 26, 9, seq).toISOString(),
  endedAt: new Date(2026, 8, 26, 9, seq, 42).toISOString(),
  durationMs: 42_000,
  outcome: 'completed',
  output: null,
  reason: null,
  quiet: false,
  ...over,
});

const text = (body: string, status = 200) => () =>
  new Response(body, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });

function runRows() {
  return [...document.body.querySelectorAll<HTMLElement>('.earlier-run')];
}

describe('the watch dialog: live run and earlier runs', () => {
  it('says Not running for an idle member, without asking the live route', async () => {
    const fetch = stubApi({});
    await mountDialog(LiveViewDialog, { ...props, running: false });

    expect(bodyText()).toContain('Not running');
    expect(bodyFind('.q-spinner')).toBeNull();
    expect(fetch.mock.calls.map(([url]) => url)).not.toContain('/api/teams/alpha/members/Builder/live');
  });

  it('shows the live run when running, above the earlier runs', async () => {
    const live = liveStream();
    stubApi({ live: live.response, pages: { '': { runs: [run(2)], nextBefore: null } } });
    await mountDialog(LiveViewDialog, { ...props, running: true });

    await live.push('Bash: npm test\n');

    expect(bodyText()).not.toContain('Not running');
    expect(bodyFind('.live-view-lines')!.textContent).toContain('npm test');
    expect(bodyText()).toContain('Earlier runs');
    expect(runRows()).toHaveLength(1);
  });

  it("lists this member's earlier runs newest first, each with its time, workflow, duration and outcome", async () => {
    const fetch = stubApi({
      pages: {
        '': {
          runs: [
            run(9, { outcome: 'handedBack', durationMs: 185_000 }),
            run(7, { outcome: 'blocked', workflow: null }),
            run(3, { outcome: 'failed', durationMs: 3_725_000 }),
          ],
          nextBefore: null,
        },
      },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    expect(fetch).toHaveBeenCalledWith('/api/teams/alpha/members/Builder/runs', undefined);

    const rows = runRows();
    expect(rows.map((row) => row.dataset.seq)).toEqual(['9', '7', '3']);
    expect(rows[0]!.querySelector('.earlier-run-when')!.textContent).toContain('09:09');
    expect(rows[0]!.textContent).toContain('workflow #409');
    expect(rows[0]!.querySelector('.earlier-run-duration')!.textContent).toBe('3m 05s');
    expect(rows[0]!.querySelector('.earlier-run-outcome')!.textContent).toBe('handed back');
    expect(rows[1]!.textContent).toContain('no workflow');
    expect(rows[1]!.querySelector('.earlier-run-outcome')!.textContent).toBe('blocked');
    expect(rows[2]!.querySelector('.earlier-run-duration')!.textContent).toBe('1h 02m');
    expect(rows[2]!.querySelector('.earlier-run-outcome')!.textContent).toBe('failed');
  });

  it('marks a quiet run as quiet, and a loud one not at all', async () => {
    stubApi({
      pages: { '': { runs: [run(9, { quiet: true }), run(7, { quiet: false })], nextBefore: null } },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    const [quiet, loud] = runRows();
    expect(quiet!.querySelector('.earlier-run-quiet')?.textContent?.trim()).toBe('quiet');
    expect(loud!.querySelector('.earlier-run-quiet')).toBeNull();
    expect(quiet!.querySelector('.earlier-run-outcome')!.textContent).toBe('completed');
  });

  it('loads the next older page with the cursor the last one gave, and stops when there is none', async () => {
    const fetch = stubApi({
      pages: {
        '': { runs: [run(30), run(29)], nextBefore: 29 },
        '29': { runs: [run(5)], nextBefore: null },
      },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    (bodyFind('.earlier-runs-older') as HTMLElement).click();
    await flushPromises();

    expect(fetch).toHaveBeenCalledWith('/api/teams/alpha/members/Builder/runs?before=29', undefined);
    expect(runRows().map((row) => row.dataset.seq)).toEqual(['30', '29', '5']);
    expect(bodyFind('.earlier-runs-older')).toBeNull();
  });

  it('shows a run whose start is not in the log as unknown, never as 1970 or 0s', async () => {
    stubApi({
      pages: { '': { runs: [run(5, { startedAt: null, durationMs: null })], nextBefore: null } },
      transcripts: { 5: text('Done.\n') },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    const row = runRows()[0]!;
    expect(row.querySelector('.earlier-run-when')!.textContent!.trim()).toBe('start unknown');
    expect(row.querySelector('.earlier-run-when')!.getAttribute('title')).toBeNull();
    expect(row.querySelector('.earlier-run-duration')!.textContent!.trim()).toBe('—');

    row.click();
    await flushPromises();

    const heading = bodyFind('.earlier-run-heading')!.textContent!;
    expect(heading).toContain('start unknown');
    expect(heading).toContain('—');
    for (const shown of [document.body.innerHTML, heading]) {
      expect(shown).not.toContain('1970');
      expect(shown).not.toMatch(/\b0s\b/);
      expect(shown).not.toContain('1 Jan');
    }
  });

  it('says so when the member has no earlier runs', async () => {
    stubApi({});
    await mountDialog(LiveViewDialog, { ...props, running: false });

    expect(bodyText()).toContain('No earlier runs to show.');
  });

  it("clicking a run shows its whole transcript, one row per step with its time", async () => {
    const at = new Date(2026, 8, 26, 9, 7, 12);
    const fetch = stubApi({
      pages: { '': { runs: [run(7)], nextBefore: null } },
      transcripts: {
        7: text(`${at.toISOString()}\tRead /work/spec.md\n${at.toISOString()}\tBash: npm test\n\tDone.\n`),
      },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    runRows()[0]!.click();
    await flushPromises();

    expect(fetch).toHaveBeenCalledWith('/api/teams/alpha/members/Builder/runs/7/transcript', undefined);

    const rows = [...document.body.querySelectorAll<HTMLElement>('.run-transcript-line')];
    expect(rows.map((row) => row.dataset.kind)).toEqual(['tool', 'tool', 'text']);
    expect(rows[0]!.querySelector('.live-view-time')!.textContent).toBe('09:07:12');
    expect(rows[0]!.querySelector('.live-view-label')!.textContent).toBe('Read');
    expect(rows[1]!.querySelector('.live-view-text')!.textContent).toBe('npm test');
    expect(rows[2]!.querySelector('.live-view-time')).toBeNull();
    expect(rows[2]!.textContent).toBe('Done.');

    (bodyFind('.earlier-runs-back') as HTMLElement).click();
    await flushPromises();

    expect(document.body.querySelectorAll('.run-transcript-line')).toHaveLength(0);
    expect(runRows()).toHaveLength(1);
  });

  it('shows the sentence when the transcript is no longer on disk', async () => {
    stubApi({
      pages: { '': { runs: [run(4)], nextBefore: null } },
      transcripts: { 4: text("This run's transcript is no longer on disk.", 410) },
    });
    await mountDialog(LiveViewDialog, { ...props, running: false });

    runRows()[0]!.click();
    await flushPromises();

    expect(bodyFind('.run-transcript-reason')!.textContent).toBe("This run's transcript is no longer on disk.");
    expect(document.body.querySelectorAll('.run-transcript-line')).toHaveLength(0);
  });

  it('follows the member when it starts running while the dialog is open', async () => {
    const live = liveStream();
    stubApi({ live: live.response });
    const wrapper = await mountDialog(LiveViewDialog, { ...props, running: false });
    expect(bodyText()).toContain('Not running');

    await wrapper.setProps({ running: true });
    await flushPromises();
    await live.push('Working.\n');

    expect(bodyText()).not.toContain('Not running');
    expect(bodyFind('.live-view-lines')!.textContent).toContain('Working.');
  });
});
