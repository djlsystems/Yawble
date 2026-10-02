<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { ActionRefused, getPluginManifest, installPlugin, listPlugins, removePlugin, rescanPlugins } from '../api/client';
import type { ContainerSnapshot, InstalledPlugin, PluginInstallResult, PluginList, PluginMemberRef } from '../api/types';
import { formatManifest, pluginEvents, pluginRows, pluginSkill, type PluginRow } from '../lib/plugins';
import { defaultLabel, setByPerson } from '../lib/pluginSettings';
import { slotSummary } from '../lib/connections';
import { useConsoleStore } from '../stores/console';
import HostPathPicker from './HostPathPicker.vue';
import MemberSettingsDialog from './MemberSettingsDialog.vue';
import SolutionWizard from './SolutionWizard.vue';
import { checkSolution } from '../api/client';
import { isNotAPackage } from '../lib/solutions';
import type { SolutionCheck } from '../api/types';

/**
 * ADMIN > PLUGINS: every plugin version under the plugins directory and what the Host made of it -
 * installed, or refused with the Host's own sentence - from `GET /api/plugins`.
 *
 * For an installed version: its description, its settings as the manifest declares them, its
 * secrets BY NAME (no route carries a value), its connection slots ("needs a Google or Microsoft
 * connection"), the events it publishes, its skill, and the members
 * hired on it - each a link to that member's settings, where a plugin member's settings are edited.
 *
 * Rescan re-reads the directory; View manifest shows `plugin.json` read-only; Install from a folder
 * installs a built plugin that is already inside the instance - chosen with the host folder picker,
 * offered the data root only, because the Host refuses a path anywhere else - and shows the Host's
 * verdict, which refuses an existing version unless Replace is ticked. Remove takes one version, or
 * the whole plugin, with `plugin remove`'s rules: it asks first, and the Host refuses the
 * whole plugin while members are hired on it (naming them) and the active version while others are
 * kept - its sentence is shown in the question. Every one of these is a person's: the routes that
 * change anything are humans-only.
 */
const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

const list = ref<PluginList | null>(null);
const rows = computed<PluginRow[]>(() => (list.value ? pluginRows(list.value) : []));

/** The filter's text; empty (or cleared, which is null) shows every version. */
const filterText = ref<string | null>('');

