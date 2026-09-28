/**
 * THE RIBBON, IN CODE, AND THERE IS ONLY ONE OF IT.
 *
 * There is no seed file, no fallback copy and no per-instance `ribbon.json`: a new action is added
 * here and nowhere else. PER-INSTANCE CUSTOMISATION IS DELIBERATELY NOT SUPPORTED - it would cost a
 * multi-place rule, a preserved build artifact, a parser and a fallback, and with no file there is
 * nothing to typo that could leave someone unable to create a team.
 *
 * A TAB here is a labelled group on the strip, not a panel you switch to: every tab is on screen
 * at once, side by side.
 */
/**
 * THERE IS NO `documents` DROPDOWN KIND. Documents is an ordinary button that opens the manager:
 * a menu listing every team's folder would be a list nobody chooses from - the dialog it opens has
 * the same picker at the top of it, better, with the retired folders explained - and would widen
 * the Projects block by a caret's worth for no affordance.
 */
export type RibbonItemKind = 'button' | 'separator' | 'team-switcher';

export interface RibbonItem {
  kind: RibbonItemKind;
  /**
   * Names the handler, AND names the gate. Not buttons only: the `documents` item carries one too,
   * because the two disable predicates below read this field and an item without one cannot be
   * gated by the prefix rule at all, and would need hand-written disabling in the Vue files
   * instead.
   */
  action?: string;
  label?: string;
  icon?: string;
  tooltip?: string;
  size?: 'large' | 'small';
}

export interface RibbonTab {
  id: string;
  /** The small name UNDER the block. Always the fixed word - it names the block, not its subject. */
  label: string;
  /** An optional heading INSIDE the block, above its items. */
  title?: string;
  /** Takes that heading from live state instead. Only `active-team` is understood. */
  titleFrom?: 'active-team';
  items: RibbonItem[];
}

export interface RibbonSpec {
  tabs: RibbonTab[];
}

/**
 * The documents command's name, written once. `AgentsAction` and `SkillsAction` are the precedent:
 * an action string that more than one file has to agree on is a constant, not a literal repeated
 * in a ribbon, a handler and a test.
 *
 * UNPREFIXED, DELIBERATELY - see the prefix note under the `Ribbon` constant below.
 */
export const DocumentsAction = 'documents-manage';

/** Admin > Plugins, the installed plugins and installing one from a folder inside the instance. */
export const PluginsAction = 'admin-plugins';

/**
 * Admin > Connections, the OAuth accounts the Host holds for plugins and each provider's client.
 * Directly after Plugins: what a plugin acts on, beside the plugins themselves.
 */
export const ConnectionsAction = 'admin-connections';

/** Admin > Repositories, the instance's local repositories: listed, and deleted after asking. */
export const RepositoriesAction = 'admin-repositories';

/** Admin > Sites, every team's published sites: opened, rolled back, unpublished, deleted. */
export const SitesAction = 'admin-sites';

/** Active Team > Sites, the same screen filtered to the active team. `team-`, so it needs one. */
export const TeamSitesAction = 'team-sites';

/** Admin > Settings, the instance-wide Tenant Settings dialog. */
export const TenantSettingsAction = 'admin-settings';

