<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import * as api from '../api/client';
import { type Agent } from '../api/types';
import { visibleAgents } from '../lib/hiddenAgents';
import { joinTags, parseTags } from '../lib/agentDefinitionDraft';
import { AgentTags, tagMapOf, validateTags } from '../lib/tenantSettings';
import {
  installGuidance,
  installStatus,
  installationFor,
  isNotInstalled,
} from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import { authReportFor, authStatus, refreshAgentAuth, useAgentAuth } from '../lib/useAgentAuth';
import { toolsReportFor, toolsStatus } from '../lib/agentTools';
import type { PresetToolReport } from '../api/types';
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

/** The row mid-DELETE, not a plain boolean, so the spinner lands on the row that is going. */
const removing = ref<string | null>(null);
const confirmingAgent = ref<Agent | null>(null);

/** A preset serves exactly one mode, so this is the picker it would turn up in. */
const captionFor = (agent: Agent) => (agent.mode === 'Headless' ? 'headless' : 'interactive');
const reportsUsage = (agent: Agent) => (agent.launch?.usageFormat ?? null) !== null;

async function load() {
  busy.value = true;
  error.value = '';

  try {
    const catalog = await api.listCatalog();

    agents.value = catalog.agents;
    installations.value = catalog.installations ?? [];
  } catch (failure) {
    error.value = (failure as Error).message;
    agents.value = [];
    installations.value = [];
  } finally {
    busy.value = false;
  }
}

// Reset on every OPENING rather than at mount: this is constructed once at layout mount and reused
// for the tab's life, so state left behind is state the next opening starts in.
watch(open, (showing) => {
  if (!showing) return;

  agentEditOpen.value = false;
  confirmingAgent.value = null;
  taggingAgent.value = null;
  error.value = '';
  formError.value = '';
  void load();
  // Its own call, beside the catalog load rather than inside it: the probe runs each CLI's own
  // status command and is the slower of the two, and a list that waited for it would open blank.
  void refreshAgentAuth();
  void loadTools();
});

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

function openAgent(agent: Agent | null) {
  // A built-in is never opened for editing: the route would refuse the save anyway.
  if (agent !== null && isBuiltIn(agent)) return;

  formError.value = '';
  editingAgent.value = agent;
  agentEditOpen.value = true;
}

