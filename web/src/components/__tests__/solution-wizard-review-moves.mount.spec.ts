// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD'S REVIEW OF AN UPDATE THAT MOVES A MEMBER ONTO ANOTHER PLUGIN. The package's
// 1.1.0 runs Scout on plugin job-board-plugin where the team has it on job-board: Review prints the
// Host's "Scout moves from plugin job-board to job-board-plugin" line, and a warning for each trigger a
// person made by hand on job-board's events, which the update does not rewrite and so stops firing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import type { SolutionPreview } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, installedRow, reply, updatePreview, wizardRoutes } from '../../test/solutionFixtures';

const MoveLine = 'Scout moves from plugin job-board to job-board-plugin';
const StrandedLine =
  'Trigger Posting seen, made by hand on plugin.job-board.posting.found, will no longer fire once the team moves off plugin job-board; '
  + 'the update does not change it - point it at plugin.job-board-plugin.posting.found to keep it';

function movingPreview(withMoves: boolean): SolutionPreview {
  const preview = updatePreview('job-tracker', hostPlan());
  if (!preview.ok || preview.mode !== 'update' || !withMoves) return preview;
  return {
    ...preview,
    diff: {
      ...preview.diff,
      moves: [{ member: 'Scout', from: 'job-board', to: 'job-board-plugin', line: MoveLine }],
      noLongerFire: [
        {
          trigger: 'Posting seen',
          id: 'trg_posting_seen',
          member: 'scout',
          eventType: 'plugin.job-board.posting.found',
          from: 'job-board',
          to: 'job-board-plugin',
          line: StrandedLine,
        },
      ],
    },
  };
}

function serve(withMoves: boolean) {
  const plan = hostPlan();
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost([
        (call) =>
          call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
            ? reply(200, movingPreview(withMoves))
            : undefined,
        ...wizardRoutes({ plan, installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] }),
      ], []),
    ),
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
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

describe('Solution wizard - Review of an update that moves a member to another plugin', () => {
  it('prints the move line and warns that a hand-made trigger on the old plugin will no longer fire', async () => {
    serve(true);
    await reviewTheUpdate();

    expect(bodyFind('[data-move="Scout"]')?.textContent?.trim()).toBe(MoveLine);
    expect(bodyFind('[data-no-longer-fires="Posting seen"] [data-line]')?.textContent?.trim()).toBe(StrandedLine);
  });

  it('shows no move section when the update moves nobody', async () => {
    serve(false);
    await reviewTheUpdate();

    expect(bodyFind('[data-moves]')).toBeNull();
  });
});
