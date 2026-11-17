import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

// A member's avatar animates by its OWN state only. Its state rules are anchored on the avatar itself
// (.member-avatar.is-running), never on any ancestor with that class: a running team's tile in the Teams list is
// .team-card.is-running, and an unanchored ".is-running .ring" turned every avatar on it, idle members too.
const css = readFileSync(path.resolve(__dirname, '../../src/renderer/src/faces/MemberAvatar.vue'), 'utf8')
  .split('<style scoped>')[1]!;

describe("the avatar's state rules", () => {
  it('every .is-<state> selector is on .member-avatar itself', () => {
    const selectors = [...css.matchAll(/^(\.[^{]*)\{/gm)].flatMap((m) => m[1]!.split(',').map((s) => s.trim()));
    const withState = selectors.filter((s) => /\.is-[a-z]+/.test(s));
    expect(withState.length).toBeGreaterThanOrEqual(6);
    for (const s of withState) expect(s, s).toMatch(/^\.member-avatar\.is-[a-z]+ /);
  });
});