export const Ribbon: RibbonSpec = {
  tabs: [
    {
      id: 'new',
      label: 'New',
      items: [
        { kind: 'button', action: 'admin-new-team', label: 'New Team', icon: 'group_add', size: 'large' },
      ],
    },
    {
      id: 'teams',
      label: 'Teams',
      items: [{ kind: 'team-switcher', label: 'Choose Team', icon: 'groups', size: 'large' }],
    },
    {
      id: 'active',
      label: 'Active Team',
      title: 'No team',
      titleFrom: 'active-team',
      items: [
        { kind: 'button', action: 'team-members', label: 'Members', icon: 'groups', size: 'small' },
        { kind: 'button', action: 'team-kanban', label: 'Kanban', icon: 'view_kanban', size: 'small' },
        { kind: 'button', action: 'team-git', label: 'Git', icon: 'account_tree', size: 'small' },
        { kind: 'button', action: TeamSitesAction, label: 'Sites', icon: 'public', size: 'small' },
        { kind: 'button', action: 'team-settings', label: 'Settings', icon: 'settings', size: 'small' },
        { kind: 'button', action: 'team-reset', label: 'Reset', icon: 'restart_alt', size: 'small' },
      ],
    },
    /**
     * NO `title`, AND THAT IS THE EDIT RATHER THAN AN OMISSION. It read `Backlog` over a group
     * whose only button was already labelled Backlog - the heading repeated the control. With
     * Documents beside it the heading is not merely redundant, it is WRONG: it names one of the
     * two items as though it named the group.
     *
     * The block's own label underneath - `Projects` - is what names it, which is what the small
     * label under every block is for.
     */
    {
      id: 'projects',
      label: 'Projects',
      items: [
        { kind: 'button', action: 'backlog', label: 'Backlog', icon: 'checklist', size: 'large' },
        // NOT UNDER ACTIVE TEAM: documents are a TENANT's, held in
        // one root with a folder per team, and they outlive the team that wrote them. A control
        // under a heading naming the active team says the opposite.
        //
        // IT CARRIES AN ACTION. See `needsActiveWorkTeam` below: the prefix IS the gate, and an
        // item with no action at all cannot be gated by it - so it would need a hard-coded
        // "disabled without an active team" in the Vue files, which is exactly what must not apply.
        { kind: 'button', action: DocumentsAction, label: 'Documents', icon: 'folder_open', size: 'large' },
      ],
    },
    {
      id: 'admin',
      label: 'Admin',
      items: [
        { kind: 'button', action: 'admin-teams', label: 'Teams', icon: 'groups', size: 'large' },
        { kind: 'button', action: 'admin-users', label: 'Users', icon: 'manage_accounts', size: 'large' },
        { kind: 'button', action: 'admin-agents', label: 'Agents', icon: 'smart_toy', size: 'large' },
        { kind: 'button', action: 'admin-skills', label: 'Skills', icon: 'psychology', size: 'large' },
        { kind: 'button', action: 'admin-keys', label: 'Keys', icon: 'key', size: 'large' },
        { kind: 'button', action: 'admin-log', label: 'Log', icon: 'receipt_long', size: 'large' },
        // BESIDE Log AND NOT THE SAME THING. `Log` is what a PERSON did to the
        // instance; this is what the instance did when it went wrong, and no row here names an
        // actor because nobody did any of it.
        //
        // `admin-` NAMES THE GROUP, NOT A GATE. Every person is an administrator, so the prefix
        // disables nothing; it is kept because MainLayout's handler and the
        // `AgentsAction`/`SkillsAction` constants are spelled with it. The server refuses a
        // machine principal on `GET /api/diagnostics` outright.
        { kind: 'button', action: 'admin-diagnostics', label: 'Diagnostics', icon: 'monitor_heart', size: 'large' },
        // The instance's local repositories - git kept on the volume, which a team names as
        // `local:<name>`. Before Sites.
        { kind: 'button', action: RepositoriesAction, label: 'Repositories', icon: 'account_tree', size: 'large' },
        // Every team's published sites. After Repositories, before Plugins.
        { kind: 'button', action: SitesAction, label: 'Sites', icon: 'public', size: 'large' },
        // What is installed on the instance, then the accounts it acts on, then how it is configured.
        { kind: 'button', action: PluginsAction, label: 'Plugins', icon: 'extension', size: 'large' },
        // DIRECTLY AFTER Plugins, and Settings follows: the OAuth accounts plugins act on.
        { kind: 'button', action: ConnectionsAction, label: 'Connections', icon: 'link', size: 'large' },
        // THE GEAR. Every instance-wide setting - the WIP limit among them - in one dialog. The
        // Active Team group's own Settings is that team's; this one names no team.
        {
          kind: 'button',
          action: TenantSettingsAction,
          label: 'Settings',
          icon: 'settings',
          size: 'large',
          tooltip: 'Instance-wide settings: admission, spend, Concierge, sweeps, kanban',
        },
      ],
    },
  ],
};

