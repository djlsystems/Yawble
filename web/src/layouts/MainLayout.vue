<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { storeToRefs } from 'pinia';
import { useQuasar } from 'quasar';
import { useRouter } from 'vue-router';
import { useConsoleStore } from '../stores/console';
import { useWorkflowNavStore } from '../stores/workflowNav';
import { asDocumentsFolderKey, type DocumentsFolderKey, type TeamId } from '../api/types';
import { useSessionStore } from '../stores/session';
import { useTerminalDisplayStore } from '../stores/terminalDisplay';
import { useAppearanceStore } from '../stores/appearance';
import { selectedLabel } from '../lib/appearance';
import ProfileDialog from '../components/ProfileDialog.vue';
import BoardDisplayDialog from '../components/BoardDisplayDialog.vue';
import ThemesDialog from '../components/ThemesDialog.vue';
import RibbonBar from '../components/RibbonBar.vue';
import RibbonMobileMenu from '../components/RibbonMobileMenu.vue';
import CreateTeamDialog from '../components/CreateTeamDialog.vue';
import TeamSettingsDialog from '../components/TeamSettingsDialog.vue';
import TeamGitDialog from '../components/TeamGitDialog.vue';
import DocumentsDialog from '../components/DocumentsDialog.vue';
import UsersDialog from '../components/UsersDialog.vue';
import AgentsDialog from '../components/AgentsDialog.vue';
import TenantSettingsDialog from '../components/TenantSettingsDialog.vue';
import ResetTeamDialog from '../components/ResetTeamDialog.vue';
import { productLogoFor, productMark, productTitle } from '../presentation/product';
import BacklogDialog from '../components/BacklogDialog.vue';
import TenantLogDialog from '../components/TenantLogDialog.vue';
import DiagnosticsDialog from '../components/DiagnosticsDialog.vue';
import SkillsDialog from '../components/SkillsDialog.vue';
import PluginsDialog from '../components/PluginsDialog.vue';
import LocalReposDialog from '../components/LocalReposDialog.vue';
import SitesDialog from '../components/SitesDialog.vue';
import OutcomesDialog from '../components/OutcomesDialog.vue';
import ConciergePanel from '../components/ConciergePanel.vue';
import StatusStrip from '../components/StatusStrip.vue';
import VersionTag from '../components/VersionTag.vue';
import UpdateNotice from '../components/UpdateNotice.vue';
import CapacityMonitor from '../components/CapacityMonitor.vue';
import NeedsYouControl from '../components/NeedsYouControl.vue';
import ConnectionsDialog from '../components/ConnectionsDialog.vue';
import FirstRunGuide from '../components/FirstRunGuide.vue';
import CommandPalette from '../components/CommandPalette.vue';
import { PaletteShortcut } from '../lib/palette';
import { bundleBuild, releaseLabel } from '../lib/buildInfo';
import { headerFit as fitHeader, keepThrough, nextGiveWay } from '../lib/headerFit';
import { callbackOutcome, type CallbackOutcome } from '../lib/connections';
import { announceProviderReturn, leaveForProvider } from '../lib/providerReturn';
import { goTo } from '../lib/browserNavigation';
import {
  ConnectionsAction,
  DocumentsAction,
  TeamDocumentsAction,
  OutcomesAction,
  PluginsAction,
  RepositoriesAction,
  Ribbon,
  SitesAction,
  SolutionsAction,
  TeamBoardAction,
  TeamSitesAction,
  TenantSettingsAction,
} from '../lib/ribbon';

const board = useConsoleStore();
const { connected } = storeToRefs(board);

const session = useSessionStore();
const router = useRouter();

/** The account menu's Theme line names what is on screen and opens Themes. */
const appearance = useAppearanceStore();
const themeLabel = computed(() =>
  selectedLabel(appearance.resolved.selected, (appearance.draft ?? appearance.record)?.themes ?? []),
);
const themesOpen = ref(false);
const $q = useQuasar();

