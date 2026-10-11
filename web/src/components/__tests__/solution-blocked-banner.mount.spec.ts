// @vitest-environment happy-dom
//
// THE TEAM BOARD'S "BLOCKED" BANNER for a team installed from a solution package: one line per
// input still missing (`GET /api/teams/{team}/solution` answering `missing`), an upload box for a
// document input that uploads into that folder and re-reads. Nothing for a team missing nothing,
// and nothing for a team not installed from a package (a 404).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { QFile } from 'quasar';

import SolutionBlockedBanner from '../SolutionBlockedBanner.vue';
import { asTeamId } from '../../api/types';
import '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';

const Resume = { kind: 'document', name: 'Resume', member: null, description: 'Your reference resume, .docx or PDF.' };
const Mail = { kind: 'connection', name: 'mail', member: 'Scout', description: 'Where postings are emailed from.' };

let calls: Call[] = [];
let missing: unknown[] = [];

function serve(routes: Route[]) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(routes, calls)));
}

const record: Route = (call) =>
  call.method === 'GET' && call.url === '/api/teams/job-tracker/solution'
    ? reply(200, {
        team: 'job-tracker',
        id: 'job-tracker',
        name: 'Job Tracker',
        version: '1.1.0',
        installedAt: '2026-09-29T10:00:00Z',
        installedBy: 'dana@example.com',
        plugins: [],
        missing,
      })
    : undefined;

beforeEach(() => {
  missing = [Resume, Mail];
  serve([record]);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function banner(team = 'job-tracker') {
  const wrapper = mount(SolutionBlockedBanner, { props: { team: asTeamId(team) } });
  await settle();
  return wrapper;
}

describe('the blocked banner', () => {
  it('shows one line per missing input', async () => {
    const wrapper = await banner();

    expect(wrapper.find('[data-solution-blocked]').exists()).toBe(true);
    const lines = wrapper.findAll('[data-missing]').map((line) => line.text());
    expect(lines[0]).toContain('Waiting for Resume/ - Your reference resume, .docx or PDF.');
    expect(lines[1]).toContain('Waiting for Scout connection mail - Where postings are emailed from.');
    expect(lines[1]).toContain('Admin → Connections');
  });

  it('uploads a document into its folder and re-reads, and the line goes', async () => {
    serve([
      (call) => {
        if (call.url !== '/api/teams/job-tracker/documents/upload') return undefined;
        missing = [Mail];
        return reply(200, { name: 'cv.pdf', path: 'Resume/cv.pdf' });
      },
      record,
    ]);
    const wrapper = await banner();

    const boxes = wrapper.findAllComponents(QFile);
    expect(boxes).toHaveLength(1);
    boxes[0]!.vm.$emit('update:modelValue', new File(['cv'], 'cv.pdf'));
    await settle();

    const upload = sent(calls, 'POST', '/api/teams/job-tracker/documents/upload')[0]!;
    expect((upload.body as FormData).get('path')).toBe('Resume');
    expect(((upload.body as FormData).get('file') as File).name).toBe('cv.pdf');
    expect(sent(calls, 'GET', '/api/teams/job-tracker/solution')).toHaveLength(2);
    expect(wrapper.find('[data-missing="Resume"]').exists()).toBe(false);
    expect(wrapper.find('[data-missing="mail"]').exists()).toBe(true);
  });

  it('renders nothing when nothing is missing', async () => {
    missing = [];
    const wrapper = await banner();
    expect(wrapper.find('[data-solution-blocked]').exists()).toBe(false);
  });

  it('renders nothing for a team not installed from a package (404)', async () => {
    serve([(call) => (call.url === '/api/teams/plain/solution' ? reply(404, { error: 'Team plain was not installed from a package.' }) : undefined)]);
    const wrapper = await banner('plain');
    expect(sent(calls, 'GET', '/api/teams/plain/solution')).toHaveLength(1);
    expect(wrapper.find('[data-solution-blocked]').exists()).toBe(false);
    expect(wrapper.text()).toBe('');
  });
});
