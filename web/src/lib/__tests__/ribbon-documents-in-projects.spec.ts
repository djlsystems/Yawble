import { describe, expect, it } from 'vitest'
import { DocumentsAction, Ribbon, needsActiveWorkTeam } from '../ribbon'

/**
 * The ribbon shows Documents under PROJECTS, not under ACTIVE TEAM, and the
 * Projects group carries no redundant title.
 *
 * WHY THIS IS NOT COSMETIC. A document outlives the team that wrote it, so a control reachable
 * only while that team exists would leave a surviving folder that nothing on screen can open.
 *
 * ASSERTED ON THE `Ribbon` CONSTANT, which is where this fact lives. There is exactly ONE
 * definition, and both `RibbonBar.vue` and `RibbonMobileMenu.vue` render `Ribbon.tabs`
 * generically, so a `documents` item in the Projects tab IS Documents appearing under Projects in
 * both.
 */
describe('Documents belongs to Projects, not to the active team', () => {
  const tab = (id: string) => Ribbon.tabs.find((t) => t.id === id)

  /** By ACTION rather than by kind - see the note on the one-item case below. */
  const actions = (id: string) => (tab(id)?.items ?? []).map((item) => item.action)

  it('puts the Documents item in the Projects group', () => {
    expect(tab('projects')).toBeDefined()
    expect(actions('projects')).toContain(DocumentsAction)
  })

  it('takes it out of the Active Team group', () => {
    expect(tab('active')).toBeDefined()
    expect(actions('active')).not.toContain(DocumentsAction)
  })

  /**
   * ONE of them. An item in one group with a copy in another is the failure this catches: both groups
   * would render a Documents control, and the two would disagree about which folder they meant.
   *
   * FOUND BY ACTION, NOT BY KIND. `DocumentsAction` is the identity that survives a change of
   * rendering, which is the whole reason it is a constant; a filter on `kind` would silently match
   * nothing if the item's kind changed.
   */
  it('has exactly one Documents item on the whole ribbon', () => {
    const all = Ribbon.tabs
      .flatMap((t) => t.items)
      .filter((item) => item.action === DocumentsAction)

    expect(all).toHaveLength(1)
  })

  /**
   * NO `title: 'Backlog'`. The group already has a button labelled Backlog, so the heading would
   * repeat it; with a second item in the group it is simply wrong - the heading would announce
   * that Documents is part of the backlog.
   *
   * `titleFrom` is asserted too, because taking the title from live state instead would put a
   * heading back over the same group by another route.
   */
  it('leaves the Projects group with no redundant title', () => {
    expect(tab('projects')?.title).toBeUndefined()
    expect(tab('projects')?.titleFrom).toBeUndefined()
  })

  /**
   * THE GATING, WHICH IS AN EXPLICIT CHOICE RATHER THAN A CONSEQUENCE. Documents from Projects
   * follows `backlog`: no prefix, so no active team. The prefix IS the rule, so this is
   * pinned by pinning the ABSENCE of a prefix.
   *
   * It is what would notice a `team-documents` action being attached, which would hide the control
   * behind the very team the documents do not belong to.
   */
  it('gates Documents the way it gates the backlog, which is not at all', () => {
    const documents = Ribbon.tabs
      .flatMap((t) => t.items)
      .find((item) => item.action === DocumentsAction)

    expect(needsActiveWorkTeam(documents?.action, undefined)).toBe(false)
  })
})
