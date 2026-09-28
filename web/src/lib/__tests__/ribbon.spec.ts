import { describe, expect, it } from 'vitest'
import * as ribbonModule from '../ribbon'
import { ConnectionsAction, DocumentsAction, PluginsAction, RepositoriesAction, Ribbon, TenantSettingsAction, needsActiveWorkTeam } from '../ribbon'

describe('the ribbon', () => {
  /**
   * ONE DEFINITION, IN CODE. A fetched ribbon would need a seed, a fallback and a parser, and a new
   * action would have to be added to every copy or it would half-exist. Per-instance customisation
   * is not worth a test suite policing copies of one fact.
   */
  it('is a constant, not a fetch', () => {
    expect(Ribbon.tabs.length).toBeGreaterThan(0)
  })

  it('has no team-schedules action', () => {
    const actions = Ribbon.tabs.flatMap((tab) => tab.items.map((item) => item.action))
    expect(actions).not.toContain('team-schedules')
  })

  it('still gates team actions by prefix', () => {
    expect(needsActiveWorkTeam('team-kanban', undefined)).toBe(true)
    expect(needsActiveWorkTeam('team-kanban', 'alpha')).toBe(false)
  })

  /**
   * THERE IS NO TIER PREDICATE. Every person is an administrator, so the `admin-` prefix names a
   * group and gates nothing. Pinned as an absence (no `needsTenantAdmin`) so nobody introduces a
   * disabled-for-tier state that no server route would honour.
   */
  it('has no tier predicate - an admin action needs nothing but a session', () => {
    expect(needsActiveWorkTeam('admin-agents', undefined)).toBe(false)
    expect('needsTenantAdmin' in ribbonModule).toBe(false)
  })

  /**
   * THE UNPREFIXED CASE, PINNED SO IT READS AS A DECISION RATHER THAN AN OVERSIGHT.
   *
   * The predicate is opt-in by prefix, so an UNPREFIXED action is enabled for every signed-in
   * person by construction. `backlog` is the first of them and that is exactly right for it: it
   * needs no active team, because its items are not addressed through one.
   *
   * Without this case the next reader sees a button with no prefix among a strip of prefixed ones
   * and "fixes" it - which would hide the backlog from everybody without a team.
   */
  it('leaves an unprefixed action enabled for everybody, which is the backlog', () => {
    expect(needsActiveWorkTeam('backlog', undefined)).toBe(false)
  })

  /** And it really is unprefixed - naming the TAB would break the rule the other two rest on. */
  it('names the backlog action for what it needs rather than for its tab', () => {
    const actions = Ribbon.tabs.flatMap((tab) => tab.items.map((item) => item.action))

    expect(actions).toContain('backlog')
    expect(actions).not.toContain('projects-backlog')
  })

  /**
   * DOCUMENTS FOLLOWS `backlog`, AND THIS IS THE CHOICE RATHER THAN THE CONSEQUENCE.
   *
   * A hand-written `!activeWorkTeamId` gate in the Vue files would live outside the prefix rule and
   * say nothing about why. The documents root is the TENANT's, holding a folder per team, and a
   * folder outlives the team that wrote it: requiring an active team would make every other team's folder unreachable,
   * including exactly the ones whose team is gone.
   *
   * Pinned at the same strength as `backlog`, because the tempting "fix" is the same one - a
   * reader seeing an unprefixed action among prefixed ones and renaming it `team-documents`, which
   * would both be a lie about where documents live and hide them from everybody without a team.
   */
  it('gates documents like the backlog - no active team needed', () => {
    expect(needsActiveWorkTeam(DocumentsAction, undefined)).toBe(false)
  })

  it('names the documents action for what it needs rather than for its tab or its team', () => {
    const actions = Ribbon.tabs.flatMap((tab) => tab.items.map((item) => item.action))

    expect(actions).toContain(DocumentsAction)
    expect(actions).not.toContain('team-documents')
    expect(actions).not.toContain('projects-documents')
  })

  /**
   * A DOCUMENT OUTLIVES THE TEAM THAT WROTE IT, so the control cannot sit under a heading naming
   * the active team.
   *
   * ASSERTED ON THE ACTION, NOT THE KIND. The item is an ordinary button, so a kind test would
   * assert `false` on BOTH tabs and pass the half that matters by accident. WHERE it lives is the
   * claim; how it renders is not.
   */
  it('puts documents in Projects beside the backlog, not under the active team', () => {
    const active = Ribbon.tabs.find((tab) => tab.id === 'active')
    const projects = Ribbon.tabs.find((tab) => tab.id === 'projects')

    expect(active?.items.some((item) => item.action === DocumentsAction)).toBe(false)
    expect(projects?.items.some((item) => item.action === DocumentsAction)).toBe(true)
    expect(projects?.items.some((item) => item.action === 'backlog')).toBe(true)
  })

  /**
   * The group already has a button labelled Backlog, so the heading repeated the control; with a
   * second item beside it the heading names one of the two as though it named the group. The block
   * label underneath - `Projects` - is what names it.
   */
  it('carries no heading over Projects, which its own button already labels', () => {
    const projects = Ribbon.tabs.find((tab) => tab.id === 'projects')

    expect(projects?.title).toBeUndefined()
    expect(projects?.titleFrom).toBeUndefined()
  })

  /**
   * Admin › Diagnostics, beside Log in the ADMIN group.
   *
   * Open to every signed-in person, like the rest of the group; `GET /api/diagnostics` refuses a
   * machine principal on the server regardless.
   */
  it('puts Diagnostics in the Admin group, beside Log', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    // The Concierge's settings are a tab of Settings; there is no separate Concierge button.
    expect(actions).not.toContain('admin-concierge')
    expect(actions).toContain('admin-diagnostics')
    expect(actions.indexOf('admin-diagnostics')).toBe(actions.indexOf('admin-log') + 1)

    // And it needs no active team - the store is per-INSTANCE and has no team column at all, so a
    // `team-` prefix would be the wrong requirement rather than a stricter one.
    expect(needsActiveWorkTeam('admin-diagnostics', undefined)).toBe(false)
  })

  /** Admin › Repositories, the instance's local repositories: after Diagnostics, before Plugins. */
  it('puts Repositories in the Admin group, before Plugins, needing no active team', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    expect(actions).toContain(RepositoriesAction)
    expect(actions.indexOf(RepositoriesAction)).toBe(actions.indexOf('admin-diagnostics') + 1)
    expect(actions.indexOf(RepositoriesAction)).toBe(actions.indexOf(PluginsAction) - 1)
    expect(needsActiveWorkTeam(RepositoriesAction, undefined)).toBe(false)
  })

  /** Admin › Connections: directly after Plugins, Settings after it, needing no active team. */
  it('puts Connections directly after Plugins and before Settings, needing no active team', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    expect(actions.indexOf(ConnectionsAction)).toBe(actions.indexOf(PluginsAction) + 1)
    expect(actions.indexOf(TenantSettingsAction)).toBe(actions.indexOf(ConnectionsAction) + 1)
    expect(needsActiveWorkTeam(ConnectionsAction, undefined)).toBe(false)
  })

  /** Projects sits BEFORE Admin. */
  it('puts Projects between the active team and Admin', () => {
    const ids = Ribbon.tabs.map((tab) => tab.id)

    expect(ids.indexOf('projects')).toBeGreaterThan(ids.indexOf('active'))
    expect(ids.indexOf('projects')).toBeLessThan(ids.indexOf('admin'))
  })
})
