<script setup lang="ts">
import ChipListInput from './ChipListInput.vue';
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import {
  checkSolution,
  installSolution,
  previewSolution,
  solutionsInstalled,
  teamSolution,
  updateSolution,
} from '../api/client';
import { uploadDocument } from '../api/documents';
import {
  asDocumentsFolderKey,
  asTeamId,
  type InstalledSolution,
  type SolutionCheck,
  type SolutionDiff,
  type SolutionDiffSection,
  type SolutionInstallResult,
  type SolutionKept,
  type SolutionMissing,
  type SolutionPersonSetting,
  type SolutionPlan,
  type SolutionPreview,
  type SolutionRefusal,
  type SolutionSecret,
  type SolutionStep,
} from '../api/types';
import { firstProblem, teamLabel } from '../lib/rules';
import {
  SolutionStepTitles,
  capWords,
  diffMark,
  failureSentence,
  isBlank,
  keptValueWords,
  missingLine,
  refusalLine,
  removedItems,
  firstRunLine,
  secretNeeded,
  secretSetWith,
  secretSentence,
  secretState,
  settingDefaultWords,
  settingInputKind,
  settingKey,
  settingStartValue,
  settingsBody,
  slotKey,
  triggerSource,
  updateCandidates,
  wakeWords,
} from '../lib/solutions';
import { boundsHint, boundsProblem } from '../lib/numberBounds';
import { useConsoleStore } from '../stores/console';

/**
 * INSTALL A SOLUTION PACKAGE: a whole team from one folder, in four steps a person walks through.
 *
 * 1. **Team**: a new team (its name, editable, checked here and by the Host's preview; a local
 *    repository or not), or an update of a team installed from an earlier version of this package.
 * 2. **Review**: everything the install makes, WHOLE. Every member's instructions and every
 *    trigger's instruction become prompts, so none is shortened; each trigger says what fires it,
 *    whom it wakes, how the Manager is woken and its daily cap. An update marks what is new or
 *    changed and lists what goes.
 * 3. **Your part**: what only a person provides - person-only settings, connections, documents -
 *    and the secrets the package binds, by key name: set on the Host, not set (its source fails
 *    until it is, and how to set it), or not needed for a source left off. Never a value.
 *    Skipping a required one is allowed; the team then shows as blocked, naming it, until provided.
 * 4. **Install**: nothing is written before the person presses Install. A failed step is named by
 *    its number and title, and the Host undid everything. After a success the chosen documents are
 *    uploaded and what is still missing is read back.
 *
 * NEVER INSTALLS BY ITSELF: the deep link opens this filled in, and only the Install button sends.
 */
const props = defineProps<{
  /** The package folder, an absolute path inside the instance. */
  folder: string;
  /** `link` for the deep link: the Host then refuses a folder outside documents and team folders. */
  from?: 'link';
  /** A check the opener already made (Install from a folder), so it is not asked twice. */
  check?: SolutionCheck | null;
}>();

const emit = defineEmits<{ opened: [team: string] }>();

const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

type Step = 'team' | 'review' | 'inputs' | 'install';
const stepOrder: Step[] = ['team', 'review', 'inputs', 'install'];
const stepTitles: Record<Step, string> = { team: 'Team', review: 'Review', inputs: 'Your part', install: 'Install' };

const step = ref<Step>('team');
const stepNumber = computed(() => stepOrder.indexOf(step.value) + 1);

// --- The check ----------------------------------------------------------------------------------

const checking = ref(false);
/** The Host's sentence for a refused folder (a 400), or a failure to ask. Nothing else happens. */
const folderRefusal = ref('');
const checked = ref<SolutionCheck | null>(null);
const refusals = computed<SolutionRefusal[]>(() => (checked.value && !checked.value.ok ? checked.value.refusals : []));
const checkPlan = computed<SolutionPlan | null>(() => (checked.value?.ok ? checked.value.plan : null));

const installed = ref<InstalledSolution[]>([]);

function reset() {
  step.value = 'team';
  checking.value = false;
  folderRefusal.value = '';
  checked.value = null;
  installed.value = [];
  mode.value = 'install';
  teamName.value = '';
  localRepository.value = true;
  updateTeam.value = '';
  preview.value = null;
  previewProblem.value = '';
  values.value = {};
  bindings.value = {};
  files.value = {};
  result.value = null;
  installError.value = '';
  uploads.value = [];
  missing.value = null;
  missingProblem.value = '';
}

async function start() {
  reset();
  checking.value = true;

  try {
    checked.value = props.check ?? (await checkSolution(props.folder, props.from));
  } catch (cause) {
    folderRefusal.value = cause instanceof Error ? cause.message : String(cause);
    return;
  } finally {
    checking.value = false;
  }

  const plan = checkPlan.value;
  if (!plan) return;

  teamName.value = plan.team.name;
  seedValues(plan);

  try {
    installed.value = await solutionsInstalled();
  } catch {
    // Without the list there is nothing to update; a new team is still offered.
    installed.value = [];
  }

  await previewName();
}

watch(open, (showing) => {
  if (showing) void start();
});

// --- Team ---------------------------------------------------------------------------------------

const mode = ref<'install' | 'update'>('install');
const teamName = ref('');
const localRepository = ref(true);
const updateTeam = ref('');