/**
 * Width, not touch. Whether the ribbon FITS is a question about pixels: Quasar's sm is 600px
 * and md is 1024px, so `lt-md` would fold an iPad's ribbon into a hamburger (md starts where
 * an iPad sits) and `lt-sm` would not. The case this is built for is a phone - iPhone 14/15
 * is 390px - and the same header already treats xs (< 600px) as the phone split for the
 * account name. Copying ConciergePanel's `$q.platform.has.touch` here would put a
 * hamburger on a 27-inch touchscreen and leave a phone scrolling sideways.
 *
 * Overlay drawer: the layout view is `hHh lpR fFf`, so a left overlay slot is already there.
 * The ribbon stays inside q-header on desktop because a QLayout computes ONE header offset;
 * a second QHeader would leave the page sliding under whichever it did not measure. The
 * drawer is not a header and does not change that offset. `v-if` (not `v-show`) on the strip
 * is what lets the header shrink on a phone so the board is not pushed below the fold by a
 * band that is not on screen.
 */
const ribbonMenuOpen = ref(false);

watch(
  () => $q.screen.lt.sm,
  (mobile) => {
    if (!mobile) ribbonMenuOpen.value = false;
  },
);

function onMobileRibbonAction(action: string) {
  ribbonMenuOpen.value = false;
  onRibbonAction(action);
}

const profileOpen = ref(false);
const boardDisplayOpen = ref(false);
const creatingTeam = ref(false);
const teamSettingsOpen = ref(false);
const teamSettingsTab = ref<'general' | 'members'>('general');
const gitOpen = ref(false);
const documentsOpen = ref(false);

/** Which team's folder the documents manager opens on: the active team's from Active Team ›
 *  Documents, or null (the documents root, every team's folder) from Projects › Documents. */
const documentsStart = ref<DocumentsFolderKey | null>(null);
const usersOpen = ref(false);
const agentsOpen = ref(false);

watch(
  gitOpen,
  (isOpen) => {
    if (isOpen) {
      void board.pullRepoStatus();
    }
  },
);

/** Admin → Concierge, which is the SETTINGS dialog and not the panel `conciergeOpen` above
 *  opens. It hangs off the shell rather than off Team Settings because what it sets is true of
 *  the INSTANCE: one Concierge per person, serving every team they reach. Opened from a team's
 *  own dialog, changing it changed every other team's too. */
const skillsOpen = ref(false);
/** Admin > Plugins. */
const pluginsOpen = ref(false);
/** Admin > Connections. */
const connectionsOpen = ref(false);
/** What the provider's round trip came back with, shown once in the Connections dialog. */
const connectionNotice = ref<CallbackOutcome | null>(null);

/**
 * BACK FROM A PROVIDER'S CONSENT PAGE. The Host's callback answers a redirect to
 * `/console?connection=…`; the Connections dialog opens with what it says, and the query is taken
 * off the address so a reload does not say it again. Opened AFTER mount, so the dialog's
 * `watch(open)` sees the opening edge and loads.
 */
onMounted(() => {
  const signin = router.currentRoute.value.query.signin;
  if (typeof signin === 'string' && signin !== '') {
    void leaveForSignin(signin);
    return;
  }

  const returned = callbackOutcome(router.currentRoute.value.query);
  if (!returned) return;

  connectionNotice.value = returned;
  connectionsOpen.value = true;
  void router.replace({ query: {} });

  // A slot's Add connection sent this tab to the provider and waits in its own: once it has taken
  // what came back, this tab closes. A tab the browser will not close shows the notice as before.
  void announceProviderReturn(returned).then((taken) => {
    if (taken) window.close();
  });
});

/** Opened from a slot's Add connection: on to the provider that tab started, or why not. */
async function leaveForSignin(tag: string) {
  void router.replace({ query: {} });
  const url = await leaveForProvider(tag);
  if (url) {
    goTo(url);
    return;
  }
  connectionNotice.value = {
    outcome: 'refused',
    reason: 'The page that started this sign-in is no longer waiting for it. Start it again there.',
  };
  connectionsOpen.value = true;
}
const repositoriesOpen = ref(false);
const outcomesOpen = ref(false);
/** The outcome the Outcomes dialog opens at (the palette's outcome row), or null for the list. */
const outcomesAt = ref<string | null>(null);
/** Admin > Sites, and Active Team > Sites, which is the same screen with `sitesTeam` set. */
const sitesOpen = ref(false);
const sitesTeam = ref<TeamId | null>(null);
const resetOpen = ref(false);
/** The Backlog dialog is the nav store's, so a workflow's item chip anywhere opens it on that item. */
const workflowNav = useWorkflowNavStore();
const { backlogOpen, backlogItem } = storeToRefs(workflowNav);
/** The Git dialog asked for from elsewhere (Needs you's merge row): the store made the team active. */
watch(
  () => workflowNav.gitAsks,
  () => {
    conciergeOpen.value = false;
    gitOpen.value = true;
  },
);
const tenantLogOpen = ref(false);
/** Admin → Diagnostics. Beside the tenant log on the ribbon and a different store: `Log` is what a
 *  person did to the instance, this is what the instance did when it went wrong. */
