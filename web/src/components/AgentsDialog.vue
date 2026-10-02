<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import * as api from '../api/client';
import { type Agent } from '../api/types';
import { visibleAgents } from '../lib/hiddenAgents';
import { AgentCredentialSources, AgentTags, sourceMapOf, tagMapOf } from '../lib/tenantSettings';
import {
  installGuidance,
  installStatus,
  installationFor,
  isNotInstalled,
} from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import { authReportFor, authStatus, refreshAgentAuth, sourceLabel, useAgentAuth } from '../lib/useAgentAuth';
import {
  ProviderTermsSentence,
  conciergeLine,
  credentialFor,
  credentialState,
  kindLabel,
  sharedLine,
  withCredentialStatus,
  withSource,
} from '../lib/agentCredentials';
import { toolsReportFor, toolsStatus } from '../lib/agentTools';
import { cliVersionFor, heldLine, updateInGate, updateStateLine, versionLine, withCliVersion } from '../lib/agentVersions';
import type { AgentCredential, AgentCredentialSource, AgentUpdateState, CliVersion, PresetToolReport } from '../api/types';
import AgentEditDialog from './AgentEditDialog.vue';

/**
 * The tenant catalog of Agent presets: how each CLI is launched. Every person is an administrator
 * and may edit it; `PUT /api/agents` refuses a machine principal on its own, so this screen is not
 * the boundary.
 *
 * LAUNCH DEFINITIONS ONLY. What an agent is TOLD is the built-in prompt for its role, chosen
 * by role and never here, so this screen has no prompt editor. The presets the build ships (claude,
 * codex, copilot, grok and their headless forms) arrive `builtIn` and are listed read-only: every
 * route refuses to edit or delete one. A person may add custom presets and edit or delete only those.
 *
 * EXCEPT A BUILT-IN'S TAGS. They drive hiring and the seed's are an opinion, so a built-in
 * row offers "Edit tags" and "Reset to the build's tags" and nothing else. Both write the tenant
 * setting `agents.tags` through `PUT /api/tenant/settings` - never a changed catalog, which the
 * route refuses - so the second writer below is that setting, and this screen is its only editor.
 *
 * ONE WRITER, here. `PUT /api/agents` replaces the whole file rather than patching a row, so this
 * component - which holds the whole list - is the only thing that can build a correct body. The
 * edit dialog is presentational: it emits a value, this saves it, and on a refusal this leaves it
 * OPEN with the server's words. Closing on submit and reporting afterwards would put the refusal
 * behind the list somebody was just looking at, which reads as nothing having happened.
 */
const open = defineModel<boolean>({ required: true });

const $q = useQuasar();

const agents = ref<Agent[]>([]);

/**
 * Whether each preset's command is on this machine, as `GET /api/agents` answered it.
 *
 * A LIST BESIDE THE CATALOG, never merged into `agents.value`, and that is not tidiness: this
 * component's saves are composed FROM `agents.value` and `PUT /api/agents` replaces the catalog
 * wholesale, so anything folded into a definition here is something the next save writes into
 * `agents.json`. This is a measurement of the machine, not configuration.
 *
 * Empty when the server did not probe, which the library reads as "not checked" rather than as
 * "everything is fine".
 *
 * THE SHARED REF, not a local one, and that is what makes the dogfooding step work: putting a CLI
 * back and opening this screen refreshes the ribbon badge in the same breath, with no restart and
 * no second request. One store of the fact and several readers is the rule; a local copy here would
 * be a screen saying one thing while the toolbar above it said another.
 */
const { installations } = useAgentInstallations();

/**
 * Whether each preset's CLI is SIGNED IN, from `GET /api/agents/auth` - the second measurement,
 * held the same way and for the same reasons. Refreshed on every opening, so a CLI signed in
 * inside the container reads as such the next time this screen is looked at.
 */
const { reports: authReports } = useAgentAuth();

/** EVERY row gets a state, referenced or not: the screen LISTS what exists and the ribbon badge
 *  WARNS about what is in use. An unused preset reading "not found on this machine" is information
 *  rather than a problem, and hiding it would leave a person guessing why a hire failed. */
const statusOf = (agent: Agent) => installStatus(installationFor(installations.value, agent.name));

/** The sign-in caption. `Not measured` is grey and never a warning - see `authStatus`. */
const authOf = (agent: Agent) => authStatus(authReportFor(authReports.value, agent.name));
const authSourceOf = (agent: Agent) => sourceLabel(authReportFor(authReports.value, agent.name));

/**
 * Each model preset's credential source and its command's issued credential, from
 * `GET /api/agents/credentials`. Beside the catalog for `installations`' reason: the source is the
 * tenant setting `agents.credentialSource` and the credential is the Host's store, and neither may
 * be folded into an `Agent` that the wholesale save writes back. Empty when it could not be read,
 * which shows no credential block rather than a wrong one.
 *
 * NO VALUE IS EVER HELD HERE. The Host answers set, by whom and when; the field a person types into
 * is the only place a value exists in this screen, and it is emptied once it is saved.
 */
const credentials = ref<AgentCredential[]>([]);
const credentialOf = (agent: Agent) => credentialFor(credentials.value, agent.name);
const declared = (agent: Agent) => credentialOf(agent)?.issuedCredential != null;

async function loadCredentials() {
  try {
    credentials.value = await api.getAgentCredentials();
  } catch {
    credentials.value = [];
  }
}