const preview = ref<SolutionPreview | null>(null);
const previewing = ref(false);
const previewProblem = ref('');

const okPreview = computed(() => (preview.value && preview.value.ok ? preview.value : null));
const installPreview = computed(() => (okPreview.value?.mode === 'install' ? okPreview.value : null));
const updatePreview = computed(() => (okPreview.value?.mode === 'update' ? okPreview.value : null));

const nameProblem = computed(() => firstProblem([teamLabel], teamName.value));
/** The Host's reason the name cannot be used, for the name it was asked about. */
const nameRefusal = computed(() =>
  mode.value === 'install' && installPreview.value && installPreview.value.teamName === teamName.value.trim()
    ? installPreview.value.nameRefusal
    : null,
);

const candidates = computed(() =>
  checkPlan.value ? updateCandidates(installed.value, checkPlan.value.package.id, checkPlan.value.package.version) : [],
);

/** Each preview asked; only the latest one's answer is kept, so a slow answer cannot overwrite a newer one. */
let previewSeq = 0;

async function runPreview(team: string | undefined) {
  const seq = ++previewSeq;
  previewing.value = true;
  previewProblem.value = '';

  try {
    const answer = await previewSolution(props.folder, team);
    if (seq !== previewSeq) return;
    preview.value = answer;
    if (!answer.ok) {
      previewProblem.value =
        answer.error ?? (answer.refusals?.length ? answer.refusals.map(refusalLine).join(' ') : 'The Host refused the preview.');
    }
  } catch (cause) {
    if (seq !== previewSeq) return;
    preview.value = null;
    previewProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    if (seq === previewSeq) previewing.value = false;
  }
}

async function previewName() {
  if (!checkPlan.value || nameProblem.value) return;
  await runPreview(teamName.value.trim());
}

let nameTimer: ReturnType<typeof setTimeout> | null = null;

/** A changed name is re-previewed after a short pause, so each keystroke does not ask the Host. */
watch(teamName, () => {
  if (nameTimer) clearTimeout(nameTimer);
  if (!checkPlan.value || mode.value !== 'install') return;
  nameTimer = setTimeout(() => {
    nameTimer = null;
    void previewName();
  }, 400);
});

onBeforeUnmount(() => {
  if (nameTimer) clearTimeout(nameTimer);
});

async function chooseMode(next: 'install' | 'update') {
  mode.value = next;
  preview.value = null;
  previewProblem.value = '';
  if (next === 'install') await previewName();
  else if (updateTeam.value) await runPreview(updateTeam.value);
}

async function chooseUpdateTeam(team: string) {
  updateTeam.value = team;
  mode.value = 'update';
  preview.value = null;
  await runPreview(team);
}

const teamReady = computed(() => {
  if (!checkPlan.value || previewing.value) return false;
  if (mode.value === 'install') return !nameProblem.value && !!installPreview.value && !nameRefusal.value;
  return !!updatePreview.value && updatePreview.value.team === updateTeam.value;
});

/** Next is offered while the name is legal and the Host has not refused it; Next itself re-asks. */
const teamCanAdvance = computed(() => {
  if (!checkPlan.value || previewing.value) return false;
  if (mode.value === 'install') return !nameProblem.value && !nameRefusal.value;
  return !!updatePreview.value && updatePreview.value.team === updateTeam.value;
});

/** Next from Team asks the Host again about the name as it stands, so a debounce cannot be skipped. */
async function nextFromTeam() {
  if (mode.value === 'install') {
    if (nameTimer) {
      clearTimeout(nameTimer);
      nameTimer = null;
    }
    if (nameProblem.value) return;
    if (!installPreview.value || installPreview.value.teamName !== teamName.value.trim()) await previewName();
  }
  if (teamReady.value) step.value = 'review';
}

// --- Review -------------------------------------------------------------------------------------

/** The plan to review: the preview's (which knows the team), else the check's. */
const plan = computed<SolutionPlan | null>(() => okPreview.value?.plan ?? checkPlan.value);
const diff = computed<SolutionDiff | null>(() => updatePreview.value?.diff ?? null);
const removed = computed(() => (diff.value ? removedItems(diff.value) : []));

function mark(section: keyof SolutionDiff, name: string) {
  return diffMark(diff.value?.[section] as SolutionDiffSection | undefined, name);
}

const openSkills = ref<Record<string, boolean>>({});

function memberKindLine(member: SolutionPlan['members'][number]) {
  if (member.kind === 'plugin') return `plugin ${member.pluginId ?? ''} ${member.pluginVersion ?? ''}`.trim();
  return `agent${member.preset ? `, preset ${member.preset}` : ', the team chooses the preset'}`;
}

// --- Your part ----------------------------------------------------------------------------------

const values = ref<Record<string, unknown>>({});
const bindings = ref<Record<string, string>>({});
const files = ref<Record<string, File | null>>({});

function seedValues(from: SolutionPlan) {
  values.value = Object.fromEntries(from.personSettings.map((setting) => [settingKey(setting), settingStartValue(setting)]));
}

function setValue(setting: SolutionPersonSetting, value: unknown) {
  values.value = { ...values.value, [settingKey(setting)]: value };
}

