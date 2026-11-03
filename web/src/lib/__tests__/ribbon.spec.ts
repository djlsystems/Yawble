import { describe, expect, it } from 'vitest'
import * as ribbonModule from '../ribbon'
import {
  ConnectionsAction,
  DocumentsAction,
  McpServersAction,
  OutcomesAction,
  PluginsAction,
  RepositoriesAction,
  Ribbon,
  SitesAction,
  TeamBacklogAction,
  TeamBar,
  TeamBarTab,
  TeamBoardAction,
  TeamDocumentsAction,
  TeamSitesAction,
  TeamMenu,
  TenantSettingsAction,
  commandRoom,
  fitCount,
  needsActiveWorkTeam,
} from '../ribbon'

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
    expect(needsActiveWorkTeam(TeamBoardAction, undefined)).toBe(true)
    expect(needsActiveWorkTeam(TeamBoardAction, 'alpha')).toBe(false)
    expect(needsActiveWorkTeam(undefined, undefined)).toBe(false)
    expect(needsActiveWorkTeam('teams', undefined)).toBe(false)
  })

  /**
   * THE RIBBON IS THE INSTANCE'S; A TEAM'S COMMANDS ARE ON ITS BOARD. No team chooser and no team
   * group on the ribbon, and no Kanban: the console strip's Teams button chooses the team, and its
   * Kanban tab is the one door to the Kanban.
   */
  it('carries no team chooser, no team group and no Kanban', () => {
    const everything = Ribbon.tabs.flatMap((tab) => tab.items)

    expect(everything.some((item) => item.kind === 'team-switcher')).toBe(false)
    expect(everything.some((item) => item.action?.startsWith('team-'))).toBe(false)
    expect(everything.map((item) => item.label)).not.toContain('Kanban')
    expect(everything.map((item) => item.label)).not.toContain('Members')
  })

  /**
   * THE TEAM BAR, under the team's name on its board: Git, Sites, Documents, Settings, Reset and
   * Backlog, every one on that team (`team-`), and no Board - the bar is on the board. The palette
   * offers the same commands as a tab of its own.
   */
  it('lists Git, Sites, Documents, Settings, Reset and Backlog on the team bar, each on the team', () => {
    expect(TeamBar.map((item) => item.label)).toEqual(['Git', 'Sites', 'Documents', 'Settings', 'Reset', 'Backlog'])
    expect(TeamBar.every((item) => item.action.startsWith('team-'))).toBe(true)
    expect(TeamBar.map((item) => item.action)).not.toContain(TeamBoardAction)
    expect(TeamBar.find((item) => item.label === 'Documents')!.action).toBe(TeamDocumentsAction)
    expect(TeamBar.find((item) => item.label === 'Backlog')!.action).toBe(TeamBacklogAction)
    expect(TeamBarTab.items.map((item) => item.action)).toEqual(TeamBar.map((item) => item.action))
  })

  /** Every other group is as it was, in the same order. */
  it('keeps the tab order', () => {
    expect(Ribbon.tabs.map((tab) => tab.id)).toEqual(['new', 'solutions', 'projects', 'admin'])
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

  /**
   * ONE Documents item on the whole ribbon. An item in one group with a copy in another would
   * render two Documents controls that disagree about which folder they meant. Found by ACTION,
   * not kind: a filter on `kind` would silently match nothing if the item's kind changed.
   */
  it('has exactly one Documents item on the whole ribbon', () => {
    const all = Ribbon.tabs
      .flatMap((tab) => tab.items)
      .filter((item) => item.action === DocumentsAction)

    expect(all).toHaveLength(1)
  })

  it('names the documents action for what it needs rather than for its tab or its team', () => {
    const projects = Ribbon.tabs.find((tab) => tab.id === 'projects')!.items.map((item) => item.action)

    expect(projects).toContain(DocumentsAction)
    expect(DocumentsAction).not.toMatch(/^team-/)
    expect(Ribbon.tabs.flatMap((tab) => tab.items.map((item) => item.action))).not.toContain('projects-documents')
  })

  /**
   * THE TEAMS LIST'S ROW MENU: the Active Team commands with Documents and Backlog, in that order,
   * each a command the layout already handles.
   */
  it('lists Board, Git, Sites, Documents, Settings, Reset and Backlog in the Teams row menu', () => {
    expect(TeamMenu.map((item) => item.label)).toEqual(['Board', 'Git', 'Sites', 'Documents', 'Settings', 'Reset', 'Backlog'])
    expect(TeamMenu.find((item) => item.label === 'Documents')!.action).toBe(DocumentsAction)
    expect(TeamMenu.find((item) => item.label === 'Backlog')!.action).toBe('backlog')
  })

  /** A DOCUMENT OUTLIVES THE TEAM THAT WROTE IT: the ribbon's Documents is the root, beside the backlog. */
  it('puts documents in Projects beside the backlog', () => {
    const projects = Ribbon.tabs.find((tab) => tab.id === 'projects')

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
    expect(actions.indexOf(RepositoriesAction)).toBeLessThan(actions.indexOf(PluginsAction))
    expect(needsActiveWorkTeam(RepositoriesAction, undefined)).toBe(false)
  })

  /** Admin › Outcomes, the Manage Outcomes dialog: beside Teams, needing no active team. */
  it('puts Outcomes first in the Admin group, needing no active team, and no Teams button there', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    expect(actions.indexOf(OutcomesAction)).toBe(0)
    expect(actions).not.toContain('admin-teams')
    expect(admin?.items.find((item) => item.action === OutcomesAction)?.label).toBe('Outcomes')
    expect(needsActiveWorkTeam(OutcomesAction, undefined)).toBe(false)
  })

  /** Admin › Connections: directly after Plugins, Settings after it, needing no active team. */
  it('puts Connections directly after Plugins and before Settings, needing no active team', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    expect(actions.indexOf(ConnectionsAction)).toBe(actions.indexOf(PluginsAction) + 1)
    expect(actions.indexOf(TenantSettingsAction)).toBe(actions.indexOf(ConnectionsAction) + 1)
    expect(needsActiveWorkTeam(ConnectionsAction, undefined)).toBe(false)
  })

  /**
   * Admin › Sites, every team's sites: between Repositories and Plugins, so before Settings, needing
   * no active team. The team bar's Sites is the same screen for one team, and needs one.
   */
  it('puts Sites in the Admin group before Settings, and a team-scoped Sites on the team bar', () => {
    const admin = Ribbon.tabs.find((tab) => tab.id === 'admin')
    const actions = admin?.items.map((item) => item.action) ?? []

    expect(actions.indexOf(SitesAction)).toBe(actions.indexOf(RepositoriesAction) + 1)
    // MCP servers sits between Sites and Plugins.
    expect(actions.indexOf(SitesAction)).toBe(actions.indexOf(McpServersAction) - 1)
    expect(actions.indexOf(McpServersAction)).toBe(actions.indexOf(PluginsAction) - 1)
    expect(actions.indexOf(SitesAction)).toBeLessThan(actions.indexOf(TenantSettingsAction))
    expect(needsActiveWorkTeam(SitesAction, undefined)).toBe(false)

    expect(TeamBar.map((item) => item.action)).toContain(TeamSitesAction)
    expect(needsActiveWorkTeam(TeamSitesAction, undefined)).toBe(true)
  })

  /** Projects sits BEFORE Admin. */
  it('puts Projects before Admin', () => {
    const ids = Ribbon.tabs.map((tab) => tab.id)

    expect(ids.indexOf('projects')).toBeLessThan(ids.indexOf('admin'))
  })

  /** The one header row: the logo and the right cluster are reserved before the commands are fitted. */
  it('fits the commands into what the logo and the cluster leave', () => {
    const edges = [100, 200, 300, 400, 500]

    expect(fitCount(edges, commandRoom(500, 0, 0), 40)).toBe(5)
    expect(fitCount(edges, commandRoom(700, 100, 100), 40)).toBe(5)
    expect(fitCount(edges, commandRoom(700, 100, 250), 40)).toBe(3)
    expect(commandRoom(100, 80, 200)).toBe(0)
  })
})
