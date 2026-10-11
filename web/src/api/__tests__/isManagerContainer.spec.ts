import { describe, expect, it } from 'vitest'
import { asMemberId } from '../types'
import { isManagerContainer } from '../types'

/**
 * The predicate `MemberSettingsDialog` uses to decide whether to show a prompt-override control at
 * all — a manager's cannot be overridden, so it must hide the control rather than merely disable
 * one that looks editable but is not. This is the "web test for the dialog's behaviour at the
 * level you can actually test here": mounted-component tests exist in this project now, and the
 * matching RULE still belongs as a pure function tested directly rather than left inline and
 * blurrier to pin. A source-grep stood in for that once already and let a Critical through.
 */
describe('isManagerContainer', () => {
  it('matches a member whose id is the manager name', () => {
    expect(isManagerContainer(asMemberId('Manager'), asMemberId('Manager'))).toBe(true)
  })

  /**
   * Case-insensitively, matching `ContainerId` equality and `TeamRegistry.IsManager` on the server -
   * a divergence here would mean the dialog and the route disagree about which container is the
   * manager.
   */
  it('matches case-insensitively', () => {
    expect(isManagerContainer(asMemberId('manager'), asMemberId('Manager'))).toBe(true)
    expect(isManagerContainer(asMemberId('MANAGER'), asMemberId('Manager'))).toBe(true)
  })

  it('does not match an ordinary member', () => {
    expect(isManagerContainer(asMemberId('Digger'), asMemberId('Manager'))).toBe(false)
  })

  // Signature note, not a behaviour to assert: this takes an ID, never a `ContainerSnapshot`, so
  // there is no `.name` (the editable label) for a caller to pass by mistake. That is why
  // `MemberSettingsDialog` calls this with `snapshot.id` - a label-keyed check would stop applying
  // the moment someone relabelled the very container it exists to protect.
})