function listOf(setting: SolutionPersonSetting): string[] {
  const value = values.value[settingKey(setting)];
  return Array.isArray(value) ? value.map(String) : [];
}

// WHAT AN UPDATE KEEPS is shown, not asked again: the Host never changes a kept member's settings
// or bindings, and a document already in its folder does not leave the team blocked.
const kept = computed<SolutionKept | null>(() => updatePreview.value?.kept ?? null);

const keptSetting = (setting: SolutionPersonSetting) =>
  kept.value?.settings.find((entry) => settingKey(entry) === settingKey(setting));

const keptConnection = (input: { member: string; slot: string }) =>
  kept.value?.connections.find((entry) => slotKey(entry) === slotKey(input));

const keptFiles = (folder: string) => kept.value?.documents.find((entry) => entry.folder === folder)?.files ?? [];

/** The settings still asked for: an update's kept members are not asked again. */
const askedSettings = computed(() => (plan.value?.personSettings ?? []).filter((setting) => !keptSetting(setting)));

function connectionWords(id: string | null): string {
  if (!id) return 'not connected';
  return connectionOptions.value.find((option) => option.value === id)?.label ?? id;
}

const connectionOptions = computed(() =>
  (okPreview.value?.connections ?? []).map((connection) => ({
    value: connection.id,
    label: `${connection.name} - ${connection.account} (${connection.provider})${connection.status === 'ok' ? '' : `, ${connection.status}`}`,
  })),
);

// SECRETS, BY KEY NAME: whether each is needed follows the setting it depends on as the person
// chooses it here (or as the update keeps it).
const secrets = computed<SolutionSecret[]>(() => okPreview.value?.secrets ?? []);

function secretSettingValue(secret: SolutionSecret): unknown {
  if (!secret.when) return undefined;
  const key = settingKey({ member: secret.member, setting: secret.when.setting });
  const keptEntry = kept.value?.settings.find((entry) => settingKey(entry) === key);
  return keptEntry ? keptEntry.value : values.value[key];
}

const secretRows = computed(() =>
  secrets.value.map((secret) => {
    const state = secretState(secret, secretNeeded(secret, secretSettingValue(secret)));
    return { secret, state, sentence: secretSentence(secret, state) };
  }),
);

/**
 * NUMBER SETTINGS OUTSIDE THEIR BOUNDS, by setting key, with the sentence shown under each. Unlike a
 * skipped required setting - which installs and leaves the team blocked - an out-of-range value is
 * one the Host refuses, so it is held here, before Install: Next stays off until it is fixed.
 */
const settingProblems = computed<Record<string, string>>(() => {
  const problems: Record<string, string> = {};
  for (const setting of askedSettings.value) {
    if (settingInputKind(setting) !== 'number') continue;
    const problem = boundsProblem(setting.setting, setting, values.value[settingKey(setting)]);
    if (problem !== null) problems[settingKey(setting)] = problem;
  }
  return problems;
});

const settingsInRange = computed(() => Object.keys(settingProblems.value).length === 0);

/** A number setting's bounds for its hint, or undefined when it has none. */
const settingHint = (setting: SolutionPersonSetting) =>
  settingInputKind(setting) === 'number' ? (boundsHint(setting) ?? undefined) : undefined;

/** What only the person provides and was left out, by name, for the blocked sentence. */
const skippedRequired = computed<string[]>(() => {
  const current = plan.value;
  if (!current) return [];
  return [
    ...askedSettings.value
      .filter((setting) => setting.required && isBlank(values.value[settingKey(setting)]))
      .map((setting) => `the ${setting.member} setting ${setting.setting}`),
    ...current.inputs.connections
      .filter((input) => input.required && (keptConnection(input) ? !keptConnection(input)!.connection : !bindings.value[slotKey(input)]))
      .map((input) => `the ${input.member} connection ${input.slot}`),
    ...current.inputs.documents
      .filter((input) => input.required && !files.value[input.folder] && keptFiles(input.folder).length === 0)
      .map((input) => `a document in ${input.folder}/`),
  ];
});

const nothingToProvide = computed(() => {
  const current = plan.value;
  return (
    !!current &&
    current.personSettings.length === 0 &&
    current.inputs.connections.length === 0 &&
    current.inputs.documents.length === 0 &&
    secrets.value.length === 0
  );
});

function connectionsBody(): Record<string, Record<string, string>> {
  const body: Record<string, Record<string, string>> = {};
  for (const input of plan.value?.inputs.connections ?? []) {
    if (keptConnection(input)) continue;
    const id = bindings.value[slotKey(input)];
    if (id) (body[input.member] ??= {})[input.slot] = id;
  }
  return body;
}

// --- Install ------------------------------------------------------------------------------------

const installing = ref(false);
const result = ref<SolutionInstallResult | null>(null);
const installError = ref('');

interface Upload {
  folder: string;
  file: string;
  ok: boolean;
  problem: string;
}

const uploads = ref<Upload[]>([]);
const uploading = ref(false);
const missing = ref<SolutionMissing[] | null>(null);
const missingProblem = ref('');

const succeeded = computed(() => (result.value && result.value.ok ? result.value : null));