/**
 * ONE PREDICATE, OPT-IN BY PREFIX. `team-...` needs an active team; nothing else is ever disabled
 * for a signed-in person. There is no tier predicate on `admin-...` - every person is an
 * administrator. The `admin-` prefix is only a name that MainLayout's handler and the
 * `AgentsAction`/`SkillsAction` constants are spelled with.
 *
 * AN UNPREFIXED ACTION IS A DECISION RATHER THAN AN OVERSIGHT. `backlog` needs no active team,
 * because its items are not addressed through one, so `needsActiveWorkTeam('backlog', undefined)`
 * is FALSE, deliberately, and pinned.
 *
 * `documents-manage` IS THE SECOND OF THEM, AND IT WAS CHOSEN RATHER THAN INHERITED. The
 * documents root is the TENANT's, holding a folder per team; the screen names which team's folder
 * it is showing. Requiring an active team to open it would make the folders of every OTHER team
 * unreachable, including the ones whose team is gone - which is the case that matters
 * most. SO IT IS DELIBERATELY NOT `team-documents`. That name would be a lie about where documents
 * live and would impose a coupling to the active team that documents do not have.
 *
 * DO NOT NAME EITHER OF THEM `projects-...`. A prefix that names the TAB rather than a REQUIREMENT
 * breaks the rule `team-` rests on - the prefix IS the rule - and the next reader would reasonably
 * expect a `projects-` predicate to exist.
 */

/**
 * An action named `team-...` needs an active team id.
 *
 * The prefix IS the rule, so a new team-scoped command inherits it by being named for what it
 * needs. It lives in `lib/` rather than in the composable so the rule is testable without a mount.
 */
export function needsActiveWorkTeam(action: string | undefined, activeWorkTeamId: string | undefined): boolean {
  return action?.startsWith('team-') === true && !activeWorkTeamId;
}

/**
 * THE RIBBON IS ONE ROW, AND THIS IS THE ROW.
 *
 * A block per tab with a heading and a caption would cost about 130px of header before the board
 * began. It is a single 44px command bar: every item in tab order, a
 * separator where one tab ends and the next begins, and whatever does not fit in an overflow menu.
 *
 * The drawer reads the SAME list, so the order the strip spills in is the order a phone lists in.
 * A tab still names its group - as the separator's accessible label, and as the heading in the
 * overflow menu and the drawer - it just does not spend a line of pixels doing it on the strip.
 */
export interface RibbonEntry {
  /** Unique across the bar. The tab id and the item's position in it. */
  key: string;
  tab: RibbonTab;
  item: RibbonItem;
  /** The first entry of its tab: the bar draws the group separator in front of it. */
  groupStart: boolean;
}

export function ribbonEntries(spec: RibbonSpec): RibbonEntry[] {
  return spec.tabs.flatMap((tab) =>
    tab.items.map((item, index) => ({ key: `${tab.id}-${index}`, tab, item, groupStart: index === 0 })),
  );
}

/**
 * How many entries fit, from the left, given each entry's measured RIGHT EDGE on a bar where
 * everything is shown.
 *
 * ONLY A SUFFIX EVER SPILLS, which is what makes one measurement enough: hiding the tail does not
 * move anything before it, so the edges taken with everything shown stay true for every prefix.
 *
 * The overflow button's width is reserved only once something spills. A bar that fits exactly has
 * no button, and reserving room for one anyway would push its last command into a menu for nothing.
 *
 * Unmeasured edges are all zero - a test DOM, or a bar not yet laid out - and zero fits anywhere,
 * so the fallback is everything on the strip rather than everything in a menu.
 */
export function fitCount(edges: number[], available: number, overflowWidth: number): number {
  const last = edges[edges.length - 1] ?? 0;

  if (last <= available) return edges.length;

  const room = available - overflowWidth;
  const fits = edges.findIndex((edge) => edge > room);

  return fits === -1 ? edges.length : fits;
}