async function saveAgent(next: Agent) {
  // A person's preset is never built in, whatever the dialog handed back.
  const custom: Agent = { ...next, builtIn: false };

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

/** The built-in whose tags are being edited, the text in the box, and the refusal a save met. */
const taggingAgent = ref<Agent | null>(null);
const tagDraft = ref('');
const tagError = ref('');

function openTags(agent: Agent) {
  if (!isBuiltIn(agent)) return;

  tagError.value = '';
  tagDraft.value = joinTags(agent.tags);
  taggingAgent.value = agent;
}

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

async function submitTags() {
  const agent = taggingAgent.value;
  if (agent === null) return;

  const tags = parseTags(tagDraft.value) ?? [];
  const problem = validateTags(tags);
  if (problem !== null) {
    tagError.value = problem;
    return;
  }

  if (await saveTags(agent, tags, `${agent.name}'s tags saved.`)) taggingAgent.value = null;
}

async function resetTags(agent: Agent) {
  // A reset has no dialog to hold its refusal, so it surfaces on the list, as a removal's does.
  if (!(await saveTags(agent, null, `${agent.name} carries the build's tags again.`))) {
    error.value = tagError.value;
  }
}

const rowBusy = computed(() => busy.value || formBusy.value || removing.value !== null);
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="agents-card os-dialog-lg">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Agents</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs q-pb-sm">
        How each CLI is launched. Built-in presets come with this build and are read-only apart from
        their tags, which hiring matches on; add your own below. What an agent is told comes with this build, by its role.
      </q-card-section>

      <q-separator />

      <q-card-section v-if="error" class="q-py-sm text-negative">{{ error }}</q-card-section>

      <q-card-section class="agents-list">
        <q-list v-if="shownAgents.length > 0" bordered separator>
          <q-item v-for="agent in shownAgents" :key="agent.name">
            <q-item-section avatar><q-icon name="terminal" /></q-item-section>

            <q-item-section>
              <q-item-label class="mono">
                {{ agent.name }}
                <q-badge
                  :outline="!isBuiltIn(agent)"
                  :color="isBuiltIn(agent) ? 'grey-7' : 'primary'"
                  :label="isBuiltIn(agent) ? 'Built-in' : 'Custom'"
                  class="q-ml-xs agent-kind"
                />
              </q-item-label>
              <q-item-label caption>
                {{ captionFor(agent) }} · {{ agent.launch?.fileName }}
              </q-item-label>
              <!-- TAGS, on a built-in: the ones hiring uses, whose they are, and the build's beside
                   the operator's so a person can see what Reset would bring back. -->
              <q-item-label v-if="isBuiltIn(agent)" caption class="agent-tags-line">
                <span v-if="tagsText(agent.tags)">{{ tagsText(agent.tags) }}</span>
                <span v-else class="os-text-muted">no tags</span>
                <span v-if="tagsFromOperator(agent)" class="os-text-muted">
                  (yours; build's: {{ tagsText(agent.buildTags) || 'none' }})
                </span>
                <span v-else class="os-text-muted">(from the build)</span>
              </q-item-label>

              <q-item-label caption>
                <span v-if="reportsUsage(agent)" class="text-positive">reports usage</span>
                <span v-else class="os-text-muted">does not report usage</span>
              </q-item-label>

              <!-- THE STATE, ON EVERY ROW. A seeded preset this tenant never uses, reading "not
                   found on this machine", is information rather than a problem - the screen lists
                   what exists and the ribbon badge warns about what is in USE.

                   TEXT PLUS ICON, NEVER COLOUR ALONE. The colour class is an addition to both and carries nothing on its
                   own. Material SYMBOLS names: the app loads Symbols Outlined only, and an Icons-only
                   name renders as its own letters and throws the row out of line. -->
              <q-item-label caption>
                <q-icon
                  :name="statusOf(agent).icon"
                  size="14px"
                  class="q-mr-xs"
                  aria-hidden="true"
                />
                <span
                  :class="{
                    'text-warning': statusOf(agent).tone === 'warn',
                    'os-text-muted': statusOf(agent).tone !== 'warn',
                  }"
                >{{ statusOf(agent).text }}</span>
              </q-item-label>

              <!-- The remedy, and ONLY where there is something to remedy. The sentence claims
                   exactly what was checked: that a command of this name does not resolve on this
                   machine's PATH. It does not say the CLI is broken, out of date or signed out,
                   because the probe did not ask any of those - it resolves a name to a path and
                   stops, running no candidate binary.

                   THE LINK IS AN ADDITION, NEVER A SUBSTITUTE. A preset with no `install` renders
                   the identical sentence with no link, and nothing here composes a URL from the
                   preset's name: a lookup table mapping brands to vendors' documentation would
                   work everywhere immediately, and it is the change refused, because nothing in
                   code may name an Agent. -->
              <q-item-label v-if="missing(agent)" caption class="agent-install-help">
                {{ guidanceFor(agent).text }}
                <a
                  v-if="guidanceFor(agent).url"
                  :href="guidanceFor(agent).url ?? undefined"
                  target="_blank"
                  rel="noopener"
                >How to install it</a>
                <span v-if="guidanceFor(agent).hint" class="mono">
                  {{ guidanceFor(agent).hint }}
                </span>
              </q-item-label>

              <!-- THE THIRD CAPTION: signed in or not, from `GET /api/agents/auth`. Three states,
                   and the third is the one that has to be right: "Not measured" is grey and
                   carries no warning glyph, because a CLI the probe could not ask is not a CLI
                   that will fail. The probe's own sentence follows a "no", verbatim, since it
                   names what was run and what came back. -->
              <q-item-label caption class="agent-auth-line">
                <q-icon
                  :name="authOf(agent).icon"
                  size="14px"
                  class="q-mr-xs"
                  aria-hidden="true"
                />
                <span
                  :class="{
                    'text-positive': authOf(agent).tone === 'ok',
                    'text-warning': authOf(agent).tone === 'warn',
                    'os-text-muted': authOf(agent).tone === 'unknown',
                  }"
                >{{ authOf(agent).text }}</span>
                <span v-if="authOf(agent).detail" class="os-text-muted">
                  {{ authOf(agent).detail }}
                </span>
              </q-item-label>

              <!-- THE FOURTH CAPTION: what this preset's CLI would load, as the Host listed it.
                   A member's preset is isolated, has foreign tools (named), is not verified, or
                   was not measured - never green without a listing. The Concierge's tools are
                   information in grey, never a warning. Its recorded gaps follow, verbatim. -->
              <q-item-label v-if="toolsOf(agent)" caption class="agent-tools-line">
                <q-icon
                  :name="toolsOf(agent)!.icon"
                  size="14px"
                  class="q-mr-xs"
                  aria-hidden="true"
                />
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
              </q-item-label>
              <q-item-label
                v-for="gap in toolsReportOf(agent)?.gaps ?? []"
                :key="gap"
                caption
                class="os-text-muted agent-tools-gap"
              >
                Gap: {{ gap }}
              </q-item-label>
            </q-item-section>

            <!-- A BUILT-IN HAS ITS TAG CONTROLS AND NO OTHERS, rather than disabled ones: nothing
                 else a person does here can change it, and a greyed button reads as "not right now".
                 Reset appears only when there is an override to remove. -->
            <q-item-section v-if="isBuiltIn(agent)" side>
              <div class="row q-gutter-xs no-wrap items-center">
                <span class="os-text-muted agent-read-only">
                  <q-icon name="lock" size="14px" aria-hidden="true" /> Read-only
                </span>

                <span class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="edit"
                    :disable="rowBusy"
                    :aria-label="`Edit tags ${agent.name}`"
                    @click="openTags(agent)"
                  />
                  <q-tooltip>Edit tags</q-tooltip>
                </span>

                <span v-if="tagsFromOperator(agent)" class="row-btn-wrap">
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
              </div>
            </q-item-section>

            <q-item-section v-else side>
              <div class="row q-gutter-xs no-wrap">
                <span class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="tune"
                    :disable="rowBusy"
                    :aria-label="`Edit ${agent.name}`"
                    @click="openAgent(agent)"
                  />
                  <q-tooltip>Edit</q-tooltip>
                </span>

                <span class="row-btn-wrap">
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
            </q-item-section>
          </q-item>
        </q-list>

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
    :busy="formBusy"
    :error="formError"
    @save="saveAgent"
  />

  <!-- A BUILT-IN'S TAGS, and only those. Left OPEN on a refusal with the server's words, for the
       reason the edit dialog is. -->
  <q-dialog :model-value="taggingAgent !== null" @update:model-value="taggingAgent = null">
    <q-card class="os-dialog-sm">
      <q-card-section class="os-dialog-title">Tags for {{ taggingAgent?.name }}</q-card-section>

      <q-card-section class="q-pt-none">
        <q-input
          v-model="tagDraft"
          dense
          outlined
          type="textarea"
          autogrow
          label="Tags"
          :disable="formBusy"
          :hint="`One tag per line. The build's: ${tagsText(taggingAgent?.buildTags) || 'none'}. Applies to the next hire.`"
        />
        <div v-if="tagError" class="text-negative q-mt-sm">{{ tagError }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" :disable="formBusy" />
        <q-btn
          unelevated
          color="primary"
          no-caps
          label="Save tags"
          :loading="formBusy"
          @click="submitTags"
        />
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
  max-height: 28rem;
  overflow-y: auto;
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