/** The keys the result says are still to set, each with the way to set it. */
const unsetSecrets = computed(() => {
  const done = succeeded.value;
  if (!done) return [];
  const unset = done.unset ?? [];
  return unset.map((key) => ({ key, setWith: secretSetWith(key) }));
});
/** Each schedule's first run, as the result names it: ran now, or when it first runs. */
const firstRuns = computed(() =>
  (succeeded.value?.firstRuns ?? []).map((run) => ({ trigger: run.trigger, ranNow: run.ranNow, line: firstRunLine(run) })),
);
const failedStep = computed(() => (result.value && !result.value.ok && 'step' in result.value ? result.value : null));
const failedCheck = computed(() => (result.value && !result.value.ok && 'refusals' in result.value ? result.value : null));

/** The steps an install goes through, in order, for the progress list before the Host answers. */
const plannedSteps: SolutionStep[] = Object.entries(SolutionStepTitles).map(([name, title], index) => ({
  step: name,
  number: index + 1,
  title,
  done: false,
}));

const shownSteps = computed<SolutionStep[]>(() => {
  const answered = result.value && 'steps' in result.value ? result.value.steps : null;
  return answered && answered.length > 0 ? answered : plannedSteps;
});

const installLine = computed(() => {
  const current = plan.value;
  if (!current) return '';
  if (updatePreview.value) {
    return `Update ${updatePreview.value.teamName} from ${updatePreview.value.from} to ${updatePreview.value.to}.`;
  }
  return `Install ${current.package.name} ${current.package.version} as the team ${teamName.value.trim()}.`;
});

async function install() {
  if (installing.value || succeeded.value || !plan.value || !settingsInRange.value) return;

  installing.value = true;
  result.value = null;
  installError.value = '';

  const settings = settingsBody(askedSettings.value, values.value);
  const connections = connectionsBody();

  try {
    result.value =
      updatePreview.value
        ? await updateSolution({ folder: props.folder, team: updatePreview.value.team, settings, connections })
        : await installSolution({
            folder: props.folder,
            teamName: teamName.value.trim(),
            localRepository: localRepository.value,
            settings,
            connections,
          });
  } catch (cause) {
    // A 409 (the name was taken meanwhile) or a 400 (the folder refused): the Host's sentence.
    installError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    installing.value = false;
  }

  const done = succeeded.value as SolutionInstallResult | null;
  if (done && done.ok) await afterInstall(done.team, done.missing);
}