/** The options of a row's source toggle: issued only where the preset declares a credential. */
const sourceOptions = (agent: Agent) => [
  { label: 'Shared home', value: 'home' },
  { label: 'Issued', value: 'issued', disable: !declared(agent) },
];

/** The preset whose source is on its way to the Host, or null. */
const sourceBusy = ref<string | null>(null);

/**
 * Writes `agents.credentialSource` as it will read after this change - the SOURCE IS THE PRESET'S, so
 * only this preset's entry moves. The setting is one value, read fresh, and every other entry goes
 * back as it was. Home is the entry's absence, which is what the Host reads as home.
 */
async function saveSource(agent: Agent, source: AgentCredentialSource) {
  const entry = credentialOf(agent);
  if (!entry || entry.source === source) return;
  // The server refuses it too; this is so a person never sees the attempt.
  if (source === 'issued' && !declared(agent)) return;

  sourceBusy.value = agent.name;
  error.value = '';

  try {
    const settings = await api.getTenantSettings();
    const map = sourceMapOf(settings.settings.find((setting) => setting.name === AgentCredentialSources)?.value);

    for (const key of Object.keys(map)) {
      if (key.toLowerCase() === agent.name.toLowerCase()) delete map[key];
    }

    if (source === 'issued') map[agent.name] = 'issued';

    await api.saveTenantSettings({ [AgentCredentialSources]: map });
    credentials.value = withSource(credentials.value, agent.name, source);
    $q.notify({
      type: 'positive',
      message: source === 'issued'
        ? `${agent.name} signs in through its issued ${entry.command} credential.`
        : `${agent.name} signs in through the shared home.`,
    });
    void refreshAgentAuth();
  } catch (failure) {
    // Verbatim, as a tags refusal is.
    error.value = (failure as Error).message;
  } finally {
    sourceBusy.value = null;
  }
}

// --- The credential of a preset's command -------------------------------------------------------

/** The row the credential editor was opened from; the credential is its command's. */
const credentialEditing = ref<AgentCredential | null>(null);
const credentialKind = ref<'apiKey' | 'token'>('apiKey');
const credentialValue = ref('');
const clearCredential = ref(false);
const credentialBusy = ref(false);
const credentialProblem = ref('');

function editCredential(agent: Agent) {
  const entry = credentialOf(agent);
  if (!entry?.issuedCredential) return;

  credentialEditing.value = entry;
  credentialKind.value = entry.issuedCredential.kinds[0]?.kind ?? 'apiKey';
  // NEVER FILLED: the Host does not send the value, and a person replaces it by typing a new one.
  credentialValue.value = '';
  clearCredential.value = false;
  credentialProblem.value = '';
}

const credentialKinds = computed(() => credentialEditing.value?.issuedCredential?.kinds ?? []);

/**
 * Left empty, the stored value is kept; "clear" removes it. Either write answers the COMMAND'S state,
 * laid over every row of that command - the sibling preset's row included - since it is one value.
 */
async function saveCredential() {
  const entry = credentialEditing.value;
  if (!entry || credentialBusy.value) return;

  if (!clearCredential.value && credentialValue.value === '') {
    credentialEditing.value = null;
    return;
  }

  credentialBusy.value = true;
  credentialProblem.value = '';

  try {
    const status = clearCredential.value
      ? await api.clearAgentCredential(entry.agent)
      : await api.setAgentCredential(entry.agent, { kind: credentialKind.value, value: credentialValue.value });

    credentialValue.value = '';
    credentials.value = withCredentialStatus(credentials.value, status);
    credentialEditing.value = null;
    $q.notify({
      type: 'positive',
      message: status.set ? `The ${status.command} credential is saved.` : `The ${status.command} credential is cleared.`,
    });
    void refreshAgentAuth();
  } catch (failure) {
    // The server's words, which never repeat the value.
    credentialProblem.value = (failure as Error).message;
  } finally {
    credentialBusy.value = false;
  }
}

/**
 * What each preset's CLI would load, from the Host's last pre-flight (`GET /api/agents/tools`).
 * A third measurement, held beside the catalog like the other two and never folded into an `Agent`.
 * Empty until it answers, which every row reads as "not listed yet", never as isolated.
 */
const toolReports = ref<PresetToolReport[]>([]);

async function loadTools() {
  try {
    toolReports.value = (await api.getAgentTools()).presets;
  } catch {
    toolReports.value = [];
  }
}

const toolsReportOf = (agent: Agent) => toolsReportFor(toolReports.value, agent.name);
const toolsOf = (agent: Agent) => toolsStatus(toolsReportOf(agent));

/** The tags a row shows, joined. Empty when it carries none. */
const tagsText = (tags: string[] | null | undefined) => (tags ?? []).join(', ');

/** True when a built-in's tags are the operator's rather than the build's - the only time Reset
 *  has anything to undo. */
const tagsFromOperator = (agent: Agent) => agent.tagsFromOperator === true;

const missing = (agent: Agent) => isNotInstalled(installationFor(installations.value, agent.name));

/** The sentence and - only when the preset carries one - the link. Nothing is constructed. */
const guidanceFor = (agent: Agent) =>
  installGuidance(installationFor(installations.value, agent.name));

/** A preset the build ships. `=== true` exactly: an older server without the field is not a claim
 *  that anything is built in, and the failure direction is to leave the row editable. */
const isBuiltIn = (agent: Agent) => agent.builtIn === true;

