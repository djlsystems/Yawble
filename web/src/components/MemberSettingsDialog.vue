<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar, type QForm } from 'quasar';
import { listCatalog, updateMember } from '../api/client';
import { agentsForMode, type Agent, type ContainerSnapshot } from '../api/types';
import { allowedAgentOptions, allowlistIncludes, normalizeAllowlist } from '../lib/memberAllowlist';
import { installStatus, installationFor } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import { useConsoleStore } from '../stores/console';
import { MAXIMUM_LABEL_LENGTH, memberName, required } from '../lib/rules';
import { passes, refusalStatus, submitOnEnter } from '../lib/forms';

/**
 * Settings for an existing member — the card's way into `PATCH /api/teams/{team}/containers/{name}`.
 *
 * What a member is CALLED and what it RUNS. What it is TOLD is not here: the prompt is chosen by role
 * from the build - the Manager prompt for the Manager, the Member prompt for everyone else - and the
 * only words a person adds are the team's Additional instructions, in Team settings.
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

/** The server's refusal, shown in the dialog, and its status: a 409 is the name already in use. */
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

const valid = computed(
  () =>
    passes(name.value, nameRules) &&
    passes(agent.value, agentRules) &&
    allowlistIncludes(teamAllowlist.value, agent.value),
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
}, { immediate: true });

const busy = ref(false);

const refusedName = ref<string | null>(null);
const nameTaken = computed(
  () => errorStatus.value === 409 && error.value !== '' && refusedName.value === name.value.trim(),
);

const form = ref<QForm | null>(null);
const onEnter = submitOnEnter(() => form.value, () => valid.value && !busy.value);

async function submit() {
  if (!valid.value || busy.value) return;

  busy.value = true;
  error.value = '';
  errorStatus.value = undefined;
  refusedName.value = name.value.trim();

  try {
    const body: { name?: string; agent?: string } = { name: name.value.trim() };

    // Sent only when it actually moved. The server treats an unchanged Agent as a no-op anyway,
    // but sending it regardless would make every save look like a repoint in the API log.
    if (agent.value && agent.value !== props.snapshot.agent) body.agent = agent.value;

    const updated = await updateMember(props.snapshot.team, props.snapshot.id, body);

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
    // In the dialog, with what was typed still in it. A 409 marks the name as well.
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

        <div class="text-caption os-text-muted">
          What this member is told comes with this build, by its role. Words for the whole team go in
          Team settings, under Additional instructions.
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
