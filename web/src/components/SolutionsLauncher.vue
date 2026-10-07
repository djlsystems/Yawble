<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { solutionsInstalled } from '../api/client';
import type { InstalledSolution, SolutionStateKind } from '../api/types';
import { solutionMatches, stateBadge, stateChoices, teamChoices, whenWords } from '../lib/solutionPanel';
import { localInstants } from '../lib/localTime';
import FilterText from './FilterText.vue';
import InstallFromFolderDialog from './InstallFromFolderDialog.vue';
import SolutionWizard from './SolutionWizard.vue';

/**
 * THE SOLUTIONS LAUNCHER: one tile per team installed from a solution package - its name and
 * version, its team, its status line and a state badge - with Open (the package's primary site, in
 * a new tab, only when it has one), Details (everything the row says, read-only) and Manage (the
 * control panel). The tiles are the shared grid (`os-tiles` / `os-tile`, css/tiles.scss), as
 * Plugins and Agents lay theirs out, and the filter above them is the shared box: words, a team and
 * a state, narrowing what is SHOWN only.
 *
 * EVERYTHING ON A TILE THAT CAME FROM A PACKAGE IS TEXT: names, the status line and a blocked
 * reason are interpolated, never bound as HTML, so a status value that looks like markup reads as
 * the characters it is.
 *
 * With nothing installed it says how solutions arrive: a package a team built, whose Review and
 * install link appears in that team's Activity feed and on its backlog item - or install from a
 * folder, which is offered here as well, through the install dialog and the wizard Admin -> Plugins
 * opens (`InstallFromFolderDialog`).
 */
const open = defineModel<boolean>({ required: true });

const emit = defineEmits<{ manage: [team: string] }>();

const rows = ref<InstalledSolution[]>([]);
const loading = ref(false);
const error = ref('');

