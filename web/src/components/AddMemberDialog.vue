<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar, type QForm } from 'quasar';
import { addMember, getSecretKey, listCatalog, listPlugins } from '../api/client';
import { agentsForMode, type Agent, type InstalledPlugin } from '../api/types';
import { allowedAgentOptions, allowlistIncludes, normalizeAllowlist } from '../lib/memberAllowlist';
import { installStatus, installationFor } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import { MAXIMUM_LABEL_LENGTH, memberName, required } from '../lib/rules';
import { passes, refusalStatus, submitOnEnter } from '../lib/forms';
import type { TeamId } from '../api/types';
import {
  initialConfig,
  initialSecrets,
  missingRequired,
  outOfRange,
  settingsBody,
  type PluginFieldValues,
} from '../lib/pluginSettings';
import { initialBindings } from '../lib/connections';
import PluginSettingsForm from './PluginSettingsForm.vue';
import { MemberInstructionsHint, MemberInstructionsLabel } from '../lib/memberInstructions';


/**
 * Adds a member to a team.
 *
 * Modelled on CreateTeamDialog, and for the same reason its rules are so thin: what a person types
 * here is a LABEL. The server derives the identifier and nobody is ever asked for one, so the only
 * constraint is a length — everything else would be machinery leaking into the first thing someone
 * does.
 *
 * What is deliberately NOT here is a subscriptions field. See `addMember` in ../api/client for why:
 * completion types are global, and a free-text subscription box puts an unbounded wake loop one
 * keystroke from someone with no way to know. A worker is reachable without it.
 *
 * An agent member may be given its OWN INSTRUCTIONS, sent as `systemPrompt` on the hire: added to
 * its prompt after the built-in Member prompt, before the team instructions. A plugin has no prompt,
 * so the field is not offered for one and nothing is sent.
 *
 * INSTALLED PLUGINS ARE OFFERED BESIDE THE PRESETS, by their `plugin:<id>` reference, from
 * `GET /api/plugins`. Choosing one shows its manifest's config fields as inputs and its secrets BY
 * NAME: what a person types for a secret is the LOGICAL KEY set with `secret set`, never a value, and
 * no route ever answers one. The hire goes through the same route as an Agent's. A person may name
 * any installed plugin, as they may name any preset; the team's allowlist bounds machine callers.
 */
const emit = defineEmits<{ added: [] }>();

const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  team: TeamId;
  teamName: string;
  memberAgents: string[] | null;
}>();

const $q = useQuasar();

const { installations } = useAgentInstallations();

const name = ref('');

/** The member's own instructions. Kept while the choice flips between an Agent and a plugin, and
 *  sent only for an Agent. */
const instructions = ref('');

/** Matching `TeamRegistry.MaximumLabelLength`, which the server checks too. */
const MaximumLength = MAXIMUM_LABEL_LENGTH;

const nameRules = [memberName];
const agentRules = [required('Choose an Agent.')];

/** Both the button and submit() consult this, so Enter cannot bypass what the button disables. */
const valid = computed(
  () =>
    passes(name.value, nameRules) &&
    passes(agent.value, agentRules) &&
    (plugin.value
      // The server refuses every hire on a team with an empty allowlist, a plugin's included.
      ? normalizeAllowlist(props.memberAgents).length > 0 && pluginMissing.value.length === 0 && pluginOutOfRange.value.length === 0
      : allowlistIncludes(props.memberAgents, agent.value ?? '')),
);

/**
 * A member is headless, so this offers only Headless presets, filtered on `mode`. Read from the
 * catalog rather than hard-coded, so a preset somebody just added is reachable here without a
 * rebuild — the catalog is what `GET /api/agents` exists to serve to this exact dialog.
 *
 * `echo` first and the default, exactly as for a team: it proves the container, the queue and the
 * wake with no model call, which is how you tell a broken container from a broken agent — and how
 * you add a member for a test without paying for one.
 */
const agents = ref<string[]>([]);

/** Which Agent this member runs: THE TEAM'S choice for new members, or nothing. It opened on
 *  `'echo'` - a catalog entry, named in code.
 */
const agent = ref<string | null>(null);

/** The full preset objects behind `agents` above - kept so a picker can show what it needs to,
 *  which a plain name list cannot. */
