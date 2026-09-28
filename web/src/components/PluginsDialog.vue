<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { ActionRefused, getPluginManifest, installPlugin, listPlugins, rescanPlugins } from '../api/client';
import type { ContainerSnapshot, PluginInstallResult, PluginList, PluginMemberRef } from '../api/types';
import { formatManifest, pluginEvents, pluginRows, pluginSkill, type PluginRow } from '../lib/plugins';
import { defaultLabel, setByPerson } from '../lib/pluginSettings';
import { useConsoleStore } from '../stores/console';
import HostPathPicker from './HostPathPicker.vue';
import MemberSettingsDialog from './MemberSettingsDialog.vue';

/**
 * ADMIN > PLUGINS: every plugin version under the plugins directory and what the Host made of it -
 * installed, or refused with the Host's own sentence - from `GET /api/plugins`.
 *
 * For an installed version: its description, its settings as the manifest declares them, its
 * secrets BY NAME (no route carries a value), the events it publishes, its skill, and the members
 * hired on it - each a link to that member's settings, where a plugin member's settings are edited.
 *
 * Rescan re-reads the directory; View manifest shows `plugin.json` read-only; Install from a folder
 * installs a built plugin that is already inside the instance - chosen with the host folder picker,
 * offered the data root only, because the Host refuses a path anywhere else - and shows the Host's
 * verdict, which refuses an existing version unless Replace is ticked. Every one of these is a
 * person's: the routes that change anything are humans-only.
 */
const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

const list = ref<PluginList | null>(null);
const rows = computed<PluginRow[]>(() => (list.value ? pluginRows(list.value) : []));

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
  if (showing) void load();
});

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
      reason: cause instanceof Error ? cause.message : String(cause),
    };
  } finally {
    installing.value = false;
  }

  // AFTER EVERY INSTALL, whatever the verdict: a 200 with `installed: false` copied the folder and
  // rescanned before the catalog refused it, so the list has a new row to show with its reason.
  await load();
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
    <q-card class="plugins-card os-dialog-lg" data-plugins-dialog>
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

      <q-card-section class="q-pt-none plugins-body">
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>
        <q-banner v-if="memberProblem" dense class="os-bg-tint-error text-negative q-mb-md">
          {{ memberProblem }}
        </q-banner>

        <div v-if="loading && !list" class="os-text-muted">Reading the plugins…</div>
        <div v-else-if="list && rows.length === 0" class="os-text-muted" data-no-plugins>
          No plugins are installed. Install one from a folder inside the instance.
        </div>

        <q-card
          v-for="row in rows"
          :key="row.key"
          flat
          bordered
          class="plugin-row q-mb-sm"
          :data-plugin="row.id"
          :data-plugin-version="row.version ?? ''"
          :data-verdict="row.verdict"
        >
          <q-card-section class="row items-center q-gutter-x-sm q-py-sm">
            <span class="text-weight-medium">{{ row.name }}</span>
            <span class="mono os-text-muted">{{ row.id }}</span>
            <span v-if="row.version" class="mono">{{ row.version }}</span>
            <q-badge v-if="row.active" outline color="primary" label="Active" data-active />
            <q-space />
            <q-badge :color="verdictColor[row.verdict]" :label="verdictLabel[row.verdict]" />
            <q-btn
              v-if="row.version"
              flat
              dense
              no-caps
              size="sm"
              icon="description"
              label="View manifest"
              @click="viewManifest(row.id, row.version!)"
            />
          </q-card-section>

          <q-card-section v-if="row.verdict === 'refused'" class="q-pt-none text-negative" data-reason>
            {{ row.reason ?? 'The Host gave no reason.' }}
          </q-card-section>

          <q-card-section v-else-if="row.verdict === 'inactive'" class="q-pt-none os-text-muted" data-reason>
            {{ row.reason ?? 'Kept on disk and not in use: another version of this plugin is active.' }}
          </q-card-section>

          <q-card-section v-else-if="row.verdict === 'installed'" class="q-pt-none plugin-detail">
            <div v-if="row.plugin.description" class="q-mb-sm" data-description>{{ row.plugin.description }}</div>

            <dl class="plugin-facts">
              <dt>Settings</dt>
              <dd data-config>
                <template v-if="Object.keys(row.plugin.config).length === 0">None</template>
                <div v-for="(field, key) in row.plugin.config" :key="key" :data-config-field="key">
                  <span class="mono">{{ key }}</span>
                  <span class="os-text-muted"> · {{ field.type }}</span>
                  <span v-if="defaultLabel(field)" class="os-text-muted"> · {{ defaultLabel(field) }}</span>
                  <span v-if="field.required" class="os-text-muted"> · Required</span>
                  <span v-if="setByPerson(field)" class="os-text-muted"> · Set by a person only</span>
                </div>
              </dd>

              <dt>Secrets</dt>
              <dd data-secrets>
                <template v-if="Object.keys(row.plugin.secrets).length === 0">None</template>
                <span v-for="(secret, key) in row.plugin.secrets" :key="key" class="mono q-mr-sm">{{ key }}</span>
              </dd>

              <dt>Events</dt>
              <dd data-events>
                <template v-if="pluginEvents(row.plugin).length === 0">None</template>
                <div v-for="event in pluginEvents(row.plugin)" :key="event.type">
                  <span class="mono">{{ event.type }}</span>
                  <span v-if="event.summary" class="os-text-muted"> · {{ event.summary }}</span>
                </div>
              </dd>

              <dt>Skill</dt>
              <dd data-skill>
                <span v-if="pluginSkill(row.plugin)" class="mono">{{ pluginSkill(row.plugin) }}</span>
                <template v-else>None</template>
              </dd>

              <dt>Members</dt>
              <dd data-members>
                <template v-if="!row.plugin.members || row.plugin.members.length === 0">None hired</template>
                <div v-for="member in row.plugin.members ?? []" :key="`${member.team}/${member.member}`">
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

  <MemberSettingsDialog
    v-if="settingsSnapshot"
    v-model="settingsOpen"
    :snapshot="settingsSnapshot"
  />
</template>

<style scoped>
.plugins-body {
  max-height: 70vh;
  overflow-y: auto;
}

.plugin-facts {
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
}

.plugin-manifest {
  margin: 0;
  max-height: 60vh;
  overflow: auto;
  font-size: 12px;
  white-space: pre;
}
</style>