const diagnosticsOpen = ref(false);
const tenantSettingsOpen = ref(false);
const conciergeOpen = ref(false);

/** The first-run guide. It opens itself until dismissed or an agent signs in, and the account menu
 *  opens it again. */
const firstRunGuideOpen = ref(false);

/** The command palette (Ctrl+K). Opening it minimizes the Concierge, whose panel sits above dialogs. */
const paletteOpen = ref(false);
watch(paletteOpen, (isOpen) => {
  if (isOpen) conciergeOpen.value = false;
});

/** The palette's outcome row: the Outcomes dialog, at that outcome. */
function openOutcome(id: string) {
  conciergeOpen.value = false;
  outcomesAt.value = id;
  outcomesOpen.value = true;
}

const conciergeLaunchTeam = ref<TeamId | null>(null);
const conciergeLaunchTeamName = ref<string | undefined>(undefined);
const conciergeActiveTeam = computed(() => board.activeWorkTeam ?? null);

/**
 * The board follows the active tab, but a Concierge session stays pinned to the team it launched
 * on until closed. With the panel mounted at shell level, this fallback keeps `team` defined
 * before the first open while preserving the launch pin once captured.
 */
const conciergeSessionTeam = computed<TeamId | null>(() => conciergeLaunchTeam.value ?? board.activeWorkTeam?.id ?? null);
const conciergeSessionTeamName = computed(() => conciergeLaunchTeamName.value ?? board.activeWorkTeam?.name);

/** The bubble opens the Concierge and, pressed again, minimises it: one control, both ways. */
/** A dialog chosen outside the ribbon (the account menu's): the same rule as `onRibbonAction`. */
function minimizeConciergeFor(open: () => void) {
  conciergeOpen.value = false;
  open();
}

function toggleConcierge() {
  conciergeOpen.value = !conciergeOpen.value;
}

/**
 * THE BUBBLE STAYS REACHABLE WHILE THE PANEL IS OPEN, or it could not close it. A windowed panel
 * leaves the corner free, so the bubble is lifted above the panel's shell (z-index 7000). A full
 * screen panel (maximised, or a phone) covers the corner and has its own minimise button; the
 * bubble is hidden there rather than drawn over the terminal's last line.
 */
const terminalDisplay = useTerminalDisplayStore();
const conciergeFullScreen = computed(() => terminalDisplay.maximised || $q.screen.lt.sm);
/** The documents explorer full screen (maximised, or a phone) covers the corner too, and the
 *  bubble over it hid the end of its status bar and the last row's menu. It has its own close. */
const documentsFullScreen = ref(false);
const conciergeFabShown = computed(
  () => !(conciergeOpen.value && conciergeFullScreen.value) && !(documentsOpen.value && documentsFullScreen.value),
);

watch(conciergeOpen, (isOpen) => {
  if (isOpen) {
    if (!conciergeLaunchTeam.value && board.activeWorkTeam) {
      conciergeLaunchTeam.value = board.activeWorkTeam.id;
      conciergeLaunchTeamName.value = board.activeWorkTeam.name;
    }
    return;
  }

  conciergeLaunchTeam.value = null;
  conciergeLaunchTeamName.value = undefined;
});

/**
 * A team you just made is the team you meant to work in.
 *
 * Selected BEFORE the refresh, so the board never paints the old team for a frame first - and by
 * name rather than by "the last one", because the overview comes back ordered and the newest team
 * is not reliably at either end of it.
 */
async function teamCreated(id: TeamId) {
  board.setActiveTeam(id);
  await board.refreshForTeamCreated(id);
}

/**
 * The ribbon names its commands; this decides what they do. An action the file names and this does
 * not handle is ignored rather than thrown - the toolbar is hand-edited, and a stale name in it
 * should cost a dead button, not the page.
 *
 * Settings opens on its General tab; the team's members are its Members tab, one dialog for "what
 * is this team". Board is not a dialog: it is the team's board.
 */