const presets = ref<Agent[]>([]);

/** The installed plugins, offered after the presets. Empty when none is installed or the list failed. */
const plugins = ref<InstalledPlugin[]>([]);

/** The plugin chosen, when the choice is a plugin's reference; null for an Agent. */
const plugin = computed(() => plugins.value.find((entry) => entry.reference === agent.value) ?? null);

/** Presets first, as before, then each installed plugin by reference. */
const options = computed(() => [...agents.value, ...plugins.value.map((entry) => entry.reference)]);

/** A plugin reads by its name in the picker; a preset by its own. */
function optionLabel(option: string) {
  const entry = plugins.value.find((candidate) => candidate.reference === option);
  return entry ? `${entry.name} (plugin)` : option;
}

/** The chosen plugin's config values and secret keys, reset to its manifest whenever the choice changes. */
const pluginConfig = ref<PluginFieldValues>({});
const pluginSecrets = ref<Record<string, string>>({});
/** Slot -> connection id, for a plugin that declares connection slots. */
const pluginConnections = ref<Record<string, string>>({});

watch(plugin, (chosen) => {
  pluginConfig.value = chosen ? initialConfig(chosen) : {};
  pluginSecrets.value = chosen ? initialSecrets(chosen) : {};
  pluginConnections.value = chosen ? initialBindings(chosen.connections) : {};
});

const pluginMissing = computed(() =>
  plugin.value ? missingRequired(plugin.value, pluginConfig.value, pluginSecrets.value) : [],
);
/** Number fields outside their manifest bounds: held like a missing required field; the Host refuses them too. */
const pluginOutOfRange = computed(() =>
  plugin.value ? Object.keys(outOfRange(plugin.value, pluginConfig.value)) : [],
);

async function loadPlugins() {
  try {
    plugins.value = (await listPlugins()).plugins ?? [];
  } catch {
    // Nothing to offer rather than a fault: the Agent presets still work.
    plugins.value = [];
  }
}

async function loadAgents() {
  try {
    const catalog = await listCatalog();
    const headless = agentsForMode(catalog.agents, 'Headless');
    const names = headless.map((entry) => entry.name);

    presets.value = headless;
    agents.value = allowedAgentOptions(names, props.memberAgents);
  } catch {
    // EMPTY, not a name. An empty picker says what is true: nothing can be chosen until the
    // catalog answers.
    presets.value = [];
    agents.value = [];
  }

  // The team's choice while the catalog still HAS it - an Agent removed since the team was set up
  // would otherwise sit selected in the box and be refused on submit.
  agent.value = agents.value[0] ?? null;
}

/**
 * Whether the chosen Agent's command is installed. None for a plugin: it is offered only because it
 * is installed, and its install is not the Agent check. An Agent the Host has said nothing about
 * says so, rather than a bare "Not checked".
 */
const getAgentStatus = (agentName: string | null) => {
  if (!agentName || !installations.value || plugin.value) return null;
  const status = installStatus(installationFor(installations.value, agentName));
  return status.text === 'Not checked'
    ? { ...status, text: "Not checked: this Host has not said whether this Agent's command is installed." }
    : status;
};

/** The picker's hint: the Agents are the team's allowlist, and the plugins whatever is installed. */
const agentHint = computed(() => {
  if (!props.memberAgents) return 'What this member runs. Nothing is assumed — pick one.';
  return plugins.value.length > 0
    ? "Agents come from this team's allowlist; plugins from those installed on this Host."
    : "Choose from this team's allowlist.";
});

watch(open, (showing) => {
  if (!showing) return;

  error.value = '';
  errorStatus.value = undefined;
  loaded.value = false;
  void Promise.all([loadAgents(), loadPlugins()]).finally(() => {
    loaded.value = true;
  });
});

/** False until the catalog has answered, so an empty picker is not called empty before then. */
const loaded = ref(false);

/**
 * Why the Agent picker has nothing to offer, as the picker's own error state rather than a red line
 * somewhere under it. Null while it has choices.
 */