const shownRows = computed<PluginRow[]>(() => {
  const words = (filterText.value ?? '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return rows.value;

  return rows.value.filter((row) => {
    const description = row.verdict === 'installed' ? row.plugin.description ?? '' : '';
    const text = [row.name, row.id, row.version ?? '', description].join(' ').toLowerCase();
    return words.every((word) => text.includes(word));
  });
});

const loading = ref(false);
const error = ref('');

async function load() {
  loading.value = true;
  error.value = '';

  try {
    list.value = await listPlugins();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

watch(open, (showing) => {
  removed.value = '';
  if (showing) void load();
});

/** The installed version whose Details are open, or null. */
const detailsRow = ref<PluginRow | null>(null);

/** What an installed version declares, counted, for its tile: "3 settings · 1 secret · no events". */
function declaresSummary(plugin: InstalledPlugin) {
  const count = (n: number, one: string, many: string) => (n === 0 ? `no ${many}` : n === 1 ? `1 ${one}` : `${n} ${many}`);
  const parts = [
    count(Object.keys(plugin.config).length, 'setting', 'settings'),
    count(Object.keys(plugin.secrets).length, 'secret', 'secrets'),
    count(Object.keys(plugin.connections ?? {}).length, 'connection', 'connections'),
    count(pluginEvents(plugin).length, 'event', 'events'),
  ];
  if (pluginSkill(plugin)) parts.push('a skill');
  return parts.join(' · ');
}

/** The Host's verdict on a version, in words and never in colour alone. */
const verdictLabel = { installed: 'Installed', refused: 'Refused', inactive: 'Inactive' } as const;
const verdictColor = { installed: 'positive', refused: 'negative', inactive: 'grey-7' } as const;

// --- Rescan --------------------------------------------------------------------------------------

const rescanning = ref(false);

async function rescan() {
  rescanning.value = true;
  error.value = '';

  try {
    list.value = await rescanPlugins();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    rescanning.value = false;
  }
}

// --- View manifest -------------------------------------------------------------------------------

const manifestOpen = ref(false);
const manifestTitle = ref('');
const manifestText = ref('');
const manifestProblem = ref('');

async function viewManifest(id: string, version: string) {
  manifestTitle.value = `${id} ${version}`;
  manifestText.value = '';
  manifestProblem.value = '';
  manifestOpen.value = true;

  try {
    manifestText.value = formatManifest(await getPluginManifest(id, version));
  } catch (cause) {
    manifestProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

// --- Members -------------------------------------------------------------------------------------

/** The board's snapshot of a member hired on a plugin, which Member settings is opened with. */
function snapshotOf(member: PluginMemberRef): ContainerSnapshot | null {
  const team = board.teams.find((candidate) => candidate.id === member.team);
  return team?.containers.find((container) => container.id === member.member) ?? null;
}

function teamLabel(member: PluginMemberRef) {
  return board.teams.find((candidate) => candidate.id === member.team)?.name ?? member.team;
}

function memberLabel(member: PluginMemberRef) {
  return snapshotOf(member)?.name ?? member.member;
}

const settingsSnapshot = ref<ContainerSnapshot | null>(null);
const settingsOpen = ref(false);
const memberProblem = ref('');

function openMember(member: PluginMemberRef) {
  memberProblem.value = '';

  const snapshot = snapshotOf(member);
  if (!snapshot) {
    memberProblem.value = `${member.team} / ${member.member} is not on the board, so its settings cannot be opened here.`;
    return;
  }

  settingsSnapshot.value = snapshot;
  settingsOpen.value = true;
}

// --- Install from a folder -----------------------------------------------------------------------

const installOpen = ref(false);
const pickerOpen = ref(false);
const installPath = ref('');
const replace = ref(false);
const installing = ref(false);
const verdict = ref<PluginInstallResult | null>(null);

function startInstall() {
  installPath.value = '';
  replace.value = false;
  verdict.value = null;
  installOpen.value = true;
}

async function install() {
  const path = installPath.value.trim();
  if (!path || installing.value) return;

  installing.value = true;
  verdict.value = null;

  // A FOLDER HOLDING solution.json IS A SOLUTION PACKAGE: the wizard installs it instead. Only the
  // check's "no solution.json" refusal (or a refused folder) goes on to the plain plugin install.
  const solution = await checkSolution(path).catch(() => null);
  if (solution && (solution.ok || !isNotAPackage(solution.refusals))) {
    installing.value = false;
    installOpen.value = false;
    Object.assign(wizard.value, { open: true, folder: path, check: solution });
    return;
  }

  try {
    verdict.value = await installPlugin(path, replace.value);
  } catch (cause) {
    // A refusal is the Host's verdict too: its sentence names why, and nothing was written. A 409
    // is an existing version without Replace; the body names the id and version when it read them.
    const body = cause instanceof ActionRefused ? cause.body : {};
    verdict.value = {
      installed: false,
      id: typeof body.id === 'string' ? body.id : null,
      version: typeof body.version === 'string' ? body.version : null,
      replaced: false,
      reason: cause instanceof Error ? cause.message : String(cause),
    };
  } finally {
    installing.value = false;
  }

  // AFTER EVERY INSTALL, whatever the verdict: a 200 with `installed: false` copied the folder and
  // rescanned before the catalog refused it, so the list has a new row to show with its reason.
  await load();
}

const wizard = ref<{ open: boolean; folder: string; check: SolutionCheck | null }>({ open: false, folder: '', check: null });

// --- Remove --------------------------------------------------------------------------------------

/** How many version rows each plugin has: with one, removing it is removing the plugin. */
const versionCounts = computed(() => {
  const counts = new Map<string, number>();
  for (const row of rows.value) if (row.version) counts.set(row.id, (counts.get(row.id) ?? 0) + 1);
  return counts;
});

/** The first row of each plugin carries Remove plugin. */
function firstOfPlugin(row: PluginRow) {
  return rows.value.find((candidate) => candidate.id === row.id) === row;
}

/** What is about to be removed: a version, or the whole plugin when `version` is null. */
const removing = ref<{ id: string; name: string; version: string | null; versions: string[] } | null>(null);
const removeBusy = ref(false);
const removeRefusal = ref('');
const removed = ref('');

function askRemove(row: PluginRow, wholePlugin: boolean) {
  const versions = rows.value.filter((candidate) => candidate.id === row.id && candidate.version).map((candidate) => candidate.version!);
  removing.value = {
    id: row.id,
    name: row.name,
    // One version left is the whole plugin, as the Host reads it.
    version: wholePlugin || versions.length <= 1 ? null : row.version,
    versions,
  };
  removeRefusal.value = '';
}

const removeQuestion = computed(() => {
  const target = removing.value;
  if (!target) return '';

  return target.version
    ? `Remove version ${target.version} of ${target.name} (${target.id}) from the instance?`
    : `Remove the plugin ${target.name} (${target.id})${target.versions.length > 0 ? `, with ${target.versions.length === 1 ? 'its version' : 'all its versions'} ${target.versions.join(', ')},` : ''} from the instance?`;
});

async function confirmRemove() {
  const target = removing.value;
  if (!target || removeBusy.value) return;

  removeBusy.value = true;
  removeRefusal.value = '';

  try {
    const result = await removePlugin(target.id, target.version);
    removed.value = result.whole
      ? `Removed the plugin ${target.id}.`
      : `Removed version ${result.version} of ${target.id}.`;
    removing.value = null;
    await load();
  } catch (cause) {
    // The Host's sentence: in use by whom, or the active version. Nothing was removed.
    removeRefusal.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    removeBusy.value = false;
  }
}

const verdictText = computed(() => {
  const result = verdict.value;
  if (!result) return '';

  const what = [result.id, result.version].filter(Boolean).join(' ');

  return result.installed
    ? `${result.replaced ? 'Replaced' : 'Installed'}${what ? ` ${what}` : ''}. It is the active version.`
    : `Not installed${what ? ` (${what})` : ''}: ${result.reason ?? 'the Host gave no reason.'}`;
});
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="plugins-card os-dialog-xl" data-plugins-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Plugins</div>
        <q-space />
        <q-btn
          flat
          dense
          no-caps
          icon="refresh"
          label="Rescan"
          :loading="rescanning"
          @click="rescan"
        />
        <q-btn
          flat
          dense
          no-caps
          icon="drive_folder_upload"
          label="Install from a folder…"
          @click="startInstall"
        />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Every plugin version in the plugins folder, and what the Host made of it. Installing and
        changing plugins is a person's action; agents can only hire what is installed.
      </q-card-section>

      <!-- THE FILTER: free text over name, id, version and description. It narrows what is SHOWN
           only. -->
      <q-card-section class="q-pt-none q-pb-sm">
        <q-input
          v-model="filterText"
          dense
          outlined
          clearable
          class="plugin-filter-text"
          placeholder="Filter: name, id or description"
          aria-label="Filter plugins"
          data-plugin-filter
        >
          <template #prepend><q-icon name="search" /></template>
        </q-input>
      </q-card-section>

      <q-card-section class="q-pt-none plugins-body">
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>
        <q-banner v-if="memberProblem" dense class="os-bg-tint-error text-negative q-mb-md">
          {{ memberProblem }}
        </q-banner>
        <q-banner v-if="removed" dense class="os-bg-tint-ok text-positive q-mb-md" data-remove-done>
          <template #avatar><q-icon name="check_circle" /></template>
          {{ removed }}
        </q-banner>

        <div v-if="loading && !list" class="os-text-muted">Reading the plugins…</div>
        <div v-else-if="list && rows.length === 0" class="os-text-muted" data-no-plugins>
          No plugins are installed. Install one from a folder inside the instance.
        </div>

        <div v-else-if="list && shownRows.length === 0" class="os-text-muted q-pa-lg text-center" data-plugin-none-match>
          No plugin matches the filter.
        </div>

        <!-- ONE TILE PER VERSION, as the Agents screen shows its presets: the Host's verdict on
             the head, what an installed version declares in the body, the actions at the foot. -->
        <div v-if="shownRows.length > 0" class="os-tiles plugin-tiles">
          <div
            v-for="row in shownRows"
            :key="row.key"
            class="os-tile plugin-tile"
            :data-plugin="row.id"
            :data-plugin-version="row.version ?? ''"
            :data-verdict="row.verdict"
          >
            <div class="os-tile-head plugin-tile-head">
              <q-icon name="extension" size="18px" class="plugin-icon" aria-hidden="true" />
              <span class="text-weight-medium">{{ row.name }}</span>
              <span v-if="row.version" class="mono">{{ row.version }}</span>
              <q-badge v-if="row.active" outline color="primary" label="Active" data-active />
              <q-space />
              <q-badge :color="verdictColor[row.verdict]" :label="verdictLabel[row.verdict]" />
            </div>
            <div class="os-tile-line plugin-tile-line mono os-text-muted">{{ row.id }}</div>

            <div v-if="row.verdict === 'refused'" class="os-tile-line plugin-tile-line text-negative" data-reason>
              {{ row.reason ?? 'The Host gave no reason.' }}
            </div>

            <div v-else-if="row.verdict === 'inactive'" class="os-tile-line plugin-tile-line os-text-muted" data-reason>
              {{ row.reason ?? 'Kept on disk and not in use: another version of this plugin is active.' }}
            </div>

            <template v-else-if="row.verdict === 'installed'">
              <div v-if="row.plugin.description" class="os-tile-line plugin-tile-line plugin-description q-mt-xs" data-description>{{ row.plugin.description }}</div>

              <!-- WHAT IT DECLARES, COUNTED: the whole of it is in Details, where a plugin with
                   seventeen settings has the room they need. -->
              <div class="os-tile-line plugin-tile-line os-text-muted q-mt-xs" data-plugin-summary>{{ declaresSummary(row.plugin) }}</div>

              <!-- WHO IS HIRED ON IT, on the tile: each a link to that member's settings. -->
              <div class="os-tile-line plugin-tile-line" data-members-summary>
                <span class="os-text-muted">Members: </span>
                <template v-if="row.plugin.members.length === 0">None hired</template>
                <template v-for="(member, index) in row.plugin.members" :key="`${member.team}/${member.member}`">
                  <span v-if="index > 0">, </span>
                  <a
                    href="#"
                    class="plugin-member-link"
                    :data-member-link="`${member.team}/${member.member}`"
                    @click.prevent="openMember(member)"
                  >{{ teamLabel(member) }} / {{ memberLabel(member) }}</a>
                </template>
              </div>
            </template>

            <!-- ACTIONS, icons with their words in a tooltip and an aria-label, as on an Agent's
                 tile. Remove version only where the plugin has another; Remove plugin on its first
                 tile. -->
            <div class="plugin-tile-actions">
              <span v-if="row.verdict === 'installed'" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="visibility"
                  :aria-label="`Details ${row.id} ${row.version}`"
                  data-plugin-details
                  @click="detailsRow = row"
                />
                <q-tooltip>Details: its settings, secrets, connections, events, skill and members</q-tooltip>
              </span>
              <span v-if="row.version" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="description"
                  :aria-label="`View manifest ${row.id} ${row.version}`"
                  data-view-manifest
                  @click="viewManifest(row.id, row.version!)"
                />
                <q-tooltip>View manifest</q-tooltip>
              </span>
              <span v-if="row.version && (versionCounts.get(row.id) ?? 0) > 1" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  color="negative"
                  icon="delete"
                  :aria-label="`Remove version ${row.version} of ${row.id}`"
                  data-remove-version
                  @click="askRemove(row, false)"
                />
                <q-tooltip>Remove this version</q-tooltip>
              </span>
              <span v-if="firstOfPlugin(row)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  color="negative"
                  icon="delete_forever"
                  :aria-label="`Remove plugin ${row.id}`"
                  data-remove-plugin
                  @click="askRemove(row, true)"
                />
                <q-tooltip>Remove the plugin, every version</q-tooltip>
              </span>
            </div>
          </div>
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- REMOVE: asked first. A refusal is the Host's sentence - who is hired on it, or that it is the
       active version - and nothing was removed. -->
  <q-dialog :model-value="removing !== null" @update:model-value="removing = null">
    <q-card class="os-dialog-sm" data-remove-dialog>
      <q-card-section class="os-dialog-title">{{ removing?.version ? 'Remove version' : 'Remove plugin' }}</q-card-section>
      <q-card-section class="q-pt-none">
        <p data-remove-question>{{ removeQuestion }}</p>
        <p class="os-body os-text-muted">
          {{ removing?.version
            ? 'Its files are deleted. The active version is not changed.'
            : 'Its files are deleted and it can no longer be hired. The Host refuses while any member is hired on it.' }}
        </p>
        <q-banner v-if="removeRefusal" dense class="os-bg-tint-error text-negative" data-remove-refusal>
          <template #avatar><q-icon name="error" /></template>
          {{ removeRefusal }}
        </q-banner>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="removeBusy" @click="removing = null" />
        <q-btn
          color="negative"
          unelevated
          no-caps
          label="Remove"
          data-remove-confirm
          :loading="removeBusy"
          :disable="removeBusy"
          @click="confirmRemove"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- DETAILS: everything an installed version declares, read-only. -->
  <q-dialog :model-value="detailsRow !== null" @update:model-value="(showing: boolean) => { if (!showing) detailsRow = null; }">
    <q-card v-if="detailsRow && detailsRow.verdict === 'installed'" class="os-dialog-md" data-plugin-details-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div>
          <div class="os-dialog-title">{{ detailsRow.name }}</div>
          <div class="text-caption os-text-muted mono">{{ detailsRow.id }} {{ detailsRow.version }}</div>
        </div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section class="plugin-details-body">
        <div v-if="detailsRow.plugin.description" class="q-mb-sm">{{ detailsRow.plugin.description }}</div>
        <dl class="plugin-facts">
          <dt>Settings</dt>
          <dd data-config>
            <template v-if="Object.keys(detailsRow.plugin.config).length === 0">None</template>
            <div v-for="(field, key) in detailsRow.plugin.config" :key="key" :data-config-field="key">
              <span class="mono">{{ key }}</span>
              <span class="os-text-muted"> · {{ field.type }}</span>
              <span v-if="defaultLabel(field)" class="os-text-muted"> · {{ defaultLabel(field) }}</span>
              <span v-if="field.required" class="os-text-muted"> · Required</span>
              <span v-if="setByPerson(field)" class="os-text-muted"> · Set by a person only</span>
            </div>
          </dd>

          <dt>Secrets</dt>
          <dd data-secrets>
            <template v-if="Object.keys(detailsRow.plugin.secrets).length === 0">None</template>
            <span v-for="(secret, key) in detailsRow.plugin.secrets" :key="key" class="mono q-mr-sm">{{ key }}</span>
          </dd>

          <dt>Connections</dt>
          <dd data-connections>
            <template v-if="Object.keys(detailsRow.plugin.connections ?? {}).length === 0">None</template>
            <div v-for="(wanted, key) in detailsRow.plugin.connections ?? {}" :key="key" :data-connection-slot="key">
              <span class="mono">{{ key }}</span>
              <span class="os-text-muted"> · {{ slotSummary(wanted) }}</span>
              <span v-if="wanted.required" class="os-text-muted"> · Required</span>
              <span v-if="wanted.description" class="os-text-muted"> · {{ wanted.description }}</span>
            </div>
          </dd>

          <dt>Events</dt>
          <dd data-events>
            <template v-if="pluginEvents(detailsRow.plugin).length === 0">None</template>
            <div v-for="event in pluginEvents(detailsRow.plugin)" :key="event.type">
              <span class="mono">{{ event.type }}</span>
              <span v-if="event.summary" class="os-text-muted"> · {{ event.summary }}</span>
            </div>
          </dd>

          <dt>Skill</dt>
          <dd data-skill>
            <span v-if="pluginSkill(detailsRow.plugin)" class="mono">{{ pluginSkill(detailsRow.plugin) }}</span>
            <template v-else>None</template>
          </dd>

          <dt>Members</dt>
          <dd data-members>
            <template v-if="detailsRow.plugin.members.length === 0">None hired</template>
            <div v-for="member in detailsRow.plugin.members" :key="`${member.team}/${member.member}`">
              <a
                href="#"
                class="plugin-member-link"
                :data-member-link="`${member.team}/${member.member}`"
                @click.prevent="openMember(member)"
              >{{ teamLabel(member) }} / {{ memberLabel(member) }}</a>
            </div>
          </dd>
        </dl>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- VIEW MANIFEST: read-only, formatted. -->
  <q-dialog v-model="manifestOpen">
    <q-card class="os-dialog-lg" data-manifest-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div>
          <div class="os-dialog-title">plugin.json</div>
          <div class="text-caption os-text-muted mono">{{ manifestTitle }}</div>
        </div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section>
        <div v-if="manifestProblem" class="text-negative">{{ manifestProblem }}</div>
        <pre v-else class="plugin-manifest mono" data-manifest>{{ manifestText }}</pre>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- INSTALL FROM A FOLDER: a built plugin already inside the instance. The Host's verdict is shown
       as it gave it; a refusal names why, and nothing was written. -->
  <q-dialog v-model="installOpen">
    <q-card class="os-dialog-md" data-install-dialog>
      <q-card-section>
        <div class="os-dialog-title">Install from a folder</div>
        <div class="text-caption os-text-muted">
          A built plugin folder inside this instance's data root, holding its plugin.json. It is
          installed as the active version of its plugin.
        </div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-input
          v-model="installPath"
          outlined
          dense
          label="Folder"
          spellcheck="false"
          autocomplete="off"
        >
          <template #after>
            <q-btn flat dense no-caps icon="folder_open" label="Browse…" @click="pickerOpen = true" />
          </template>
        </q-input>

        <q-checkbox
          v-model="replace"
          dense
          label="Replace"
        />
        <div class="text-caption os-text-muted">
          Tick to install over a version that is already installed. Without it, the Host refuses.
        </div>

        <q-banner
          v-if="verdict"
          dense
          :class="verdict.installed ? 'os-bg-tint-ok text-positive' : 'os-bg-tint-error text-negative'"
          data-install-verdict
        >
          <template #avatar><q-icon :name="verdict.installed ? 'check_circle' : 'error'" /></template>
          {{ verdictText }}
        </q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Close" :disable="installing" />
        <q-btn
          color="primary"
          no-caps
          label="Install"
          :loading="installing"
          :disable="installing || installPath.trim() === ''"
          @click="install"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <HostPathPicker
    v-model="pickerOpen"
    instance-only
    title="Choose the plugin folder"
    @chose="installPath = $event"
  />

  <SolutionWizard v-model="wizard.open" :folder="wizard.folder" :check="wizard.check" @opened="open = false" />

  <MemberSettingsDialog
    v-if="settingsSnapshot"
    v-model="settingsOpen"
    :snapshot="settingsSnapshot"
  />
</template>

<style scoped>
.plugins-body {
  max-height: min(64vh, 44rem);
  overflow-y: auto;
}

/* WRAPPED TILES, as the Agents screen lays out its presets: as many columns as fit. */
/* The grid itself is `os-tiles` (css/tiles.scss); this only says how narrow a column may get. */
.plugin-tiles {
  --os-tile-min: 22rem;
}

.plugin-icon {
  color: var(--os-plugin-accent);
}

/* Three lines of the description on the tile; the whole of it is in Details. */
.plugin-description {
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.plugin-details-body {
  max-height: 70vh;
  overflow-y: auto;
}

.plugin-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 2px;
}

.plugin-filter-text {
  max-width: 28rem;
}

/* The tooltip lives on this WRAPPER because a disabled q-btn swallows pointer events. */
.row-btn-wrap {
  display: inline-flex;
}

.plugin-facts {
  font-size: 12px;
  line-height: 1.45;
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 16px;
  row-gap: 4px;
  margin: 0;
}

.plugin-facts dt {
  color: var(--os-ink-muted);
  font-weight: 500;
}

.plugin-facts dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
}

.plugin-manifest {
  margin: 0;
  max-height: 60vh;
  overflow: auto;
  font-size: 12px;
  white-space: pre;
}
</style>