const teamSettingsTabs: Record<string, 'general' | 'members'> = {
  'team-settings': 'general',
};

/**
 * `team` is only ever sent by a documents row - see `RibbonBar`'s emit. Every other action names
 * one thing and needs no argument, which is why it is optional rather than a second parameter
 * every branch has to thread.
 */
function onRibbonAction(action: string) {
  // CHOOSING ANOTHER COMMAND MINIMIZES THE CONCIERGE. The panel's shell sits at z-index 7000, above
  // every QDialog (6000), so a dialog opened from the ribbon with the panel open painted BEHIND the
  // terminal - and, being modal, took the keyboard from it, so neither could be used. Minimizing keeps
  // the session running; the bubble brings it back.
  conciergeOpen.value = false;

  if (action === 'admin-new-team') {
    creatingTeam.value = true;
    return;
  }

  const wantedTab = teamSettingsTabs[action];

  if (wantedTab) {
    teamSettingsTab.value = wantedTab;
    teamSettingsOpen.value = true;
    return;
  }

  // BOARD IS NOT A DIALOG: the active team's board, the same door its console tab is. There is no
  // Kanban here - the console strip's Kanban tab opens it filtered to the active team.
  if (action === TeamBoardAction) {
    if (board.activeWorkTeamId) workflowNav.openBoard(board.activeWorkTeamId);
  }
  else if (action === 'team-git') gitOpen.value = true;
  else if (action === 'team-reset') resetOpen.value = true;
  else if (action === TeamSitesAction) openSites(board.activeWorkTeamId || null);
  // UNPREFIXED, deliberately, and for the same reasons as `backlog` below - see `lib/ribbon.ts`.
  // Documents live under one tenant root, so this needs no active team.
  else if (action === DocumentsAction) openDocuments(null);
  // The same explorer, opened at the active team's own folder rather than the root.
  else if (action === TeamDocumentsAction) openDocuments(board.activeWorkTeamId ? asDocumentsFolderKey(board.activeWorkTeamId) : null);
  // NOT A DIALOG - this action selects the permanent Teams tab, the same door `IndexPage`'s own
  // Teams tab click uses.
  else if (action === 'admin-teams') board.showTeamsView();
  // UNPREFIXED, deliberately - see `lib/ribbon.ts`. The backlog needs no active team, so the
  // `team-` disable predicate does not apply to it by construction.
  else if (action === 'backlog') workflowNav.openBacklog();
  else if (action === 'admin-log') tenantLogOpen.value = true;
  else if (action === 'admin-diagnostics') diagnosticsOpen.value = true;
  else if (action === 'admin-users') usersOpen.value = true;
  else if (action === 'admin-agents') agentsOpen.value = true;
  else if (action === 'admin-skills') skillsOpen.value = true;
  else if (action === PluginsAction) pluginsOpen.value = true;
  else if (action === ConnectionsAction) {
    connectionNotice.value = null;
    connectionsOpen.value = true;
  }
  else if (action === RepositoriesAction) repositoriesOpen.value = true;
  else if (action === OutcomesAction) {
    outcomesAt.value = null;
    outcomesOpen.value = true;
  }
  else if (action === SitesAction) openSites(null);
  else if (action === TenantSettingsAction) tenantSettingsOpen.value = true;
  // NOT A DIALOG: an address, `#/solutions`, so a tile's Manage, the board header and the
  // Concierge reach the same screens. Unprefixed: it needs no active team.
  else if (action === SolutionsAction) void router.push('/solutions');
}

/** The documents explorer, at the root or at one team's folder. */
function openDocuments(start: DocumentsFolderKey | null) {
  documentsStart.value = start;
  documentsOpen.value = true;
}

/** Every team's sites, or one team's. */
function openSites(team: TeamId | null) {
  sitesTeam.value = team;
  sitesOpen.value = true;
}

/**
 * The local part of the address. A whole email does not fit a phone's toolbar beside the live chip,
 * and the domain is the same for everyone on a single-tenant instance - so it is the half that
 * carries no information. The full address is in the menu, where there is room for it.
 */
const shortName = computed(() => session.user?.email.split('@')[0] ?? '');

