// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, STEP 2: REVIEW. Agent-written instructions become prompts, so the review
// shows every one WHOLE: each member with its kind, role, preset or plugin and version and its full
// instructions; each trigger with what fires it, whom it wakes, the wake setting in words, its daily
// cap (or "no cap") and its full instruction; skills with an expandable body; sites with their file
// count; the tools folder installed as `solution/`; the plugins. An update marks what is added or
// changed, lists what goes, and says the versions from -> to.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import {
  Folder,
  LongInstruction,
  WriterInstructions,
  fakeHost,
  hostPlan,
  installedRow,
  reply,
  updatePreview,
  wizardRoutes,
  type Call,
  type Route,
} from '../../test/solutionFixtures';

let calls: Call[] = [];

function serve(extra: Route[] = [], options: Parameters<typeof wizardRoutes>[0] = {}) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ...wizardRoutes(options)], calls)));
}

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function review() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  button('Next').click();
  await settle();
  expect(bodyFind('[data-step="review"]')).not.toBeNull();
  return wrapper;
}

const trigger = (name: string) => bodyFind(`[data-trigger="${name}"]`)!;

describe('Solution wizard - Review', () => {
  it('shows every trigger with its full instruction text, never shortened, and its daily cap or "no cap"', async () => {
    await review();
    const plan = hostPlan();

    for (const expected of plan.triggers) {
      const row = trigger(expected.name);
      expect(row, expected.name).not.toBeNull();
      expect(row.querySelector('[data-instruction]')!.textContent).toBe(expected.instruction);
      expect(row.querySelector('[data-target]')!.textContent).toBe(expected.member);
    }

    expect(trigger('Apply pressed').querySelector('[data-instruction]')!.textContent).toBe(LongInstruction);

    expect(trigger('Scan for postings').querySelector('[data-cap]')!.textContent).toBe('no cap');
    expect(trigger('Apply pressed').querySelector('[data-cap]')!.textContent).toBe('400,000 tokens a day');
    expect(trigger('Resume changed').querySelector('[data-cap]')!.textContent).toBe('300,000 tokens a day');
    expect(trigger('Morning summary').querySelector('[data-cap]')!.textContent).toBe('100,000 tokens a day');
  });

  it('says what fires each trigger and how the Manager is woken, in words', async () => {
    await review();

    expect(trigger('Scan for postings').querySelector('[data-source]')!.textContent).toBe('every hour');
    expect(trigger('Apply pressed').querySelector('[data-source]')!.textContent).toBe('on site.action where siteAction eq tracker/apply');
    expect(trigger('Resume changed').querySelector('[data-source]')!.textContent).toBe('a file in Resume/ matching *');
    expect(trigger('Morning summary').querySelector('[data-source]')!.textContent).toBe('At 08:00, Monday to Friday (Europe/London)');

    expect(trigger('Scan for postings').querySelector('[data-wake]')!.textContent).toBe('Never wakes the Manager');
    expect(trigger('Apply pressed').querySelector('[data-wake]')!.textContent).toBe('Wakes the Manager only if the run hands back or fails');
    expect(trigger('Resume changed').querySelector('[data-wake]')!.textContent).toBe('Wakes the Manager whenever the run ends');
  });

  it('shows every member with its kind, role, preset or plugin and version, and its full instructions', async () => {
    await review();

    const coordinator = bodyFind('[data-member="Coordinator"]')!;
    expect(coordinator.textContent).toContain('Manager, agent, preset Manager');
    expect(coordinator.querySelector('[data-instructions]')!.textContent).toBe(hostPlan().members[0]!.instructions);

    const scout = bodyFind('[data-member="Scout"]')!;
    expect(scout.textContent).toContain('member, plugin job-board 0.2.0');

    const writer = bodyFind('[data-member="Writer"]')!;
    expect(writer.textContent).toContain('member, agent, the team chooses the preset');
    expect(writer.querySelector('[data-instructions]')!.textContent).toBe(WriterInstructions);

    expect(bodyFind('[data-team-instructions]')!.textContent).toBe('This team runs a job search for one person.');
  });

  it('lists skills (body on request), sites with their file count, the tools as solution/, and the plugins', async () => {
    await review();

    const skill = bodyFind('[data-skill="job-search-playbook"]')!;
    expect(skill.textContent).toContain('for manager, member');
    expect(skill.textContent).toContain('How this team runs a job search.');
    expect(skill.querySelector('[data-skill-body]')).toBeNull();
    button('Show the skill').click();
    await settle();
    expect(bodyFind('[data-skill="job-search-playbook"] [data-skill-body]')!.textContent).toBe(hostPlan().skills[0]!.body);

    expect(bodyFind('[data-site="tracker"]')!.textContent).toContain('3 files');
    expect(bodyFind('[data-tools]')!.textContent).toContain('solution/');
    expect(bodyFind('[data-tools]')!.textContent).toContain('make-cover-letter.py');
    expect(bodyFind('[data-plugin="job-board"]')!.textContent).toContain('job-board 0.2.0');
  });

  it('an update marks added and changed items, lists the removed ones, and says from -> to', async () => {
    serve(
      [
        (call) =>
          call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
            ? reply(200, updatePreview('job-tracker'))
            : undefined,
      ],
      { installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] },
    );
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    (bodyFind('[data-update-team="job-tracker"] .q-radio') as HTMLElement).click();
    await settle();
    button('Next').click();
    await settle();

    expect(bodyFind('[data-version-line]')!.textContent).toContain('Job Tracker: 1.0.0 -> 1.1.0');

    const markOf = (selector: string) => bodyFind(`${selector} [data-mark]`)?.textContent?.trim() ?? null;
    expect(markOf('[data-member="Writer"]')).toBe('added');
    expect(markOf('[data-member="Coordinator"]')).toBe('changed');
    expect(markOf('[data-member="Scout"]')).toBeNull();
    expect(markOf('[data-trigger="Apply pressed"]')).toBe('added');
    expect(markOf('[data-trigger="Scan for postings"]')).toBe('changed');
    expect(markOf('[data-skill="job-search-playbook"]')).toBe('changed');
    expect(markOf('[data-plugin="job-board"]')).toBe('changed');
    expect(markOf('[data-tools]')).toBe('added');

    const removed = [...document.body.querySelectorAll('[data-removed-item]')].map((item) => item.textContent);
    expect(removed).toEqual(['Member: Clerk', 'Trigger: Evening summary']);

    // The full new plan is still shown whole.
    expect(trigger('Apply pressed').querySelector('[data-instruction]')!.textContent).toBe(LongInstruction);
    expect(bodyText()).not.toContain('A new team:');
  });
});
