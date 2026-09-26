<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { storeToRefs } from 'pinia';
import { useQuasar } from 'quasar';
import { useRouter } from 'vue-router';
import { useConsoleStore } from '../stores/console';
import { useKanbanStore } from '../stores/kanban';
import type { TeamId } from '../api/types';
import { useSessionStore } from '../stores/session';
import { useDisplayStore } from '../stores/display';
import { ThemeChoices, type Theme } from '../lib/theme';
import ProfileDialog from '../components/ProfileDialog.vue';
import BoardDisplayDialog from '../components/BoardDisplayDialog.vue';
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
import { productLogoFor, productTitle } from '../presentation/product';
import BacklogDialog from '../components/BacklogDialog.vue';
import TenantLogDialog from '../components/TenantLogDialog.vue';
import DiagnosticsDialog from '../components/DiagnosticsDialog.vue';
import ApiKeysDialog from '../components/ApiKeysDialog.vue';
import SkillsDialog from '../components/SkillsDialog.vue';
import ConciergePanel from '../components/ConciergePanel.vue';
import StatusStrip from '../components/StatusStrip.vue';
import VersionTag from '../components/VersionTag.vue';
import { DocumentsAction, TenantSettingsAction } from '../lib/ribbon';

const board = useConsoleStore();
const { connected } = storeToRefs(board);

/** The Kanban tab's state. The shell only ever SELECTS it; the board itself lives on the page. */
const kanban = useKanbanStore();

const session = useSessionStore();
const router = useRouter();

/** The theme toggle in the account menu. The store persists and applies it; this only chooses. */
const display = useDisplayStore();
const themeOptions = ThemeChoices.map((choice) => ({
  value: choice.value,
  icon: choice.icon,
  attrs: { 'aria-label': choice.label, title: choice.label },
}));

function chooseTheme(theme: Theme) {
  display.setTheme(theme);
}
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

/** Which team's folder the documents manager should open on, or null for its own default. Set by
 *  the ribbon row that was clicked; documents are not the active team's. */
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
const resetOpen = ref(false);
const backlogOpen = ref(false);
const tenantLogOpen = ref(false);
/** Admin → Diagnostics. Beside the tenant log on the ribbon and a different store: `Log` is what a
 *  person did to the instance, this is what the instance did when it went wrong. */
const diagnosticsOpen = ref(false);
const keysOpen = ref(false);
const tenantSettingsOpen = ref(false);
const conciergeOpen = ref(false);
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

function openConcierge() {
  conciergeOpen.value = true;
}

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
 * Settings and Members are the same dialog opened on different tabs, not two dialogs: they show
 * the same team and would otherwise drift into two answers to "what is this team".
 */
const teamSettingsTabs: Record<string, 'general' | 'members'> = {
  'team-settings': 'general',
  'team-members': 'members',
};

/**
 * `team` is only ever sent by a documents row - see `RibbonBar`'s emit. Every other action names
 * one thing and needs no argument, which is why it is optional rather than a second parameter
 * every branch has to thread.
 */
function onRibbonAction(action: string) {
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

  // THE KANBAN ICON IS NOT A DIALOG. It selects the top-level Kanban tab and pre-applies
  // this team as the board's filter - the board is one page per tenant, and the ribbon button is
  // the team-shaped way in rather than a per-team board of its own.
  if (action === 'team-kanban') kanban.openForTeam(board.activeWorkTeamId);
  else if (action === 'team-git') gitOpen.value = true;
  else if (action === 'team-reset') resetOpen.value = true;
  // UNPREFIXED, deliberately, and for the same reasons as `backlog` below - see `lib/ribbon.ts`.
  // Documents live under one tenant root, so this needs no active team.
  else if (action === DocumentsAction) documentsOpen.value = true;
  // NOT A DIALOG - this action selects the permanent Teams tab, the same door `IndexPage`'s own
  // Teams tab click uses.
  else if (action === 'admin-teams') board.showTeamsView();
  // UNPREFIXED, deliberately - see `lib/ribbon.ts`. The backlog needs no active team, so the
  // `team-` disable predicate does not apply to it by construction.
  else if (action === 'backlog') backlogOpen.value = true;
  else if (action === 'admin-log') tenantLogOpen.value = true;
  else if (action === 'admin-diagnostics') diagnosticsOpen.value = true;
  else if (action === 'admin-users') usersOpen.value = true;
  else if (action === 'admin-agents') agentsOpen.value = true;
  else if (action === 'admin-skills') skillsOpen.value = true;
  else if (action === 'admin-keys') keysOpen.value = true;
  else if (action === TenantSettingsAction) tenantSettingsOpen.value = true;
}