/** Uploads each chosen document into its input's folder, then reads back what is still missing. */
async function afterInstall(team: string, answered: SolutionMissing[]) {
  missing.value = answered;
  uploading.value = true;

  for (const input of plan.value?.inputs.documents ?? []) {
    const file = files.value[input.folder];
    if (!file) continue;
    try {
      // The live team's documents folder is addressed by the team's identifier.
      await uploadDocument(asDocumentsFolderKey(team), file, input.folder);
      uploads.value = [...uploads.value, { folder: input.folder, file: file.name, ok: true, problem: '' }];
    } catch (cause) {
      uploads.value = [
        ...uploads.value,
        { folder: input.folder, file: file.name, ok: false, problem: cause instanceof Error ? cause.message : String(cause) },
      ];
    }
  }

  uploading.value = false;

  try {
    const record = await teamSolution(team);
    if (record) missing.value = record.missing;
  } catch (cause) {
    missingProblem.value = `What is still missing could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

async function openTeam() {
  const team = succeeded.value?.team;
  if (!team) return;
  await board.refreshForTeamCreated(team);
  board.setActiveTeam(asTeamId(team));
  board.showBoard();
  open.value = false;
  emit('opened', team);
}

// --- Moving between steps -----------------------------------------------------------------------

const canGoBack = computed(() => step.value !== 'team' && !installing.value && !succeeded.value);

function back() {
  const index = stepOrder.indexOf(step.value);
  if (index > 0) step.value = stepOrder[index - 1]!;
}

function next() {
  if (step.value === 'team') void nextFromTeam();
  else if (step.value === 'review') step.value = 'inputs';
  else if (step.value === 'inputs' && settingsInRange.value) step.value = 'install';
}
</script>

<template>
  <!-- A link's own query changing is the page's to handle (it reopens on the new folder), not a
       route change that dismisses the wizard. -->
  <q-dialog v-model="open" :persistent="installing" :no-route-dismiss="from === 'link'">
    <q-card class="os-dialog-lg solution-wizard" data-solution-wizard>
      <q-card-section class="row items-center q-pb-none">
        <div>
          <div class="os-dialog-title">Install a solution</div>
          <div class="text-caption os-text-muted mono" data-folder>{{ folder }}</div>
        </div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" :disable="installing" />
      </q-card-section>

      <q-card-section class="q-py-xs">
        <div class="row q-gutter-x-md text-caption" data-steps>
          <span
            v-for="(name, index) in stepOrder"
            :key="name"
            :class="name === step ? 'text-weight-bold' : 'os-text-muted'"
            :data-step-tab="name"
          >{{ index + 1 }}. {{ stepTitles[name] }}</span>
        </div>
      </q-card-section>

      <q-card-section class="solution-body">
        <div v-if="checking" class="os-text-muted">Checking the package…</div>

        <!-- A REFUSED FOLDER: the Host's sentence and nothing else. -->
        <q-banner v-else-if="folderRefusal" dense class="os-bg-tint-error text-negative" data-folder-refusal>
          <template #avatar><q-icon name="error" /></template>
          {{ folderRefusal }}
        </q-banner>

        <!-- A PACKAGE THAT FAILS ITS CHECK: every refusal at once, each "file field: reason". -->
        <div v-else-if="refusals.length > 0" data-refusals>
          <div class="text-negative q-mb-sm">This package cannot be installed. Change these and check it again:</div>
          <ul class="q-my-none">
            <li v-for="(refusal, index) in refusals" :key="index" data-refusal>{{ refusalLine(refusal) }}</li>
          </ul>
        </div>

        <template v-else-if="plan">
          <!-- 1. TEAM ------------------------------------------------------------------------- -->
          <div v-if="step === 'team'" data-step="team">
            <div class="text-subtitle1">{{ plan.package.name }} {{ plan.package.version }}</div>
            <div class="os-text-muted q-mb-md">{{ plan.package.description }}</div>

            <q-radio
              :model-value="mode"
              val="install"
              label="Install as a new team"
              data-mode-install
              @update:model-value="chooseMode('install')"
            />

            <div v-if="mode === 'install'" class="q-pl-lg q-mt-sm q-gutter-sm">
              <q-input
                v-model="teamName"
                outlined
                dense
                label="Team name"
                maxlength="200"
                :error="!!nameProblem || !!nameRefusal"
                :error-message="nameProblem ?? nameRefusal ?? ''"
                data-team-name
              />
              <q-checkbox
                v-model="localRepository"
                dense
                label="Create a local repository for this team"
                data-local-repository
              />
              <div class="text-caption os-text-muted">
                The team gets a local repository named after it, as a new team does. Untick for none.
              </div>
            </div>

            <template v-if="candidates.length > 0">
              <q-radio
                :model-value="mode"
                val="update"
                label="Update an existing team"
                class="q-mt-md"
                data-mode-update
                @update:model-value="chooseMode('update')"
              />
              <div class="q-pl-lg" data-update-candidates>
                <div
                  v-for="candidate in candidates"
                  :key="candidate.installed.team"
                  :data-update-team="candidate.installed.team"
                  :data-selectable="candidate.selectable"
                >
                  <q-radio
                    :model-value="mode === 'update' ? updateTeam : ''"
                    :val="candidate.installed.team"
                    :disable="!candidate.selectable"
                    :label="`${candidate.installed.teamName} (runs ${candidate.installed.version})`"
                    @update:model-value="chooseUpdateTeam(candidate.installed.team)"
                  />
                  <span v-if="!candidate.selectable" class="text-caption os-text-muted q-ml-sm" data-update-reason>
                    Cannot be updated to {{ plan.package.version }}: {{ candidate.installed.teamName }} is {{ candidate.reason }}.
                  </span>
                </div>
              </div>
            </template>

            <div v-if="previewing" class="os-text-muted q-mt-sm">Asking the Host…</div>
            <div v-if="previewProblem" class="text-negative q-mt-sm" data-preview-problem>{{ previewProblem }}</div>
          </div>

          <!-- 2. REVIEW ------------------------------------------------------------------------ -->
          <div v-else-if="step === 'review'" data-step="review">
            <div class="text-subtitle1">{{ plan.package.name }} {{ plan.package.version }}</div>
            <div class="q-mb-sm">{{ plan.package.description }}</div>
            <div v-if="updatePreview" class="text-weight-medium q-mb-sm" data-version-line>
              {{ updatePreview.teamName }}: {{ updatePreview.from }} -> {{ updatePreview.to }}
            </div>
            <div v-else class="q-mb-sm" data-new-team-line>A new team: {{ teamName.trim() }}</div>

            <section v-if="plan.team.instructions" class="q-mb-md">
              <div class="solution-heading">Team instructions</div>
              <pre class="solution-text" data-team-instructions>{{ plan.team.instructions }}</pre>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Members</div>
              <div v-for="member in plan.members" :key="member.name" class="solution-item" :data-member="member.name">
                <div class="row items-center q-gutter-x-sm">
                  <span class="text-weight-medium">{{ member.name }}</span>
                  <span class="os-text-muted">{{ member.role === 'manager' ? 'Manager' : 'member' }}, {{ memberKindLine(member) }}</span>
                  <q-badge v-if="mark('members', member.name)" outline color="primary" :label="mark('members', member.name)!" data-mark />
                </div>
                <pre v-if="member.instructions" class="solution-text" data-instructions>{{ member.instructions }}</pre>
              </div>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Triggers</div>
              <div v-if="plan.triggers.length === 0" class="os-text-muted">None</div>
              <div v-for="trigger in plan.triggers" :key="trigger.name" class="solution-item" :data-trigger="trigger.name">
                <div class="row items-center q-gutter-x-sm">
                  <span class="text-weight-medium">{{ trigger.name }}</span>
                  <span class="os-text-muted">{{ trigger.kind }}</span>
                  <q-badge v-if="mark('triggers', trigger.name)" outline color="primary" :label="mark('triggers', trigger.name)!" data-mark />
                </div>
                <dl class="solution-facts">
                  <dt>When</dt><dd data-source>{{ triggerSource(trigger) }}</dd>
                  <dt>Wakes</dt><dd data-target>{{ trigger.member }}</dd>
                  <dt>Manager</dt><dd data-wake>{{ wakeWords(trigger.wakeManager) }}</dd>
                  <dt>Daily cap</dt><dd data-cap>{{ capWords(trigger.dailyTokenCap) }}</dd>
                  <dt>Busy</dt><dd>{{ trigger.idleOnly ? 'Skipped while the member is busy' : 'Queued while the member is busy' }}</dd>
                </dl>
                <pre class="solution-text" data-instruction>{{ trigger.instruction }}</pre>
              </div>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Skills</div>
              <div v-if="plan.skills.length === 0" class="os-text-muted">None</div>
              <div v-for="skill in plan.skills" :key="skill.name" class="solution-item" :data-skill="skill.name">
                <div class="row items-center q-gutter-x-sm">
                  <span class="text-weight-medium mono">{{ skill.name }}</span>
                  <span class="os-text-muted">for {{ skill.roles.join(', ') }}</span>
                  <q-badge v-if="mark('skills', skill.name)" outline color="primary" :label="mark('skills', skill.name)!" data-mark />
                  <q-btn
                    flat
                    dense
                    no-caps
                    size="sm"
                    :label="openSkills[skill.name] ? 'Hide the skill' : 'Show the skill'"
                    @click="openSkills = { ...openSkills, [skill.name]: !openSkills[skill.name] }"
                  />
                </div>
                <div>{{ skill.description }}</div>
                <pre v-if="openSkills[skill.name]" class="solution-text" data-skill-body>{{ skill.body }}</pre>
              </div>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Sites</div>
              <div v-if="plan.sites.length === 0" class="os-text-muted">None</div>
              <div v-for="site in plan.sites" :key="site.name" :data-site="site.name">
                <span class="mono">{{ site.name }}</span>
                <span class="os-text-muted"> · {{ site.files.length }} {{ site.files.length === 1 ? 'file' : 'files' }}</span>
                <q-badge v-if="mark('sites', site.name)" outline color="primary" class="q-ml-sm" :label="mark('sites', site.name)!" data-mark />
              </div>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Tools</div>
              <div v-if="!plan.tools" class="os-text-muted">None</div>
              <div v-else data-tools>
                <div>Installed as <span class="mono">{{ plan.tools.installedAs }}/</span> in the team's folder:</div>
                <div v-for="file in plan.tools.files" :key="file" class="mono">
                  {{ file }}
                  <q-badge v-if="mark('tools', file)" outline color="primary" class="q-ml-sm" :label="mark('tools', file)!" data-mark />
                </div>
              </div>
            </section>

            <section class="q-mb-md">
              <div class="solution-heading">Plugins</div>
              <div v-if="plan.plugins.length === 0" class="os-text-muted">None</div>
              <div v-for="plugin in plan.plugins" :key="plugin.id" :data-plugin="plugin.id">
                <span class="mono">{{ plugin.id }} {{ plugin.version }}</span>
                <span v-if="plugin.description" class="os-text-muted"> · {{ plugin.description }}</span>
                <q-badge v-if="mark('plugins', plugin.id)" outline color="primary" class="q-ml-sm" :label="mark('plugins', plugin.id)!" data-mark />
              </div>
            </section>

            <section v-if="removed.length > 0" class="q-mb-md" data-removed>
              <div class="solution-heading">Removed by the update</div>
              <div v-for="item in removed" :key="item" data-removed-item>{{ item }}</div>
            </section>
          </div>

          <!-- 3. YOUR PART --------------------------------------------------------------------- -->
          <div v-else-if="step === 'inputs'" data-step="inputs" class="q-gutter-md">
            <div v-if="nothingToProvide" class="os-text-muted">This package needs nothing from you.</div>

            <section v-if="plan.personSettings.length > 0" data-person-settings>
              <div class="solution-heading">Settings</div>
              <div
                v-for="setting in plan.personSettings"
                :key="settingKey(setting)"
                class="q-mb-sm"
                :data-person-setting="settingKey(setting)"
              >
                <div v-if="keptSetting(setting)" data-kept-setting>
                  <span class="text-weight-medium">{{ setting.member }}: {{ setting.setting }}</span>
                  · kept: <span class="mono">{{ keptValueWords(keptSetting(setting)!.value) }}</span>
                  <div class="text-caption os-text-muted">
                    {{ setting.description }} The update keeps this; change it in the member's settings.
                  </div>
                </div>
                <template v-else>
                <q-toggle
                  v-if="settingInputKind(setting) === 'toggle'"
                  :model-value="values[settingKey(setting)] === true"
                  dense
                  :label="`${setting.member}: ${setting.setting}`"
                  @update:model-value="(value: boolean) => setValue(setting, value)"
                />
                <q-select
                  v-else-if="settingInputKind(setting) === 'choice'"
                  :model-value="values[settingKey(setting)]"
                  :options="setting.choices ?? []"
                  outlined
                  dense
                  clearable
                  :label="`${setting.member}: ${setting.setting}`"
                  @update:model-value="(value: unknown) => setValue(setting, value ?? '')"
                />
                <!-- A LIST - chosen from a fixed set, or free text added through its dialog - is the
                     product's one list input, as a member's plugin settings are. -->
                <ChipListInput
                  v-else-if="settingInputKind(setting) === 'choices' || settingInputKind(setting) === 'list'"
                  :model-value="listOf(setting)"
                  :options="settingInputKind(setting) === 'choices' ? setting.choices ?? [] : null"
                  :label="`${setting.member}: ${setting.setting}`"
                  :data-list-setting="settingKey(setting)"
                  @update:model-value="(value: string[]) => setValue(setting, value)"
                />
                <q-input
                  v-else
                  :model-value="values[settingKey(setting)] as string | number | null"
                  :type="settingInputKind(setting) === 'number' ? 'number' : 'text'"
                  outlined
                  dense
                  :label="`${setting.member}: ${setting.setting}`"
                  :hint="settingHint(setting)"
                  :error="settingProblems[settingKey(setting)] ? true : undefined"
                  :error-message="settingProblems[settingKey(setting)] ?? ''"
                  :data-out-of-range="settingProblems[settingKey(setting)] ? '' : undefined"
                  @update:model-value="(value: string | number | null) => setValue(setting, settingInputKind(setting) === 'number' && value !== '' && value !== null ? Number(value) : value)"
                />
                <div class="text-caption os-text-muted">
                  {{ setting.description }}
                  <template v-if="settingDefaultWords(setting) !== null"> Default: {{ settingDefaultWords(setting) }}.</template>
                  {{ setting.required ? 'Required.' : 'Optional.' }}
                </div>
                </template>
              </div>
            </section>

            <section v-if="plan.inputs.connections.length > 0" data-connections>
              <div class="solution-heading">Connections</div>
              <div v-for="input in plan.inputs.connections" :key="slotKey(input)" class="q-mb-sm" :data-connection-input="slotKey(input)">
                <div v-if="keptConnection(input)" data-kept-connection>
                  <span class="text-weight-medium">Connection for {{ input.member }}: {{ input.slot }}</span>
                  · kept: {{ connectionWords(keptConnection(input)!.connection) }}
                  <div class="text-caption os-text-muted">
                    {{ input.description }} The update keeps this; change it in the member's settings.
                  </div>
                </div>
                <template v-else>
                <q-select
                  :model-value="bindings[slotKey(input)] || null"
                  :options="connectionOptions"
                  emit-value
                  map-options
                  outlined
                  dense
                  clearable
                  :label="`Connection for ${input.member}: ${input.slot}`"
                  @update:model-value="(value: string | null) => (bindings = { ...bindings, [slotKey(input)]: value ?? '' })"
                />
                <div class="text-caption os-text-muted">
                  {{ input.description }} {{ input.required ? 'Required.' : 'Optional.' }}
                </div>
                <div v-if="connectionOptions.length === 0" class="text-caption os-text-muted" data-no-connections>
                  No connection yet. A person connects one in Admin → Connections.
                </div>
                </template>
              </div>
            </section>

            <section v-if="plan.inputs.documents.length > 0" data-documents>
              <div class="solution-heading">Documents</div>
              <div v-for="input in plan.inputs.documents" :key="input.folder" class="q-mb-sm" :data-document-input="input.folder">
                <q-file
                  :model-value="files[input.folder] ?? null"
                  outlined
                  dense
                  clearable
                  :label="`Upload to ${input.folder}/`"
                  @update:model-value="(file: File | null) => (files = { ...files, [input.folder]: file })"
                >
                  <template #prepend><q-icon name="upload_file" /></template>
                </q-file>
                <div class="text-caption os-text-muted">
                  <span class="mono">{{ input.folder }}/</span> · {{ input.description }}
                  <span v-if="input.required" class="text-weight-medium" data-required> · required</span>
                </div>
                <div v-if="keptFiles(input.folder).length > 0" class="text-caption" data-kept-files>
                  Already in {{ input.folder }}/: {{ keptFiles(input.folder).join(', ') }}. The update keeps them; upload
                  only to add another.
                </div>
              </div>
            </section>

            <section v-if="secretRows.length > 0" data-secrets>
              <div class="solution-heading">Secrets</div>
              <div class="text-caption os-text-muted q-mb-xs">
                The install binds each by its key name. Values are set on the Host by its operator, never here.
              </div>
              <div
                v-for="row in secretRows"
                :key="`${row.secret.member}/${row.secret.field}`"
                class="q-mb-sm"
                :data-secret="row.secret.key"
                :data-secret-state="row.state"
              >
                <span class="text-weight-medium mono">{{ row.secret.key }}</span>
                <span class="os-text-muted"> · {{ row.secret.member }}</span>
                <span v-if="row.state === 'set'" class="text-positive"> · set</span>
                <span v-else-if="row.state === 'unset'" class="text-warning"> · not set</span>
                <span v-else class="os-text-muted"> · not needed</span>
                <div class="text-caption os-text-muted">{{ row.secret.description }}</div>
                <div class="text-caption" data-secret-sentence>{{ row.sentence }}</div>
              </div>
            </section>

            <q-banner v-if="skippedRequired.length > 0" dense class="os-bg-tint-warn" data-skipped-required>
              <template #avatar><q-icon name="warning" /></template>
              You can install without {{ skippedRequired.join(', ') }}. Until it is provided, the team
              will show as blocked, waiting for {{ skippedRequired.length === 1 ? 'it' : 'them' }}.
            </q-banner>
          </div>

          <!-- 4. INSTALL ----------------------------------------------------------------------- -->
          <div v-else-if="step === 'install'" data-step="install">
            <div class="q-mb-md" data-install-line>{{ installLine }}</div>

            <div v-if="installing" class="row items-center q-gutter-sm q-mb-sm" data-installing>
              <q-spinner size="1.2em" />
              <span>{{ updatePreview ? 'Updating' : 'Installing' }}… this can take a minute.</span>
            </div>

            <q-banner v-if="failedStep" dense class="os-bg-tint-error text-negative q-mb-sm" data-install-failure>
              <template #avatar><q-icon name="error" /></template>
              {{ failureSentence(failedStep.step, failedStep.stepNumber, failedStep.reason, failedStep.steps) }}
            </q-banner>

            <div v-if="failedCheck" class="text-negative q-mb-sm" data-install-refusals>
              The package failed its check, and nothing was installed:
              <ul class="q-my-none">
                <li v-for="(refusal, index) in failedCheck.refusals" :key="index">{{ refusalLine(refusal) }}</li>
              </ul>
            </div>

            <q-banner v-if="installError" dense class="os-bg-tint-error text-negative q-mb-sm" data-install-error>
              <template #avatar><q-icon name="error" /></template>
              {{ installError }}
            </q-banner>

            <ol v-if="installing || result" class="q-my-sm" data-install-steps>
              <li v-for="item in shownSteps" :key="item.step" :data-install-step="item.step" :data-done="item.done">
                {{ item.title }}
                <span v-if="item.done" class="text-positive"> - done</span>
                <span v-else-if="failedStep && failedStep.step === item.step" class="text-negative"> - failed, undone</span>
              </li>
            </ol>

            <template v-if="succeeded">
              <div class="text-positive q-mb-sm" data-install-success>
                {{ updatePreview ? 'Updated' : 'Installed' }} {{ succeeded.teamName }} ({{ succeeded.version }}).
              </div>

              <div v-if="uploading" class="os-text-muted">Uploading your documents…</div>
              <div v-for="upload in uploads" :key="upload.folder + upload.file" :data-upload="upload.folder">
                <span v-if="upload.ok">Uploaded {{ upload.file }} to {{ upload.folder }}/.</span>
                <span v-else class="text-negative">{{ upload.file }} was not uploaded to {{ upload.folder }}/: {{ upload.problem }}</span>
              </div>

              <div v-if="missingProblem" class="text-negative" data-missing-problem>{{ missingProblem }}</div>
              <div v-else-if="missing && missing.length > 0 && !uploading" class="q-mt-sm" data-still-missing>
                <div>The team shows as blocked until these are provided:</div>
                <div v-for="(item, index) in missing" :key="index" data-missing-item>{{ missingLine(item) }}</div>
              </div>
              <div v-else-if="missing && !uploading" class="q-mt-sm" data-nothing-missing>Nothing is missing: the team is ready.</div>

              <div v-if="firstRuns.length > 0" class="q-mt-sm" data-first-runs>
                <div v-for="run in firstRuns" :key="run.trigger" :data-first-run="run.trigger" :data-ran-now="run.ranNow">
                  {{ run.line }}.
                </div>
              </div>

              <div v-if="unsetSecrets.length > 0" class="q-mt-sm" data-unset-secrets>
                <div>These keys are still not set on the Host. Each one's source fails until it is set:</div>
                <div v-for="secret in unsetSecrets" :key="secret.key" :data-unset-secret="secret.key">
                  <span class="mono">{{ secret.key }}</span> - {{ secret.setWith }}.
                </div>
              </div>
            </template>
          </div>
        </template>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-if="canGoBack && plan" flat no-caps label="Back" @click="back" />
        <q-space />
        <q-btn v-close-popup flat no-caps :label="succeeded ? 'Close' : 'Cancel'" :disable="installing" />
        <q-btn
          v-if="plan && step !== 'install'"
          color="primary"
          no-caps
          label="Next"
          :disable="(step === 'team' && !teamCanAdvance) || (step === 'inputs' && !settingsInRange)"
          :loading="step === 'team' && previewing"
          @click="next"
        />
        <q-btn
          v-if="plan && step === 'install' && !succeeded"
          color="primary"
          no-caps
          :label="updatePreview ? 'Update' : 'Install'"
          :loading="installing"
          :disable="installing"
          data-install-button
          @click="install"
        />
        <q-btn
          v-if="succeeded && !uploading"
          color="primary"
          no-caps
          label="Open the team"
          data-open-team
          @click="openTeam"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.solution-body {
  max-height: 65vh;
  overflow-y: auto;
  /* A package's text (a path, a URL, a long name) wraps rather than widening the dialog. */
  overflow-wrap: anywhere;
}

.solution-heading {
  font-weight: 600;
  margin-bottom: 4px;
}

.solution-item {
  margin-bottom: 12px;
}

.solution-text {
  margin: 4px 0 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  background: var(--os-surface-2, transparent);
  border-left: 2px solid var(--os-ink-faint, #ccc);
  padding: 4px 8px;
}

.solution-facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 12px;
  row-gap: 2px;
  margin: 4px 0 0;
}

.solution-facts dt {
  color: var(--os-ink-muted);
}

.solution-facts dd {
  margin: 0;
}

</style>
