<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { Agent, AgentMode } from '../api/types';
import {
  joinEnv,
  joinLines,
  joinTags,
  rebuildAgentDefinition,
} from '../lib/agentDefinitionDraft';
import {
  agentNameTaken,
  envLines,
  firstProblem,
  httpUrl,
  identifier,
  optional,
  parseEnvLines,
  positiveInt,
  required,
  usageFormat as usageFormatRule,
  usageFormatOptions,
  type Rule,
} from '../lib/rules';

/**
 * One Agent's launch, edited in its own dialog.
 *
 * PRESENTATIONAL: it builds an `Agent` and emits it. It does not save. The catalog is written
 * WHOLESALE - `PUT /api/agents` replaces the file rather than patching a row - so the list that
 * knows every other definition is the only thing that can build a correct body, and a second writer
 * here would either need its own contract or would silently drop whatever else the response
 * describes.
 *
 * That is also why `busy` and `error` are PROPS rather than state. The parent saves, and on a
 * refusal it leaves this dialog OPEN with the server's words in it. Closing on submit and reporting
 * afterwards would put the refusal behind the list the person was just looking at - which is how a
 * duplicate name reads as "nothing happened".
 *
 * A nested QDialog needs no z-index help: Quasar stacks each one above the last. The 7000 trap this
 * codebase records is the Concierge panel, which is `position: fixed` outside the dialog
 * system entirely - not a dialog opened from a dialog.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  /** The Agent being edited, or null to create one. */
  agent: Agent | null;
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{ save: [Agent] }>();

const name = ref('');
const mode = ref<AgentMode>('Headless');
const fileName = ref('');
const args = ref('');
const systemPromptArguments = ref('');

const instructionsFile = ref('');
const usageFormat = ref('');
const languageModel = ref(true);
const env = ref('');
const tags = ref('');

/**
 * Where a person goes to install this Agent's CLI, and the one line beside the link.
 *
 * CARRIED BY THIS FORM BECAUSE THIS FORM REBUILDS THE RECORD. `PUT /api/agents` replaces the
 * catalog wholesale and `rebuildAgentDefinition` composes the whole definition from these boxes, so
 * a field the form does not carry is a field the next edit DELETES - which is exactly how three
 * presets lost their `instructionsFile` on a live instance, found only by reading the file.
 *
 * It has nowhere else to come from, either: `LoadOrSeed` NEVER MERGES, so a preset in an existing
 * instance gains an install link through this box or by a hand edit and by no other route.
 */
const installUrl = ref('');
const installHint = ref('');

/** Blank means UNBOUNDED, which is the default. Held as text rather than a number so an empty box
 *  and a zero stay different things - `0` would be a limit nothing could ever finish inside. */
const timeout = ref('');

const isNew = computed(() => props.agent === null);

/**
 * Whether the run timeout applies at all.
 *
 * It does not, for an interactive preset, and that is a fact about the CODE rather than a policy:
 * `ProcessAgentRunner` is the ONLY reader of `timeoutSeconds`, and it is the headless runner.
 * `ConciergeLaunchFactory` never looks at it — a terminal has no run to bound, and an
 * unattended session is ended by `ConciergeIdleTimeout` and its reaper instead.
 */
const interactive = computed(() => mode.value === 'Interactive');

/** The label is what a person calls the mode; the value is what the wire carries. */
const modeOptions: { label: string; value: AgentMode }[] = [
  { label: 'Headless', value: 'Headless' },
  { label: 'Concierge', value: 'Interactive' },
];

/**
 * EXACTLY ONE MECHANISM PER PRESET for handing over a system prompt.
 *
 * A CLI either takes it as an argument (`claude --append-system-prompt-file`) or reads a conventional
 * file from its working directory (`AGENTS.md`, which codex, copilot and grok all read). Carrying
 * BOTH hands the same text over twice and bills it twice on every single run, so whichever one is in
 * use disables the other.
 */
const usesPromptArguments = computed(() => parseLines(systemPromptArguments.value).length > 0);
const usesInstructionsFile = computed(() => instructionsFile.value.trim().length > 0);

/**
 * Each disables the other, but ONLY WHILE IT IS ITSELF EMPTY — and that guard is the whole
 * difference between a rule and a trap.
 *
 * A hand-edited `agents.json` is a supported operator action and can carry both. Disabling on the
 * other field alone locks BOTH of them the moment that happens, and the illegal state becomes
 * unreachable to fix through the only screen that edits it. A field holding content always stays
 * editable, so whichever one is wrong can be cleared.
 */
const promptArgumentsLocked = computed(() => usesInstructionsFile.value && !usesPromptArguments.value);
const instructionsFileLocked = computed(() => usesPromptArguments.value && !usesInstructionsFile.value);

