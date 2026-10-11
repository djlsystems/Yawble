<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { computed, onMounted, ref } from 'vue';
import ConnectDialog from './ConnectDialog.vue';
import MailboxForm from './MailboxForm.vue';
import SetupQuestion from './SetupQuestion.vue';
import SolutionPlanReview from './SolutionPlanReview.vue';
import {
  getPluginSettings,
  installSolution,
  listConnectionProviders,
  listConnections,
  listMailboxPresets,
  previewSolution,
  runScheduleNow,
  solutionPanel,
  teamSolution,
} from '../api/client';
import { uploadDocument } from '../api/documents';
import {
  asDocumentsFolderKey,
  asTeamId,
  type Connection,
  type ConnectionProvider,
  type MailboxPreset,
  type SolutionMissing,
  type SolutionPlan,
  type SolutionPlanSetup,
  type SolutionSetupConnection,
  type SolutionSetupDocument,
  type SolutionSetupSetting,
} from '../api/types';
import {
  SetupStepTitles,
  advancedConnections,
  advancedDocuments,
  answerWords,
  asPersonSetting,
  finishLine,
  mailboxSettings,
  missingScreen,
  offeredFor,
  preferenceSettings,
  questionText,
  setupScreens,
  setupSettingsBody,
  setupSteps,
  startValue,
  teamNameCandidate,
  type SetupScreen,
} from '../lib/setup';
import { capWords, settingInputKind, settingKey, slotKey, triggerSource } from '../lib/solutions';
import { boundsHint, boundsProblem } from '../lib/numberBounds';
import { bindSlot, saveSettingValues } from '../lib/slotBinding';
import { useConsoleStore } from '../stores/console';

/**
 * THE GUIDED SETUP WIZARD, for a package with a `setup` block: one plain question per screen, a
 * default filled in, Next and Back, the named steps shown as progress.
 *
 * 1. **Welcome** - what the solution does and what you will need. Advanced…: the team name (the
 *    package's name, "Job Tracker 2" when taken - changed only here), what it adds, every schedule
 *    and daily cap.
 * 2. **Your mailbox**, one per connection question - the guided mailbox tiles (`MailboxForm`) and
 *    Outlook's sign-in. The login is tested before Next: "Connected: 1,240 messages in Inbox", or the
 *    Host's one sentence on what to fix. "Do this later" skips it. Advanced…: a Google app of one's
 *    own, the servers and port, which folder to watch.
 * 3. **Your documents**, one per document question - the file it names. Advanced…: the folder it is
 *    kept in.
 * 4. **Your preferences** - the package's questions with their defaults. Advanced…: every other
 *    setting, each with its bounds, and any input the package asks nothing about.
 * 5. **Ready** - the answers in sentences, then Set up; afterwards Check now (Run now on the
 *    package's first schedule). Advanced…: the full plan, as the install wizard's Review shows it.
 *
 * THEIR WORDS: the main flow never says trigger, cron, plugin, token, slot, member or a folder path;
 * those wait under Advanced…. Package text is text, never HTML.
 *
 * The caller's dialog card holds it (`os-dialog-md`), so the card stays on the dialog scale.
 *
 * THE INSTALL IS TODAY'S: Set up sends the install wizard's request (`/api/solutions/install`) with
 * the same answers, then uploads the documents and reads back what is still missing. Nothing is
 * installed before Set up.
 *
 * CHANGE SETUP (`team`): opened from the panel on the installed team's current answers. Each change
 * is saved as the screen is left, through the routes a person's other screens use - the member's
 * settings route for answers and mailbox bindings, the documents upload - and nothing is installed.
 */
const props = defineProps<{
  plan: SolutionPlan;
  /** The package folder the install is made from. */
  folder: string;
  /** Change setup: the installed team whose answers are changed. Left out, this installs. */
  team?: string;
  /** The screen to open on (`mailbox:Scout/mail`), for Finish setup. */
  start?: string | null;
}>();

const emit = defineEmits<{
  /** Setting up: the dialog holds until it answers. */
  busy: [busy: boolean];
  close: [];
  /** Open the team, after Set up. */
  opened: [team: string];
  /** Change setup saved something: whoever shows the team reads it again. */
  changed: [];
}>();

const board = useConsoleStore();

const setup = computed<SolutionPlanSetup>(() => props.plan.setup!);
const changing = computed(() => !!props.team);

// --- Screens --------------------------------------------------------------------------------------