const agentProblem = computed(() => {
  if (normalizeAllowlist(props.memberAgents).length === 0) {
    return 'This team has an empty allowlist, so hiring cannot proceed until Team settings adds at least one Agent.';
  }
  if (loaded.value && agents.value.length === 0 && plugins.value.length === 0) {
    return 'No Agent on this team\'s allowlist is in the catalog. Add one in Agents or change Team settings.';
  }
  return null;
});


const busy = ref(false);

/** The server's refusal, shown in the dialog, and its status: a 409 is the name already in use. */
const error = ref('');
const errorStatus = ref<number | undefined>(undefined);
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
    const created = plugin.value
      ? await addMember(
          props.team,
          name.value.trim(),
          agent.value!,
          settingsBody(plugin.value, pluginConfig.value, pluginSecrets.value, pluginConnections.value),
        )
      : await addMember(props.team, name.value.trim(), agent.value!, undefined, instructions.value.trim() || undefined);

    name.value = '';
    instructions.value = '';
    open.value = false;

    // Handle unresolvedAgents if present
    if (created.unresolvedAgents?.length) {
      const messages = created.unresolvedAgents
        .map((ua) => `${ua.agent}: ${ua.message}`)
        .join('\n');
      $q.notify({
        type: 'warning',
        message: `Some agents could not be resolved:\n${messages}`,
        caption: 'Member added, but some agents failed',
      });
    }

    $q.notify({
      type: 'positive',
      message: `${created.name} joined ${props.teamName}.`,
      caption: 'It is idle until something wakes it.',
      timeout: 4000,
    });

    emit('added');
  } catch (cause) {
    // IN THE DIALOG, not a toast behind it: the dialog stays open with what was typed, and the
    // server's own wording comes through - a duplicate name is a 409 that names the member already
    // in the way, and marks the name field as well.
    error.value = cause instanceof Error ? cause.message : String(cause);
    errorStatus.value = refusalStatus(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-md">
      <q-form ref="form" @submit="submit" @keydown="onEnter">
      <q-card-section>
        <div class="os-dialog-title">Add member</div>
        <div class="text-caption os-text-muted">
          A member is an Agent Container on {{ teamName }}. It is headless — it owns one agent, or one
          installed plugin, and is woken by messages addressed to it.
        </div>
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
          :options="options"
          :option-label="optionLabel"
          outlined
          dense
          label="Agent"
          lazy-rules
          :rules="agentRules"
          :error="agentProblem !== null ? true : undefined"
          :error-message="agentProblem ?? ''"
          :hint="agentHint"
        />

        <div v-if="agent && getAgentStatus(agent)" data-agent-status>
          <q-icon
            :name="getAgentStatus(agent)!.icon"
            size="14px"
            class="q-mr-xs"
            aria-hidden="true"
            :class="{ 'text-warning': getAgentStatus(agent)!.tone === 'warn' }"
          />
          <span :class="{ 'text-warning': getAgentStatus(agent)!.tone === 'warn', 'os-text-muted': getAgentStatus(agent)!.tone !== 'warn' }" data-agent-status-text>
            {{ getAgentStatus(agent)!.text }}
          </span>
        </div>

        <!-- THE CHOSEN PLUGIN'S MANIFEST, AS INPUTS: the same editor Member settings shows after
             hire. Config fields by type; secrets by NAME, each asking for the logical key a person
             set with `secret set`. No value is ever shown, because no route carries one. -->
        <div v-if="plugin" class="plugin-hire q-gutter-sm" data-plugin-hire>
          <div class="text-caption os-text-muted">{{ plugin.description }}</div>

          <PluginSettingsForm
            v-model:config="pluginConfig"
            v-model:secrets="pluginSecrets"
            v-model:connections="pluginConnections"
            :shape="plugin"
            :plugin="plugin.id"
            :check-key="getSecretKey"
          />
        </div>

        <!-- THE MEMBER'S OWN INSTRUCTIONS, for an Agent only: a plugin has no prompt. -->
        <q-input
          v-if="!plugin"
          v-model="instructions"
          type="textarea"
          autogrow
          :input-style="{ minHeight: '6em' }"
          outlined
          dense
          :label="MemberInstructionsLabel"
          :hint="MemberInstructionsHint"
        />

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
          label="Add member"
          :loading="busy"
          :disable="!valid || busy"
        />
      </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>
