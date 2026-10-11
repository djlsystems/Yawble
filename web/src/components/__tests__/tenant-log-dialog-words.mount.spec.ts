// @vitest-environment happy-dom
//
// THE ADMIN LOG IN WORDS. The Detail column says the common acts as a sentence - a trigger turned
// off, turned on, its daily cap set, a setting changed - with the JSON behind Show JSON; a trigger
// turned off and one turned on read apart in the Did column too. A row the words do not know shows
// its JSON as before, and When is on the browser's own clock.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { readTenantLog } = vi.hoisted(() => ({ readTenantLog: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  readTenantLog,
}));

import TenantLogDialog from '../TenantLogDialog.vue';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const Off = '{"team":"job-tracker","member":"scout","changed":[{"field":"enabled","from":true,"to":false}]}';
const On = '{"team":"job-tracker","member":"scout","changed":[{"field":"enabled","from":false,"to":true}]}';
const Cap = '{"team":"job-tracker","member":"scout","changed":[{"field":"dailyTokenCap","from":200000,"to":250000}]}';
const Setting = '{"setting":"concierge.mayMerge","old":"false","new":"true"}';
const Unknown = '{"files":3,"bytes":1024}';

// Rows as the Host writes them for the acts the walk-through found still showing JSON.
const SolutionUpdated =
  '{"id":"job-tracker","from":"1.0.0","to":"1.1.0","folder":"/data/packages/job-tracker","diff":{"Members":{"Added":["scout"],"Changed":[],"Removed":[],"Any":true},"Triggers":{"Added":[],"Changed":["Scan for postings"],"Removed":[],"Any":true},"Skills":{"Added":[],"Changed":[],"Removed":[],"Any":false},"Sites":{"Added":[],"Changed":[],"Removed":[],"Any":false},"Tools":{"Added":[],"Changed":[],"Removed":[],"Any":false},"Plugins":{"Added":[],"Changed":[],"Removed":[],"Any":false}}}';
const ScheduleDeleted = '{"team":"job-tracker","member":"scout"}';
const ScheduleCreated = '{"team":"job-tracker","member":"scout","solution":true}';
const SkillCreated = '{"team":"job-tracker","name":"triage","roles":["member"],"solution":true}';
const MemberAdded = '{"requestedTag":"claude","resolvedAgent":"claude","team":"job-tracker"}';

function row(seq: number, action: string, detail: string | null, subjectName = 'Scan for postings') {
  return {
    seq,
    occurredAt: '2026-10-07T14:41:07Z',
    actorId: 'u1',
    actorEmail: 'dana@example.com',
    action,
    subject: `trg_${seq}`,
    subjectName,
    detail,
  };
}

async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 60));
  await flushPromises();
}

/** The log's rows, not the virtual scroll's spacers: each has its five cells. */
const rows = () => ([...document.body.querySelectorAll('tbody tr')] as HTMLElement[]).filter((tr) => tr.querySelectorAll('td').length === 5);
const cells = (tr: HTMLElement) => [...tr.querySelectorAll('td')].map((td) => td.textContent!.trim());

beforeEach(() => {
  readTenantLog.mockReset();
  readTenantLog.mockResolvedValue({
    events: [
      row(5, 'schedule.changed', Off),
      row(4, 'schedule.changed', On),
      row(3, 'schedule.changed', Cap),
      row(2, 'tenant.settingChanged', Setting, 'concierge.mayMerge'),
      row(1, 'retired.verb', Unknown, 'Team 1'),
    ],
    total: 5,
  });
});

afterEach(resetBody);

describe('the tenant log in words', () => {
  it('tells a trigger turned off from one turned on, in Did and in Detail', async () => {
    await mountDialog(TenantLogDialog);
    await settle();

    const [off, on] = rows();
    expect(cells(off!)[2]).toBe('turned trigger off');
    expect(cells(off!)[4]).toBe('Turned off.');
    expect(cells(on!)[2]).toBe('turned trigger on');
    expect(cells(on!)[4]).toBe('Turned on.');
  });

  it('says a cap set and a setting changed as sentences, not JSON', async () => {
    await mountDialog(TenantLogDialog);
    await settle();

    const [, , cap, setting] = rows();
    expect(cells(cap!)[2]).toBe("set trigger's daily cap");
    expect(cells(cap!)[4]).toBe('Daily cap set to 250,000 tokens (was 200,000 tokens).');
    expect(cells(setting!)[4]).toBe('The setting concierge.mayMerge changed from false to true.');
    expect(bodyText()).not.toContain('"changed"');
  });

  it('keeps the JSON behind Show JSON, and shows it for a row the words do not know', async () => {
    await mountDialog(TenantLogDialog);
    await settle();

    expect(cells(rows()[4]!)[4]).toBe(Unknown);

    (document.body.querySelector('[data-show-json]') as HTMLElement).click();
    await settle();

    expect(cells(rows()[0]!)[4]).toBe(Off);
    expect(cells(rows()[2]!)[4]).toBe(Cap);
  });

  it('says a solution updated, triggers created and deleted, a skill created and a member added in words', async () => {
    readTenantLog.mockResolvedValue({
      events: [
        row(5, 'solution.updated', SolutionUpdated, 'Job tracker'),
        row(4, 'schedule.deleted', ScheduleDeleted),
        row(3, 'schedule.created', ScheduleCreated),
        row(2, 'skill.created', SkillCreated, 'triage'),
        row(1, 'member.added', MemberAdded, 'scout'),
      ],
      total: 5,
    });
    await mountDialog(TenantLogDialog);
    await settle();

    const [updated, deleted, created, skill, member] = rows();
    expect(cells(updated!)[2]).toBe('updated a solution');
    expect(cells(updated!)[4]).toBe('Updated job-tracker from 1.0.0 to 1.1.0; members added scout; triggers changed Scan for postings.');
    expect(cells(deleted!)[2]).toBe('deleted trigger');
    expect(cells(deleted!)[4]).toBe('Deleted; it ran scout on team job-tracker.');
    expect(cells(created!)[2]).toBe('created trigger');
    expect(cells(created!)[4]).toBe('Runs scout on team job-tracker; by its solution.');
    expect(cells(skill!)[2]).toBe('created skill');
    expect(cells(skill!)[4]).toBe('Skill triage on team job-tracker; for member; by its solution.');
    expect(cells(member!)[2]).toBe('added member');
    expect(cells(member!)[4]).toBe('Hired on team job-tracker running claude.');
    expect(bodyText()).not.toContain('{"');
  });

  it("reads When on the browser's own clock", async () => {
    await mountDialog(TenantLogDialog);
    await settle();

    expect(cells(rows()[0]!)[0]).toBe(new Date('2026-10-07T14:41:07Z').toLocaleString());
    expect(bodyText()).not.toContain('2026-10-07T14:41:07Z');
  });
});
