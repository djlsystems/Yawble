// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD'S REVIEW OF AN UPDATE: OLD AND NEW SIDE BY SIDE. A changed trigger shows each
// value the update changes as the team has it now beside what the package makes it - its daily cap
// (200,000 to 250,000), its schedule, its instruction's words - and a changed agent member its own
// instructions, old beside new. What the team has now is read from its panel and the member's
// stored row; a value that does not change is not listed.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, installedRow, reply, updatePreview, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { panelRead, panelTrigger } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];

const OldCoordinator = 'You coordinate the job search. END-OF-OLD-COORDINATOR.';

/** The package's 1.1.0: Scan for postings is capped at 250,000 and says a new sentence. */
function newPlan() {
  const plan = hostPlan();
  plan.triggers = plan.triggers.map((trigger) =>
    trigger.name === 'Scan for postings'
      ? { ...trigger, dailyTokenCap: 250000, instruction: 'Scan the boards, then post each new job to the tracker.' }
      : trigger,
  );
  return plan;
}

/** The team as it is now, on 1.0.0: capped at 200,000, its old sentence, the Coordinator's old instructions. */
function teamNow() {
  return panelRead({
    members: [
      { packageName: 'Coordinator', member: 'Manager', kind: 'agent', role: 'manager', state: 'idle', lastRun: null },
      { packageName: 'Scout', member: 'scout', kind: 'plugin', role: 'member', state: 'idle', lastRun: null },
    ],
    triggers: [
      panelTrigger({
        id: 'trg_scan',
        packageName: 'Scan for postings',
        packageKind: 'schedule',
        kind: 'every',
        intervalSeconds: 3600,
        container: 'scout',
        instruction: 'scan',
        wakeManager: 'never',
        idleOnly: true,
        dailyTokenCap: 200000,
      }),
    ],
  });
}

function serve() {
  calls = [];
  const plan = newPlan();
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
              ? reply(200, updatePreview('job-tracker', plan))
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/teams/job-tracker/solution/panel' ? reply(200, teamNow()) : undefined),
          (call) =>
            call.method === 'GET' && call.url === '/api/teams/job-tracker/containers/Manager'
              ? reply(200, { team: 'job-tracker', id: 'Manager', name: 'Manager', systemPrompt: OldCoordinator })
              : undefined,
          ...wizardRoutes({ plan, installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] }),
        ],
        calls,
      ),
    ),
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function reviewTheUpdate() {
  await mountDialog(SolutionWizard, { folder: Folder, team: 'job-tracker' }, { pinia: false });
  await settle();
  button('Next').click();
  await settle();
  expect(bodyFind('[data-step="review"]')).not.toBeNull();
}

const change = (item: string, label: string) => bodyFind(`${item} [data-change="${label}"]`);
const was = (item: string, label: string) => change(item, label)?.querySelector('[data-was]')?.textContent?.replace(/^Was/, '').trim();
const now = (item: string, label: string) => change(item, label)?.querySelector('[data-now]')?.textContent?.replace(/^Now/, '').trim();

describe('Solution wizard - Review of an update, old beside new', () => {
  it("a changed trigger's cap reads 200,000 beside 250,000, and its instruction's old words beside the new", async () => {
    await reviewTheUpdate();
    const scan = '[data-trigger="Scan for postings"]';

    expect(was(scan, 'Daily cap')).toBe('200,000 tokens a day');
    expect(now(scan, 'Daily cap')).toBe('250,000 tokens a day');
    expect(was(scan, 'Instruction')).toBe('scan');
    expect(now(scan, 'Instruction')).toBe('Scan the boards, then post each new job to the tracker.');

    // What does not change is not listed.
    expect(change(scan, 'When')).toBeNull();
    expect(change(scan, 'Manager')).toBeNull();
  });

  it("a changed agent member's own instructions read old beside new", async () => {
    await reviewTheUpdate();
    const coordinator = '[data-member="Coordinator"]';

    expect(was(coordinator, 'Instructions')).toBe(OldCoordinator);
    expect(now(coordinator, 'Instructions')).toBe(hostPlan().members[0]!.instructions);
  });

  it('an added trigger has nothing to compare, so lists no changes', async () => {
    await reviewTheUpdate();

    expect(bodyFind('[data-trigger="Apply pressed"] [data-changes]')).toBeNull();
  });
});