/**
 * WHAT IS RENDERED, never what is submitted.
 *
 * `agents.value` stays COMPLETE. `saveAgent` composes its body from it - `[...agents.value, next]`
 * on a create, `agents.value.map(...)` on an edit - and `PUT /api/agents` replaces the catalog
 * wholesale, so filtering that ref instead of this computed would permanently delete every hidden
 * preset on the next save of anything at all, with nothing failing - the same way a dialog that
 * rebuilds a `launch` from its own inputs silently drops any field it does not render.
 *
 * Built-ins first, then a person's own, each in the order the catalog holds them.
 */
const shownAgents = computed(() => {
  const shown = visibleAgents(agents.value);
  return [...shown.filter(isBuiltIn), ...shown.filter((agent) => !isBuiltIn(agent))];
});

const busy = ref(false);
const formBusy = ref(false);

/** The list-level failure - a load that could not happen. An EDIT's failure goes to the edit dialog
 *  instead, where the person is still looking. */
const error = ref('');

/** The refusal a save met, handed to the edit dialog. */
const formError = ref('');

const editingAgent = ref<Agent | null>(null);
const agentEditOpen = ref(false);

/** The new Agent a Clone fills the dialog from, already renamed; null for a plain create or edit. */
const cloneSource = ref<Agent | null>(null);

/**
 * Each preset's CLI version, from `cliVersions` on `GET /api/agents` - the Host's CLI version record,
 * the one the operator CLI's `doctor` and `agents` read. Beside the catalog for `installations`' reason.
 */
const cliVersions = ref<(CliVersion & { agent: string })[]>([]);
const versionOf = (agent: Agent) => versionLine(cliVersionFor(cliVersions.value, agent.name));

/**
 * EACH CLI'S UPDATE AS THE HOST'S GATE HOLDS IT, keyed by command: every preset that launches it
 * shows it, since they share one install. Read from the Host on every opening and polled while one is
 * waiting or updating, so closing the dialog or reloading loses nothing - the update lives in the
 * gate, not in this screen.
 */
const updateStates = ref<Record<string, AgentUpdateState>>({});
const updateStateOf = (agent: Agent) => (agent.launch ? updateStates.value[agent.launch.fileName] : undefined);
const outcomeOf = (agent: Agent) => updateStateLine(updateStateOf(agent)) ?? undefined;
const heldOf = (agent: Agent) => heldLine(updateStateOf(agent));
const inGate = (agent: Agent) => updateInGate(updateStateOf(agent));
const canCancel = (agent: Agent) => updateStateOf(agent)?.phase === 'waiting';

/** How often the row re-reads an update still in the gate. */
const UpdatePollMs = 2000;
let pollTimer: ReturnType<typeof setTimeout> | 0 = 0;

function stopPolling() {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = 0;
}

function pollWhileInGate() {
  stopPolling();
  if (!open.value || !Object.values(updateStates.value).some(updateInGate)) return;
  pollTimer = setTimeout(() => void readUpdates(), UpdatePollMs);
}

/**
 * Lays a state from the Host over the row. When an update this screen saw in the gate has left it
 * (or a request answered its outcome at once), the outcome is announced and the version line is read
 * again, laid over from the update's own answer in case that read raced the write.
 */
async function applyUpdateState(state: AgentUpdateState, announce: boolean) {
  const before = updateStates.value[state.command];
  updateStates.value = { ...updateStates.value, [state.command]: state };

  if (updateInGate(state) || !(announce || updateInGate(before))) return;

  if (state.phase === 'done' && state.result) {
    $q.notify({ type: state.result.updated ? 'positive' : 'warning', message: state.result.detail });
    await load();
    if (state.result.cliVersion) cliVersions.value = withCliVersion(cliVersions.value, state.result.cliVersion);
  } else if (state.phase === 'failed' && state.error) {
    $q.notify({ type: 'negative', message: state.error });
  }
}

/** Reads every update the gate holds. A failed read keeps what the row showed. */
async function readUpdates() {
  try {
    const states = await api.listAgentUpdates();
    for (const state of states) await applyUpdateState(state, false);
  } catch {
    // The row keeps its last state; the next opening reads again.
  }

  pollWhileInGate();
}

/** The row mid-DELETE, not a plain boolean, so the spinner lands on the row that is going. */
const removing = ref<string | null>(null);

/** The preset whose update request is on its way to the Host, or null. Brief: the Host answers at once. */
const updating = ref<string | null>(null);

/** Whether the platform can update this preset's CLI: it declares an update command. */
const canUpdate = (agent: Agent) => (agent.updates?.update?.length ?? 0) > 0;

/**
 * Asks the platform to update this preset's CLI. The Host answers AT ONCE with the gate's state: it
 * waits for that CLI's runs in flight and holds new ones - they wait, never fail - and the row polls
 * it through waiting and updating to its outcome, whose server sentence is shown VERBATIM.
 */
async function updateCli(agent: Agent) {
  updating.value = agent.name;

  try {
    await applyUpdateState(await api.updateAgentCli(agent.name), true);
    pollWhileInGate();
  } catch (failure) {
    $q.notify({ type: 'negative', message: (failure as Error).message });
  } finally {
    updating.value = null;
  }
}

/** Cancels an update still waiting; the launches it held go ahead. A running one the Host refuses. */
async function cancelUpdate(agent: Agent) {
  try {
    await applyUpdateState(await api.cancelAgentUpdate(agent.name), false);
  } catch (failure) {
    $q.notify({ type: 'negative', message: (failure as Error).message });
    await readUpdates();
  }
}
const confirmingAgent = ref<Agent | null>(null);

/** A preset serves exactly one mode, so this is the picker it would turn up in. The wire value stays
 *  `Interactive`; a person reads "concierge", as the edit dialog's Mode select says. */
