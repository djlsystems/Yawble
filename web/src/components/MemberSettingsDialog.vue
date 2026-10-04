<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar, type QForm } from 'quasar';
import { getMember, getPluginSettings, listCatalog, savePluginSettings, updateMember } from '../api/client';
import {
  agentsForMode,
  isManagerContainer,
  type Agent,
  type ContainerSnapshot,
  type MemberDetail,
  type PluginHire,
} from '../api/types';
import { allowedAgentOptions, allowlistIncludes, normalizeAllowlist } from '../lib/memberAllowlist';
import { installStatus, installationFor } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import { useConsoleStore } from '../stores/console';
import { MAXIMUM_LABEL_LENGTH, memberName, required } from '../lib/rules';
import { passes, refusalStatus, submitOnEnter } from '../lib/forms';
import {
  ManagerInstructionsHint,
  MemberInstructionsHint,
  MemberInstructionsLabel,
  MemberInstructionsTakesEffect,
  instructionsWrittenBy,
} from '../lib/memberInstructions';
import {
  initialConfig,
  initialSecrets,
  missingRequired,
  refusedOutOfRange,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
} from '../lib/pluginSettings';
import { bindingsBody, initialBindings } from '../lib/connections';
import { bindSlot } from '../lib/slotBinding';
import PluginSettingsForm from './PluginSettingsForm.vue';


/**
 * Settings for an existing member — the card's way into `PATCH /api/teams/{team}/containers/{name}`.
 *
 * What a member is CALLED, what it RUNS, and its OWN INSTRUCTIONS (`systemPrompt`), which are added
 * to its prompt after the built-in role prompt and before the team instructions. The built-in prompt
 * itself is chosen by role from the build and is nobody's to edit.
 *
 * THE INSTRUCTIONS ARE ONE FIELD, VISIBLE AND EDITABLE, on every agent member's card - the Manager's
 * too. When a Manager hired the member it shows exactly what the Manager wrote, and what is saved is
 * what the member gets. A plugin member has no prompt, so the field is absent for one.
 *
 * A PLUGIN MEMBER HAS A SETTINGS SECTION instead: the same manifest-shaped editor Add member shows,
 * filled with what is stored for it, saved through `PUT .../plugin-settings` - a person's route,
 * validated by the Host exactly as a hire is, taking effect on the member's next run. Its secrets are
 * key names; no value is ever read or shown.
 *
 * The Agent IS editable. The preset is resolved by name on every invocation, so a repoint is a
 * single write and takes effect on the member’s next wake, with no Host restart. The manager can be repointed like any other member.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{ snapshot: ContainerSnapshot }>();

const emit = defineEmits<{ saved: [ContainerSnapshot] }>();

const $q = useQuasar();
const board = useConsoleStore();

const { installations } = useAgentInstallations();

/** Matching `TeamRegistry.MaximumLabelLength`, which the server checks too - same rule
 *  `AddMemberDialog` uses for a member's name. */
const MaximumLength = MAXIMUM_LABEL_LENGTH;

const name = ref('');
const nameRules = [memberName];
const agentRules = [required('Choose an Agent.')];

/** The server's refusal, shown in the dialog as the Host worded it, and its status. */
const error = ref('');
const errorStatus = ref<number | undefined>(undefined);

/**
 * The headless presets this member could run.
 *
 * Fetched when the dialog opens rather than held by the parent, because a preset added through the
 * Agents screen is available IMMEDIATELY - `PUT /api/agents` replaces the catalog the server
 * resolves from, with no restart - and a list captured when the board loaded would not have it.
 *
 * Headless only, and never by name: a member is always woken by a message, and offering an
 * interactive preset here produces a refusal the person can only meet after choosing.
 */
const headless = ref<Agent[]>([]);
const agent = ref('');
const teamAllowlist = computed(
  () => board.teams.find((team) => team.id === props.snapshot.team)?.memberAgents ?? null,
);

const agents = computed(() => {
  const names = headless.value.map((a) => a.name);
  return allowedAgentOptions(names, teamAllowlist.value);
});

/** A plugin member has no prompt, so it has no instructions to show. */
const hasInstructions = computed(() => props.snapshot.kind !== 'plugin');

const isManager = computed(() => isManagerContainer(props.snapshot.id, board.managerName));

/** The member's own instructions as typed, and the stored row they were read from. Null until
 *  `getMember` answers, and the field is disabled until then: a Save before it would write over
 *  instructions nobody was shown. */
const instructions = ref('');
const stored = ref<MemberDetail | null>(null);
const instructionsProblem = ref<string | null>(null);