/** One line per non-empty entry, TRIMMED - so an argument carrying a leading or trailing space
 *  typed by accident is not silently preserved as part of a command line. */
function parseLines(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/**
 * Filled on every OPENING rather than at mount, because the parent keeps one instance and retargets
 * it - a form populated at construction would show whichever row was pressed first for the rest of
 * the session. `immediate` covers the parent mounting this behind a `v-if` and setting `open` in the
 * same tick, where there is no false -> true transition left to observe.
 */
watch(
  open,
  (showing) => {
    if (!showing) return;

    name.value = props.agent?.name ?? '';
    mode.value = props.agent?.mode ?? 'Headless';
    fileName.value = props.agent?.launch?.fileName ?? '';
    args.value = joinLines(props.agent?.launch?.arguments);
    systemPromptArguments.value = joinLines(props.agent?.launch?.systemPromptArguments);
    instructionsFile.value = props.agent?.launch?.instructionsFile ?? '';
    usageFormat.value = props.agent?.launch?.usageFormat ?? '';
    languageModel.value = props.agent?.launch?.languageModel ?? true;
    env.value = joinEnv(props.agent?.env);
    tags.value = joinTags(props.agent?.tags);
    timeout.value = props.agent?.timeoutSeconds ? String(props.agent.timeoutSeconds) : '';
    installUrl.value = props.agent?.install?.url ?? '';
    installHint.value = props.agent?.install?.hint ?? '';
  },
  { immediate: true },
);

/**
 * Every field's rules, from `lib/rules`. Held here rather than inline in the template so `valid`
 * reads the SAME list the fields render: the button and the sentence under a field cannot disagree.
 *
 * The name is checked only when creating - an existing preset's name is fixed, and one that
 * predates the identifier rule must stay editable in every other respect.
 */
const nameRules: Rule[] = [identifier];
const fileNameRules: Rule[] = [required('Name the program to run.')];
const usageFormatRules: Rule[] = [usageFormatRule];
const envRules: Rule[] = [envLines];
const installUrlRules: Rule[] = optional(httpUrl);
const timeoutRules: Rule[] = [positiveInt];

/** One entry per `KEY=value` line with its own refusal, so each bad line is named under the box. */
const envRows = computed(() => parseEnvLines(env.value));
const envProblems = computed(() => [
  ...envRows.value.rows.filter((row) => row.error !== null).map((row) => row.error as string),
  ...(envRows.value.error ? [envRows.value.error] : []),
]);

/** The server refused this name (a duplicate), so the field is marked as well as the banner. */
const nameRefused = computed(() => props.error !== '' && agentNameTaken(props.error));

const valid = computed(
  () =>
    (!isNew.value || firstProblem(nameRules, name.value) === null) &&
    firstProblem(fileNameRules, fileName.value) === null &&
    firstProblem(usageFormatRules, usageFormat.value) === null &&
    envRows.value.valid &&
    firstProblem(installUrlRules, installUrl.value) === null &&
    // An interactive preset's timeout is disabled and ignored, so a stale value cannot block Save.
    (interactive.value || firstProblem(timeoutRules, timeout.value) === null),
);

function submit() {
  if (!valid.value || props.busy) return;

  emit(
    'save',
    rebuildAgentDefinition({
      name: name.value,
      mode: mode.value,
      fileName: fileName.value,
      args: args.value,
      systemPromptArguments: systemPromptArguments.value,
      instructionsFile: instructionsFile.value,
      usageFormat: usageFormat.value,
      languageModel: languageModel.value,
      env: env.value,
      timeout: timeout.value,
      tags: tags.value,
      installUrl: installUrl.value,
      installHint: installHint.value,
      isolation: props.agent?.isolation ?? null,
      updates: props.agent?.updates ?? null,
    }),
  );
}
</script>

<template>
  <q-dialog v-model="open" no-backdrop-dismiss>
    <q-card class="os-dialog-lg">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">{{ isNew ? 'New Agent' : agent?.name }}</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="busy" />
      </q-card-section>

      <q-form lazy-rules="ondemand" @submit="submit">
        <q-card-section class="q-gutter-md">
          <!-- Editable only when creating. To rename, delete and recreate: the name is what every
               member row and every team's Concierge setting references, and renaming in place
               would break each of them silently. -->
          <q-input
            v-model="name"
            dense
            outlined
            label="Name"
            :disable="!isNew || busy"
            :rules="isNew ? nameRules : []"
            :error="nameRefused"
            :error-message="nameRefused ? error : undefined"
            hint="Letters, digits, - and _, starting alphanumeric, at most 32 characters. To rename, delete and recreate."
          />

          <!-- ONE mode, not two checkboxes. A Team Member is always Headless and a team's Concierge
               is always Interactive-mode, so a preset serves exactly one world - and re-moding one a
               member still names breaks that reference exactly as deleting it would, which the server
               refuses by name. The wire value stays `Interactive`; the person reads "Concierge". -->
          <q-select
            v-model="mode"
            :options="modeOptions"
            emit-value
            map-options
            dense
            outlined
            label="Mode"
            :disable="busy"
            hint="Headless: a Team Member woken by a message. Concierge: a terminal a person types into."
          />

          <!-- TWO CHANNELS, AND THE HEADINGS EXIST BECAUSE THE FIELDS DO NOT SAY SO.
               An agent is given two separate things, and listing all six inputs in one flat run makes
               `AGENTS.md` and `{userPrompt}` read as two ways of doing one job. They are not
               alternatives: every headless preset needs both, and copilot carries both on the same
               screen. -->
          <div class="text-caption text-weight-medium os-text-muted q-mt-md">
            Launch, and how the work reaches the agent
          </div>
          <div class="text-caption os-text-muted q-mb-sm">
            The work is this run's job — the member's history, then the message that woke it. Different
            every run.
          </div>

          <q-input
            v-model="fileName"
            dense
            outlined
            label="Executable"
            :disable="busy"
            :rules="fileNameRules"
            hint="The program to run — claude, codex, grok, copilot, cmd.exe."
          />

          <q-input
            v-model="args"
            dense
            outlined
            type="textarea"
            autogrow
            label="Arguments"
            :disable="busy"
            hint="One per line. This is also where the WORK is delivered: {userPrompt} puts that text on the command line, {userPromptFile} writes it to a temporary file and substitutes the path — safer where a CLI supports it, since a prompt carries newlines and quotes. Use neither and it goes to the agent on stdin."
          />

          <div class="text-caption text-weight-medium os-text-muted q-mt-md">
            The system prompt — what the agent is told it is
          </div>
          <div class="text-caption os-text-muted q-mb-sm">
            The Prompt chosen for the member, composed with its tokens resolved. Standing instructions,
            the same every run. Use ONE of the two below, never both.
          </div>

          <q-input
            v-model="systemPromptArguments"
            dense
            outlined
            type="textarea"
            autogrow
            label="System-prompt arguments (optional)"
            :disable="busy || promptArgumentsLocked"
            hint="How this Agent is handed the Prompt chosen for the member — one argument per line, with {systemPromptFile} where the path goes. The platform composes that Prompt, writes it to a temporary file and substitutes the path here. Leave empty for a CLI with no such flag; use the instructions file below instead."
          />

          <!-- THE OTHER WAY A SYSTEM PROMPT REACHES AN AGENT. None of codex, copilot or grok accepts a
               system prompt on the command line; a conventional file in the working directory is the
               only mechanism they have. It must be on this form, not merely stored: this dialog
               REBUILDS the launch object and the save replaces the catalog wholesale, so a field
               missing here would be silently deleted by editing the preset for any reason at all.

               Disabled when system-prompt arguments are present, for the same reason those are
               disabled when this is: one mechanism per preset, or the same text is handed over twice
               and billed twice on every run. -->
          <q-input
            v-model="instructionsFile"
            dense
            outlined
            label="Instructions file (optional)"
            :disable="busy || instructionsFileLocked"
            :hint="instructionsFileLocked
              ? 'Not used while system-prompt arguments are set — one mechanism per preset, or the same Prompt is handed over twice and billed twice.'
              : 'The other way the member’s Prompt reaches this Agent: the composed text is written into this file in the working directory before every launch. AGENTS.md for codex, copilot and grok, which have no system-prompt flag. Leave empty for a CLI that takes one.'"
          />

          <!-- A SELECT, because `ProcessAgentRunner` parses exactly the formats `USAGE_FORMATS` lists
               and anything else silently reports no usage. A stored value outside the list still
               shows, marked, until it is changed. -->
          <q-select
            v-model="usageFormat"
            :options="usageFormatOptions"
            emit-value
            map-options
            dense
            outlined
            label="Usage format (optional)"
            :disable="busy"
            :rules="usageFormatRules"
            hint="How this preset reports usage. None where this preset does not report usage."
          />

          <!-- ADDED WITH THE FIELD IT CONTROLS: `AgentLaunch.languageModel` reached the C# record
               with no matching control here, so this dialog rebuilt every launch it saved with the
               key absent — the server filled in the constructor default of `true`, and saving ANY
               preset through this screen silently turned a `languageModel: false` program back into
               a billed, probed, firehose-eligible model. Placed beside Usage format, the closest
               sibling: both describe what kind of preset this is rather than how it launches. -->
          <q-checkbox v-model="languageModel" dense label="Language model" :disable="busy" />
          <div class="text-caption os-text-muted q-mt-xs">
            Clear this for a preset that runs an ordinary program rather than a model — it is then not
            billed, not probed for CLI use, and may subscribe to high-volume events.
          </div>

          <div class="text-caption text-weight-medium os-text-muted q-mt-md">
            Environment and limits
          </div>

          <q-input
            v-model="env"
            dense
            outlined
            type="textarea"
            autogrow
            label="Environment (optional)"
            :disable="busy"
            :rules="envRules"
            hint="KEY=value, one per line. Merged first — the platform's own HARNESS_* variables are injected last and win, and a key starting HARNESS_ is refused."
          />
          <!-- EVERY bad line, not only the first the field's rule reports: a pasted block with three
               mistakes should not take three rounds to fix. -->
          <ul v-if="envProblems.length > 1" class="text-negative text-caption q-my-none">
            <li v-for="problem in envProblems" :key="problem">{{ problem }}</li>
          </ul>

          <q-input
            v-model="tags"
            dense
            outlined
            type="textarea"
            autogrow
            label="Tags (optional)"
            :disable="busy"
            hint="One tag per line. Tenant-wide free-text advice on what this Agent is good at; matching is case-insensitive."
          />

          <!-- WHERE TO GET THE CLI, and OPTIONAL in the ordinary sense rather than the grudging one.
               A preset with no link reads the same sentence without one, and nothing anywhere
               composes a URL from the preset's name - a table in the product mapping brands to
               vendors' documentation would work everywhere immediately and is the change refused,
               because nothing in code may name an Agent.

               THIS IS THE ONLY ROUTE BY WHICH AN EXISTING INSTANCE GETS ONE. `LoadOrSeed` never
               merges into an agents.json that is already there, so the value the build seeds reaches
               new instances alone; every tenant that already exists shows the no-link form until
               somebody fills this box. That is correct behaviour rather than a defect, and it is why
               emptying the box is also how a stale link is REMOVED. -->
          <q-input
            v-model="installUrl"
            dense
            outlined
            label="Install link (optional)"
            :disable="busy"
            :rules="installUrlRules"
            hint="Where a person goes to install this Agent's CLI, shown when its command is not found on this machine's PATH. Empty means no link is shown — nothing is guessed."
          />

          <q-input
            v-model="installHint"
            dense
            outlined
            label="Install hint (optional)"
            :disable="busy || installUrl.trim().length === 0"
            :hint="installUrl.trim().length === 0
              ? 'Needs an install link — a hint with nowhere to go is a remedy nobody can follow.'
              : 'One line beside the link, in practice the install command.'"
          />

          <!-- HEADLESS ONLY, and DISABLED rather than merely ignored for an interactive preset.
               An enabled box that does nothing is not neutral — it invites somebody to set a limit,
               watch a terminal run past it, and go looking for the bug in the platform. Disabling it
               with a reason TEACHES the rule at the only moment anyone cares.

               The stored value is PRESERVED, not cleared, when the mode flips. Clearing it would make
               a mode change silently discard a field the person did not touch — the same defect the
               member PATCH's three-state `systemPrompt` exists to avoid — so a Headless → Interactive
               → Headless round trip gets its timeout back. Nothing reads it in between. -->
          <q-input
            v-model="timeout"
            dense
            outlined
            type="number"
            label="Run timeout in seconds (optional)"
            :disable="busy || interactive"
            :rules="interactive ? [] : timeoutRules"
            :hint="interactive
              ? 'Not used by the Concierge — a terminal has no run to bound. Unattended sessions are ended by the Concierge idle timeout instead. Switch Mode to Headless to set one.'
              : 'How long ONE run may take before the platform stops it and reports the member as failed. Empty means no limit, which is the default — a run then continues until the agent ends it.'"
          />

          <!-- The server's words, VERBATIM, and here rather than behind this dialog: it names what
               would break - a duplicate name, an Agent a member still runs - and a paraphrase is how
               two descriptions of one rule start disagreeing. -->
          <q-banner v-if="error" dense class="os-bg-tint-error text-negative">
            <template #avatar><q-icon name="error" /></template>
            {{ error }}
          </q-banner>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps label="Cancel" :disable="busy" />
          <q-btn
            unelevated
            color="primary"
            no-caps
            label="Save"
            type="submit"
            :loading="busy"
            :disable="!valid || busy"
          />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>