const captionFor = (agent: Agent) => (agent.mode === 'Headless' ? 'headless' : 'concierge');

/**
 * THE FILTER. Free text matched, case-insensitively, against everything a tile says about how the
 * preset launches - its name, its command and arguments, its mode, its tags and whether it is built
 * in - so "claude" finds every preset that runs claude, whatever it is called. And one checkbox per
 * mode, both on at first.
 */
const filterText = ref('');
const showHeadless = ref(true);
const showConcierge = ref(true);

function searchText(agent: Agent): string {
  return [
    agent.name,
    captionFor(agent),
    isBuiltIn(agent) ? 'built-in' : 'custom',
    agent.launch?.fileName ?? '',
    ...(agent.launch?.arguments ?? []),
    ...(agent.tags ?? []),
  ].join(' ').toLowerCase();
}

/** WHAT IS RENDERED after the filter - `shownAgents` narrowed, and never what is submitted. */
const filteredAgents = computed(() => {
  const words = filterText.value.toLowerCase().split(/\s+/).filter((word) => word.length > 0);

  return shownAgents.value.filter((agent) => {
    if (agent.mode === 'Headless' ? !showHeadless.value : !showConcierge.value) return false;
    const text = searchText(agent);
    return words.every((word) => text.includes(word));
  });
});

/** The gaps a tile counts: the preset's declared ones and the pre-flight's, once each. */
const gapCount = (agent: Agent) =>
  new Set([...(agent.isolation?.gaps ?? []), ...(toolsReportOf(agent)?.gaps ?? [])]).size;
const reportsUsage = (agent: Agent) => (agent.launch?.usageFormat ?? null) !== null;

async function load() {
  busy.value = true;
  error.value = '';

  try {
    const catalog = await api.listCatalog();

    agents.value = catalog.agents;
    installations.value = catalog.installations ?? [];
    cliVersions.value = catalog.cliVersions ?? [];
  } catch (failure) {
    error.value = (failure as Error).message;
    agents.value = [];
    installations.value = [];
    cliVersions.value = [];
  } finally {
    busy.value = false;
  }
}

// Reset on every OPENING rather than at mount: this is constructed once at layout mount and reused
// for the tab's life, so state left behind is state the next opening starts in.
watch(open, (showing) => {
  if (!showing) {
    stopPolling();
    return;
  }

  agentEditOpen.value = false;
  confirmingAgent.value = null;
  cloneSource.value = null;
  error.value = '';
  formError.value = '';
  updateStates.value = {};
  void load();
  void readUpdates();
  // Its own call, beside the catalog load rather than inside it: the probe runs each CLI's own
  // status command and is the slower of the two, and a list that waited for it would open blank.
  void refreshAgentAuth();
  void loadTools();
  credentialEditing.value = null;
  void loadCredentials();
});

onUnmounted(stopPolling);

/**
 * The catalog as it will read after this edit, sent whole - built-ins included and exactly as they
 * were read, which the route does not count as an edit. Returns whether it landed, so a caller can
 * close its dialog on success and leave it open on a refusal.
 */
async function save(nextAgents: Agent[], successMessage: string): Promise<boolean> {
  formBusy.value = true;
  formError.value = '';

  try {
    await api.saveCatalog(nextAgents);
    $q.notify({ type: 'positive', message: successMessage });
    await load();

    return true;
  } catch (failure) {
    // VERBATIM. The server names what would break - a duplicate name, a built-in's name, an Agent a
    // member still runs - and a paraphrase here is how two descriptions of one rule start disagreeing.
    formError.value = (failure as Error).message;

    return false;
  } finally {
    formBusy.value = false;
  }
}

/**
 * Opens the details dialog. A BUILT-IN OPENS TOO, read-only apart from its tags: a person can see
 * its whole launch, which this screen used to hide. Its tags save through `agents.tags`.
 */
function openAgent(agent: Agent | null) {
  formError.value = '';
  cloneSource.value = null;
  editingAgent.value = agent;
  agentEditOpen.value = true;
}