/**
 * The local part of the address. A whole email does not fit a phone's toolbar beside the live chip,
 * and the domain is the same for everyone on a single-tenant instance - so it is the half that
 * carries no information. The full address is in the menu, where there is room for it.
 */
const shortName = computed(() => session.user?.email.split('@')[0] ?? '');

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
      <q-toolbar>
        <!-- Phone-only. The ribbon does not fit below `sm` (600px); this is the door into the
             same commands as a list. Round + dense keeps the tap target without crowding the
             logo beside it. -->
        <q-btn
          v-if="$q.screen.lt.sm"
          flat
          dense
          round
          icon="menu"
          :aria-label="ribbonMenuOpen ? 'Close menu' : 'Open menu'"
          :aria-expanded="ribbonMenuOpen ? 'true' : 'false'"
          aria-controls="ribbon-menu"
          @click="ribbonMenuOpen = !ribbonMenuOpen"
        />

        <!-- The mark goes home. A board with no way back to the front page is how someone who
             signed out on the landing page ends up with two apps that do not know about each
             other. -->
        <q-toolbar-title class="row items-center no-wrap">
          <router-link to="/" class="brand-link" :aria-label="`${productTitle} home`">
            <img :src="productLogoFor($q.dark.isActive)" :alt="productTitle" width="1066" height="365" />
          </router-link>
          <!-- Which build this is, beside the mark. It gives way first on a narrow bar. -->
          <VersionTag class="header-version" />
        </q-toolbar-title>

        <!--
          Live or not, stated. A board that has quietly stopped receiving pushes
          looks exactly like a board where nothing is happening, and the two want
          very different reactions from the person looking at it.

          There is deliberately NO refresh button. Not an oversight: a manual
          refresh lets a broken push hide behind a click, and this board's whole
          claim is that it does not need one.
        -->
        <!-- The brand orange when live - the one accent the header carries - and an outline when
             not, so offline reads as ABSENT rather than as a second colour to decode. -->
        <q-chip
          dense
          class="live-chip"
          :class="{ 'live-chip-on': connected }"
          :icon="connected ? 'sync' : 'sync_disabled'"
          :label="connected ? 'live' : 'offline'"
        />

        <!-- One control, not a name beside a logout icon. The account is a place you go rather
             than a label with a button next to it, and the profile has nowhere else to live. -->
        <q-btn-dropdown
          v-if="session.user"
          flat
          dense
          no-caps
          class="q-ml-sm user-menu"
          :aria-label="`Account: ${session.user.email}`"
        >
          <template #label>
            <q-icon name="account_circle" size="20px" />
            <!-- The name hides below 600px so the toolbar does not crowd on a phone; the icon and
                 the caret remain, which is still a full tap target. -->
            <span class="gt-xs q-ml-xs">{{ shortName }}</span>
          </template>

          <q-list>
            <q-item class="text-caption os-text-muted">
              <q-item-section>{{ session.user.email }}</q-item-section>
            </q-item>

            <q-separator />

            <!-- A DISPLAY preference, which is why it is here rather than in the ribbon: it is
                 about this browser's view rather than about a team or the tenant, and it sits
                 beside the other thing in this menu that is about you. -->
            <q-item v-close-popup clickable @click="boardDisplayOpen = true">
              <q-item-section avatar>
                <q-icon name="dashboard_customize" />
              </q-item-section>
              <q-item-section>Board display</q-item-section>
            </q-item>

            <!-- Theme, beside Board display for the same reason: it is this browser's view. Not a
                 v-close-popup row - the change is visible at once, so the menu stays open to show
                 it and to let a person try the other two. -->
            <q-item>
              <q-item-section avatar>
                <q-icon name="contrast" />
              </q-item-section>
              <q-item-section>Theme</q-item-section>
              <q-item-section side>
                <q-btn-toggle
                  :model-value="display.theme"
                  :options="themeOptions"
                  dense
                  flat
                  toggle-color="primary"
                  aria-label="Theme"
                  data-test="theme-toggle"
                  @update:model-value="chooseTheme"
                />
              </q-item-section>
            </q-item>

            <q-item v-close-popup clickable @click="profileOpen = true">
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
      </q-toolbar>

      <!-- The application bar, inside the header rather than beside it: a QLayout computes ONE
           header offset, so a second QHeader would leave the page sliding under whichever of them
           it did not measure. Being in here also means it stays put while the board scrolls.
           Unmounted below `sm` rather than hidden: a `display: none` strip still occupies the
           header box QLayout measured, and that is how a whole panel lands one viewport below
           the fold while every test stays green. -->
      <RibbonBar v-if="!$q.screen.lt.sm" @action="onRibbonAction" />
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
    </q-page-container>

    <!-- The bubble is available with no active team AND with no teams at all: an empty
         instance is exactly when you want to ask the Concierge for a team. The original
         UAT complaint was a door to nothing when there were zero teams; the guard tested
         whether one was active, which is a different state. -->
    <q-page-sticky position="bottom-right" :offset="[18, 18]">
      <!-- The brand orange rather than Quasar's primary blue: this is the door into the
           product's own surface, and the stock primary reads as a control borrowed from
           somewhere else. Same value as the landing page and the sign-in button. -->
      <q-btn fab icon="terminal" class="concierge-fab" @click="openConcierge">
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
          Open the Concierge
        </q-tooltip>
      </q-btn>
    </q-page-sticky>

    <ConciergePanel
      v-model="conciergeOpen"
      :team="conciergeSessionTeam"
      :team-name="conciergeSessionTeamName"
      :active-team="conciergeActiveTeam?.id ?? null"
      :active-team-name="conciergeActiveTeam?.name"
    />

    <ProfileDialog v-model="profileOpen" />
    <BoardDisplayDialog v-model="boardDisplayOpen" />
    <CreateTeamDialog v-model="creatingTeam" @created="teamCreated" />
    <TeamSettingsDialog v-model="teamSettingsOpen" :initial-tab="teamSettingsTab" />
    <TeamGitDialog
      v-if="board.activeWorkTeam"
      v-model="gitOpen"
      :repo-status="board.repoStatusTeam === board.activeWorkTeam.id ? board.repoStatus : null"
      :team-id="board.activeWorkTeam.id"
      :any-member-running="board.activeWorkTeam.containers.some((c) => c.state === 'Running')"
    />
    <DocumentsDialog v-model="documentsOpen" />
    <UsersDialog v-model="usersOpen" />
    <AgentsDialog v-model="agentsOpen" />
    <TenantSettingsDialog
      v-model="tenantSettingsOpen"
      @open-agents="agentsOpen = true"
      @open-skills="skillsOpen = true"
    />
    <SkillsDialog v-model="skillsOpen" />
    <!-- Guarded on there BEING an active team, because the dialog reads that team's members and
         addresses it by id. The ribbon disables a `team-` action without one, so this is a
         backstop rather than the way anyone meets the rule - but the prop is non-nullable and a
         `team-reset` reaching here with nothing selected would be a render error rather than a
         no-op. -->
    <ResetTeamDialog v-if="board.activeWorkTeam" v-model="resetOpen" :team="board.activeWorkTeam" />
  <BacklogDialog v-model="backlogOpen" />
    <TenantLogDialog v-model="tenantLogOpen" />
    <DiagnosticsDialog v-model="diagnosticsOpen" />
    <ApiKeysDialog v-model="keysOpen" />
  </q-layout>
</template>

<style scoped>
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

.q-toolbar__title .header-version {
  margin-left: 10px;
  flex: 0 1 auto;
}

/* A phone bar (below Quasar's sm) also carries the menu button, the live chip and the account.
   Measured at 390px wide: a release (2026.09.23.1) fits whole at this size and ellipsised at the
   desktop one; a dev build's longer string ellipsises, and its hover and aria-label still say it.
   Under the title's class so it outranks VersionTag's own rule rather than tying with it. */
@media (max-width: 599.98px) {
  .q-toolbar__title .header-version {
    margin-left: 6px;
    font-size: 0.66rem;
  }
}

.user-menu {
  max-width: 12rem;
}

.ribbon-menu-drawer {
  background: var(--os-chrome);
  color: var(--os-ink-muted);
}

.concierge-fab {
  background: var(--os-primary);
  color: white;
}

.concierge-fab:hover {
  background: var(--os-primary-hover);
}
</style>