/** The live chip's word, said on the chip, or in its tooltip when the chip is a dot. */
const liveWord = computed(() => (connected.value ? 'live' : 'offline'));

/** The logo's tooltip: the product and which build this is, the version the row no longer carries. */
const brandTooltip = `${productTitle} ${releaseLabel(bundleBuild.version)}`;

/**
 * What of the right cluster the one header row carries (`lib/headerFit.ts`). Each fitting the
 * ribbon reports that keeps fewer than New Team through Backlog takes one more step; a wider window
 * starts again from none, and the ribbon's next fitting takes back the steps it still needs.
 */
const giveWay = ref(0);
const mustKeep = keepThrough(Ribbon);
/** An update is pending, so the notice is on the row (UpdateNotice says). */
const updatePending = ref(false);
const headerFit = computed(() => fitHeader(giveWay.value, $q.screen.lt.sm, updatePending.value));

function onRibbonFit(shown: number) {
  giveWay.value = nextGiveWay(giveWay.value, shown, mustKeep);
}

/** After each step, fit again: a step with nothing to take (no update pending) changes no width the
 *  ribbon's observer would see, and the next step would otherwise wait for an unrelated resize. */
const ribbonBar = ref<InstanceType<typeof RibbonBar> | null>(null);

watch(giveWay, async (level, before) => {
  if (level <= before) return;
  await nextTick();
  ribbonBar.value?.refit();
});

watch(
  () => $q.screen.width,
  (width, before) => {
    if (width > before) giveWay.value = 0;
  },
);

async function signOut() {
  // The redirect happens whether or not the request did. `session.signOut()` clears the local user
  // in a finally but still rethrows, so a network-level failure - the ordinary case on a phone -
  // would otherwise leave the board rendered, the store already signed out, and nothing at all
  // said to the person who pressed the button. The local state is gone by then, so navigating is the correct thing to do even when the call failed.
  try {
    await session.signOut();
  } catch {
    // Nothing worth showing: they asked to leave, and they are leaving. The server-side cookie is
    // dropped on the next 401 anyway.
  }

  // Home, not the credential page: the landing page is the front door, and it shows a Log in
  // button once the session is gone. Sending them to a bare form instead would be a dead end with
  // no way back to the product.
  await router.replace('/');
}
</script>