/** A free name for a copy of `name`: `<name>-copy`, then `-copy-2`, ... within the 32 characters. */
function copyName(name: string): string {
  const taken = new Set(agents.value.map((agent) => agent.name.toLowerCase()));

  for (let n = 1; ; n++) {
    const suffix = n === 1 ? '-copy' : `-copy-${n}`;
    const candidate = `${name.slice(0, 32 - suffix.length)}${suffix}`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}

/** What a preset carries that says where it came from rather than how it launches. */
function ownFields(agent: Agent): Agent {
  const { builtIn: _builtIn, tagsFromOperator: _operator, buildTags: _build, ...rest } = agent;
  return { ...rest, hidden: false };
}

/**
 * CLONE: a new custom Agent filled in from this one - its launch, isolation, updates and live view
 * all carried - to change what it needs (a model on the command line, say) and save as a person's
 * own. Nothing is saved until the dialog is.
 */
function cloneAgent(agent: Agent) {
  formError.value = '';
  editingAgent.value = null;
  cloneSource.value = { ...ownFields(agent), name: copyName(agent.name) };
  agentEditOpen.value = true;
}

async function saveAgent(next: Agent) {
  // EVERY FIELD THE DIALOG DOES NOT REBUILD IS CARRIED from what it was filled from - `liveView`
  // above all, which the form has no box for: a save that rebuilt only what it shows would drop it,
  // and a clone of a watchable preset would come out unwatchable. What the dialog rebuilt wins.
  const base = editingAgent.value ?? cloneSource.value;
  // A person's preset is never built in, whatever the dialog handed back.
  const custom: Agent = { ...(base ? ownFields(base) : {}), ...next, builtIn: false };

  const list =
    editingAgent.value === null
      ? [...agents.value, custom]
      : agents.value.map((a) => (a.name === editingAgent.value?.name ? custom : a));

  if (await save(list, `${next.name} saved.`)) agentEditOpen.value = false;
}

async function removeAgent(agent: Agent) {
  confirmingAgent.value = null;
  if (isBuiltIn(agent)) return;

  removing.value = agent.name;

  const landed = await save(
    agents.value.filter((a) => a.name !== agent.name),
    `${agent.name} removed.`,
  );

  // A DELETE has no dialog to hold its refusal, so it surfaces on the list instead.
  if (!landed) error.value = formError.value;

  removing.value = null;
}

/** The refusal a save of a built-in's tags met. */
const tagError = ref('');

/**
 * Writes `agents.tags` as it will read after this change. The setting is ONE value, so the map is
 * read fresh from the server and only this preset's entry moves: every other entry - including one
 * naming no built-in, which is ignored where it is read - goes back as it was. `null` removes the
 * entry, which is what resetting to the build's tags is.
 */
async function saveTags(agent: Agent, tags: string[] | null, successMessage: string): Promise<boolean> {
  formBusy.value = true;
  tagError.value = '';

  try {
    const settings = await api.getTenantSettings();
    const map = tagMapOf(settings.settings.find((setting) => setting.name === AgentTags)?.value);

    for (const key of Object.keys(map)) {
      if (key.toLowerCase() === agent.name.toLowerCase()) delete map[key];
    }

    if (tags !== null) map[agent.name] = tags;

    await api.saveTenantSettings({ [AgentTags]: map });
    $q.notify({ type: 'positive', message: successMessage });
    await load();

    return true;
  } catch (failure) {
    // Verbatim, as a catalog refusal is: the server names the setting and the tag it refused.
    tagError.value = (failure as Error).message;

    return false;
  } finally {
    formBusy.value = false;
  }
}

/** A built-in's tags from its details dialog, which stays OPEN with the server's words on a refusal. */
async function saveDetailsTags(tags: string[]) {
  const agent = editingAgent.value;
  if (agent === null || !isBuiltIn(agent)) return;

  formError.value = '';
  if (await saveTags(agent, tags, `${agent.name}'s tags saved.`)) agentEditOpen.value = false;
  else formError.value = tagError.value;
}

async function resetDetailsTags() {
  const agent = editingAgent.value;
  if (agent === null || !isBuiltIn(agent)) return;

  formError.value = '';
  if (await saveTags(agent, null, `${agent.name} carries the build's tags again.`)) agentEditOpen.value = false;
  else formError.value = tagError.value;
}

async function resetTags(agent: Agent) {
  // A reset has no dialog to hold its refusal, so it surfaces on the list, as a removal's does.
  if (!(await saveTags(agent, null, `${agent.name} carries the build's tags again.`))) {
    error.value = tagError.value;
  }
}

const rowBusy = computed(
  () => busy.value || formBusy.value || removing.value !== null || updating.value !== null || sourceBusy.value !== null,
);
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="agents-card os-dialog-xl">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Agents</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs q-pb-sm">
        How each CLI is launched. Built-in Agents come with this build: open one to see its whole
        launch, read-only apart from its tags, which hiring matches on, or clone it to make your own
        with a different launch. What an agent is told comes with this build, by its role.
        Each preset signs in through the shared home or through the one credential issued for its
        command. {{ ProviderTermsSentence }}
      </q-card-section>

      <!-- THE FILTER: free text over name, command, arguments, mode and tags, and one checkbox per
           mode. It narrows what is SHOWN only; a save still sends the whole catalog. -->
      <q-card-section class="agent-filters q-py-sm" data-agent-filters>
        <q-input
          v-model="filterText"
          dense
          outlined
          clearable
          class="agent-filter-text"
          placeholder="Filter: name, command, argument or tag"
          aria-label="Filter Agents"
          data-agent-filter
        >
          <template #prepend><q-icon name="search" /></template>
        </q-input>
        <q-checkbox v-model="showHeadless" dense label="Headless" data-agent-show="headless" />
        <q-checkbox v-model="showConcierge" dense label="Concierge" data-agent-show="concierge" />
      </q-card-section>

      <q-separator />

      <q-card-section v-if="error" class="q-py-sm text-negative">{{ error }}</q-card-section>

      <q-card-section class="agents-list">
        <div v-if="filteredAgents.length > 0" class="os-tiles agent-tiles">
          <div
            v-for="agent in filteredAgents"
            :key="agent.name"
            class="os-tile agent-tile"
            :data-agent-tile="agent.name"
          >
            <div class="os-tile-head agent-tile-head">
              <q-icon name="terminal" size="18px" aria-hidden="true" />
              <span class="mono agent-name text-weight-medium">{{ agent.name }}</span>
              <q-badge
                :outline="!isBuiltIn(agent)"
                :color="isBuiltIn(agent) ? 'grey-7' : 'primary'"
                :label="isBuiltIn(agent) ? 'Built-in' : 'Custom'"
                class="agent-kind"
              />
            </div>
            <div class="os-tile-line agent-tile-line os-text-muted">
              {{ captionFor(agent) }} · <span class="mono">{{ agent.launch?.fileName }}</span>
            </div>

            <!-- TAGS: the ones hiring uses, whose they are, and on a built-in the build's beside
                 the operator's so a person can see what Reset would bring back. -->
            <div class="os-tile-line agent-tile-line agent-tags-line">
              <span v-if="tagsText(agent.tags)">{{ tagsText(agent.tags) }}</span>
              <span v-else class="os-text-muted">no tags</span>
              <template v-if="isBuiltIn(agent)">
                <span v-if="tagsFromOperator(agent)" class="os-text-muted">
                  (yours; build's: {{ tagsText(agent.buildTags) || 'none' }})
                </span>
                <span v-else class="os-text-muted">(from the build)</span>
              </template>
            </div>

            <div class="os-tile-line agent-tile-line">
              <span v-if="reportsUsage(agent)" class="text-positive">reports usage</span>
              <span v-else class="os-text-muted">does not report usage</span>
            </div>

            <!-- THE STATE, ON EVERY TILE. TEXT PLUS ICON, NEVER COLOUR ALONE. Material SYMBOLS names:
                 the app loads Symbols Outlined only. -->
            <div class="os-tile-line agent-tile-line">
              <q-icon :name="statusOf(agent).icon" size="14px" class="q-mr-xs" aria-hidden="true" />
              <span
                :class="{
                  'text-warning': statusOf(agent).tone === 'warn',
                  'os-text-muted': statusOf(agent).tone !== 'warn',
                }"
              >{{ statusOf(agent).text }}</span>
            </div>

            <!-- THE CLI'S VERSION, on a built-in: what the Host's CLI version record says, when it
                 last changed and who brought it. "version not known" is never a guess. -->
            <div v-if="isBuiltIn(agent)" class="os-tile-line agent-tile-line agent-version-line">
              <q-icon name="sync" size="14px" class="q-mr-xs" aria-hidden="true" />
              <span v-if="versionOf(agent).version" class="mono agent-version">{{ versionOf(agent).version }}</span>
              <span v-else class="os-text-muted agent-version">version not known</span>
              <span v-if="versionOf(agent).updated" class="os-text-muted q-ml-xs agent-version-updated">
                · {{ versionOf(agent).updated }}
              </span>
            </div>
            <!-- ITS UPDATE, AS THE HOST'S GATE HOLDS IT: who it waits for by team and member, that it is
                 updating, or what it came to. A waiting one can be cancelled; a running one cannot. -->
            <div
              v-if="outcomeOf(agent)"
              class="os-tile-line agent-tile-line agent-version-outcome"
              :data-phase="updateStateOf(agent)?.phase"
            >
              <q-spinner v-if="inGate(agent)" size="12px" class="q-mr-xs" aria-hidden="true" />
              <span class="agent-update-text">{{ outcomeOf(agent) }}</span>
              <q-btn
                v-if="canCancel(agent)"
                dense
                flat
                no-caps
                size="sm"
                color="negative"
                class="q-ml-xs agent-update-cancel"
                label="Cancel"
                :aria-label="`Cancel the waiting ${agent.launch?.fileName} update`"
                @click="cancelUpdate(agent)"
              />
            </div>
            <div v-if="heldOf(agent)" class="os-tile-line agent-tile-line os-text-muted agent-update-held">
              {{ heldOf(agent) }}
            </div>

            <!-- The remedy, and ONLY where there is something to remedy; the link is an addition,
                 never a substitute, and nothing composes one from the preset's name. -->
            <div v-if="missing(agent)" class="os-tile-line agent-tile-line agent-install-help">
              {{ guidanceFor(agent).text }}
              <a
                v-if="guidanceFor(agent).url"
                :href="guidanceFor(agent).url ?? undefined"
                target="_blank"
                rel="noopener"
              >How to install it</a>
              <span v-if="guidanceFor(agent).hint" class="mono">{{ guidanceFor(agent).hint }}</span>
            </div>

            <!-- Signed in or not. "Not measured" is grey and carries no warning glyph. -->
            <div class="os-tile-line agent-tile-line agent-auth-line">
              <q-icon :name="authOf(agent).icon" size="14px" class="q-mr-xs" aria-hidden="true" />
              <span
                :class="{
                  'text-positive': authOf(agent).tone === 'ok',
                  'text-warning': authOf(agent).tone === 'warn',
                  'os-text-muted': authOf(agent).tone === 'unknown',
                }"
              >{{ authOf(agent).text }}</span>
              <span v-if="authOf(agent).detail" class="os-text-muted">{{ authOf(agent).detail }}</span>
              <span v-if="authSourceOf(agent)" class="os-text-muted agent-auth-source">· {{ authSourceOf(agent) }}</span>
            </div>

            <!-- THE CREDENTIAL SOURCE, the preset's own, and its COMMAND'S credential, shared by every
                 preset that runs it: who set it and when, never a value. -->
            <div
              v-if="credentialOf(agent)"
              class="os-tile-line agent-tile-line agent-credential"
              :data-agent-credential="agent.name"
            >
              <div class="agent-source">
                <span class="os-text-muted">Signs in through</span>
                <q-btn-toggle
                  :model-value="credentialOf(agent)!.source"
                  :options="sourceOptions(agent)"
                  dense
                  no-caps
                  unelevated
                  size="sm"
                  toggle-color="primary"
                  :disable="rowBusy"
                  :aria-label="`Credential source ${agent.name}`"
                  data-credential-source
                  @update:model-value="(source: AgentCredentialSource) => saveSource(agent, source)"
                />
              </div>
              <div v-if="!declared(agent)" class="os-text-muted" data-no-declaration>
                This preset declares no issued credential: it signs in through the shared home only.
              </div>
              <template v-else>
                <div data-credential-state>
                  <q-icon name="key" size="14px" class="q-mr-xs" aria-hidden="true" />
                  <span class="mono">{{ credentialOf(agent)!.command }}</span> credential:
                  <span :class="credentialOf(agent)!.set ? 'text-positive' : 'os-text-muted'">
                    {{ credentialState(credentialOf(agent)!) }}
                  </span>
                </div>
                <div class="os-text-muted" data-credential-shared>{{ sharedLine(credentialOf(agent)!) }}</div>
                <div
                  v-if="credentialOf(agent)!.source === 'issued' && !credentialOf(agent)!.set"
                  class="text-warning"
                  data-credential-missing
                >
                  Not set: its member runs do not start until one is.<span
                    v-if="agent.mode !== 'Headless'"
                    data-concierge-fallback
                  > The Concierge starts on the person's own login.</span>
                </div>
                <div v-if="agent.mode !== 'Headless'" class="os-text-muted" data-concierge-line>
                  {{ conciergeLine(credentialOf(agent)!.issuedCredential!) }}
                </div>
              </template>
            </div>

            <!-- What this preset's CLI would load, as the Host listed it. Its gaps are counted here
                 and explained in Details, where they have room to say what they mean. -->
            <div v-if="toolsOf(agent)" class="os-tile-line agent-tile-line agent-tools-line">
              <q-icon :name="toolsOf(agent)!.icon" size="14px" class="q-mr-xs" aria-hidden="true" />
              <span
                :class="{
                  'text-positive': toolsOf(agent)!.tone === 'ok',
                  'text-warning': toolsOf(agent)!.tone === 'warn',
                  'os-text-muted': toolsOf(agent)!.tone === 'info',
                }"
              >{{ toolsOf(agent)!.text }}</span>
              <span v-if="toolsOf(agent)!.names.length" class="mono agent-tools-names q-ml-xs">
                {{ toolsOf(agent)!.names.join(', ') }}
              </span>
              <span v-if="toolsReportOf(agent)?.detail" class="os-text-muted q-ml-xs">
                {{ toolsReportOf(agent)?.detail }}
              </span>
            </div>
            <div v-if="gapCount(agent) > 0" class="os-tile-line agent-tile-line os-text-muted agent-gap-count">
              {{ gapCount(agent) === 1 ? '1 gap' : `${gapCount(agent)} gaps` }}, explained in Details
            </div>

            <!-- ACTIONS. Details on every tile (a built-in's is read-only apart from its tags);
                 Update where the preset declares an update command; Clone on every tile; Reset only
                 when there is an operator's override to remove; Remove on a custom one. -->
            <div class="agent-tile-actions">
              <span v-if="isBuiltIn(agent)" class="os-text-muted agent-read-only q-mr-auto">
                <q-icon name="lock" size="14px" aria-hidden="true" /> Read-only
              </span>

              <span v-if="declared(agent)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="key"
                  :disable="rowBusy"
                  :aria-label="`Credential ${agent.name}`"
                  @click="editCredential(agent)"
                />
                <q-tooltip>Set, replace or clear the {{ credentialOf(agent)?.command }} credential</q-tooltip>
              </span>

              <span v-if="canUpdate(agent)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="upgrade"
                  :loading="updating === agent.name"
                  :disable="rowBusy || inGate(agent)"
                  :aria-label="`Update the CLI ${agent.name} runs`"
                  @click="updateCli(agent)"
                />
                <q-tooltip>Update the {{ agent.launch?.fileName }} CLI now (waits for its runs, holds new ones)</q-tooltip>
              </span>

              <span v-if="isBuiltIn(agent) && tagsFromOperator(agent)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="undo"
                  :disable="rowBusy"
                  :aria-label="`Reset to the build's tags ${agent.name}`"
                  @click="resetTags(agent)"
                />
                <q-tooltip>Reset to the build's tags</q-tooltip>
              </span>

              <span class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="content_copy"
                  :disable="rowBusy"
                  :aria-label="`Clone ${agent.name}`"
                  @click="cloneAgent(agent)"
                />
                <q-tooltip>Clone: a new Agent of your own, starting from this one</q-tooltip>
              </span>

              <span class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  :icon="isBuiltIn(agent) ? 'visibility' : 'edit'"
                  :disable="rowBusy"
                  :aria-label="`Details ${agent.name}`"
                  @click="openAgent(agent)"
                />
                <q-tooltip>{{ isBuiltIn(agent) ? 'Details, and edit its tags' : 'Details and edit' }}</q-tooltip>
              </span>

              <span v-if="!isBuiltIn(agent)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  color="negative"
                  icon="delete"
                  :loading="removing === agent.name"
                  :disable="rowBusy"
                  :aria-label="`Remove ${agent.name}`"
                  @click="confirmingAgent = agent"
                />
                <q-tooltip>Remove</q-tooltip>
              </span>
            </div>
          </div>
        </div>

        <div v-else-if="!busy && shownAgents.length > 0" class="os-text-muted q-pa-lg text-center" data-agent-none-match>
          No Agent matches the filter.
        </div>
        <div v-else-if="!busy" class="os-text-muted q-pa-lg text-center">
          No Agents yet. Add one below.
        </div>

        <div class="row justify-end q-mt-sm">
          <q-btn
            flat
            dense
            no-caps
            icon="add"
            label="Add an Agent"
            :disable="rowBusy"
            @click="openAgent(null)"
          />
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>

  <AgentEditDialog
    v-model="agentEditOpen"
    :agent="editingAgent"
    :clone-of="cloneSource"
    :read-only="editingAgent !== null && isBuiltIn(editingAgent)"
    :report-gaps="editingAgent ? toolsReportOf(editingAgent)?.gaps ?? [] : []"
    :busy="formBusy"
    :error="formError"
    @save="saveAgent"
    @save-tags="saveDetailsTags"
    @reset-tags="resetDetailsTags"
  />

  <!-- THE CREDENTIAL EDITOR, write-only as Connections' client secret is: the field is never filled,
       empty keeps what is stored, clearing is a checkbox, and the field is emptied once it is saved. -->
  <q-dialog :model-value="credentialEditing !== null" @update:model-value="credentialEditing = null">
    <q-card v-if="credentialEditing" class="os-dialog-sm" data-credential-editor>
      <q-card-section class="os-dialog-title">The {{ credentialEditing.command }} credential</q-card-section>

      <q-card-section class="q-pt-none column q-gutter-sm">
        <div class="os-text-muted">
          Stored once for <span class="mono">{{ credentialEditing.command }}</span>.
          {{ sharedLine(credentialEditing) }} A preset uses it when its source is Issued.
        </div>
        <div data-credential-editor-state>Now: {{ credentialState(credentialEditing) }}</div>

        <q-option-group
          v-if="credentialKinds.length > 1"
          v-model="credentialKind"
          :options="credentialKinds.map((k) => ({ label: `${kindLabel(k.kind)} (${k.variable})`, value: k.kind }))"
          :disable="clearCredential"
          dense
          inline
        />
        <q-input
          v-model="credentialValue"
          outlined
          dense
          type="password"
          :label="kindLabel(credentialKind)"
          :hint="credentialEditing.set
            ? 'Set. Leave empty to keep it; type a new one to replace it.'
            : 'Not set.'"
          :disable="clearCredential"
          autocomplete="new-password"
          spellcheck="false"
          data-credential-input
        >
          <template v-if="credentialEditing.set" #append>
            <q-badge outline color="positive" label="set" data-credential-set />
          </template>
        </q-input>
        <q-checkbox
          v-if="credentialEditing.set"
          v-model="clearCredential"
          dense
          :label="`Clear it, for every preset that runs ${credentialEditing.command}`"
        />
        <div class="os-text-muted" data-provider-terms>{{ ProviderTermsSentence }}</div>
        <div v-if="credentialProblem" class="text-negative" data-credential-problem>{{ credentialProblem }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="credentialBusy" @click="credentialEditing = null" />
        <q-btn unelevated color="primary" no-caps label="Save" :loading="credentialBusy" @click="saveCredential" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- Its own `q-dialog` in this template rather than `$q.dialog()`. The plugin's `class` option
       lands on the inner card and not on the dialog root, so a confirmation opened this way cannot
       be lifted if it ever needs to be - and a DESTRUCTIVE action whose confirmation is invisible is
       the worst version of that bug: the click appears to do nothing, so the natural response is to
       click again. -->
  <q-dialog :model-value="confirmingAgent !== null" @update:model-value="confirmingAgent = null">
    <q-card class="os-dialog-sm">
      <q-card-section class="os-dialog-title">Remove {{ confirmingAgent?.name }}?</q-card-section>

      <q-card-section class="q-pt-none os-text-muted">
        A member or a team's Concierge that still names it will refuse this — the server
        says which.
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" />
        <q-btn
          unelevated
          color="negative"
          no-caps
          label="Remove"
          @click="confirmingAgent && removeAgent(confirmingAgent)"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* A CAP with its own scroll, or a tenant with twenty Agents gets a dialog taller than the viewport
   and no way to reach its buttons. On the list rather than the card, so the title stays put while
   the list moves under it. */
.agents-list {
  max-height: min(64vh, 44rem);
  overflow-y: auto;
}

/* WRAPPED TILES: as many columns as fit, each at least wide enough for a version line. */
/* The grid itself is `os-tiles` (css/tiles.scss); this only says how narrow a column may get. */
.agent-tiles {
  --os-tile-min: 19rem;
}

.agent-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 2px;
}

.agent-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 16px;
}

