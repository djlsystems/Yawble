// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S STATUS SECTION: each member's state and last run, when each schedule
// next fires, what the team is blocked on with the fix inline - an upload box into the missing folder,
// a connection picker that binds the slot through the member's own settings route - and today's
// MEASURED spend against each cap, unmeasured runs counted as such. Package text stays text.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QFile, QSelect } from 'quasar';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { HtmlLooking, panelRead, panelRoutes, panelTrigger } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';
import { localTime } from '../../lib/localTime';
import { whenWords } from '../../lib/solutionPanel';

let calls: Call[] = [];
let read: PanelShape;

function serve(extra: Route[] = []) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ...panelRoutes(() => read)], calls)));
}

beforeEach(() => {
  read = panelRead();
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function panel() {
  const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
  await settle();
  return wrapper;
}

describe('the control panel: Status', () => {
  it('reads the panel and shows the state, the status line and the description', async () => {
    await panel();

    expect(sent(calls, 'GET', '/api/teams/job-tracker/solution/panel').length).toBeGreaterThan(0);
    expect(bodyFind('[data-panel-title]')?.textContent).toContain('Job Tracker');
    expect(bodyFind('[data-panel-state="blocked"]')?.textContent).toContain('Blocked: Upload a file to Resume/');
    expect(bodyFind('[data-panel-status]')?.textContent).toBe(`3 new jobs · last checked ${localTime(Date.parse('2026-09-30T08:00:00Z'), { date: true })}`);
    expect(bodyFind('[data-section="status"]')).not.toBeNull();
  });

  it("shows each member's state and last run", async () => {
    await panel();

    const scout = bodyFind('[data-member="scout"]')!;
    expect(scout.textContent).toContain('Scout');
    expect(scout.querySelector('[data-member-state]')?.textContent).toBe('idle');
    expect(scout.textContent).toContain('completed');
    expect(bodyFind('[data-member="Manager"]')?.textContent).toContain('No runs yet');
  });

  it('shows when a schedule next fires and that an event trigger fires on its event', async () => {
    await panel();

    expect(bodyFind('[data-trigger="trg_scan"] [data-next-fire]')?.textContent).toMatch(/^Next /);
    expect(bodyFind('[data-trigger="trg_apply"] [data-next-fire]')?.textContent).toBe('Fires on its event');
  });

  it("shows today's measured spend against the cap and counts unmeasured runs, never estimating them", async () => {
    await panel();

    // The Triggers dialog's own words (`spentTodayLine`), so the two screens cannot disagree.
    expect(bodyFind('[data-trigger="trg_scan"] [data-spend]')?.textContent?.trim()).toBe(
      'spent today 12,345 tokens + 1 run not measured / cap 200,000 tokens',
    );
    expect(bodyFind('[data-trigger="trg_apply"] [data-spend]')?.textContent?.trim()).toBe('spent today 0 tokens (no cap)');
  });

  it('does not claim zero for a day whose runs were none of them measured, and says a reached cap', async () => {
    read = panelRead({
      triggers: [
        { ...panelRead().triggers[0]!, spentToday: { billableTokens: 0, measuredRuns: 0, unmeasuredRuns: 2 }, capReachedToday: true, cappedUntil: '2026-10-01T00:00:00Z' },
      ],
    });
    await panel();

    const spend = bodyFind('[data-trigger="trg_scan"] [data-spend]')!;
    expect(spend.textContent).toContain('nothing measured + 2 runs not measured / cap 200,000 tokens');
    expect(spend.textContent).toContain('cap reached');
    // When it resumes, on the reader's own clock (the panel's one clock), not the trigger's.
    expect(spend.querySelector('[data-capped]')?.textContent).toContain(`Capped until ${whenWords('2026-10-01T00:00:00Z')}`);
  });

  it('says what each blocked item needs, in the Host\'s words', async () => {
    await panel();

    expect(bodyFind('[data-blocked="Resume/"] [data-blocked-reason]')?.textContent).toBe('Upload a file to Resume/');
    expect(bodyFind('[data-blocked="mailbox"] [data-blocked-reason]')?.textContent).toBe("Connect Scout's mailbox");
  });

  it('shows what a member snapshot says: blocked, a decision it waits for, work queued, or gone', async () => {
    read = panelRead({
      members: [
        { ...panelRead().members[1]!, state: 'running', blocked: null, failed: null, needsDecision: 'Which region?', queueDepth: 2 },
        { packageName: 'Writer', member: 'writer', kind: 'agent', role: 'member', state: 'missing', lastRun: null },
      ],
    });
    await panel();

    expect(bodyFind('[data-member="scout"] [data-member-state]')?.textContent).toBe('running · waiting for a decision: Which region? · 2 queued');
    expect(bodyFind('[data-member="writer"] [data-member-state]')?.textContent).toBe('no longer on the team');
  });

  it('uploads a missing document into its folder and reads the panel again', async () => {
    serve([
      (call) => {
        if (call.url !== '/api/teams/job-tracker/documents/upload') return undefined;
        read = panelRead({ blocked: read.blocked.filter((item) => item.kind !== 'document') });
        return reply(200, { name: 'cv.pdf', path: 'Resume/cv.pdf' });
      },
    ]);
    const wrapper = await panel();
    const before = sent(calls, 'GET', '/api/teams/job-tracker/solution/panel').length;

    const boxes = wrapper.findAllComponents(QFile);
    expect(boxes).toHaveLength(1);
    boxes[0]!.vm.$emit('update:modelValue', new File(['cv'], 'cv.pdf'));
    await settle();

    const upload = sent(calls, 'POST', '/api/teams/job-tracker/documents/upload')[0]!;
    expect((upload.body as FormData).get('path')).toBe('Resume');
    expect(((upload.body as FormData).get('file') as File).name).toBe('cv.pdf');
    expect(sent(calls, 'GET', '/api/teams/job-tracker/solution/panel').length).toBe(before + 1);
    expect(bodyFind('[data-blocked="Resume/"]')).toBeNull();
  });

  it("binds a missing connection through the member's settings route, keeping its other settings", async () => {
    serve([(call) => (call.method === 'PUT' && call.url.endsWith('/members/scout/plugin-settings') ? reply(204) : undefined)]);
    const wrapper = await panel();

    const fix = bodyFind('[data-blocked="mailbox"] [data-fix-connection]');
    expect(fix).not.toBeNull();
    const picker = wrapper.findAllComponents(QSelect).find((select) => fix!.contains(select.element));
    expect(picker).toBeDefined();
    picker!.vm.$emit('update:modelValue', 'conn_1');
    await settle();

    const put = sent(calls, 'PUT', '/api/teams/job-tracker/members/scout/plugin-settings');
    expect(put).toHaveLength(1);
    expect(put[0]!.body).toEqual({
      config: { keywords: ['dotnet'], region: 'EU' },
      secrets: { apiKey: 'JOB_BOARD_KEY' },
      connections: { mailbox: 'conn_1' },
    });
  });

  it('renders package text and run values that look like HTML as their characters', async () => {
    read = panelRead({ status: HtmlLooking, description: '<b>bold</b>', state: { kind: 'blocked', reason: '<i>x</i>' } });
    await panel();

    expect(bodyFind('[data-panel-status]')?.textContent).toBe(HtmlLooking);
    expect(bodyFind('[data-panel-description]')?.textContent).toBe('<b>bold</b>');
    expect(bodyFind('[data-solution-panel] img')).toBeNull();
    expect(bodyFind('[data-panel-description] b')).toBeNull();
  });

  it('lays members, triggers and sites out as tiles on the shared grid, one per item', async () => {
    read = panelRead({
      sites: [
        { name: 'tracker', url: '/sites/job-tracker/tracker/', published: true },
        { name: 'admin', url: '/sites/job-tracker/admin/', published: false },
      ],
    });
    await panel();

    const tilesOf = (grid: string) => {
      const el = bodyFind(grid)!;
      expect(el.classList).toContain('os-tiles');
      return [...el.children].map((child) => {
        expect(child.classList).toContain('os-tile');
        return child;
      });
    };
    expect(tilesOf('[data-panel-members]').map((el) => el.getAttribute('data-member'))).toEqual(['Manager', 'scout']);
    expect(tilesOf('[data-panel-schedules]').map((el) => el.getAttribute('data-trigger'))).toEqual(['trg_scan', 'trg_apply']);
    expect(tilesOf('.solution-site-tiles').map((el) => el.getAttribute('data-panel-site'))).toEqual(['tracker', 'admin']);
    expect(bodyFind('[data-member="Manager"] .os-tile-head')?.textContent).toContain('Manager');

    // Each grid caps its least column width at its own, so a phone gets one column that fits.
    const source = readFileSync(join(import.meta.dirname, '../SolutionPanel.vue'), 'utf8');
    for (const grid of ['solution-member-tiles', 'solution-trigger-tiles', 'solution-site-tiles']) {
      expect(new RegExp(`\\.${grid}[^{]*\\{[^}]*--os-tile-min:\\s*min\\(\\d+rem, 100%\\)`).test(source), grid).toBe(true);
    }
  });

  it('keeps a trigger tile on Status to its name, member, next fire and spend: no instruction, no Details', async () => {
    const tail = 'A SENTENCE ONLY THE INSTRUCTION HAS.';
    read = panelRead({
      triggers: [panelTrigger({ id: 'trg_scan', packageName: 'Scan for postings', packageKind: 'schedule', instruction: `Scan the boards.\n${tail}` })],
    });
    await panel();

    const tile = bodyFind('[data-trigger="trg_scan"]')!;
    expect(tile.textContent).toContain('Scan for postings');
    expect(tile.textContent).not.toContain('Scan the boards.');
    expect(tile.textContent).not.toContain(tail);
    expect(tile.querySelector('[data-trigger-instruction]')).toBeNull();
    expect(tile.querySelector('[data-trigger-details]')).toBeNull();
  });

  it("opens a published site in a new tab, and disables Open for an unpublished one", async () => {
    read = panelRead({
      sites: [
        { name: 'tracker', url: '/sites/job-tracker/tracker/', published: true },
        { name: 'admin', url: '/sites/job-tracker/admin/', published: false },
      ],
    });
    await panel();

    const published = bodyFind('[data-panel-site="tracker"]')!;
    const open = published.querySelector<HTMLAnchorElement>('a[data-site-open]')!;
    expect(open.getAttribute('href')).toBe('/sites/job-tracker/tracker/');
    expect(open.getAttribute('target')).toBe('_blank');
    expect(open.getAttribute('rel')).toContain('noopener');
    expect(published.querySelector('[data-site-published]')?.textContent).toBe('Published');

    const unpublished = bodyFind('[data-panel-site="admin"]')!;
    const closed = unpublished.querySelector('[data-site-open]')!;
    expect(closed.getAttribute('href')).toBeNull();
    expect(closed.classList.contains('disabled') || closed.hasAttribute('disabled') || closed.getAttribute('aria-disabled') === 'true').toBe(true);
    expect(unpublished.querySelector('[data-site-published]')?.textContent).toBe('Not published');
  });

  it('shows no Sites heading and no grid for a package with no sites', async () => {
    read = panelRead({ sites: [] });
    await panel();

    expect(bodyFind('[data-panel-site]')).toBeNull();
    expect(bodyFind('.solution-site-tiles')).toBeNull();
    const headings = [...document.body.querySelectorAll('.solution-heading')].map((el) => el.textContent?.trim());
    expect(headings).not.toContain('Sites');
  });

  it('keeps the member state and spend lines to their own words: any label sits outside them', async () => {
    await panel();

    const state = bodyFind('[data-member="scout"] [data-member-state]')!;
    expect(state.textContent).toBe('idle');
    expect(state.children).toHaveLength(0);
    const spend = bodyFind('[data-trigger="trg_apply"] [data-spend]')!;
    expect(spend.textContent?.trim()).toBe('spent today 0 tokens (no cap)');
  });

  it("says the Host's sentence for a team not installed from a package", async () => {
    calls = [];
    vi.stubGlobal('fetch', vi.fn(fakeHost([() => reply(404, { error: 'Team plain was not installed from a package.' })], calls)));
    await mountDialog(SolutionPanel, { team: 'plain' });
    await settle();

    expect(bodyFind('[data-panel-problem]')?.textContent).toContain('Team plain was not installed from a package.');
  });
});