<template>
  <q-layout view="hHh lpR fFf">
    <!-- WARM NEAR-BLACK, and a class rather than `bg-dark`: Quasar's `$dark` is a
         cool grey-violet, and it is the same in both themes. The ground is the wordmark's own ink
         on a light console and the dark surface on a dark one - both tokens, so a theme change
         reaches the header with nothing here knowing the values. -->
    <q-header elevated class="app-header" :class="{ 'app-header-dark': $q.dark.isActive }">
      <!-- ONE ROW, 44px: the logo, the ribbon's commands, and the right cluster at its end. The
           brand row it replaced, the ribbon and the console strip spent about 150px before the
           first card. The ribbon reserves the cluster's width before it fits its commands, so what
           gives way is a command into the overflow menu, never the cluster.

           Below `sm` the same row carries no commands - the hamburger opens them as a list - and
           the header shrinks to it. One header element: a QLayout computes ONE header offset. -->
      <RibbonBar ref="ribbonBar" :commands="!$q.screen.lt.sm" :tight="headerFit.tight" @action="onRibbonAction" @fit="onRibbonFit">
        <template #start>
          <!-- Phone-only. The ribbon does not fit below `sm` (600px); this is the door into the
               same commands as a list. -->
          <q-btn
            v-if="$q.screen.lt.sm"
            flat
            dense
            round
            icon="menu"
            class="q-mr-xs"
            :aria-label="ribbonMenuOpen ? 'Close menu' : 'Open menu'"
            :aria-expanded="ribbonMenuOpen ? 'true' : 'false'"
            aria-controls="ribbon-menu"
            @click="ribbonMenuOpen = !ribbonMenuOpen"
          />

          <!-- The mark goes home. Which build this is lives in its tooltip, so the version needs no
               room on the bar. -->
          <router-link to="/" class="brand-link" :aria-label="`${productTitle} home`" data-test="brand-link">
            <!-- Short of room, the last step: the head mark alone; the name stays its alt and tooltip. -->
            <img v-if="headerFit.mark" :src="productMark" :alt="productTitle" width="128" height="128" data-test="brand-mark" />
            <img v-else :src="productLogoFor($q.dark.isActive)" :alt="productTitle" width="1066" height="365" />
            <q-tooltip anchor="bottom left" self="top left" :offset="[0, 6]">{{ brandTooltip }}</q-tooltip>
          </router-link>
        </template>

        <template #end>
          <!-- Which build this is. The second thing to leave the row when it is short of room: it
               is under the logo's tooltip and in the account menu as well. -->
          <VersionTag v-if="headerFit.version" class="header-version" />
          <!-- "Update available" when the release check found a newer release; nothing otherwise. The
               first thing to give way: its icon alone, the words its tooltip and aria-label. -->
          <UpdateNotice :word="headerFit.updateWord" @pending="(pending) => (updatePending = pending)" />

          <!--
            Live or not, stated. A board that has quietly stopped receiving pushes looks exactly
            like a board where nothing is happening, and the two want very different reactions.
            There is deliberately NO refresh button: a manual refresh lets a broken push hide
            behind a click.

            The brand orange when live and an outline when not, so offline reads as ABSENT rather
            than as a second colour to decode. Short of room it is a dot, the word in its tooltip.
          -->
          <q-chip
            dense
            class="live-chip"
            :class="{ 'live-chip-on': connected, 'live-chip-dot': !headerFit.liveWord }"
            :icon="connected ? 'sync' : 'sync_disabled'"
            :label="headerFit.liveWord ? liveWord : undefined"
            :aria-label="liveWord"
            data-test="live-chip"
          >
            <q-tooltip v-if="!headerFit.liveWord">{{ liveWord }}</q-tooltip>
          </q-chip>

          <!-- What is waiting on a person, as one count, left of the gauge. People only, as its route is. -->
          <NeedsYouControl v-if="session.user" :word="headerFit.needsYouWord" />
          <!-- The activity monitor: the instance's load as one gauge, its figures a click away.
               People only, as its route is. -->
          <CapacityMonitor v-if="session.user" />

            <!-- One control, not a name beside a logout icon. The account is a place you go rather
                 than a label with a button next to it, and the profile has nowhere else to live. -->
            <q-btn-dropdown
              v-if="session.user"
              flat
              dense
              no-caps
              class="user-menu"
              content-class="above-concierge"
              :aria-label="`Account: ${session.user.email}`"
            >
              <template #label>
                <q-icon name="account_circle" size="20px" />
                <!-- The name hides below 600px, and on a row short of room (`lib/headerFit.ts`), so the
                     toolbar does not crowd; the icon and the caret remain, which is still a full tap
                     target, and the name is the menu's first line. -->
                <span v-if="headerFit.accountName" class="gt-xs q-ml-xs" data-test="account-name">{{ shortName }}</span>
              </template>

              <q-list>
                <q-item class="text-caption os-text-muted" data-test="account-email">
                  <q-item-section>{{ session.user.email }}</q-item-section>
                </q-item>

                <!-- Which build this is: off the bar, here and under the logo's tooltip (and in Updates). -->
                <q-item class="text-caption os-text-muted" data-test="account-version">
                  <q-item-section>
                    <VersionTag />
                  </q-item-section>
                </q-item>

                <q-separator />

                <!-- A DISPLAY preference, which is why it is here rather than in the ribbon: it is
                     about this browser's view rather than about a team or the tenant, and it sits
                     beside the other thing in this menu that is about you. -->
                <q-item v-close-popup clickable @click="minimizeConciergeFor(() => (boardDisplayOpen = true))">
                  <q-item-section avatar>
                    <q-icon name="dashboard_customize" />
                  </q-item-section>
                  <q-item-section>Board display</q-item-section>
                </q-item>

                <!-- Theme, beside Board display for the same reason: it is about you. It names what is on
                     screen and opens Themes, where it is chosen and saved for the person. -->
                <q-item
                  v-close-popup
                  clickable
                  data-test="theme-open"
                  @click="minimizeConciergeFor(() => (themesOpen = true))"
                >
                  <q-item-section avatar>
                    <q-icon name="contrast" />
                  </q-item-section>
                  <q-item-section>Theme: {{ themeLabel }}</q-item-section>
                </q-item>

                <!-- The first-run guide, again: its own text says this is where it lives once dismissed. -->
                <q-item
                  v-close-popup
                  clickable
                  data-test="account-first-run-guide"
                  @click="minimizeConciergeFor(() => (firstRunGuideOpen = true))"
                >
                  <q-item-section avatar>
                    <q-icon name="flag" />
                  </q-item-section>
                  <q-item-section>Getting started</q-item-section>
                </q-item>

                <!-- The command palette, and the one place on the bar its shortcut is said. -->
                <q-item
                  v-close-popup
                  clickable
                  data-test="account-command-palette"
                  @click="paletteOpen = true"
                >
                  <q-item-section avatar>
                    <q-icon name="search" />
                  </q-item-section>
                  <q-item-section>Find anything</q-item-section>
                  <q-item-section side>
                    <kbd class="account-shortcut" data-test="account-palette-shortcut">{{ PaletteShortcut }}</kbd>
                  </q-item-section>
                </q-item>

                <q-item v-close-popup clickable @click="minimizeConciergeFor(() => (profileOpen = true))">
                  <q-item-section avatar>
                    <q-icon name="manage_accounts" />
                  </q-item-section>
                  <q-item-section>User profile</q-item-section>
                </q-item>

                <q-item v-close-popup clickable @click="signOut">
                  <q-item-section avatar>
                    <q-icon name="logout" />
                  </q-item-section>
                  <q-item-section>Log out</q-item-section>
                </q-item>
              </q-list>
            </q-btn-dropdown>
        </template>
      </RibbonBar>
    </q-header>
    <StatusStrip />

    <q-drawer
      id="ribbon-menu"
      v-model="ribbonMenuOpen"
      side="left"
      overlay
      bordered
      :width="300"
      :breakpoint="600"
      class="ribbon-menu-drawer"
    >
      <q-scroll-area class="fit">
        <RibbonMobileMenu
          v-if="$q.screen.lt.sm"
          @action="onMobileRibbonAction"
          @close="ribbonMenuOpen = false"
        />
      </q-scroll-area>
    </q-drawer>

    <q-page-container>
      <router-view />
      <!-- The screen an address opens over the Console (the install review, the Solutions
           launcher, a control panel): see `routes.ts`. -->
      <router-view name="overlay" />
    </q-page-container>

    <!-- The bubble is available with no active team AND with no teams at all: an empty
         instance is exactly when you want to ask the Concierge for a team. The original
         UAT complaint was a door to nothing when there were zero teams; the guard tested
         whether one was active, which is a different state. -->
    <q-page-sticky v-show="conciergeFabShown" position="bottom-right" :offset="[18, 18]" class="concierge-fab-dock">
      <!-- The brand orange rather than Quasar's primary blue: this is the door into the
           product's own surface, and the stock primary reads as a control borrowed from
           somewhere else. Same value as the landing page and the sign-in button. -->
      <!-- NAMED ON THE BUTTON while it is closed, not only on hover: an unlabelled round button was
           not found by a new person looking for "the Concierge" the README and the CLI name. A
           phone keeps the round button, where a label would cover the board. -->
      <q-btn
        fab
        icon="terminal"
        :label="conciergeOpen || $q.screen.lt.sm ? undefined : 'Concierge'"
        no-caps
        class="concierge-fab"
        :aria-label="conciergeOpen ? 'Minimize the Concierge' : 'Open the Concierge'"
        :aria-expanded="conciergeOpen"
        @click="toggleConcierge"
      >
        <!-- Anchored to the LEFT of the button, and not allowed to wrap. The default places a
             tooltip above-centre, which for a button pinned to the bottom-right corner lands it
             against the viewport edge: it wrapped to two lines and was then clipped by the
             scrollbar, so the label read "Open the" and nothing else. There is always room to the
             left of a bottom-right control; there is never reliably room above one. -->
        <q-tooltip
          anchor="center left"
          self="center right"
          :offset="[10, 0]"
          class="text-no-wrap"
        >
          {{ conciergeOpen ? 'Minimize the Concierge' : 'Open the Concierge' }}
        </q-tooltip>
      </q-btn>
    </q-page-sticky>

    <FirstRunGuide
      v-if="session.user"
      v-model:open="firstRunGuideOpen"
      :concierge-open="conciergeOpen"
    />

    <ConciergePanel
      v-model="conciergeOpen"
      :team="conciergeSessionTeam"
      :team-name="conciergeSessionTeamName"
      :active-team="conciergeActiveTeam?.id ?? null"
      :active-team-name="conciergeActiveTeam?.name"
    />

    <ProfileDialog v-model="profileOpen" />
    <BoardDisplayDialog v-model="boardDisplayOpen" />
    <ThemesDialog v-model="themesOpen" />
    <CreateTeamDialog v-model="creatingTeam" @created="teamCreated" />
    <TeamSettingsDialog v-model="teamSettingsOpen" :initial-tab="teamSettingsTab" />
    <TeamGitDialog
      v-if="board.activeWorkTeam"
      v-model="gitOpen"
      :repo-status="board.repoStatusTeam === board.activeWorkTeam.id ? board.repoStatus : null"
      :team-id="board.activeWorkTeam.id"
      :any-member-running="board.activeWorkTeam.containers.some((c) => c.state === 'Running')"
    />
    <DocumentsDialog v-model="documentsOpen" v-model:full-screen="documentsFullScreen" :start="documentsStart" />
    <UsersDialog v-model="usersOpen" />
    <AgentsDialog v-model="agentsOpen" />
    <TenantSettingsDialog
      v-model="tenantSettingsOpen"
      :covered="agentsOpen || skillsOpen"
      @open-agents="agentsOpen = true"
      @open-skills="skillsOpen = true"
    />
    <SkillsDialog v-model="skillsOpen" />
    <PluginsDialog v-model="pluginsOpen" />
    <ConnectionsDialog v-model="connectionsOpen" :notice="connectionNotice" />
    <LocalReposDialog v-model="repositoriesOpen" />
    <OutcomesDialog v-model="outcomesOpen" :outcome="outcomesAt" />
    <CommandPalette v-if="session.user" v-model:open="paletteOpen" @action="onRibbonAction" @outcome="openOutcome" />
    <SitesDialog v-model="sitesOpen" :team="sitesTeam" />
    <!-- Guarded on there BEING an active team, because the dialog reads that team's members and
         addresses it by id. The ribbon disables a `team-` action without one, so this is a
         backstop rather than the way anyone meets the rule - but the prop is non-nullable and a
         `team-reset` reaching here with nothing selected would be a render error rather than a
         no-op. -->
    <ResetTeamDialog v-if="board.activeWorkTeam" v-model="resetOpen" :team="board.activeWorkTeam" />
  <BacklogDialog v-model="backlogOpen" :item="backlogItem" />
    <TenantLogDialog v-model="tenantLogOpen" />
    <DiagnosticsDialog v-model="diagnosticsOpen" />
  </q-layout>