const screens = computed(() => setupScreens(setup.value));
const steps = computed(() => setupSteps(screens.value));
const index = ref(0);
const screen = computed<SetupScreen>(() => screens.value[index.value] ?? screens.value[0]!);
const advancedOpen = ref(false);
/** What the last save or step refused, shown above the buttons. */
const problem = ref('');
const saving = ref(false);

function go(to: number) {
  index.value = Math.max(0, Math.min(to, screens.value.length - 1));
  advancedOpen.value = false;
  problem.value = '';
}

// --- Answers --------------------------------------------------------------------------------------

const values = ref<Record<string, unknown>>(
  Object.fromEntries(setup.value.settings.map((setting) => [settingKey(setting), startValue(setting)])),
);
/** Change setup: the answers as stored, so only what changed is saved. */
const storedValues = ref<Record<string, unknown>>({});
const bindings = ref<Record<string, string>>({});
/** Change setup: the bindings as stored, so a pick under Advanced… is saved only when it changed. */
const storedBindings = ref<Record<string, string>>({});
/** Each mailbox's login sentence, by its question's key, once it is connected here or chosen. */
const sentences = ref<Record<string, string>>({});
const files = ref<Record<string, File | null>>({});

const setValue = (setting: SolutionSetupSetting, value: unknown) => (values.value = { ...values.value, [settingKey(setting)]: value });

/** Number answers outside their bounds, by key: Next stays off on their screen until fixed. */
const problems = computed<Record<string, string>>(() => {
  const found: Record<string, string> = {};
  for (const setting of setup.value.settings) {
    if (settingInputKind(asPersonSetting(setting)) !== 'number') continue;
    const problem = boundsProblem('This answer', setting, values.value[settingKey(setting)]);
    if (problem) found[settingKey(setting)] = problem;
  }
  return found;
});

// --- Loading --------------------------------------------------------------------------------------

const presets = ref<MailboxPreset[]>([]);
const providers = ref<ConnectionProvider[]>([]);
const connections = ref<Connection[]>([]);
const loading = ref(false);

/** Change setup: package member name -> the team's stored member name. */
const storedMembers = ref<Record<string, string>>({});
const storedMember = (member: string) => storedMembers.value[member] ?? member;

onMounted(() => void load());

async function load() {
  loading.value = true;
  try {
    await Promise.all([loadMailboxes(), changing.value ? loadCurrent() : pickTeamName()]);
  } finally {
    loading.value = false;
  }
  const opening = props.start ? screens.value.findIndex((candidate) => candidate.id === props.start) : -1;
  if (opening > 0) go(opening);
}

async function loadMailboxes() {
  if (setup.value.connections.length === 0) return;
  await Promise.all([
    listMailboxPresets()
      .then((read) => (presets.value = read))
      .catch(() => undefined),
    listConnections()
      .then((read) => (connections.value = read))
      .catch(() => undefined),
    listConnectionProviders()
      .then((read) => (providers.value = read))
      .catch(() => undefined),
  ]);
}

