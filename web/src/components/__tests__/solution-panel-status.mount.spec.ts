// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S STATUS SECTION (B001J): each member's state and last run, when each schedule
// next fires, what the team is blocked on with the fix inline - an upload box into the missing folder,
// a connection picker that binds the slot through the member's own settings route - and today's
// MEASURED spend against each cap, unmeasured runs counted as such. Package text stays text.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QFile, QSelect } from 'quasar';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { HtmlLooking, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';

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
    expect(bodyFind('[data-panel-status]')?.textContent).toBe('3 new jobs · last checked 2026-09-30T08:00:00Z');
    expect(bodyFind('[data-section="status"]')).not.toBeNull();
  });

  it("shows each member's state and last run", async () => {
    await panel();

    const scout = bodyFind('[data-member="scout"]')!;
    expect(scout.textContent).toContain('Scout');
    expect(scout.textContent).toContain('idle');
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

    expect(bodyFind('[data-trigger="trg_scan"] [data-spend]')?.textContent).toBe(
      '12,345 of 200,000 tokens today (1 run not measured)',
    );
    expect(bodyFind('[data-trigger="trg_apply"] [data-spend]')?.textContent).toBe('0 tokens today, no cap');
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

  it("says the Host's sentence for a team not installed from a package", async () => {
    calls = [];
    vi.stubGlobal('fetch', vi.fn(fakeHost([() => reply(404, { error: 'Team plain was not installed from a package.' })], calls)));
    await mountDialog(SolutionPanel, { team: 'plain' });
    await settle();

    expect(bodyFind('[data-panel-problem]')?.textContent).toContain('Team plain was not installed from a package.');
  });
});