</template>

<style scoped>
/* The palette's shortcut in the account menu: a key cap, muted. */
.account-shortcut {
  padding: 0 6px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  font-family: inherit;
  font-size: 0.75rem;
  color: var(--os-ink-muted);
}

/* The bar follows the theme - a light surface in light, the dark surface in dark - so the logo
   for each theme sits on the ground it was drawn for. */
.app-header,
.app-header-dark {
  background: var(--os-surface);
  color: var(--os-ink);
}

.live-chip {
  background: transparent;
  color: inherit;
  border: 1px solid currentColor;
  opacity: 0.7;
}

.live-chip-on {
  background: var(--os-primary, var(--q-primary));
  border-color: var(--os-primary, var(--q-primary));
  color: white;
  opacity: 1;
}

.brand-link {
  display: inline-flex;
  align-items: center;
}

.brand-link {
  color: inherit;
  text-decoration: none;
  gap: 8px;
}

.brand-link img {
  height: 30px;
  width: auto;
  display: block;
}

.header-version {
  margin-right: 4px;
  flex: 0 1 auto;
}

/* The live chip short of room: the dot of its colour and its icon, the word in its tooltip. */
.live-chip-dot {
  padding: 0 4px;
}

.user-menu {
  max-width: 12rem;
}

.ribbon-menu-drawer {
  background: var(--os-chrome);
  color: var(--os-ink-muted);
}

/* Above the Concierge shell (7000), so a windowed panel can be minimised from the bubble. */
.concierge-fab-dock {
  z-index: 7001;
}

.concierge-fab {
  background: var(--os-primary);
  color: white;
}

.concierge-fab:hover {
  background: var(--os-primary-hover);
}
</style>