/** Change setup: the team's current answers, read from each member's stored settings. */
async function loadCurrent() {
  const team = props.team!;
  try {
    const panel = await solutionPanel(team);
    storedMembers.value = Object.fromEntries(panel.members.map((member) => [member.packageName, member.member]));
    panelTriggers.value = panel.triggers.map((trigger) => ({ id: trigger.id, packageName: trigger.packageName, runNow: trigger.runNow }));
  } catch (cause) {
    problem.value = `The team could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
    return;
  }
  const members = [...new Set([...setup.value.settings, ...setup.value.connections].map((entry) => entry.member))];
  const nextValues = { ...values.value };
  const nextBindings: Record<string, string> = {};
  await Promise.all(
    members.map(async (member) => {
      try {
        const stored = await getPluginSettings(team, storedMember(member));
        for (const setting of setup.value.settings.filter((entry) => entry.member === member)) {
          const current = stored.config[setting.setting];
          if (current !== undefined && current !== null) nextValues[settingKey(setting)] = current;
        }
        for (const connection of setup.value.connections.filter((entry) => entry.member === member)) {
          const bound = stored.connections?.[connection.slot];
          if (bound) nextBindings[slotKey(connection)] = bound;
        }
      } catch {
        // This member's answers start at their defaults; saving still goes through its route.
      }
    }),
  );
  values.value = nextValues;
  storedValues.value = { ...nextValues };
  bindings.value = nextBindings;
  storedBindings.value = { ...nextBindings };
  try {
    const record = await teamSolution(team);
    missing.value = record?.missing ?? null;
  } catch {
    // Without it nothing says what is still missing.
  }
}

// --- Welcome: the team name -----------------------------------------------------------------------

const teamName = ref(props.plan.team.name);
const nameProblem = ref('');
/** The name the Host last accepted, so Next asks again only about a changed one. */
let acceptedName = '';

/** The package's name, else "Name 2", "Name 3"… - the first the Host does not refuse. */
async function pickTeamName() {
  for (let attempt = 1; attempt <= 20; attempt++) {
    const name = teamNameCandidate(props.plan.team.name, attempt);
    try {
      const answer = await previewSolution(props.folder, name);
      if (!answer.ok) break;
      if (answer.mode === 'install' && !answer.nameRefusal) {
        teamName.value = name;
        acceptedName = name;
        return;
      }
    } catch {
      break;
    }
  }
  // Not answered: the name stays the package's, and Set up asks the Host.
}

/** Next from Welcome: a name changed under Advanced… is asked about; a refused one opens Advanced…. */
async function checkTeamName(): Promise<boolean> {
  const name = teamName.value.trim();
  if (changing.value || name === acceptedName) return true;
  nameProblem.value = '';
  if (name === '') {
    nameProblem.value = 'Give the team a name.';
  } else {
    try {
      const answer = await previewSolution(props.folder, name);
      if (answer.ok && answer.mode === 'install' && answer.nameRefusal) nameProblem.value = answer.nameRefusal;
      else if (!answer.ok) nameProblem.value = answer.error ?? 'That name cannot be used.';
    } catch (cause) {
      nameProblem.value = cause instanceof Error ? cause.message : String(cause);
    }
  }
  if (nameProblem.value) {
    advancedOpen.value = true;
    return false;
  }
  acceptedName = name;
  return true;
}

const schedules = computed(() => props.plan.triggers);

// --- Your mailbox ---------------------------------------------------------------------------------

const mailboxForm = ref<InstanceType<typeof MailboxForm> | null>(null);

const takes = (connection: SolutionSetupConnection, provider: string) => connection.providers.includes(provider);
const mailboxTiles = (connection: SolutionSetupConnection) => (takes(connection, 'imap') ? presets.value : []);

const boundTo = (connection: SolutionSetupConnection) => {
  const id = bindings.value[slotKey(connection)];
  return id ? (connections.value.find((candidate) => candidate.id === id) ?? null) : null;
};

const boundSentence = (connection: SolutionSetupConnection) => {
  const key = slotKey(connection);
  if (sentences.value[key]) return sentences.value[key];
  const bound = boundTo(connection);
  return bound ? `Connected: ${bound.account}` : 'Connected.';
};

/** The person's working connections this question takes, offered so a mailbox is not added twice. */
const offered = (connection: SolutionSetupConnection) =>
  connections.value.filter((candidate) => offeredFor(candidate, connection) && candidate.id !== bindings.value[slotKey(connection)]);

/** A mailbox connected or chosen: bound for the install, or - changing setup - bound now. */
async function choose(connection: SolutionSetupConnection, chosen: Connection, sentence: string) {
  const key = slotKey(connection);
  connections.value = [...connections.value.filter((candidate) => candidate.id !== chosen.id), chosen];
  if (changing.value) {
    saving.value = true;
    problem.value = '';
    try {
      await bindSlot(props.team!, storedMember(connection.member), connection.slot, chosen.id);
      emit('changed');
    } catch (cause) {
      problem.value = cause instanceof Error ? cause.message : String(cause);
      return;
    } finally {
      saving.value = false;
    }
  }
  bindings.value = { ...bindings.value, [key]: chosen.id };
  sentences.value = { ...sentences.value, [key]: sentence };
}

function chooseAnother(connection: SolutionSetupConnection) {
  const { [slotKey(connection)]: _, ...rest } = bindings.value;
  bindings.value = rest;
}

async function testAndConnect() {
  await mailboxForm.value?.save();
}

/** Outlook's sign-in, or Google through an app of one's own: today's Add connection, for this question. */
const signIn = ref<{ open: boolean; connection: SolutionSetupConnection | null; providers: string[] }>({
  open: false,
  connection: null,
  providers: [],
});

function openSignIn(connection: SolutionSetupConnection, provider: string) {
  signIn.value = { open: true, connection, providers: [provider] };
}

const signInNeed = computed(() => {
  const connection = signIn.value.connection;
  if (!connection) return null;
  return {
    plugin: connection.plugin ?? '',
    slot: connection.slot,
    providers: signIn.value.providers,
    scopes: connection.scopes,
    pluginName: props.plan.plugins.find((plugin) => plugin.id === connection.plugin)?.name,
  };
});

function signedIn(made: Connection) {
  const connection = signIn.value.connection;
  if (connection) void choose(connection, made, `Connected: ${made.account}`);
}

// --- Your documents -------------------------------------------------------------------------------

const fileInput = ref<HTMLInputElement | null>(null);

function pickFile(document: SolutionSetupDocument, event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0] ?? null;
  files.value = { ...files.value, [document.folder]: file };
}

// --- Your preferences -----------------------------------------------------------------------------

const preferences = computed(() => preferenceSettings(setup.value));
const mainPreferences = computed(() => preferences.value.filter((setting) => !setting.advanced));
const advancedPreferences = computed(() => preferences.value.filter((setting) => setting.advanced));
const otherConnections = computed(() => advancedConnections(setup.value));
const otherDocuments = computed(() => advancedDocuments(setup.value));

/** Every setting's bounds, for Advanced…: the main questions' too. */
const mainBounds = computed(() =>
  mainPreferences.value
    .map((setting) => ({ setting, words: settingInputKind(asPersonSetting(setting)) === 'number' ? boundsHint(setting) : null }))
    .filter((entry) => entry.words),
);

/** The settings a screen shows, main and Advanced…, whose answers it saves when changing setup. */
function screenSettings(current: SetupScreen): SolutionSetupSetting[] {
  if (current.kind === 'mailbox' && current.connection) return mailboxSettings(setup.value, current.connection);
  if (current.kind === 'preferences') return preferences.value;
  return [];
}

const screenProblem = computed(() => screenSettings(screen.value).some((setting) => problems.value[settingKey(setting)]));

// --- Moving ---------------------------------------------------------------------------------------

const canGoOn = computed(() => {
  if (loading.value || saving.value || screenProblem.value) return false;
  const current = screen.value;
  if (current.kind === 'mailbox' && current.connection) return !!bindings.value[slotKey(current.connection)];
  if (current.kind === 'documents' && current.document) return changing.value || !!files.value[current.document.folder];
  return true;
});

/** "Do this later": a mailbox or document left for the panel's Finish setup. */
const canSkip = computed(() => {
  const current = screen.value;
  if (current.kind === 'mailbox' && current.connection) return !bindings.value[slotKey(current.connection)];
  if (current.kind === 'documents' && current.document) return !changing.value && !files.value[current.document.folder];
  return false;
});

async function next() {
  if (!canGoOn.value) return;
  if (screen.value.kind === 'welcome' && !(await checkTeamName())) return;
  if (changing.value && !(await saveScreen(screen.value))) return;
  go(index.value + 1);
}

function skip() {
  go(index.value + 1);
}

/** Change setup: what this screen changed, saved through the member's settings route and the upload. */
async function saveScreen(current: SetupScreen): Promise<boolean> {
  const team = props.team!;
  saving.value = true;
  problem.value = '';
  try {
    const changed = screenSettings(current).filter(
      (setting) => JSON.stringify(values.value[settingKey(setting)]) !== JSON.stringify(storedValues.value[settingKey(setting)]),
    );
    const byMember = new Map<string, Record<string, unknown>>();
    for (const setting of changed) {
      byMember.set(setting.member, { ...(byMember.get(setting.member) ?? {}), [setting.setting]: values.value[settingKey(setting)] });
    }
    for (const [member, changes] of byMember) await saveSettingValues(team, storedMember(member), changes);
    for (const setting of changed) storedValues.value[settingKey(setting)] = values.value[settingKey(setting)];

    // A connection picked for an input the package asks nothing about, bound through the same route.
    const rebound = current.kind === 'preferences' ? otherConnections.value.filter((connection) => bindings.value[slotKey(connection)] !== storedBindings.value[slotKey(connection)]) : [];
    for (const connection of rebound) {
      const id = bindings.value[slotKey(connection)] ?? null;
      await bindSlot(team, storedMember(connection.member), connection.slot, id);
      storedBindings.value = id ? { ...storedBindings.value, [slotKey(connection)]: id } : Object.fromEntries(Object.entries(storedBindings.value).filter(([key]) => key !== slotKey(connection)));
    }

    const documents = current.kind === 'documents' && current.document ? [current.document] : current.kind === 'preferences' ? otherDocuments.value : [];
    let saved = changed.length > 0 || rebound.length > 0;
    for (const document of documents) {
      const file = files.value[document.folder];
      if (!file) continue;
      await uploadDocument(asDocumentsFolderKey(team), file, document.folder);
      files.value = { ...files.value, [document.folder]: null };
      uploaded.value = [...uploaded.value, file.name];
      saved = true;
    }
    if (saved) emit('changed');
    return true;
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
    return false;
  } finally {
    saving.value = false;
  }
}

// --- Ready ----------------------------------------------------------------------------------------

/** The answers in sentences, in the order they were asked. */
const summary = computed<string[]>(() => {
  const lines: string[] = [];
  if (!changing.value) lines.push(`Set up makes the team ${teamName.value.trim()}.`);
  for (const connection of setup.value.connections) {
    const bound = boundTo(connection);
    if (bound) lines.push(`Your mailbox is ${bound.account}.`);
    else if (!connection.advanced) lines.push('No mailbox is connected yet: you can connect one later.');
  }
  for (const document of setup.value.documents) {
    const file = files.value[document.folder];
    if (file) lines.push(`${questionText(document).replace(/[.?]$/, '')}: ${file.name}.`);
    else if (!document.advanced && !changing.value) lines.push(`${questionText(document).replace(/[.?]$/, '')}: not yet, you can do it later.`);
  }
  for (const setting of setup.value.settings.filter((entry) => !entry.advanced)) {
    lines.push(`${questionText(setting).replace(/[.?]$/, '')}: ${answerWords(values.value[settingKey(setting)])}.`);
  }
  return lines;
});

const installing = ref(false);
const installed = ref<{ team: string; teamName: string } | null>(null);
const missing = ref<SolutionMissing[] | null>(null);
const uploaded = ref<string[]>([]);
const notUploaded = ref<string[]>([]);

async function setUp() {
  if (installing.value || installed.value) return;
  installing.value = true;
  emit('busy', true);
  problem.value = '';
  try {
    const result = await installSolution({
      folder: props.folder,
      teamName: teamName.value.trim(),
      localRepository: true,
      settings: setupSettingsBody(setup.value.settings, values.value, props.plan.personSettings),
      connections: connectionsBody(),
    });
    if (!result.ok) {
      problem.value =
        'step' in result
          ? `Set up stopped at step ${result.stepNumber}: ${result.reason} Nothing was left behind.`
          : `The package failed its check, and nothing was set up: ${result.refusals.map((refusal) => refusal.reason).join(' ')}`;
      return;
    }
    installed.value = { team: result.team, teamName: result.teamName };
    missing.value = result.missing;
    await afterSetUp(result.team);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    installing.value = false;
    emit('busy', false);
  }
}

function connectionsBody(): Record<string, Record<string, string>> {
  const body: Record<string, Record<string, string>> = {};
  for (const connection of setup.value.connections) {
    const id = bindings.value[slotKey(connection)];
    if (id) (body[connection.member] ??= {})[connection.slot] = id;
  }
  return body;
}

/** The chosen documents go into their folders, then what is still missing is read back. */
async function afterSetUp(team: string) {
  for (const document of setup.value.documents) {
    const file = files.value[document.folder];
    if (!file) continue;
    try {
      await uploadDocument(asDocumentsFolderKey(team), file, document.folder);
      uploaded.value = [...uploaded.value, file.name];
    } catch (cause) {
      notUploaded.value = [...notUploaded.value, `${file.name} was not uploaded: ${cause instanceof Error ? cause.message : String(cause)}`];
    }
  }
  try {
    const record = await teamSolution(team);
    if (record) missing.value = record.missing;
  } catch {
    // The install's own answer stands.
  }
}

/** "Finish setup: connect a mailbox", from what is still missing. */
const finish = computed(() => (missing.value ? finishLine(missing.value, setup.value, invertedMembers.value) : null));
const invertedMembers = computed(() => Object.fromEntries(Object.entries(storedMembers.value).map(([name, stored]) => [stored, name])));
const finishScreen = computed(() => {
  const first = (missing.value ?? []).map((item) => missingScreen(item, setup.value, invertedMembers.value)).find(Boolean);
  return first ?? null;
});

function openFinish() {
  const to = screens.value.findIndex((candidate) => candidate.id === finishScreen.value);
  if (to >= 0) go(to);
}

// CHECK NOW: Run now on the package's first schedule, found among the team's own by its name.
const panelTriggers = ref<{ id: string; packageName: string; runNow: boolean }[]>([]);
const checking = ref(false);
const checkSentence = ref('');
const firstSchedule = computed(() => props.plan.triggers.find((trigger) => trigger.kind === 'schedule') ?? null);
const setUpTeam = computed(() => props.team ?? installed.value?.team ?? null);

async function checkNow() {
  const team = setUpTeam.value;
  const schedule = firstSchedule.value;
  if (!team || !schedule || checking.value) return;
  checking.value = true;
  checkSentence.value = '';
  try {
    if (panelTriggers.value.length === 0) {
      const panel = await solutionPanel(team);
      panelTriggers.value = panel.triggers.map((trigger) => ({ id: trigger.id, packageName: trigger.packageName, runNow: trigger.runNow }));
    }
    const trigger = panelTriggers.value.find((candidate) => candidate.packageName === schedule.name);
    if (!trigger) {
      checkSentence.value = 'There is nothing to check yet.';
      return;
    }
    const run = await runScheduleNow(asTeamId(team), trigger.id);
    checkSentence.value =
      run.outcome === 'fired'
        ? 'Checking now. What it finds shows on the team\'s page.'
        : `Not checked now${run.reason ? `: ${run.reason}` : '.'}`;
  } catch (cause) {
    checkSentence.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    checking.value = false;
  }
}

async function openTeam() {
  const team = installed.value?.team;
  if (!team) return;
  await board.refreshForTeamCreated(team);
  board.setActiveTeam(asTeamId(team));
  board.showBoard();
  emit('opened', team);
}

const title = computed(() => (changing.value ? `Change setup: ${props.plan.package.name}` : `Set up ${props.plan.package.name}`));
const isLast = computed(() => screen.value.kind === 'ready');
</script>

<template>
  <div class="setup-wizard" data-setup-wizard>
    <q-card-section class="row items-center q-pb-none">
      <div class="os-dialog-title">{{ title }}</div>
      <q-space />
      <q-btn flat round dense icon="close" aria-label="Close" :disable="installing" @click="emit('close')" />
    </q-card-section>

    <q-card-section class="q-py-xs">
      <div class="row q-gutter-x-md text-caption" data-setup-steps>
        <span
          v-for="(kind, position) in steps"
          :key="kind"
          :class="kind === screen.kind ? 'text-weight-bold' : 'os-text-muted'"
          :data-setup-step-tab="kind"
          :data-current="kind === screen.kind"
        >{{ position + 1 }}. {{ SetupStepTitles[kind] }}</span>
      </div>
    </q-card-section>

    <q-card-section class="setup-body" :data-setup-screen="screen.kind" :data-setup-id="screen.id">
      <div v-if="loading" class="os-text-muted">One moment…</div>

      <!-- 1. WELCOME ------------------------------------------------------------------------------ -->
      <template v-else-if="screen.kind === 'welcome'">
        <div class="text-subtitle1 q-mb-sm">{{ plan.package.name }}</div>
        <div class="setup-welcome q-mb-md" data-setup-welcome>{{ setup.welcome }}</div>
        <div v-if="setup.needs" data-setup-needs>You will need {{ setup.needs }}.</div>

        <div v-if="advancedOpen" class="setup-advanced" data-setup-advanced>
          <q-input
            v-if="!changing"
            v-model="teamName"
            outlined
            dense
            label="Team name"
            maxlength="200"
            :error="!!nameProblem"
            :error-message="nameProblem"
            data-setup-team-name
          />
          <div v-else data-setup-team-name>Team: {{ plan.team.name }}</div>
          <div class="setup-heading">What it adds</div>
          <div v-for="member in plan.members" :key="member.name" :data-setup-adds="member.name">
            {{ member.name }} - {{ member.kind === 'plugin' ? `plugin ${member.pluginId ?? ''}` : 'agent' }}{{ member.role === 'manager' ? ', the Manager' : '' }}
          </div>
          <div v-for="plugin in plan.plugins" :key="plugin.id" :data-setup-adds-plugin="plugin.id">
            Plugin {{ plugin.name }} {{ plugin.version }}
          </div>
          <div class="setup-heading">Schedules and daily caps</div>
          <div v-for="trigger in schedules" :key="trigger.name" :data-setup-schedule="trigger.name">
            {{ trigger.name }}: {{ triggerSource(trigger) }}. Daily cap: {{ capWords(trigger.dailyTokenCap) }}.
          </div>
        </div>
      </template>

      <!-- 2. YOUR MAILBOX ------------------------------------------------------------------------- -->
      <template v-else-if="screen.kind === 'mailbox' && screen.connection">
        <div class="text-subtitle1">{{ questionText(screen.connection) }}</div>
        <div v-if="screen.connection.hint" class="os-text-muted q-mb-md">{{ screen.connection.hint }}</div>

        <template v-if="bindings[slotKey(screen.connection)]">
          <div class="text-positive q-mb-sm" data-mailbox-sentence>{{ boundSentence(screen.connection) }}</div>
          <q-btn flat dense no-caps color="primary" label="Use another mailbox" @click="chooseAnother(screen.connection)" />
        </template>
        <template v-else>
          <div v-if="offered(screen.connection).length > 0" class="q-mb-md" data-setup-offered>
            <div class="text-caption os-text-muted">Already connected:</div>
            <q-btn
              v-for="connection in offered(screen.connection)"
              :key="connection.id"
              outline
              no-caps
              class="q-mr-sm q-mt-xs"
              :label="`Use ${connection.account}`"
              :data-setup-use="connection.id"
              @click="choose(screen.connection, connection, `Connected: ${connection.account}`)"
            />
          </div>
          <MailboxForm
            v-if="mailboxTiles(screen.connection).length > 0"
            :key="screen.id"
            ref="mailboxForm"
            :presets="mailboxTiles(screen.connection)"
            :preset="mailboxTiles(screen.connection)[0]!.id"
            :advanced="takes(screen.connection, 'google')"
            :servers="advancedOpen || mailboxForm?.presetId === 'other'"
            @advanced="advancedOpen = true"
            @saved="(connection: Connection, sentence: string) => choose(screen.connection!, connection, sentence)"
          />
          <div class="row q-gutter-sm q-mt-sm">
            <q-btn
              v-if="mailboxTiles(screen.connection).length > 0"
              color="primary"
              no-caps
              label="Test and connect"
              :loading="mailboxForm?.busy"
              :disable="!mailboxForm?.ready"
              data-setup-test
              @click="testAndConnect"
            />
            <q-btn
              v-if="takes(screen.connection, 'microsoft')"
              outline
              no-caps
              label="Outlook or Microsoft 365"
              data-setup-outlook
              @click="openSignIn(screen.connection, 'microsoft')"
            />
          </div>
        </template>

        <div v-if="advancedOpen" class="setup-advanced" data-setup-advanced>
          <q-btn
            v-if="takes(screen.connection, 'google')"
            flat
            dense
            no-caps
            color="primary"
            label="Gmail through a Google app of your own"
            data-setup-google-app
            @click="openSignIn(screen.connection, 'google')"
          />
          <div v-if="mailboxTiles(screen.connection).length > 0 && !bindings[slotKey(screen.connection)]" class="text-caption os-text-muted">
            The servers and port are shown with the mailbox above.
          </div>
          <SetupQuestion
            v-for="setting in mailboxSettings(setup, screen.connection)"
            :key="settingKey(setting)"
            :setting="setting"
            :model-value="values[settingKey(setting)]"
            :problem="problems[settingKey(setting)]"
            bounds
            @update:model-value="(value: unknown) => setValue(setting, value)"
          />
        </div>
      </template>

      <!-- 3. YOUR DOCUMENTS ----------------------------------------------------------------------- -->
      <template v-else-if="screen.kind === 'documents' && screen.document">
        <div class="text-subtitle1">{{ questionText(screen.document) }}</div>
        <div v-if="screen.document.hint" class="os-text-muted q-mb-md">{{ screen.document.hint }}</div>
        <input :key="screen.id" ref="fileInput" type="file" class="hidden" @change="(event: Event) => pickFile(screen.document!, event)" />
        <div class="row items-center q-gutter-sm">
          <q-btn outline no-caps icon="upload_file" label="Choose a file" @click="fileInput?.click()" />
          <span v-if="files[screen.document.folder]" data-setup-file>{{ files[screen.document.folder]!.name }}</span>
        </div>

        <div v-if="advancedOpen" class="setup-advanced" data-setup-advanced>
          It is kept in the folder <span class="mono">{{ screen.document.folder }}/</span> of the team's documents.
        </div>
      </template>

      <!-- 4. YOUR PREFERENCES --------------------------------------------------------------------- -->
      <template v-else-if="screen.kind === 'preferences'">
        <SetupQuestion
          v-for="setting in mainPreferences"
          :key="settingKey(setting)"
          :setting="setting"
          :model-value="values[settingKey(setting)]"
          :problem="problems[settingKey(setting)]"
          @update:model-value="(value: unknown) => setValue(setting, value)"
        />
        <div v-if="mainPreferences.length === 0" class="os-text-muted">Nothing to choose: the usual answers are filled in.</div>

        <div v-if="advancedOpen" class="setup-advanced" data-setup-advanced>
          <div v-for="entry in mainBounds" :key="settingKey(entry.setting)" class="text-caption" data-setup-bounds>
            {{ entry.setting.member }}: {{ entry.setting.setting }} - {{ entry.words }}
          </div>
          <SetupQuestion
            v-for="setting in advancedPreferences"
            :key="settingKey(setting)"
            :setting="setting"
            :model-value="values[settingKey(setting)]"
            :problem="problems[settingKey(setting)]"
            bounds
            @update:model-value="(value: unknown) => setValue(setting, value)"
          />
          <div v-for="connection in otherConnections" :key="slotKey(connection)" class="q-mb-md" :data-setup-other-connection="slotKey(connection)">
            <q-select
              :model-value="bindings[slotKey(connection)] ?? null"
              :options="connections.filter((candidate) => offeredFor(candidate, connection)).map((candidate) => ({ label: candidate.account, value: candidate.id }))"
              emit-value
              map-options
              clearable
              outlined
              dense
              :label="questionText(connection)"
              @update:model-value="(id: string | null) => (bindings = id ? { ...bindings, [slotKey(connection)]: id } : Object.fromEntries(Object.entries(bindings).filter(([key]) => key !== slotKey(connection))))"
            >
              <template #append><FieldHelp :text="`${connection.member}: ${connection.slot}`" /></template>
            </q-select>
          </div>
          <div v-for="document in otherDocuments" :key="document.folder" class="q-mb-md" :data-setup-other-document="document.folder">
            <div>{{ questionText(document) }} <span class="mono">({{ document.folder }}/)</span></div>
            <input type="file" @change="(event: Event) => pickFile(document, event)" />
          </div>
        </div>
      </template>

      <!-- 5. READY -------------------------------------------------------------------------------- -->
      <template v-else-if="screen.kind === 'ready'">
        <div v-if="!installed" data-setup-summary>
          <p v-for="(line, position) in summary" :key="position" class="q-mb-xs" data-setup-sentence>{{ line }}</p>
        </div>

        <div v-if="installing" class="row items-center q-gutter-sm q-mt-sm" data-setup-installing>
          <q-spinner size="1.2em" />
          <span>Setting up… this can take a minute.</span>
        </div>

        <template v-if="installed">
          <div class="text-positive q-mb-sm" data-setup-done>{{ installed.teamName }} is set up.</div>
          <div v-for="name in uploaded" :key="name">Saved {{ name }}.</div>
          <div v-for="line in notUploaded" :key="line" class="text-negative">{{ line }}</div>
        </template>

        <div v-if="(installed || changing) && finish" class="q-mt-sm" data-setup-finish>
          <q-btn flat dense no-caps color="primary" :label="finish" @click="openFinish" />
        </div>
        <div v-else-if="installed && missing" class="q-mt-sm" data-setup-nothing-missing>Everything it needs is in place.</div>

        <div v-if="checkSentence" class="q-mt-sm" data-setup-check-sentence>{{ checkSentence }}</div>

        <div v-if="advancedOpen" class="setup-advanced" data-setup-advanced>
          <SolutionPlanReview :plan="plan" />
        </div>
      </template>

      <div v-if="problem" class="text-negative q-mt-md" data-setup-problem>{{ problem }}</div>
    </q-card-section>

    <q-card-actions align="right">
      <q-btn v-if="index > 0 && !installed && !installing" flat no-caps label="Back" @click="go(index - 1)" />
      <q-btn
        flat
        no-caps
        :label="advancedOpen ? 'Hide Advanced…' : 'Advanced…'"
        data-setup-advanced-toggle
        @click="advancedOpen = !advancedOpen"
      />
      <q-space />
      <q-btn v-if="canSkip" flat no-caps label="Do this later" data-setup-skip @click="skip" />
      <q-btn v-if="!isLast" color="primary" no-caps label="Next" :disable="!canGoOn" :loading="saving" @click="next" />
      <template v-else>
        <q-btn
          v-if="(installed || changing) && firstSchedule"
          outline
          no-caps
          label="Check now"
          :loading="checking"
          data-setup-check-now
          @click="checkNow"
        />
        <q-btn v-if="!changing && !installed" color="primary" no-caps label="Set up" :loading="installing" data-setup-button @click="setUp" />
        <q-btn v-if="installed" color="primary" no-caps label="Open the team" data-setup-open @click="openTeam" />
        <q-btn v-if="changing" color="primary" no-caps label="Done" @click="emit('close')" />
      </template>
    </q-card-actions>

    <ConnectDialog
      v-if="signIn.open"
      v-model="signIn.open"
      :providers="providers"
      :connections="connections"
      :need="signInNeed"
      @connected="signedIn"
    />
  </div>
</template>

<style scoped>
.setup-body {
  max-height: 65vh;
  overflow-y: auto;
  overflow-wrap: anywhere;
}

.setup-welcome {
  white-space: pre-line;
}

.setup-heading {
  font-weight: 600;
  margin-top: 8px;
}

.setup-advanced {
  margin-top: 16px;
  padding-top: 8px;
  border-top: 1px solid var(--os-ink-faint, #ccc);
}
</style>
