<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import {
  getPluginSettings,
  listConnectionProviders,
  listConnections,
  pauseTeam,
  readRunTranscript,
  resumeTeam,
  runScheduleNow,
  savePluginSettings,
  solutionPanel,
  teamSolution,
  uninstallSolution,
  updateSchedule,
} from '../api/client';
import { uploadDocument } from '../api/documents';
import {
  asDocumentsFolderKey,
  asTeamId,
  type Connection,
  type ConnectionProvider,
  type SolutionPanel,
  type MemberId,
  type SolutionPanelBlocked,
  type SolutionPanelRun,
  type SolutionPanelTrigger,
  type SolutionUninstallResult,
} from '../api/types';
import {
  initialConfig,
  initialSecrets,
  outOfRange,
  refusedOutOfRange,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
} from '../lib/pluginSettings';
import { capWords, missingLine } from '../lib/solutions';
import { localInstants } from '../lib/localTime';
import {
  cappedWords,
  hasNewRun,
  instructionFirstLine,
  memberStateLine,
  nextFireLine,
  parseCap,
  RunWatchEveryMs,
  RunWatchSlowEveryMs,
  RunWatchTries,
  runKeys,
  sizeWords,
  stateBadge,
  triggerFacts,
  whenWords,
} from '../lib/solutionPanel';
import { spentTodayLine } from '../lib/triggers';
import ConnectionPicker from './ConnectionPicker.vue';
import { bindSlot } from '../lib/slotBinding';
import { guidedProviders } from '../lib/connections';
import InstallFromFolderDialog from './InstallFromFolderDialog.vue';
import PluginSettingsForm from './PluginSettingsForm.vue';
import SolutionWizard from './SolutionWizard.vue';
import DialogTabs from './DialogTabs.vue';

/**
 * ONE SOLUTION'S CONTROL PANEL, built from its `solution.json` and the team's live state
 * (`GET /api/teams/{team}/solution/panel`). THE PLATFORM'S OWN SCREEN, NOT A SITE: its controls act
 * with the person's authority, and a site is deliberately sandboxed without it.
 *
 * Four sections:
 * - **Status**: each member's state and last run, when each schedule next fires, the package's sites
 *   with Open, what the team is
 *   blocked on with the fix inline (an upload box for a missing document, a picker for a missing
 *   connection), and today's MEASURED spend against each trigger's cap - runs that reported nothing
 *   are counted as such, never estimated.
 * - **Controls**: pause and resume, Run now per schedule, each trigger's on/off and daily cap - and
 *   its whole instruction in Details - the
 *   settings the package lists first, "All settings" for the rest (person-only included), and the
 *   connection bindings.
 * - **Results**: the package's output folders, newest first, with downloads; recent runs and their output.
 * - **Maintenance**: version and source folder, Update from a folder (the install dialog - a typed,
 *   picked or uploaded folder - then the wizard's update path),
 *   and Uninstall, which asks first.
 *
 * Members, triggers and sites are tiles in the shared grid (`os-tiles` / `os-tile`,
 * css/tiles.scss), as Plugins and Agents lay theirs out; the forms stay forms.
 *
 * EVERY CONTROL IS AN EXISTING ROUTE (pause, triggers, plugin settings, documents upload, the wizard):
 * this screen adds none of its own but the uninstall. And every string from the package or its site
 * data is interpolated as TEXT - nothing here is bound as HTML.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{ team: string }>();

const emit = defineEmits<{ launcher: []; uninstalled: [team: string] }>();

type Section = 'status' | 'controls' | 'results' | 'maintenance';

/** The trigger whose Details are open, or null. */
const detailsTrigger = ref<SolutionPanelTrigger | null>(null);
const section = ref<Section>('status');

const panel = ref<SolutionPanel | null>(null);
const loading = ref(false);
/** The read failed: the Host's sentence (a 404 names a team not installed from a package). */
const error = ref('');
/** The last control's refusal, or what Run now said. */
const problem = ref('');
const notice = ref('');
const busy = ref('');

