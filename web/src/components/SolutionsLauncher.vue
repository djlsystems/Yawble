<script setup lang="ts">
import { ref, watch } from 'vue';
import { solutionsInstalled } from '../api/client';
import type { InstalledSolution } from '../api/types';
import { stateBadge } from '../lib/solutionPanel';
import HostPathPicker from './HostPathPicker.vue';
import SolutionWizard from './SolutionWizard.vue';

/**
 * THE SOLUTIONS LAUNCHER: one tile per team installed from a solution package - its name and
 * version, its team, its status line and a state badge - with Open (the package's primary site, in
 * a new tab, only when it has one) and Manage (the control panel).
 *
 * EVERYTHING ON A TILE THAT CAME FROM A PACKAGE IS TEXT: names, the status line and a blocked
 * reason are interpolated, never bound as HTML, so a status value that looks like markup reads as
 * the characters it is.
 *
 * With nothing installed it says how solutions arrive: ask the Concierge, or install from a folder -
 * which is offered here as well, through the same wizard Admin -> Plugins opens.
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

// --- Install from a folder: the wizard, as Admin -> Plugins opens it ------------------------------

const pickerOpen = ref(false);
const wizard = ref<{ open: boolean; folder: string }>({ open: false, folder: '' });

function chose(folder: string) {
  wizard.value = { open: true, folder };
}

// A finished install adds a tile: read the list again when the wizard closes.
watch(() => wizard.value.open, (showing, was) => {
  if (was && !showing && open.value) void load();
});
</script>

<template>
  <q-dialog v-model="open">
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
          @click="pickerOpen = true"
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
            A solution is a whole team from one package: its members, schedules, skills and a site to use it from.
            There are two ways to get one:
          </p>
          <ul class="q-my-none">
            <li><strong>Ask the Concierge</strong> for one - it can build a package and hand you the link to install it.</li>
            <li><strong>Install from a folder</strong> that holds a <code>solution.json</code>, with the button above.</li>
          </ul>
        </div>

        <div v-else class="solutions-grid">
          <q-card
            v-for="row in rows"
            :key="row.team"
            flat
            bordered
            class="solution-tile column"
            :data-solution-tile="row.team"
          >
            <q-card-section class="q-pb-xs">
              <div class="row items-baseline no-wrap q-gutter-x-sm">
                <div class="text-subtitle1 text-weight-medium ellipsis" data-tile-name>{{ row.name }}</div>
                <div class="text-caption os-text-muted" data-tile-version>{{ row.version }}</div>
              </div>
              <div class="text-caption os-text-muted" data-tile-team>Team {{ row.teamName }}</div>
            </q-card-section>

            <q-card-section class="q-py-xs col">
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
              <div v-if="row.status" class="os-body q-mt-xs" data-tile-status>{{ row.status }}</div>
            </q-card-section>

            <q-card-actions align="right">
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
                unelevated
                dense
                no-caps
                color="primary"
                icon="tune"
                label="Manage"
                data-tile-manage
                @click="emit('manage', row.team)"
              />
            </q-card-actions>
          </q-card>
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>

  <HostPathPicker
    v-model="pickerOpen"
    instance-only
    title="Choose the solution folder"
    @chose="chose"
  />

  <SolutionWizard v-model="wizard.open" :folder="wizard.folder" />
</template>

<style scoped>
.solutions-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: 12px;
}

.solution-tile {
  min-height: 11rem;
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
</style>
