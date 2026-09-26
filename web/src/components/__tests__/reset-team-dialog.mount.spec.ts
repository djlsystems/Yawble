// @vitest-environment happy-dom
//
// `wouldDoSomething(choices)` IS "is the Reset button enabled", and `choicesFor(members)` IS which
// checkboxes exist. Both are already tested in `lib/__tests__/reset.spec.ts`; what is tested here
// is that the dialog asks them rather than deciding for itself.
//
// NOTHING IN THIS SPEC CLICKS RESET. `resetTeam` is called only from the click handler, so the
// destructive path is never entered and no API mock is needed. If a later change makes the dialog
// call the API on OPEN, this spec starts hitting the network and must gain a mock rather than
// lose an assertion.
//
// THE DEFAULT IS ENABLED, NOT DISABLED, and getting that backwards is the easy mistake here:
// `choicesFor` ticks EVERY member and sets `deleteMemory: true`, so a team with members opens
// ready to reset. The disabled case is a team with NO members, and unticking.
import { afterEach, describe, expect, it } from 'vitest';
import ResetTeamDialog from '../ResetTeamDialog.vue';
import { choicesFor, wouldDoSomething } from '../../lib/reset';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type Team,
  type TeamId,
} from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

const container = (id: string): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  name: id,
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  id: asMemberId(id),
});

const teamWith = (...members: string[]): Team => ({
  id: 'alpha' as TeamId,
  name: 'Alpha',
  concierge: 'claude-interactive',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  containers: members.map(container),
});

function resetButton(): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === 'Reset');

  if (!found) throw new Error('no Reset button in the rendered dialog');

  return found as HTMLButtonElement;
}

describe('ResetTeamDialog, mounted', () => {
  it('renders one checkbox per member the lib function offers', async () => {
    const team = teamWith('Manager', 'Digger', 'Writer');

    const wrapper = await mountDialog(ResetTeamDialog, { team });

    // Counted from choicesFor rather than from the literal 3, so a change to what a fresh choice
    // set contains cannot leave this spec silently checking the wrong number. The three flags
    // (deleteMemory, clearWorkspaces, clearSharedDocuments) get checkboxes of their own, so the
    // total is members PLUS those - which is why this is a lower bound plus a named member rather
    // than an equality against a number that would encode the flag count as a magic literal.
    const members = Object.keys(choicesFor(team.containers.map((c) => c.id)).members);

    expect(members).toEqual(['Manager', 'Digger', 'Writer']);
    expect(document.body.querySelectorAll('.q-checkbox').length).toBeGreaterThanOrEqual(members.length);

    for (const member of members) expect(bodyText()).toContain(member);

    wrapper.unmount();
  });

  it('opens ENABLED for a team with members, because every member starts ticked', async () => {
    const team = teamWith('Manager');

    expect(wouldDoSomething(choicesFor(['Manager']))).toBe(true);

    const wrapper = await mountDialog(ResetTeamDialog, { team });

    expect(resetButton().hasAttribute('disabled')).toBe(false);

    wrapper.unmount();
  });

  it('disables Reset for a team with NO members, because nothing would happen', async () => {
    expect(wouldDoSomething(choicesFor([]))).toBe(false);

    const wrapper = await mountDialog(ResetTeamDialog, { team: teamWith() });

    expect(resetButton().hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('disables Reset once the only member is unticked', async () => {
    const wrapper = await mountDialog(ResetTeamDialog, { team: teamWith('Manager') });

    const memberBox = [...document.body.querySelectorAll('.q-checkbox')]
      .find((box) => box.textContent?.includes('Manager'));

    expect(memberBox, 'no checkbox for the member').toBeDefined();

    (memberBox as HTMLElement).click();
    await wrapper.vm.$nextTick();

    expect(resetButton().hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });
});