async function load() {
  const team = props.team;
  loading.value = true;
  error.value = '';
  try {
    const read = await solutionPanel(team);
    if (team !== props.team) return;
    panel.value = read;
    await loadSettings(read);
  } catch (cause) {
    if (team === props.team) error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

function reset() {
  stopWatchingRun();
  detailsTrigger.value = null;
  section.value = 'status';
  panel.value = null;
  problem.value = '';
  notice.value = '';
  settings.value = {};
  caps.value = {};
  capSaved.value = {};
  transcripts.value = {};
  uninstall.value = { asking: false, plugins: [], removePlugins: false, busy: false, result: null, problem: '' };
}


/** Runs a control, says its refusal, and reads the panel again so what it changed shows. */
async function act(key: string, action: () => Promise<unknown>) {
  busy.value = key;
  problem.value = '';
  notice.value = '';
  try {
    await action();
    await load();
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = '';
  }
}

const badge = computed(() => stateBadge(panel.value?.state));
const memberName = (member: string) =>
  panel.value?.members.find((candidate) => candidate.member === member)?.packageName ?? member;

// --- Status: fixes inline ------------------------------------------------------------------------

const uploading = ref('');

async function upload(item: SolutionPanelBlocked, file: File | null) {
  const folder = item.fix?.upload?.folder ?? item.name.replace(/\/$/, '');
  if (!file) return;
  uploading.value = item.name;
  await act(`upload:${item.name}`, () => uploadDocument(asDocumentsFolderKey(props.team), file, folder));
  if (problem.value) problem.value = `${file.name} was not uploaded: ${problem.value}`;
  uploading.value = '';
}

/** Binds one slot and keeps every other setting the member has: the settings route replaces the whole body. */
async function bind(item: SolutionPanelBlocked, connection: string) {
  const fix = item.fix?.connection;
  const state = fix ? settings.value[fix.member] : undefined;
  if (!fix || !state) return;
  state.connections = { ...state.connections, [fix.slot]: connection };
  await saveSettings(fix.member);
}

/**
 * A sign-in from a slot completed: the new connection is bound through the member's settings route,
 * with its STORED settings. A refusal is thrown for the slot to show. The panel is read again once
 * the dialog closes: read now, the fix the dialog was opened from would leave with it.
 */
async function bindConnected(member: string, slot: string, connection: string) {
  await bindSlot(props.team, member, slot, connection);
  const state = settings.value[member];
  if (state) state.connections = { ...state.connections, [slot]: connection };
}

/** Add connection from a slot was closed: the panel is read again, so a slot bound there is no longer listed as missing. */
async function rereadPanel() {
  try {
    panel.value = { ...(await solutionPanel(props.team)) };
  } catch {
    // The next read shows it.
  }
}

/** The connections and providers again, after a sign-in from a slot made one. */
async function reloadConnections() {
  try {
    [available.value, providers.value] = await Promise.all([listConnections(), listConnectionProviders()]);
  } catch {
    // The pickers keep what they had.
  }
}

// --- Controls: triggers --------------------------------------------------------------------------

const caps = ref<Record<string, string>>({});
/** The cap each trigger was last saved at, by trigger id, for its "Saved" line; gone once edited again. */
const capSaved = ref<Record<string, number | null>>({});

function capText(trigger: SolutionPanelTrigger): string {
  return caps.value[trigger.id] ?? (trigger.dailyTokenCap === null ? '' : String(trigger.dailyTokenCap));
}

const capProblem = (trigger: SolutionPanelTrigger) =>
  parseCap(capText(trigger)) === undefined ? 'A whole number of tokens, or empty for no cap.' : '';

const capChanged = (trigger: SolutionPanelTrigger) => {
  const cap = parseCap(capText(trigger));
  return cap !== undefined && cap !== trigger.dailyTokenCap;
};

function saveCap(trigger: SolutionPanelTrigger) {
  const cap = parseCap(capText(trigger));
  if (cap === undefined) return;
  void act(`cap:${trigger.id}`, async () => {
    await updateSchedule(asTeamId(props.team), trigger.id, { dailyTokenCap: cap });
    const { [trigger.id]: _, ...rest } = caps.value;
    caps.value = rest;
    capSaved.value = { ...capSaved.value, [trigger.id]: cap };
  });
}

function editCap(trigger: SolutionPanelTrigger, value: string | number | null) {
  caps.value = { ...caps.value, [trigger.id]: value === null ? '' : String(value) };
  const { [trigger.id]: _, ...rest } = capSaved.value;
  capSaved.value = rest;
}

function setEnabled(trigger: SolutionPanelTrigger, enabled: boolean) {
  void act(`enabled:${trigger.id}`, () => updateSchedule(asTeamId(props.team), trigger.id, { enabled }));
}

async function runNow(trigger: SolutionPanelTrigger) {
  let said = '';
  let fired = false;
  const seen = runKeys(panel.value?.recentRuns ?? []);
  await act(`run:${trigger.id}`, async () => {
    const run = await runScheduleNow(asTeamId(props.team), trigger.id);
    fired = run.outcome === 'fired';
    if (fired) said = `${trigger.packageName} is running now.`;
    else {
      const why = run.outcome === 'member-missing' ? 'its member is gone' : (run.reason ?? run.outcome);
      said = `${trigger.packageName} was skipped: ${why}.`;
    }
  });
  if (said) notice.value = said;
  if (fired && !problem.value) watchForRun(seen, said);
}

// The run route answers once the run is queued, not when it ends: read the panel again on a short
// timer until a run it had not seen shows, so Recent runs, each member's last run and the status
// line catch up without Refresh. Only the panel is re-read - settings being edited are kept.
// The run showing there means it has ended, so its "is running now" line goes with it. Past the
// short tries a long run is still watched, more slowly, while that line is up; once another
// notice replaces it there is nothing left to correct and the watch stops.
let runWatch: ReturnType<typeof setTimeout> | undefined;

function stopWatchingRun() {
  if (runWatch !== undefined) clearTimeout(runWatch);
  runWatch = undefined;
}

function watchForRun(seen: ReadonlySet<string>, running: string, triesLeft = RunWatchTries) {
  stopWatchingRun();
  const team = props.team;
  runWatch = setTimeout(async () => {
    runWatch = undefined;
    let read: SolutionPanel | null = null;
    try {
      read = await solutionPanel(team);
    } catch {
      // A failed read is one try used; the next may answer.
    }
    if (team !== props.team || !open.value) return;
    if (read) {
      panel.value = read;
      if (hasNewRun(seen, read.recentRuns)) {
        if (notice.value === running) notice.value = '';
        return;
      }
    }
    if (triesLeft > 1 || notice.value === running) watchForRun(seen, running, triesLeft - 1);
  }, triesLeft > 0 ? RunWatchEveryMs : RunWatchSlowEveryMs);
}

onBeforeUnmount(stopWatchingRun);

function togglePaused() {
  const team = asTeamId(props.team);
  void act('pause', () => (panel.value?.paused ? resumeTeam(team) : pauseTeam(team)));
}

// --- Controls: plugin settings -------------------------------------------------------------------

interface MemberSettings {
  shape: PluginSettingsShape;
  config: PluginFieldValues;
  secrets: Record<string, string>;
  connections: Record<string, string>;
  /** The plugin it runs, for a slot's Connect; empty when its settings could not be read. */
  plugin: string;
  problem: string;
  saved: boolean;
}

/** Each plugin member's settings, by stored member name: one state that the listed settings, All
 *  settings and Connections all edit, so saving one never writes back another's stale copy. */
const settings = ref<Record<string, MemberSettings>>({});
const available = ref<Connection[]>([]);
const providers = ref<ConnectionProvider[]>([]);

async function loadSettings(read: SolutionPanel) {
  const plugins = read.members.filter((member) => member.kind === 'plugin');
  const next: Record<string, MemberSettings> = {};
  await Promise.all(
    plugins.map(async ({ member }) => {
      try {
        const stored = await getPluginSettings(read.team, member);
        const shape: PluginSettingsShape = {
          config: stored.fields,
          secrets: stored.secretFields,
          connections: stored.connectionFields ?? {},
        };
        const config = initialConfig(shape, stored.config);
        next[member] = {
          shape: { ...shape, stored: { config, outOfRange: stored.outOfRange ?? {} } },
          config,
          secrets: initialSecrets(shape, stored.secrets),
          connections: { ...(stored.connections ?? {}) },
          plugin: stored.plugin,
          problem: '',
          saved: false,
        };
      } catch (cause) {
        next[member] = {
          shape: { config: {}, secrets: {}, connections: {} },
          config: {},
          secrets: {},
          connections: {},
          plugin: '',
          problem: `The settings could not be read: ${cause instanceof Error ? cause.message : String(cause)}`,
          saved: false,
        };
      }
    }),
  );
  settings.value = next;

  const needsConnections =
    Object.values(next).some((state) => Object.keys(state.shape.connections ?? {}).length > 0) ||
    read.blocked.some((item) => item.fix?.connection);
  if (needsConnections && available.value.length === 0) {
    try {
      [available.value, providers.value] = await Promise.all([listConnections(), listConnectionProviders()]);
    } catch {
      // The pickers say there is nothing to choose; the forms say the rest.
    }
  }
}

/** The package's listed settings, grouped by member, each group a shape holding only those fields. */
const listed = computed(() => {
  const groups: { member: string; packageMember: string; shape: PluginSettingsShape }[] = [];
  for (const entry of panel.value?.settings ?? []) {
    const state = settings.value[entry.member];
    const field = state?.shape.config[entry.setting];
    if (!state || !field) continue;
    let group = groups.find((candidate) => candidate.member === entry.member);
    if (!group) {
      group = { member: entry.member, packageMember: entry.packageMember, shape: { config: {}, secrets: {}, connections: {} } };
      groups.push(group);
    }
    group.shape.config[entry.setting] = field;
  }
  return groups;
});

const pluginMembers = computed(() => (panel.value?.members ?? []).filter((member) => member.kind === 'plugin'));

const slotShape = (member: string): PluginSettingsShape => ({
  config: {},
  secrets: {},
  connections: settings.value[member]?.shape.connections ?? {},
});

const withSlots = computed(() =>
  pluginMembers.value.filter((member) => Object.keys(settings.value[member.member]?.shape.connections ?? {}).length > 0),
);

async function saveSettings(member: string) {
  const state = settings.value[member];
  if (!state) return;
  state.saved = false;
  state.problem = '';
  // Held before sending, as the dialogs hold it: the field shows the sentence, and so does the Save.
  // A stored out-of-range value left as it was is not held: the Host keeps it and saves the rest.
  const shown = outOfRange(state.shape, state.config);
  const bounds = refusedOutOfRange(state.shape, state.config).map((name) => shown[name]);
  if (bounds.length > 0) {
    state.problem = bounds.join(' ');
    return;
  }
  busy.value = `settings:${member}`;
  try {
    const answer = await savePluginSettings(props.team, member, settingsBody(state.shape, state.config, state.secrets, state.connections));
    state.saved = true;
    if (answer) state.shape = { ...state.shape, stored: { config: { ...state.config }, outOfRange: answer.outOfRange ?? {} } };
    const read = await solutionPanel(props.team);
    panel.value = { ...read };
  } catch (cause) {
    state.problem = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = '';
  }
}

// --- Results: an agent member's transcript, read when asked ---------------------------------------

const transcripts = ref<Record<string, string>>({});
const runKey = (run: SolutionPanelRun) => `${run.member}/${run.seq}`;

async function toggleTranscript(run: SolutionPanelRun) {
  const key = runKey(run);
  if (transcripts.value[key]) {
    const { [key]: _, ...rest } = transcripts.value;
    transcripts.value = rest;
    return;
  }
  busy.value = `transcript:${key}`;
  problem.value = '';
  try {
    const lines = await readRunTranscript(asTeamId(props.team), run.member as MemberId, run.seq);
    transcripts.value = { ...transcripts.value, [key]: lines.join('\n') || '(empty)' };
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = '';
  }
}

// --- Maintenance ---------------------------------------------------------------------------------

const updateOpen = ref(false);
const wizard = ref<{ open: boolean; folder: string }>({ open: false, folder: '' });

watch(() => wizard.value.open, (showing, was) => {
  if (was && !showing && open.value) void load();
});

function update(folder: string) {
  updateOpen.value = false;
  wizard.value = { open: true, folder };
}

const uninstall = ref<{
  asking: boolean;
  plugins: string[];
  removePlugins: boolean;
  busy: boolean;
  result: SolutionUninstallResult | null;
  problem: string;
}>({ asking: false, plugins: [], removePlugins: false, busy: false, result: null, problem: '' });

/** ASKS FIRST, listing what goes and what stays. The plugins are offered only when the package has any. */
/** The package's sites the uninstall takes offline and keeps, by name. */
const uninstallSites = computed(() => {
  const current = panel.value;
  if (!current) return [];
  const named = (current.sites ?? []).map((site) => site.name);
  if (named.length > 0) return named;
  return current.primarySite ? [current.primarySite.name] : [];
});

async function askUninstall() {
  uninstall.value = { asking: true, plugins: [], removePlugins: false, busy: false, result: null, problem: '' };
  try {
    uninstall.value.plugins = (await teamSolution(props.team))?.plugins ?? [];
  } catch {
    // No plugins to offer; the uninstall itself still works.
  }
}

async function confirmUninstall() {
  uninstall.value.busy = true;
  uninstall.value.problem = '';
  try {
    uninstall.value.result = await uninstallSolution(props.team, uninstall.value.removePlugins);
    emit('uninstalled', props.team);
  } catch (cause) {
    uninstall.value.problem = cause instanceof Error ? cause.message : String(cause);
  } finally {
    uninstall.value.busy = false;
  }
}

// LAST, because it runs at once when the panel is mounted already open (an address landing on it),
// and `reset` touches every piece of state declared above.
watch(
  () => [open.value, props.team] as const,
  ([showing]) => {
    if (!showing) {
      stopWatchingRun();
      return;
    }
    reset();
    void load();
  },
  { immediate: true },
);

function closeUninstall() {
  const done = uninstall.value.result !== null;
  uninstall.value.asking = false;
  if (done) emit('launcher');
}
</script>

<template>
  <q-dialog v-model="open" no-route-dismiss>
    <q-card class="os-dialog-xl solution-panel" data-solution-panel>
      <q-card-section class="row items-center q-pb-none no-wrap">
        <q-btn flat dense round icon="arrow_back" aria-label="All solutions" data-panel-back @click="emit('launcher')">
          <q-tooltip>All solutions</q-tooltip>
        </q-btn>
        <div class="os-dialog-title ellipsis q-ml-xs" data-panel-title>
          <template v-if="panel">{{ panel.name }} <span class="os-text-muted text-body2">{{ panel.version }}</span></template>
          <template v-else>Solution</template>
        </div>
        <q-space />
        <q-btn
          v-if="panel?.primarySite"
          flat
          dense
          no-caps
          icon="open_in_new"
          label="Open"
          :href="panel.primarySite.published ? panel.primarySite.url : undefined"
          target="_blank"
          rel="noopener"
          :disable="!panel.primarySite.published"
          data-panel-open
        />
        <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section v-if="error" class="os-body text-negative" data-panel-problem>
        Could not read this solution: {{ error }}
      </q-card-section>

      <template v-else-if="panel">
        <q-card-section class="q-pb-sm">
          <div class="row items-center q-gutter-sm">
            <q-badge
              v-if="badge"
              :color="badge.color"
              :text-color="badge.textColor"
              class="solution-badge"
              :data-panel-state="panel.state.kind"
            >
              <q-icon :name="badge.icon" size="14px" class="q-mr-xs" />{{ badge.text }}
            </q-badge>
            <span class="os-body" data-panel-status>{{ localInstants(panel.status) }}</span>
          </div>
          <div class="text-caption os-text-muted q-mt-xs">Team {{ panel.teamName }}</div>
          <div v-if="panel.description" class="os-body q-mt-xs" data-panel-description>{{ panel.description }}</div>
        </q-card-section>

        <DialogTabs v-model="section" class="q-px-md">
          <q-tab name="status" label="Status" data-section-tab="status" />
          <q-tab name="controls" label="Controls" data-section-tab="controls" />
          <q-tab name="results" label="Results" data-section-tab="results" />
          <q-tab name="maintenance" label="Maintenance" data-section-tab="maintenance" />
        </DialogTabs>
        <q-separator />

        <q-card-section v-if="problem" class="os-body text-negative q-pb-none" data-control-problem>{{ problem }}</q-card-section>
        <q-card-section v-if="notice" class="os-body q-pb-none" data-control-notice>{{ notice }}</q-card-section>

        <!-- STATUS -->
        <q-card-section v-if="section === 'status'" data-section="status">
          <div v-if="panel.blocked.length > 0" class="q-mb-md" data-panel-blocked>
            <div class="solution-heading">Blocked until you provide</div>
            <div
              v-for="item in panel.blocked"
              :key="`${item.kind}/${item.member ?? ''}/${item.name}`"
              class="q-mt-xs"
              :data-blocked="item.name"
            >
              <div class="os-body">{{ missingLine(item) }}</div>
              <div v-if="item.reason" class="text-caption text-weight-medium" data-blocked-reason>{{ localInstants(item.reason) }}</div>
              <q-file
                v-if="item.kind === 'document'"
                :model-value="null"
                dense
                outlined
                :label="`Upload to ${(item.fix?.upload?.folder ?? item.name).replace(/\/$/, '')}/`"
                :loading="uploading === item.name"
                :disable="uploading !== ''"
                class="blocked-fix"
                data-fix-upload
                @update:model-value="(file: File | null) => upload(item, file)"
              />
              <div
                v-else-if="item.kind === 'connection' && item.fix?.connection && settings[item.fix.connection.member]?.shape.connections?.[item.fix.connection.slot]"
                class="blocked-fix"
                data-fix-connection
              >
                <ConnectionPicker
                  :model-value="settings[item.fix.connection.member]?.connections[item.fix.connection.slot] ?? ''"
                  :slot-name="item.fix.connection.slot"
                  :spec="settings[item.fix.connection.member]!.shape.connections![item.fix.connection.slot]!"
                  :connections="available"
                  :providers="providers"
                  :plugin="settings[item.fix.connection.member]!.plugin || undefined"
                  :bind="(id: string) => bindConnected(item.fix!.connection!.member, item.fix!.connection!.slot, id)"
                  @update:model-value="(value: string) => bind(item, value)"
                  @connected="reloadConnections"
                  @closed="rereadPanel"
                />
                <div
                  v-if="guidedProviders(settings[item.fix.connection.member]!.shape.connections![item.fix.connection.slot]!).length === 0"
                  class="text-caption os-text-muted"
                >
                  No account yet? Connect one in Admin → Connections.
                </div>
              </div>
              <div v-else class="text-caption os-text-muted">Set it under Controls.</div>
            </div>
          </div>

          <div class="solution-heading">Members</div>
          <div class="os-tiles solution-member-tiles q-mb-md" data-panel-members>
            <div v-for="member in panel.members" :key="member.member" class="os-tile solution-panel-tile" :data-member="member.member">
              <div class="os-tile-head">
                <q-icon :name="member.kind === 'plugin' ? 'extension' : 'smart_toy'" size="18px" class="solution-panel-icon" aria-hidden="true" />
                <span class="text-weight-medium solution-panel-tile-name">{{ member.packageName }}</span>
                <q-badge v-if="member.role === 'manager'" outline color="grey-7" label="Manager" />
              </div>
              <div class="os-tile-line"><span class="os-text-muted">State </span><span data-member-state>{{ localInstants(memberStateLine(member)) }}</span></div>
              <div class="os-tile-line">
                <span class="os-text-muted">Last run </span>
                <template v-if="member.lastRun">{{ whenWords(member.lastRun.at) }} · {{ member.lastRun.outcome }}</template>
                <span v-else class="os-text-muted">No runs yet</span>
              </div>
            </div>
          </div>

          <div class="solution-heading">Triggers today</div>
          <div v-if="panel.triggers.length === 0" class="os-body os-text-muted">No triggers.</div>
          <!-- Name, member, next fire and spend only: the instruction and its Details are on the
               Controls tile. -->
          <div v-else class="os-tiles solution-trigger-tiles q-mb-md" data-panel-schedules>
            <div v-for="trigger in panel.triggers" :key="trigger.id" class="os-tile solution-panel-tile" :data-trigger="trigger.id">
              <div class="os-tile-head">
                <q-icon name="bolt" size="18px" class="solution-panel-icon" aria-hidden="true" />
                <span class="text-weight-medium solution-panel-tile-name">{{ trigger.packageName }}</span>
                <span class="os-text-muted">· {{ memberName(trigger.container) }}</span>
              </div>
              <div class="os-tile-line" data-next-fire>{{ nextFireLine(trigger) }}</div>
              <div class="os-tile-line" data-spend :class="{ 'text-negative': trigger.capReachedToday }">
                {{ spentTodayLine(trigger) ?? 'spent today not known (no cap)' }}
                <div v-if="cappedWords(trigger)" class="text-caption" data-capped>{{ cappedWords(trigger) }}</div>
              </div>
            </div>
          </div>

          <!-- THE PACKAGE'S SITES: Open is a real link in a new tab, disabled while a site is
               unpublished, where it would 404. -->
          <template v-if="panel.sites?.length">
            <div class="solution-heading">Sites</div>
            <div class="os-tiles solution-site-tiles">
              <div v-for="site in panel.sites" :key="site.name" class="os-tile solution-panel-tile" :data-panel-site="site.name">
                <div class="os-tile-head">
                  <q-icon name="public" size="18px" class="solution-panel-icon" aria-hidden="true" />
                  <span class="text-weight-medium solution-panel-tile-name">{{ site.name }}</span>
                </div>
                <div class="os-tile-line os-text-muted" data-site-published>{{ site.published ? 'Published' : 'Not published' }}</div>
                <div class="solution-panel-tile-actions">
                  <q-btn
                    flat
                    dense
                    no-caps
                    icon="open_in_new"
                    label="Open"
                    :href="site.published ? site.url : undefined"
                    target="_blank"
                    rel="noopener"
                    :disable="!site.published"
                    data-site-open
                  >
                    <q-tooltip v-if="!site.published">The site {{ site.name }} is not published.</q-tooltip>
                  </q-btn>
                </div>
              </div>
            </div>
          </template>
        </q-card-section>

        <!-- CONTROLS -->
        <q-card-section v-else-if="section === 'controls'" data-section="controls">
          <div class="row items-center q-gutter-sm q-mb-md">
            <q-btn
              outline
              dense
              no-caps
              :icon="panel.paused ? 'play_arrow' : 'pause'"
              :label="panel.paused ? 'Resume the team' : 'Pause the team'"
              :loading="busy === 'pause'"
              data-control-pause
              @click="togglePaused"
            />
            <span class="text-caption os-text-muted">
              {{ panel.paused ? 'Paused: no new work is delivered until you resume.' : 'Pausing stops new work being delivered; a run already going finishes.' }}
            </span>
          </div>

          <div class="solution-heading">Triggers</div>
          <div v-if="panel.triggers.length === 0" class="os-body os-text-muted q-mb-md">No triggers.</div>
          <div v-else class="os-tiles solution-trigger-tiles q-mb-md" data-control-triggers>
            <div v-for="trigger in panel.triggers" :key="trigger.id" class="os-tile solution-panel-tile" :data-control-trigger="trigger.id">
              <div class="os-tile-head">
                <q-icon name="bolt" size="18px" class="solution-panel-icon" aria-hidden="true" />
                <span class="text-weight-medium solution-panel-tile-name">{{ trigger.packageName }}</span>
                <q-space />
                <q-toggle
                  :model-value="trigger.enabled"
                  dense
                  :disable="busy !== ''"
                  :aria-label="`${trigger.packageName} on`"
                  data-trigger-enabled
                  @update:model-value="(value: boolean) => setEnabled(trigger, value)"
                />
              </div>
              <div class="os-tile-line os-text-muted">{{ nextFireLine(trigger) }}</div>
              <!-- The instruction's first line, clipped to two lines; the whole of it is in Details. -->
              <div v-if="trigger.instruction" class="os-tile-line solution-trigger-instruction" data-trigger-instruction>{{ instructionFirstLine(trigger.instruction) }}</div>
              <div class="row items-center no-wrap q-gutter-x-xs q-mt-xs">
                <q-input
                  :model-value="capText(trigger)"
                  dense
                  outlined
                  placeholder="no cap"
                  label="Daily cap (tokens)"
                  class="cap-input"
                  :error="capProblem(trigger) !== '' ? true : undefined"
                  :error-message="capProblem(trigger)"
                  :aria-label="`${trigger.packageName} daily cap`"
                  data-trigger-cap
                  @update:model-value="(value) => editCap(trigger, value)"
                />
                <q-btn
                  flat
                  dense
                  no-caps
                  label="Save"
                  :disable="!capChanged(trigger)"
                  :loading="busy === `cap:${trigger.id}`"
                  data-trigger-cap-save
                  @click="saveCap(trigger)"
                />
              </div>
              <div v-if="trigger.id in capSaved" class="text-caption" data-trigger-cap-saved>
                Saved: {{ capWords(capSaved[trigger.id] ?? null) }}.
              </div>
              <div class="solution-panel-tile-actions">
                <q-btn
                  flat
                  dense
                  round
                  icon="visibility"
                  :aria-label="`Details ${trigger.packageName}`"
                  data-trigger-details
                  @click="detailsTrigger = trigger"
                >
                  <q-tooltip>Details: its whole instruction, what fires it, its cap and its last fire</q-tooltip>
                </q-btn>
                <q-btn
                  v-if="trigger.runNow"
                  outline
                  dense
                  no-caps
                  icon="play_arrow"
                  label="Run now"
                  :loading="busy === `run:${trigger.id}`"
                  :disable="busy !== '' && busy !== `run:${trigger.id}`"
                  data-run-now
                  @click="runNow(trigger)"
                />
              </div>
            </div>
          </div>

          <div class="solution-heading">Settings</div>
          <div v-if="listed.length === 0" class="os-body os-text-muted q-mb-sm">This package lists no settings of its own; see All settings.</div>
          <div v-for="group in listed" :key="`listed-${group.member}`" class="q-mb-md" :data-listed-settings="group.member">
            <div class="text-weight-medium q-mb-xs">{{ group.packageMember }}</div>
            <PluginSettingsForm
              v-model:config="settings[group.member]!.config"
              v-model:secrets="settings[group.member]!.secrets"
              :shape="group.shape"
            />
            <div class="row items-center q-gutter-sm q-mt-xs">
              <q-btn
                unelevated
                dense
                no-caps
                color="primary"
                label="Save"
                :loading="busy === `settings:${group.member}`"
                data-settings-save
                @click="saveSettings(group.member)"
              />
              <span v-if="settings[group.member]?.saved" class="text-caption" data-settings-saved>Saved. It takes effect on the member's next run.</span>
              <span v-if="settings[group.member]?.problem" class="text-caption text-negative" data-settings-problem>{{ settings[group.member]?.problem }}</span>
            </div>
          </div>

          <q-expansion-item dense dense-toggle label="All settings" class="q-mb-md" data-all-settings header-class="solution-heading-toggle">
            <div class="text-caption os-text-muted q-mb-sm">
              Every setting of every plugin member, including those only a person may set. Secrets are key names, never values.
            </div>
            <div v-if="pluginMembers.length === 0" class="os-body os-text-muted">This solution has no plugin members.</div>
            <div v-for="member in pluginMembers" :key="`all-${member.member}`" class="q-mb-md" :data-all-settings-member="member.member">
              <div class="text-weight-medium q-mb-xs">{{ member.packageName }}</div>
              <template v-if="settings[member.member]">
                <PluginSettingsForm
                  v-model:config="settings[member.member]!.config"
                  v-model:secrets="settings[member.member]!.secrets"
                  v-model:connections="settings[member.member]!.connections"
                  :shape="settings[member.member]!.shape"
                  :plugin="settings[member.member]!.plugin || undefined"
                  :bind-slot="(slot: string, id: string) => bindConnected(member.member, slot, id)"
                />
                <div class="row items-center q-gutter-sm q-mt-xs">
                  <q-btn
                    unelevated
                    dense
                    no-caps
                    color="primary"
                    label="Save"
                    :loading="busy === `settings:${member.member}`"
                    data-settings-save
                    @click="saveSettings(member.member)"
                  />
                  <span v-if="settings[member.member]?.saved" class="text-caption">Saved. It takes effect on the member's next run.</span>
                  <span v-if="settings[member.member]?.problem" class="text-caption text-negative">{{ settings[member.member]?.problem }}</span>
                </div>
              </template>
            </div>
          </q-expansion-item>

          <div class="solution-heading">Connections</div>
          <div v-if="withSlots.length === 0" class="os-body os-text-muted">No member of this solution takes a connection.</div>
          <div v-for="member in withSlots" :key="`slots-${member.member}`" class="q-mb-md" :data-connections-member="member.member">
            <div class="text-weight-medium q-mb-xs">{{ member.packageName }}</div>
            <PluginSettingsForm
              v-model:config="settings[member.member]!.config"
              v-model:secrets="settings[member.member]!.secrets"
              v-model:connections="settings[member.member]!.connections"
              :shape="slotShape(member.member)"
              :plugin="settings[member.member]!.plugin || undefined"
              :bind-slot="(slot: string, id: string) => bindConnected(member.member, slot, id)"
            />
            <q-btn
              unelevated
              dense
              no-caps
              color="primary"
              label="Save"
              class="q-mt-xs"
              :loading="busy === `settings:${member.member}`"
              data-connections-save
              @click="saveSettings(member.member)"
            />
          </div>
        </q-card-section>

        <!-- RESULTS -->
        <q-card-section v-else-if="section === 'results'" data-section="results">
          <div v-if="panel.outputs.length === 0" class="os-body os-text-muted q-mb-md">This package declares no output folders.</div>
          <div v-for="output in panel.outputs" :key="output.folder" class="q-mb-md" :data-output="output.folder">
            <div class="solution-heading">{{ output.folder }}/</div>
            <div v-if="!output.exists || output.files.length === 0" class="os-body os-text-muted">Nothing here yet.</div>
            <q-list v-else dense bordered separator>
              <q-item v-for="file in output.files" :key="file.path" :data-output-file="file.path">
                <q-item-section>
                  <q-item-label>{{ file.name }}</q-item-label>
                  <q-item-label caption>{{ file.path }} · {{ sizeWords(file.size) }} · {{ whenWords(file.modifiedAt) }}</q-item-label>
                </q-item-section>
                <q-item-section side>
                  <q-btn flat dense no-caps icon="download" label="Download" :href="file.download" :download="file.name" data-download />
                </q-item-section>
              </q-item>
            </q-list>
            <div v-if="output.more" class="text-caption os-text-muted q-mt-xs">Showing the newest {{ output.files.length }}; the rest are in Documents.</div>
          </div>

          <div class="solution-heading">Recent runs</div>
          <div v-if="panel.recentRuns.length === 0" class="os-body os-text-muted">No runs yet.</div>
          <q-list v-else dense bordered separator data-recent-runs>
            <q-item v-for="run in panel.recentRuns" :key="`${run.member}/${run.seq}`" :data-run="run.seq">
              <q-item-section>
                <q-item-label>{{ run.packageMember }} · {{ run.outcome }}</q-item-label>
                <q-item-label caption>{{ whenWords(run.endedAt) }}</q-item-label>
                <div v-if="run.output" class="run-output mono" data-run-output>{{ localInstants(run.output) }}</div>
                <div v-if="run.reason" class="run-output text-negative" data-run-reason>{{ localInstants(run.reason) }}</div>
                <div v-if="transcripts[runKey(run)]" class="run-output mono transcript" data-run-transcript>{{ transcripts[runKey(run)] }}</div>
              </q-item-section>
              <q-item-section v-if="run.transcript" side top>
                <q-btn
                  flat
                  dense
                  no-caps
                  size="sm"
                  :label="transcripts[runKey(run)] ? 'Hide transcript' : 'Transcript'"
                  :loading="busy === `transcript:${runKey(run)}`"
                  data-run-transcript-toggle
                  @click="toggleTranscript(run)"
                />
              </q-item-section>
            </q-item>
          </q-list>
        </q-card-section>

        <!-- MAINTENANCE -->
        <q-card-section v-else data-section="maintenance">
          <q-markup-table flat bordered dense separator="horizontal" class="q-mb-md" data-maintenance-facts>
            <tbody>
              <tr><th class="text-left">Package</th><td>{{ panel.id }}</td></tr>
              <tr><th class="text-left">Version</th><td data-maintenance-version>{{ panel.version }}</td></tr>
              <tr><th class="text-left">Source folder</th><td class="mono" data-maintenance-folder>{{ panel.folder }}</td></tr>
              <tr><th class="text-left">Installed</th><td>{{ whenWords(panel.installedAt) }} by {{ panel.installedBy }}</td></tr>
              <tr v-if="panel.updatedAt"><th class="text-left">Updated</th><td>{{ whenWords(panel.updatedAt) }}</td></tr>
            </tbody>
          </q-markup-table>

          <div class="row items-start q-gutter-md">
            <div class="col">
              <div class="solution-heading">Update from a folder</div>
              <div class="text-caption os-text-muted q-mb-xs">
                Choose, type or upload the folder of a newer version. You review what changes before anything is written; your settings, bindings and documents are kept.
              </div>
              <q-btn outline dense no-caps icon="upgrade" label="Update from a folder" data-update-from-folder @click="updateOpen = true" />
            </div>
            <div class="col">
              <div class="solution-heading">Uninstall</div>
              <div class="text-caption os-text-muted q-mb-xs">
                Removes the solution's triggers, members, team skills, sites and tools. The team and its documents stay.
              </div>
              <q-btn outline dense no-caps color="negative" icon="delete" label="Uninstall…" data-uninstall @click="askUninstall" />
            </div>
          </div>
        </q-card-section>
      </template>

      <q-card-section v-else-if="loading" class="os-body os-text-muted">Reading…</q-card-section>
    </q-card>
  </q-dialog>

  <!-- UNINSTALL ASKS FIRST, naming what goes and what stays; then says what it did. -->
  <q-dialog v-model="uninstall.asking" persistent>
    <q-card class="os-dialog-md" data-uninstall-dialog>
      <q-card-section class="os-dialog-title">
        {{ uninstall.result ? 'Uninstalled' : `Uninstall ${panel?.name ?? 'this solution'}?` }}
      </q-card-section>

      <q-card-section v-if="!uninstall.result && panel" class="os-body" data-uninstall-ask>
        <div class="q-mb-xs">This removes, from team {{ panel.teamName }}:</div>
        <ul class="q-my-none">
          <li v-if="panel.triggers.length > 0">Triggers: {{ panel.triggers.map((trigger) => trigger.packageName).join(', ') }}</li>
          <li v-if="panel.members.some((member) => member.role !== 'manager')">
            Members: {{ panel.members.filter((member) => member.role !== 'manager').map((member) => member.packageName).join(', ') }}
          </li>
          <li>Its team skills</li>
          <li>Its tools folder</li>
        </ul>
        <div class="q-mt-sm" data-uninstall-sites-kept>
          It takes offline and keeps
          <template v-if="uninstallSites.length > 0">the site{{ uninstallSites.length === 1 ? '' : 's' }} {{ uninstallSites.join(', ') }}</template>
          <template v-else>every site the package published</template>,
          with <strong>all their data</strong> and versions: nobody can open them while the package is uninstalled, and installing
          it onto team {{ panel.teamName }} again brings them back as they were.
        </div>
        <div class="q-mt-sm">It keeps the team, its Manager and <strong>all of its documents</strong>.</div>
        <div class="q-mt-sm os-text-muted" data-uninstall-team-delete>
          Deleting the team later removes the kept sites and their data for good.
        </div>
        <q-checkbox
          v-if="uninstall.plugins.length > 0"
          v-model="uninstall.removePlugins"
          dense
          class="q-mt-sm"
          :label="`Also remove the package's plugins (${uninstall.plugins.join(', ')}) where no other team uses them`"
          data-uninstall-plugins
        />
        <div v-if="uninstall.problem" class="text-negative q-mt-sm" data-uninstall-problem>{{ uninstall.problem }}</div>
      </q-card-section>

      <q-card-section v-else-if="uninstall.result" class="os-body" data-uninstall-result>
        <div>
          {{ panel?.name ?? uninstall.result.id }} {{ uninstall.result.version }} was uninstalled{{ uninstall.result.ok ? '' : ', except what is named below' }}.
          Team {{ uninstall.result.teamName ?? panel?.teamName ?? uninstall.result.team }} stays, with its Manager.
        </div>
        <ul class="q-my-sm">
          <li v-if="uninstall.result.removed.triggers.length">Triggers removed: {{ uninstall.result.removed.triggers.join(', ') }}</li>
          <li v-if="uninstall.result.removed.members.length">Members removed: {{ uninstall.result.removed.members.join(', ') }}</li>
          <li v-if="uninstall.result.removed.skills.length">Skills removed: {{ uninstall.result.removed.skills.join(', ') }}</li>
          <li v-if="uninstall.result.sitesKept?.length" data-sites-kept>
            Taken offline and kept with their data: {{ uninstall.result.sitesKept.join(', ') }}. Reinstall the package onto this team to bring them back.
          </li>
          <li v-if="uninstall.result.removed.tools">Tools folder removed</li>
          <li v-if="uninstall.result.plugins.removed.length">Plugins removed: {{ uninstall.result.plugins.removed.join(', ') }}</li>
          <li v-for="kept in uninstall.result.plugins.kept" :key="kept.id" data-plugin-kept>
            Plugin {{ kept.id }} kept{{ kept.usedBy.length ? `: used by ${kept.usedBy.join(', ')}` : '' }}
          </li>
        </ul>
        <div data-documents-kept>
          <template v-if="uninstall.result.documentsKept">Documents kept in <span class="mono">{{ uninstall.result.documentsKept }}</span>.</template>
          <template v-else>The team's documents were kept.</template>
        </div>
        <div v-for="failure in uninstall.result.failures" :key="failure" class="text-negative q-mt-xs" data-uninstall-failure>{{ failure }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <template v-if="!uninstall.result">
          <q-btn flat no-caps label="Cancel" :disable="uninstall.busy" data-uninstall-cancel @click="closeUninstall" />
          <q-btn
            unelevated
            no-caps
            color="negative"
            label="Uninstall"
            :loading="uninstall.busy"
            data-uninstall-confirm
            @click="confirmUninstall"
          />
        </template>
        <q-btn v-else unelevated no-caps color="primary" label="Done" data-uninstall-done @click="closeUninstall" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <InstallFromFolderDialog
    v-model="updateOpen"
    title="Update from a folder"
    caption="The folder of a newer version of this solution, inside this instance."
    picker-title="Choose the folder of the newer version"
    action="Update"
    instance-only
    @install="update"
  />

  <SolutionWizard v-model="wizard.open" :folder="wizard.folder" :team="panel?.team ?? team" />

  <!-- A TRIGGER'S DETAILS: its whole instruction, newlines kept, and what fires it - all text. -->
  <q-dialog :model-value="detailsTrigger !== null" @update:model-value="(showing: boolean) => { if (!showing) detailsTrigger = null; }">
    <q-card v-if="detailsTrigger" class="os-dialog-md" data-trigger-details-dialog>
      <q-card-section class="row items-center no-wrap q-pb-none">
        <div class="solution-details-title">
          <div class="os-dialog-title">{{ detailsTrigger.packageName }}</div>
          <div class="text-caption os-text-muted">{{ memberName(detailsTrigger.container) }}</div>
        </div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section class="solution-details-body">
        <div class="solution-heading">Instruction</div>
        <div class="os-body solution-trigger-instruction-whole q-mb-md" data-trigger-details-instruction>{{ detailsTrigger.instruction }}</div>
        <dl class="solution-facts">
          <template v-for="fact in triggerFacts(detailsTrigger)" :key="fact.label">
            <dt>{{ fact.label }}</dt>
            <dd :data-trigger-fact="fact.label">{{ fact.value }}</dd>
          </template>
        </dl>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.solution-heading {
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.09em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
  margin: 4px 0;
}

.solution-badge {
  white-space: normal;
  line-height: 1.3;
  padding: 3px 6px;
}

.blocked-fix {
  max-width: 28rem;
}

.cap-input {
  width: 9rem;
}

/* The grids themselves are `os-tiles` (css/tiles.scss); these only say how narrow a column may
   get. min(): a phone narrower than one column still gets a tile that fits it. */
.solution-member-tiles,
.solution-site-tiles {
  --os-tile-min: min(15rem, 100%);
}

.solution-trigger-tiles {
  --os-tile-min: min(19rem, 100%);
}

.solution-panel-tile {
  min-width: 0;
}

.solution-panel-icon {
  color: var(--os-ink-muted);
}

.solution-panel-tile-name {
  min-width: 0;
  overflow-wrap: anywhere;
}

.solution-panel-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 4px;
}

/* The first line of a trigger's instruction, at most two lines on the tile; Details has it whole. */
.solution-trigger-instruction {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* The whole instruction in Details: its own newlines kept, long words wrapped, never interpreted. */
.solution-trigger-instruction-whole {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
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

/* Run output is plain text from the plugin: kept as written, wrapped, never interpreted. */
.transcript {
  max-height: 20rem;
  overflow: auto;
}

.run-output {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: 12px;
  margin-top: 2px;
}
</style>