/** Trimmed on both sides, like the team's instructions: trailing whitespace is not a change. */
const instructionsChanged = computed(
  () => stored.value !== null && instructions.value.trim() !== (stored.value.systemPrompt ?? '').trim(),
);

const writtenBy = computed(() => instructionsWrittenBy(stored.value));

async function loadInstructions() {
  stored.value = null;
  instructions.value = '';
  instructionsProblem.value = null;

  if (!hasInstructions.value) return;

  try {
    const detail = await getMember(props.snapshot.team, props.snapshot.id);

    stored.value = detail;
    instructions.value = detail.systemPrompt ?? '';
  } catch (cause) {
    instructionsProblem.value = `Could not read this member's instructions: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

/**
 * A PLUGIN MEMBER'S SETTINGS. `pluginShape` is the manifest of the version it runs, as the settings
 * route carries it (`fields`, `secretFields`), and null until that answers. `pluginSaved` is the body
 * as loaded, so Save writes the settings only when they moved.
 */
const isPlugin = computed(() => props.snapshot.kind === 'plugin');
const pluginShape = ref<PluginSettingsShape | null>(null);
const pluginConfig = ref<PluginFieldValues>({});
const pluginSecrets = ref<Record<string, string>>({});
/** Slot -> connection id; saved with the settings, which replace the bindings whole. */
const pluginConnections = ref<Record<string, string>>({});
const pluginSaved = ref('');
const pluginProblem = ref<string | null>(null);
/** The plugin it runs, for a slot's Connect. */
const pluginId = ref('');

/**
 * A sign-in from a slot completed: the new connection is bound now, through the settings route, and
 * counted as saved - the rest of the form stays as the person left it.
 */
async function bindPluginSlot(slot: string, connectionId: string) {
  await bindSlot(props.snapshot.team, props.snapshot.id, slot, connectionId);
  pluginConnections.value = { ...pluginConnections.value, [slot]: connectionId };
  const saved = JSON.parse(pluginSaved.value) as PluginHire;
  saved.connections = bindingsBody(pluginShape.value?.connections, { ...(saved.connections ?? {}), [slot]: connectionId });
  pluginSaved.value = JSON.stringify(saved);
}

const pluginMissing = computed(() =>
  pluginShape.value ? missingRequired(pluginShape.value, pluginConfig.value, pluginSecrets.value) : [],
);
/**
 * Number fields outside their manifest bounds: held like a missing required field; the Host refuses
 * them too. A stored out-of-range value left as it was is not held - the Host keeps it.
 */
const pluginOutOfRange = computed(() =>
  pluginShape.value ? refusedOutOfRange(pluginShape.value, pluginConfig.value) : [],
);

const pluginBody = computed(() =>
  pluginShape.value ? settingsBody(pluginShape.value, pluginConfig.value, pluginSecrets.value, pluginConnections.value) : null,
);

const pluginChanged = computed(
  () => pluginBody.value !== null && JSON.stringify(pluginBody.value) !== pluginSaved.value,
);

async function loadPluginSettings() {
  pluginShape.value = null;
  pluginProblem.value = null;

  if (!isPlugin.value) return;

  try {
    // A plugin that is no longer installed is the route's 409, whose sentence lands below.
    const settings = await getPluginSettings(props.snapshot.team, props.snapshot.id);
    pluginId.value = settings.plugin;
    const shape: PluginSettingsShape = {
      config: settings.fields,
      secrets: settings.secretFields,
      connections: settings.connectionFields ?? {},
    };

    pluginConfig.value = initialConfig(shape, settings.config);
    pluginSecrets.value = initialSecrets(shape, settings.secrets);
    pluginConnections.value = initialBindings(shape.connections, settings.connections ?? {});
    pluginShape.value = { ...shape, stored: { config: pluginConfig.value, outOfRange: settings.outOfRange ?? {} } };
    pluginSaved.value = JSON.stringify(settingsBody(shape, pluginConfig.value, pluginSecrets.value, pluginConnections.value));
  } catch (cause) {
    pluginProblem.value = `Could not read this member's settings: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

const valid = computed(
  () =>
    passes(name.value, nameRules) &&
    passes(agent.value, agentRules) &&
    // A plugin member kept on its plugin is not held to the team's Agent allowlist - a person may
    // hire any installed plugin, and editing its settings is not a repoint.
    ((isPlugin.value && agent.value === props.snapshot.agent) || allowlistIncludes(teamAllowlist.value, agent.value)) &&
    pluginMissing.value.length === 0 && pluginOutOfRange.value.length === 0,
);

/** Why the Agent picker cannot be used, as its own error state. Null while it has choices. */
const agentProblem = computed(() =>
  normalizeAllowlist(teamAllowlist.value).length === 0
    ? 'This team has no allowed Agents for members. Update Team settings before saving.'
    : null,
);

const getAgentStatus = (agentName: string | null) => {
  if (!agentName || !installations.value) return null;
  return installStatus(installationFor(installations.value, agentName));
};

async function loadAgents() {
  try {
    const catalog = await listCatalog();

    headless.value = agentsForMode(catalog.agents, 'Headless');
  } catch {
    // Left empty: `agents` above always falls back to the member's CURRENT preset, so the select is
    // never blank and never silently offers a repoint to nothing.
    headless.value = [];
  }
}

// `immediate` IS LOAD-BEARING, because this component is mounted two different ways.
//
// `ContainerCard` keeps it mounted always and flips `open` — so a plain watcher sees false -> true
// and fires. `TeamSettingsDialog` mounts it behind `v-if="settingsSnapshot"` and sets that ref and
// `settingsOpen` in the SAME tick, so the component is created with `open` ALREADY true and there is
// no transition left to observe. Without `immediate` nothing below ran on that path: the dialog
// opened with an empty name, an empty Agent picker and no fetch, while its own subtitle correctly
// named the member.
//
// Safe on both paths precisely because of the guard on the next line: an immediate run against a
// closed dialog returns before touching anything.
watch(open, (showing) => {
  if (!showing) return;

  name.value = props.snapshot.name;
  agent.value = props.snapshot.agent;
  error.value = '';
  errorStatus.value = undefined;

  void loadAgents();
  void loadInstructions();
  void loadPluginSettings();
}, { immediate: true });

const busy = ref(false);

/**
 * THE NAME IS FLAGGED ONLY WHEN THE NAME WAS THE PROBLEM: a 409 from the member's PATCH on a save
 * that renamed it, while the field still holds the refused name. A plugin member's settings save
 * answers 409 too - its plugin is no longer installed - and so may a PATCH that renamed nothing;
 * neither is about the name, and the banner already carries the Host's sentence.
 */
const refusedName = ref<string | null>(null);
const nameTaken = computed(
  () => errorStatus.value === 409 && error.value !== '' && refusedName.value !== null
    && refusedName.value === name.value.trim(),
);

const form = ref<QForm | null>(null);
const onEnter = submitOnEnter(() => form.value, () => valid.value && !busy.value);

async function submit() {
  if (!valid.value || busy.value) return;

  busy.value = true;
  error.value = '';
  errorStatus.value = undefined;
  refusedName.value = null;

  /** The name the PATCH would rename the member to, or null when it keeps its name. */
  const renamedTo = name.value.trim() !== props.snapshot.name ? name.value.trim() : null;

  try {
    // THE SETTINGS FIRST, and only when they moved: the Host validates them as it does a hire, and
    // a refusal naming a field should leave the rest of the dialog unsaved too.
    if (pluginShape.value && pluginChanged.value && pluginBody.value) {
      const answer = await savePluginSettings(props.snapshot.team, props.snapshot.id, pluginBody.value);
      pluginSaved.value = JSON.stringify(pluginBody.value);
      if (answer) {
        pluginShape.value = { ...pluginShape.value, stored: { config: pluginConfig.value, outOfRange: answer.outOfRange ?? {} } };
      }
    }

    // ONLY WHAT CHANGED. `name` goes only on a rename: the Host records every PATCH that names one
    // as `member.changed {"renamed": true}`, so sending it unchanged logged a rename nobody made.
    const body: { name?: string; agent?: string; systemPrompt?: string } = {};

    if (renamedTo !== null) body.name = renamedTo;

    // Sent only when it actually moved. The server treats an unchanged Agent as a no-op anyway,
    // but sending it regardless would make every save look like a repoint in the API log.
    if (agent.value && agent.value !== props.snapshot.agent) body.agent = agent.value;

    // Only when it moved, so a rename is not recorded as an edit of the instructions. Blank is sent
    // as blank, which clears them.
    if (instructionsChanged.value) body.systemPrompt = instructions.value.trim();

    // A save that changes nothing about the member writes nothing: no PATCH, no tenant row.
    if (Object.keys(body).length === 0) {
      open.value = false;
      $q.notify({ type: 'positive', message: `${props.snapshot.name} saved.`, timeout: 4000 });
      emit('saved', props.snapshot);
      return;
    }

    let updated: ContainerSnapshot;
    try {
      updated = await updateMember(props.snapshot.team, props.snapshot.id, body);
    } catch (cause) {
      if (refusalStatus(cause) === 409 && renamedTo !== null) refusedName.value = renamedTo;
      throw cause;
    }

    open.value = false;

    // Handle unresolvedAgents if present
    if (updated.unresolvedAgents?.length) {
      const messages = updated.unresolvedAgents
        .map((ua) => `${ua.agent}: ${ua.message}`)
        .join('\n');
      $q.notify({
        type: 'warning',
        message: `Some agents could not be resolved:\n${messages}`,
        caption: 'Member updated, but some agents failed',
      });
    }

    $q.notify({ type: 'positive', message: `${updated.name} saved.`, timeout: 4000 });

    emit('saved', updated);
  } catch (cause) {
    // In the dialog, in the Host's words, with what was typed still in it. `nameTaken` decides
    // whether the name field is marked as well.
    error.value = cause instanceof Error ? cause.message : String(cause);
    errorStatus.value = refusalStatus(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="member-settings-card os-dialog-md">
      <q-form ref="form" @submit="submit" @keydown="onEnter">
      <q-card-section>
        <div class="os-dialog-title">Member settings</div>
        <div class="text-caption os-text-muted">{{ snapshot.name }}</div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-input
          v-model="name"
          autofocus
          outlined
          dense
          label="Member name"
          lazy-rules
          :rules="nameRules"
          :error="nameTaken ? true : undefined"
          :error-message="error"
          :maxlength="MaximumLength"
          hint="Whatever you want to call it — spaces and accents are fine."
        />

        <q-select
          v-model="agent"
          :options="agents"
          outlined
          dense
          emit-value
          map-options
          label="Agent"
          lazy-rules
          :rules="agentRules"
          :error="agentProblem !== null ? true : undefined"
          :error-message="agentProblem ?? ''"
          hint="Headless presets only - a member is woken by a message, never typed at. Takes effect on the member's next wake, with no restart."
        />

        <div v-if="agent && getAgentStatus(agent)">
          <q-icon
            :name="getAgentStatus(agent)!.icon"
            size="14px"
            class="q-mr-xs"
            aria-hidden="true"
            :class="{ 'text-warning': getAgentStatus(agent)!.tone === 'warn' }"
          />
          <span :class="{ 'text-warning': getAgentStatus(agent)!.tone === 'warn', 'os-text-muted': getAgentStatus(agent)!.tone !== 'warn' }">
            {{ getAgentStatus(agent)!.text }}
          </span>
        </div>

        <!-- THE MEMBER'S OWN INSTRUCTIONS: every agent member, the Manager included, with the
             Manager's own hint on its card. Absent for a plugin, which has no prompt. -->
        <div v-if="hasInstructions" data-member-instructions>
          <q-input
            v-model="instructions"
            type="textarea"
            autogrow
            :input-style="{ minHeight: '6em' }"
            outlined
            dense
            :label="MemberInstructionsLabel"
            :hint="isManager ? ManagerInstructionsHint : MemberInstructionsHint"
            :disable="stored === null"
            :error="instructionsProblem !== null ? true : undefined"
            :error-message="instructionsProblem ?? ''"
          />
          <div v-if="writtenBy" class="text-caption os-text-muted q-mt-lg" data-written-by>{{ writtenBy }}</div>
          <div class="text-caption os-text-muted" :class="writtenBy ? '' : 'q-mt-lg'">
            {{ MemberInstructionsTakesEffect }}
          </div>
        </div>

        <!-- A PLUGIN MEMBER'S SETTINGS: the manifest-shaped editor Add member shows, filled with
             what is stored. Secrets are key names only. -->
        <div v-if="isPlugin" class="q-gutter-sm" data-member-plugin-settings>
          <div class="text-subtitle2">Settings</div>
          <PluginSettingsForm
            v-if="pluginShape"
            v-model:config="pluginConfig"
            v-model:secrets="pluginSecrets"
            v-model:connections="pluginConnections"
            :shape="pluginShape"
            :plugin="pluginId"
            :bind-slot="bindPluginSlot"
          />
          <div v-else-if="pluginProblem" class="text-negative text-caption">{{ pluginProblem }}</div>
          <div v-else class="text-caption os-text-muted">Reading its settings…</div>
          <div class="text-caption os-text-muted">A change takes effect on the member's next run.</div>
        </div>

          <q-banner v-if="error" dense class="os-bg-tint-error text-negative">
            <template #avatar><q-icon name="error" /></template>
            {{ error }}
          </q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat label="Cancel" :disable="busy" />
        <q-btn
          type="submit"
          color="primary"
          label="Save"
          :loading="busy"
          :disable="!valid || busy"
        />
      </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>

<style scoped>
</style>