.agent-filter-text {
  flex: 1 1 16rem;
  max-width: 28rem;
}

.agent-kind {
  vertical-align: middle;
}

.agent-read-only {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  white-space: nowrap;
}

/* The tooltip lives on this WRAPPER because a disabled q-btn swallows pointer events, so one nested
   inside would never fire - and every button here disables while a save is in flight. Same reason
   UsersDialog has one. */
.row-btn-wrap {
  display: inline-flex;
}

/* The install guidance wraps rather than ellipsising, because it is a sentence with a remedy at the
   end of it - a caption that truncates would cut off the one part worth reading. The gap keeps the
   link and the install command off the words when the line does wrap. */
.agent-install-help {
  white-space: normal;
  display: flex;
  flex-wrap: wrap;
  gap: 0 0.35rem;
}

/* The tags wrap: a preset with several, and the build's beside them, runs past one line. */
.agent-tags-line {
  white-space: normal;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0 0.35rem;
}

/* The credential block wraps: its sentences are the part a person reads. */
.agent-credential {
  white-space: normal;
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin-top: 2px;
}

.agent-source {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0 0.5rem;
}

/* The sign-in caption wraps for the same reason: the probe's sentence is the part worth reading
   when the answer is no. */
.agent-auth-line {
  white-space: normal;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0 0.35rem;
}

</style>