async function load() {
  loading.value = true;
  error.value = '';
  try {
    rows.value = await solutionsInstalled();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

watch(open, (showing) => {
  if (showing) void load();
}, { immediate: true });

// --- The filter: what is shown, never what is loaded ----------------------------------------------

const filterText = ref('');
const filterTeam = ref<string | null>(null);
const filterState = ref<SolutionStateKind | null>(null);

const shownRows = computed(() =>
  rows.value.filter((row) => solutionMatches(row, { text: filterText.value, team: filterTeam.value, state: filterState.value })));

// --- Details: one solution, read-only -------------------------------------------------------------

const detailsRow = ref<InstalledSolution | null>(null);

// --- Install from a folder: the wizard, as Admin -> Plugins opens it ------------------------------

const installOpen = ref(false);
const wizard = ref<{ open: boolean; folder: string }>({ open: false, folder: '' });

function install(folder: string) {
  installOpen.value = false;
  wizard.value = { open: true, folder };
}

// A finished install adds a tile: read the list again when the wizard closes.
watch(() => wizard.value.open, (showing, was) => {
  if (was && !showing && open.value) void load();
});
</script>

<template>
  <q-dialog v-model="open" no-route-dismiss>
    <q-card class="os-dialog-xl" data-solutions-launcher>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Solutions</div>
        <q-space />
        <q-btn
          flat
          dense
          no-caps
          icon="create_new_folder"
          label="Install from a folder"
          data-install-from-folder
          @click="installOpen = true"
        />
        <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section>
        <div v-if="error" class="os-body text-negative" data-launcher-problem>
          Could not list the solutions: {{ error }}
        </div>

        <div v-else-if="!loading && rows.length === 0" class="os-body os-text-muted" data-solutions-empty>
          <p class="q-mb-sm">No solutions are installed yet.</p>
          <p class="q-mb-sm">
            A solution is a package a team built: a whole team in one package, with its members, schedules, skills and a site to use it from.
            There are two ways to get one:
          </p>
          <ul class="q-my-none">
            <li>
              <strong>Ask the Concierge</strong>, or a team, to build one. When the package passes its check, the team's
              <strong>Activity feed</strong> and its <strong>backlog item</strong> say it is ready, with a
              <strong>Review and install</strong> link that opens the install wizard here.
            </li>
            <li><strong>Install from a folder</strong> that holds a <code>solution.json</code>, with the button above.</li>
          </ul>
        </div>

        <template v-else>
          <!-- THE FILTER: words over name, package id, version, team and status line; one team; one
               state. It narrows what is SHOWN only: nothing is fetched or sent again. -->
          <div class="solutions-filters q-mb-md" data-solutions-filters>
            <FilterText
              v-model="filterText"
              class="solutions-filter-text"
              placeholder="Filter: name, team or status"
              aria-label="Filter solutions"
              data-solutions-filter
            />
            <q-select
              v-model="filterTeam"
              :options="teamChoices(rows)"
              emit-value
              map-options
              dense
              outlined
              clearable
              label="Team"
              class="solutions-filter-select"
              data-solutions-filter-team
            />
            <q-select
              v-model="filterState"
              :options="stateChoices(rows)"
              emit-value
              map-options
              dense
              outlined
              clearable
              label="State"
              class="solutions-filter-select"
              data-solutions-filter-state
            />
          </div>

          <div v-if="shownRows.length === 0" class="os-body os-text-muted" data-solutions-none-match>
            No solution matches the filter.
          </div>

          <div v-else class="os-tiles solutions-tiles">
            <div
              v-for="row in shownRows"
              :key="row.team"
              class="os-tile solution-tile"
              :data-solution-tile="row.team"
            >
              <!-- THE TITLE ROW DOES NOT WRAP: the shared head wraps, and on a wrapping line the name
                   would be as wide as its text and never cut. -->
              <div class="os-tile-head">
                <q-icon name="apps" size="18px" class="solution-tile-icon" aria-hidden="true" />
                <div class="row items-baseline no-wrap q-gutter-x-sm solution-tile-title">
                  <div class="text-subtitle1 text-weight-medium ellipsis solution-tile-name" :title="row.name" data-tile-name>{{ row.name }}</div>
                  <div class="text-caption os-text-muted solution-tile-version" data-tile-version>{{ row.version }}</div>
                </div>
              </div>
              <div class="os-tile-line os-text-muted ellipsis" :title="row.teamName" data-tile-team>Team {{ row.teamName }}</div>
              <div class="os-tile-line">
                <q-badge
                  v-if="stateBadge(row.state)"
                  :color="stateBadge(row.state)?.color"
                  :text-color="stateBadge(row.state)?.textColor"
                  class="solution-badge"
                  :data-tile-state="row.state?.kind"
                >
                  <q-icon :name="stateBadge(row.state)?.icon" size="14px" class="q-mr-xs" />
                  <span class="solution-badge-text">{{ stateBadge(row.state)?.text }}</span>
                </q-badge>
              </div>
              <div v-if="row.status" class="os-tile-line solution-tile-status" data-tile-status>{{ localInstants(row.status) }}</div>

              <div class="solution-tile-actions">
                <!-- A REAL LINK, in a new tab: the site is the solution's app. Shown only when the
                     package names one; disabled while it is unpublished, where it would 404. -->
                <q-btn
                  v-if="row.primarySite"
                  flat
                  dense
                  no-caps
                  icon="open_in_new"
                  label="Open"
                  :href="row.primarySite.published ? row.primarySite.url : undefined"
                  target="_blank"
                  rel="noopener"
                  :disable="!row.primarySite.published"
                  data-tile-open
                >
                  <q-tooltip v-if="!row.primarySite.published">The site {{ row.primarySite.name }} is not published.</q-tooltip>
                </q-btn>
                <q-btn
                  flat
                  dense
                  round
                  icon="visibility"
                  :aria-label="`Details ${row.name}`"
                  data-tile-details
                  @click="detailsRow = row"
                >
                  <q-tooltip>Details: its package, folder, who installed it and its plugins</q-tooltip>
                </q-btn>
                <q-btn
                  unelevated
                  dense
                  no-caps
                  color="primary"
                  icon="tune"
                  label="Manage"
                  data-tile-manage
                  @click="emit('manage', row.team)"
                />
              </div>
            </div>
          </div>
        </template>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- DETAILS: everything the row says about one solution, read-only and as text. -->
  <q-dialog :model-value="detailsRow !== null" @update:model-value="(showing: boolean) => { if (!showing) detailsRow = null; }">
    <q-card v-if="detailsRow" class="os-dialog-md" data-solution-details>
      <q-card-section class="row items-center no-wrap q-pb-none">
        <div class="solution-details-title">
          <div class="os-dialog-title ellipsis">{{ detailsRow.name }}</div>
          <div class="text-caption os-text-muted mono">{{ detailsRow.id }} {{ detailsRow.version }}</div>
        </div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section class="solution-details-body">
        <dl class="solution-facts">
          <dt>Package</dt>
          <dd class="mono" data-detail="package">{{ detailsRow.id }}</dd>
          <dt>Version</dt>
          <dd data-detail="version">{{ detailsRow.version }}</dd>
          <dt>Team</dt>
          <dd data-detail="team">{{ detailsRow.teamName }}</dd>
          <dt>Installed</dt>
          <dd data-detail="installed">{{ whenWords(detailsRow.installedAt) }} by <span data-detail="installed-by">{{ detailsRow.installedBy }}</span></dd>
          <template v-if="detailsRow.updatedAt">
            <dt>Updated</dt>
            <dd data-detail="updated">{{ whenWords(detailsRow.updatedAt) }}</dd>
          </template>
          <template v-if="detailsRow.folder">
            <dt>Folder</dt>
            <dd class="mono" data-detail="folder">{{ detailsRow.folder }}</dd>
          </template>
          <dt>Plugins</dt>
          <dd data-detail="plugins">{{ detailsRow.plugins.length ? detailsRow.plugins.join(', ') : 'None' }}</dd>
          <dt>Site</dt>
          <dd data-detail="site">
            <template v-if="detailsRow.primarySite">
              {{ detailsRow.primarySite.name }} · {{ detailsRow.primarySite.published ? 'published' : 'not published' }}
            </template>
            <template v-else>None</template>
          </dd>
          <template v-if="stateBadge(detailsRow.state)">
            <dt>State</dt>
            <dd data-detail="state">{{ stateBadge(detailsRow.state)?.text }}</dd>
          </template>
          <template v-if="detailsRow.status">
            <dt>Status</dt>
            <dd data-detail="status">{{ localInstants(detailsRow.status) }}</dd>
          </template>
        </dl>
      </q-card-section>
    </q-card>
  </q-dialog>

  <InstallFromFolderDialog
    v-model="installOpen"
    caption="A solution package inside this instance: a folder holding its solution.json."
    picker-title="Choose the solution folder"
    @install="install"
  />

  <SolutionWizard v-model="wizard.open" :folder="wizard.folder" />
</template>

<style scoped>
/* The grid itself is `os-tiles` (css/tiles.scss); this only says how narrow a column may get.
   min(): a phone narrower than one column still gets a tile that fits it. */
.solutions-tiles {
  --os-tile-min: min(18rem, 100%);
}

/* A LONG NAME IS CUT INSIDE THE TILE, never pushing Open and Manage out of it: a grid item and a
   flex item both refuse to shrink below their content unless told `min-width: 0`, and the title
   row must not wrap (`no-wrap` in the template): a wrapping line is as wide as its widest child,
   so the name would never shrink. */
.solution-tile {
  min-width: 0;
  overflow: hidden;
}

.solution-tile-icon {
  color: var(--os-ink-muted);
}

.solution-tile-title {
  min-width: 0;
  flex: 1 1 0;
}

.solution-tile-name {
  min-width: 0;
  flex: 1 1 auto;
}

.solution-tile-version {
  flex: 0 0 auto;
}

.solution-tile-status {
  overflow-wrap: anywhere;
}

.solution-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 4px;
}

/* A blocked reason can be a sentence: the badge wraps rather than running off the tile. */
.solution-badge {
  white-space: normal;
  line-height: 1.3;
  padding: 3px 6px;
}

.solution-badge-text {
  overflow-wrap: anywhere;
}

.solutions-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
}

.solutions-filter-text {
  flex: 1 1 16rem;
  max-width: 28rem;
}

.solutions-filter-select {
  flex: 0 1 12rem;
  min-width: 9rem;
}

.solution-details-title {
  min-width: 0;
}

.solution-details-body {
  max-height: 70vh;
  overflow-y: auto;
}

.solution-facts {
  font-size: 12px;
  line-height: 1.45;
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 16px;
  row-gap: 4px;
  margin: 0;
}

.solution-facts dt {
  color: var(--os-ink-muted);
  font-weight: 500;
}

.solution-facts dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
}
</style>
