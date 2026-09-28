<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { createTeam, fileSystemRoots, listCatalog } from '../api/client';
import {
  agentsForMode,
  type Catalog,
  type FileSystemRoot,
  type TeamId,
  type UnresolvedAgent,
} from '../api/types';
import { normalizeAllowlist } from '../lib/memberAllowlist';
import { budgetFieldIsLegal, budgetFieldRefusal, budgetFieldValue } from '../lib/teamBudget';
import { applyDefaults, readRemembered, remember } from '../lib/newTeamDefaults';
import { useConsoleStore } from '../stores/console';
import { teamFolderLine } from '../lib/teamRoot';
import { installStatus, installationFor } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import {
  MAXIMUM_LABEL_LENGTH,
  firstProblem,
  optional,
  repoUrlRules,
  teamLabel,
  teamNameTaken,
  type Rule,
} from '../lib/rules';
import { TeamInstructionsHint, TeamInstructionsLabel } from '../lib/additionalInstructions';
import { upstreamUrlProblem } from '../lib/contributor';
import HostPathPicker from './HostPathPicker.vue';
import ForkItForMe from './ForkItForMe.vue';
import type { ForkResult } from '../api/types';

/** Carries the new team's IDENTIFIER, so whoever opened this can switch to the team that was just
 *  made rather than re-reading the board and guessing which one is new.
 *
 *  It has to come from the RESPONSE and not from the field above: what was typed is the label, and
 *  the identifier is derived from it by the server. Emitting the typed text would hand
 *  `setActiveTeam` a name no team answers to, and the board would quietly select nothing. */
const emit = defineEmits<{ created: [TeamId] }>();

const open = defineModel<boolean>({ required: true });

const $q = useQuasar();
const board = useConsoleStore();

const { installations } = useAgentInstallations();

const name = ref('');

/**
 * Where this team's files go, as a person TYPES it — free text, exactly like `name`. Empty means
 * untouched: `submit()` sends `undefined` rather than this box's raw value, so the server stores
 * NULL and the team follows the instance root wherever it moves. Never resolved to a default here —
 * that would be a second, client-side answer to what "unset" means, alongside the one already given
 * to the Manager's Agent.
 */
const root = ref('');
const repoInput = ref('');
const repos = ref<string[]>([]);

/**
 * Each listed repository's upstream, by POSITION beside `repos` and moved with it. Blank is an
 * owned repository; set, the repository's URL is the fork and its clone is made with an `upstream`
 * remote. Sent in the create request itself, never a follow-up call.
 */
const upstreams = ref<string[]>([]);
const upstreamRules = (index: number): Rule[] => [
  (value) => upstreamUrlProblem(String(value ?? ''), repos.value[index]) ?? true,
];
const upstreamsAreLegal = computed(() =>
  repos.value.every((url, index) => upstreamUrlProblem(upstreams.value[index] ?? '', url) === null),
);

/** The create request's `upstreams`: each repository with one, keyed by its URL. */
function upstreamsByRepo(): Record<string, string> {
  return Object.fromEntries(
    repos.value.flatMap((url, index) => {
      const upstream = (upstreams.value[index] ?? '').trim();
      return upstream === '' ? [] : [[url.trim(), upstream]];
    }),
  );
}

function setUpstream(index: number, url: string | number | null) {
  const next = [...upstreams.value];
  next[index] = String(url ?? '');
  upstreams.value = next;
}
const repoSuggestions = ref<string[]>([]);

/**
 * The instance's own data root, exactly as `GET /api/fs/roots` answers it — found by the route's
 * own `isInstance` flag. Never composed here: the data root is a runtime value the Host resolves
 * from `--DataRoot`, then `HARNESS_DATA_ROOT`, and a client-side guess at it would be wrong the
 * moment anyone passes `--DataRoot`.
 *
 * NOT `roots[0]`, which is right only while the instance root survives. `Effective` does add
 * it before every configured root, but `FileBrowserPolicy`'s constructor then normalises the whole
 * list and can drop it — `--DataRoot=\\?\C:\foo` is enough. Index nought is then the operator's
 * FIRST CONFIGURED root, and the placeholder would show ITS path while a blank box still puts the
 * team in the real data root: a preview naming the wrong folder. The server answers "which one is
 * the instance root".
 *
 * `null` means only "no instance root to show" — either still loading, or there genuinely is none
 * (every root dropped). A FAILED fetch is `instanceRootError` instead, and is rendered: see below.
 */
const instanceRootPath = ref<string | null>(null);

/**
 * WHY THERE IS NOTHING TO PREVIEW, when that is a failure rather than an absence. A catch that
 * swallowed everything would leave the placeholder blank, `folderLine` computed from `''` and
 * rendered blank, and a person in front of an empty box with nothing telling them where their
 * team would go.
 */
const instanceRootError = ref<string | null>(null);

/** What `root` resolves to for the PREVIEW below — a person's own typed or browsed path, or the
 *  resolved instance root while the box is left blank. This is preview-only, exactly like
 *  `folderLine` itself: `submit()` still sends `root.value.trim()` untouched, `undefined` when
 *  blank, so the server goes on storing NULL and following the instance root if it ever moves. */
const effectiveRoot = computed(() => root.value.trim() || instanceRootPath.value || '');

/** A PREVIEW ONLY. `deriveTeamId` inside `teamFolderLine` is a second store of
 *  `ContainerId.DeriveName` — it must never become the value sent; the request always carries
 *  `name` as typed. This just shows the folder before it exists. */
const folderLine = computed(() => teamFolderLine(effectiveRoot.value, name.value));

/**
 * The longest a team name may be, matching `TeamRegistry.MaximumLabelLength`.
 *
 * This is the ONLY rule in this dialog, and its absence of company is the point. What someone types
 * here is a label, not an identifier: the server derives an identifier from it and never asks
 * anyone to invent one. So a name is whatever a person wants to call their team, and no character
 * rule from `ContainerId.IsLegalName` leaks into the very first thing they do.
 */
const MaximumLength = MAXIMUM_LABEL_LENGTH;

const nameRules: Rule[] = [teamLabel];

/** Both the button and submit() consult this, so Enter cannot bypass what the button disables. */
const nameIsLegal = () => firstProblem(nameRules, name.value) === null;

/**
 * The server's refusal, IN the dialog rather than in a toast behind it: a duplicate name has to
 * stay beside the field that caused it. `nameTaken` marks the name as well.
 */
const serverError = ref('');
const nameTaken = computed(() => teamNameTaken(serverError.value));

/**
 * The Manager this team is created with runs headlessly — it is an Agent Container, woken by
 * messages, same as any other member — so this offers only Headless presets, read from the
 * catalog rather than hard-coded. See `AddMemberDialog`'s own remark on the same choice.
 *
 * `Manager` is the seeded default and the one every tenant starts with — `AgentCatalogFile`'s own
 * self-upgrade reinserts it if it is ever missing from the file, so its absence FROM THIS LIST
 * means it was deliberately removed from the catalog by a tenant admin. That is reachable, so there
 * is no silent fallback onto some other preset standing in for it: `agent` starts at `null` in that
 * case and the template says why underneath the picker, rather than picking a stand-in a person did
 * not choose. A team is never created without SOME Manager preset — `submit()` refuses to run until
 * one is chosen.
 */
const agents = ref<string[]>([]);
const agent = ref<string | null>(null);

/* THERE IS NO CONCIERGE HERE, AND THAT IS THE POINT. It is one setting for the whole instance —
   `ConciergeSessionKey` is the USER alone, one Concierge per person serving every team they reach
   — so it is chosen once, on Admin → Concierge, and not again by everybody who makes a team.
   A picker here would write that single tenant-wide row: making one team would silently repoint
   the Concierge for every team that already exists. */

/* THERE IS NO PROMPT HERE EITHER. What a Manager and its members are told is the built-in
   role prompt, chosen by role and never by a person, so there are no prompt pickers. What a person
   may add is `additionalInstructions` below, appended after it. */

/**
 * DYNAMIC MEMBERS: which Agents a member the manager hires while nobody is watching may run. Not
 * preselected, and not answered by a literal inside the manager's own prompt - managers copy such a
 * literal and hire workers on the wrong CLI.
 */
const memberAgents = ref<string[]>([]);
const memberAgentToAdd = ref<string | null>(null);

/** TEAM INSTRUCTIONS (`additionalInstructions` on the wire): read by every agent member of the team,
 *  the Manager too, after its built-in prompt and its own instructions. Empty adds nothing, and it
 *  never replaces either. Not remembered: it is about this team. */
const additionalInstructions = ref('');

/**
 * WHAT ONE WORKFLOW ON THIS TEAM MAY SPEND, in tokens, input and output together.
 *
 * PREFILLED WITH THE INSTANCE'S OWN FIGURE. Spend on items that look comparable when they are
 * written can differ by an order of magnitude, so nobody can invent a right number - and nobody is
 * asked to. The box arrives holding
 * what is already in force; editing it is a deliberate act rather than the price of making a team.
 *
 * IT BOUNDS ONE WORKFLOW, NOT THE TEAM. A team on 100,000,000 with three workflows open has three
 * budgets of 100,000,000, one each, and nothing is shared between them.
 *
 * NOTHING IS CLAMPED. A figure ABOVE the instance's own is stored as typed - the route is
 * `{team}`-gated so a clamp here would be no backstop, and a visible control beats an invisible
 * guarantee. A field that silently refuses what a person typed is a worse lie
 * than a number they can change.
 *
 * EMPTY AND 0 ARE DIFFERENT ANSWERS. Empty is `null` - this team chooses nothing and runs on the
 * instance figure. `0` is this team choosing UNLIMITED. Collapsing either into the other is the
 * single easiest thing to get wrong here.
 *
 * HELD AS WHAT THE BOX GIVES BACK, not as a number: a `q-input type="number"` answers the empty
 * string for a cleared box. `budgetFieldValue` is the one place that is turned into the answer the
 * server understands, and both dialogs call it rather than each deciding what empty means.
 */
const budgetTokens = ref<number | string | null>(null);

const getAgentStatus = (agentName: string | null) => {
  if (!agentName || !installations.value) return null;
  return installStatus(installationFor(installations.value, agentName));
};

async function loadDefaults() {
  let catalog: Catalog = { agents: [] };
  let roots: FileSystemRoot[] = [];

  instanceRootError.value = null;
  serverError.value = '';

  const [catalogResult, rootsResult] = await Promise.allSettled([listCatalog(), fileSystemRoots()]);

  if (catalogResult.status === 'fulfilled') {
    catalog = catalogResult.value;
  }

  if (rootsResult.status === 'fulfilled') {
    roots = rootsResult.value.roots;
    instanceRootPath.value = roots.find((entry) => entry.isInstance)?.path ?? null;
  } else {
    instanceRootPath.value = null;
    instanceRootError.value = rootsResult.reason instanceof Error
      ? rootsResult.reason.message
      : String(rootsResult.reason);
  }

  agents.value = agentsForMode(catalog.agents, 'Headless').map((entry) => entry.name);

  const defaults = applyDefaults(
    readRemembered(),
    catalog,
    roots,
    board.teams.map((team) => team.id),
  );

  name.value = defaults.name;
  agent.value = defaults.managerAgent;
  memberAgents.value = defaults.memberAgents ?? [];
  memberAgentToAdd.value = null;
  additionalInstructions.value = '';
  root.value = defaults.root ?? '';
  repoSuggestions.value = defaults.repoSuggestions;
  repos.value = defaults.repos;
  upstreams.value = [];
  repoInput.value = '';

  // THE INSTANCE FIGURE, NOT A LITERAL AND NOT A REMEMBERED ONE. `board.workflowSpendLimit` is
  // what `GET /api/overview` answered, already fetched by the time this dialog opens, so the box
  // holds the figure this team would actually run under if nobody touched it. A constant here
  // would be a second answer to "what is in force", and this dialog has one job in that regard:
  // show the first.
  //
  // NOT read from `readRemembered()` beside the choices above, deliberately. Those are
  // preferences a person re-picks every time; a spend bound is a fact about the instance, and
  // remembering one typed on a different day would quietly re-apply it to a team nobody set it for.
  budgetTokens.value = board.workflowSpendLimit;
}

watch(open, (showing) => {
  if (showing) {
    void loadDefaults();
  }
});

/**
 * The folder picker for `root` above — `HostPathPicker`, a reusable component bounded to the
 * allowlist `GET /api/fs/roots` answers, rather than a browser of this dialog's own: `GET
 * /api/fs/browse` lists files as well as directories, so a naive browser would list them as though
 * they were folders.
 *
 * THE PICKER RENDERS THE SHARED `FileBrowser` over a host-filesystem source, which this call
 * site cannot tell apart: a `v-model`, a `mode`, and a `chose` payload — a path on the Host,
 * emitted only when somebody presses "Choose this folder". The shared part is the LOOK and the
 * NAVIGATION; the boundary is `FileBrowserPolicy` and the four `/api/fs` routes.
 */
const pickerOpen = ref(false);

function pathChosen(path: string) {
  root.value = path;
}

/**
 * The box a new URL is typed into, checked against the list it would join (`-1` is no entry, so
 * every listed URL counts as a duplicate candidate). Empty passes: the box is optional.
 */
const repoInputRules = computed<Rule[]>(() => optional(...repoUrlRules(repos.value, -1)));

/** Every listed URL, and the half-typed one, pass - or Save stays off. The server is never asked. */
const reposAreLegal = computed(
  () =>
    repos.value.every((url, index) => firstProblem(repoUrlRules(repos.value, index), url) === null) &&
    firstProblem(repoInputRules.value, repoInput.value) === null,
);

const canAddRepo = computed(
  () => repoInput.value.trim().length > 0 && firstProblem(repoInputRules.value, repoInput.value) === null,
);

function setRepo(index: number, url: string | number | null) {
  repos.value = repos.value.map((entry, at) => (at === index ? String(url ?? '') : entry));
}

function addRepo() {
  const url = repoInput.value.trim();
  if (!url || !canAddRepo.value) return;

  repos.value = [...repos.value, url];
  repoInput.value = '';
}

/**
 * FORK IT FOR ME: the fork GitHub made joins the list as the repository's URL, with the
 * upstream it was forked from beside it - exactly as if both had been typed.
 */
function addFork(fork: ForkResult) {
  const at = repos.value.indexOf(fork.forkUrl);
  if (at >= 0) {
    setUpstream(at, fork.upstreamUrl);
    return;
  }
  repos.value = [...repos.value, fork.forkUrl];
  setUpstream(repos.value.length - 1, fork.upstreamUrl);
}

function applyRepoSuggestion(url: string) {
  repoInput.value = url;
}

function removeRepo(index: number) {
  repos.value = repos.value.filter((_, entry) => entry !== index);
  upstreams.value = upstreams.value.filter((_, entry) => entry !== index);
}

function moveRepo(index: number, direction: -1 | 1) {
  const target = index + direction;
  if (target < 0 || target >= repos.value.length) return;

  const next = [...repos.value];
  const [item] = next.splice(index, 1);
  if (item === undefined) return;
  next.splice(target, 0, item);
  repos.value = next;

  const nextUpstreams = Array.from({ length: repos.value.length }, (_, at) => upstreams.value[at] ?? '');
  const [upstream] = nextUpstreams.splice(index, 1);
  nextUpstreams.splice(target, 0, upstream ?? '');
  upstreams.value = nextUpstreams;
}

/** Both the button and submit() consult this, so Enter cannot bypass what the button disables —
 *  same shape as `nameIsLegal`, extended with the fields that have no safe default. */
const formIsLegal = () =>
  nameIsLegal() &&
  agent.value !== null &&
  memberAgents.value.length > 0 &&
  reposAreLegal.value &&
  upstreamsAreLegal.value &&

  // A NEGATIVE OR A FRACTION IS REFUSED RATHER THAN CORRECTED. See `budgetFieldIsLegal`: the route
  // answers 400 for a negative and cannot deserialise a fraction into its `long?` at all, so a
  // button that sent either would only produce a worse-worded refusal after everything else had
  // been filled in.
  budgetFieldIsLegal(budgetTokens.value);

/** The field's own rule, so the refusal appears on the box rather than only in a dead button. The
 *  WORDING is `teamBudget`'s and never this dialog's - the other dialog renders the same sentence
 *  from the same place, which is the only way the two stay agreed about what the field refuses. */
const budgetRules = [
  (value: number | string | null) => budgetFieldRefusal(value) ?? true,
];

const memberAgentOptions = computed(() =>
  agents.value.filter((name) => !normalizeAllowlist(memberAgents.value).some((entry) => entry.toLowerCase() === name.toLowerCase())),
);

const canAddMemberAgent = computed(() => !!memberAgentToAdd.value);

function addMemberAgent() {
  if (!memberAgentToAdd.value) return;
  memberAgents.value = normalizeAllowlist([...memberAgents.value, memberAgentToAdd.value]);
  memberAgentToAdd.value = null;
}

function removeMemberAgent(index: number) {
  memberAgents.value = memberAgents.value.filter((_, entry) => entry !== index);
}

function moveMemberAgent(index: number, direction: -1 | 1) {
  const target = index + direction;
  if (target < 0 || target >= memberAgents.value.length) return;

  const next = [...memberAgents.value];
  const [item] = next.splice(index, 1);
  if (!item) return;
  next.splice(target, 0, item);
  memberAgents.value = next;
}

const busy = ref(false);

const valid = computed(() => formIsLegal());

async function submit() {
  if (!formIsLegal() || !agent.value || busy.value) return;

  busy.value = true;
  serverError.value = '';

  try {
    const created = await createTeam(
      name.value.trim(),
      agent.value,
      memberAgents.value,
      // Untouched means `undefined`, never `''` - `JSON.stringify` drops it so the server stores
      // NULL and the team follows the instance root if it ever moves.
      root.value.trim() === '' ? undefined : root.value.trim(),
      repos.value,

      // EXACTLY WHAT WAS TYPED, including a figure above the instance's own. Nothing is clamped,
      // lowered or rounded on the way out - see `budgetTokens`' own remark for why a lower-only
      // field was offered and rejected.
      //
      // `null` FOR AN EMPTY BOX AND NEVER NaN. A `q-input type="number"` yields the empty string
      // for a cleared box, and NaN survives every comparison - so the normalisation is done here,
      // once, and it maps empty to the one thing the server understands as "this team chose
      // nothing". A typed 0 is left alone: that is "this team chose unlimited".
      //
      // ONE REQUEST - never a follow-up PUT after creation. That shape has a window the
      // server-side apply does not: if the second call never lands (network, a race, a restart
      // between the two), the team already exists holding a figure nobody chose and the chosen
      // number is silently gone with nothing on screen saying so.
      budgetFieldValue(budgetTokens.value),

      // Omitted when blank, so nothing is appended to the role prompt.
      additionalInstructions.value,

      // In this request, so each clone is made with its upstream remote.
      upstreamsByRepo(),
    );

    remember({
      managerAgent: agent.value,
      memberAgents: memberAgents.value,
      root: root.value.trim() === '' ? null : root.value.trim(),
      repos: repos.value,
    });

    // Handle unresolvedAgents if present
    if (created.unresolvedAgents?.length) {
      const messages = created.unresolvedAgents
        .map((ua) => `${ua.agent}: ${ua.message}`)
        .join('\n');
      $q.notify({
        type: 'warning',
        message: `Some agents could not be resolved:\n${messages}`,
        caption: 'Team created, but some agents failed',
      });
    }

    name.value = '';
    root.value = '';
    repos.value = [];
    upstreams.value = [];
    repoInput.value = '';
    open.value = false;
    // The ID. `setActiveTeam` stores what `activeTeam` matches on, and that getter compares
    // `team.id` - so emitting the name selects nothing at all for any team whose name is not
    // already its identifier, which is every team with a space in it.
    emit('created', created.id);
  } catch (cause) {
    // In the dialog, verbatim, and the dialog stays open - see `serverError`.
    serverError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <!-- TWO ROOT NODES, so this component is a FRAGMENT and Vue does not auto-inherit attributes onto
       it. `<CreateTeamDialog class="…">` compiles, renders, and silently does nothing - the same
       wall ContainerCard already documents, which is why that one carries its own sizing rule and
       reads its variables through the cascade. Anything that needs to style this from outside has
       to go through a prop or a global rule, or the second dialog below has to be nested (it is
       not, deliberately - see its own comment). -->
  <q-dialog v-model="open">
    <q-card class="os-dialog-md">
      <q-card-section>
        <div class="os-dialog-title">New team</div>
        <div class="text-caption os-text-muted">
          A team is created with its Manager. There is no team without a door into it.
        </div>
      </q-card-section>

      <q-form lazy-rules="ondemand" @submit="submit">
        <q-card-section class="q-gutter-md">
          <q-input
            v-model="name"
            autofocus
            outlined
            dense
            label="Team name"
            :rules="nameRules"
            :maxlength="MaximumLength"
            :error="nameTaken"
            :error-message="nameTaken ? serverError : undefined"
            hint="Whatever you want to call it — spaces and accents are fine."
            @update:model-value="serverError = ''"
          />

          <!-- Where this team's files go. NO q-col-gutter here: that class gives every child
               `padding-top: 8px` and a `q-btn`'s own padding rule beats it on source order at equal
               specificity, so the input takes the 8px and the button rides above it. `row items-center
               no-wrap` plus a plain `q-ml-sm` margin on the button, matching ProfileDialog's key row,
               does not lose that fight. -->
          <div>
            <div class="row items-center no-wrap">
              <q-input
                v-model="root"
                class="col"
                outlined
                dense
                hide-bottom-space
                label="Place team in"
                :placeholder="instanceRootPath ?? ''"
              />
              <q-btn
                class="col-auto q-ml-sm"
                outlined
                dense
                no-caps
                label="Browse…"
                @click="pickerOpen = true"
              />
            </div>
            <!-- THE FAILURE IS RENDERED, not swallowed. Without this line a failed
                 `GET /api/fs/roots` leaves the placeholder blank and `folderLine` blank, and the
                 person is in front of an empty box with nothing at all saying why. Only while the box is empty: once somebody has typed
                 or browsed a path there IS a preview, and the instance root is no longer what they
                 are getting. -->
            <div
              v-if="instanceRootError && !root.trim()"
              class="os-body text-negative q-mt-xs"
            >
              Could not read this host's folders, so there is nothing to preview:
              {{ instanceRootError }} Leaving this blank still puts the team in the default place.
            </div>
            <div v-else class="text-caption os-text-muted q-mt-xs">
              {{ folderLine }}
            </div>
          </div>

          <div>
            <div class="row items-center no-wrap">
              <q-input
                v-model="repoInput"
                class="col"
                outlined
                dense
                hide-bottom-space
                label="GitHub Repos"
                placeholder="https://github.com/owner/repo.git"
                :rules="repoInputRules"
                @keydown.enter.prevent="addRepo"
              />
              <q-btn
                class="col-auto q-ml-sm"
                outlined
                dense
                no-caps
                label="Add"
                :disable="!canAddRepo"
                @click="addRepo"
              />
            </div>

            <ForkItForMe @forked="addFork" />

            <div v-if="repoSuggestions.length > 0" class="q-mt-sm">
              <div class="text-caption os-text-muted">Recent repositories:</div>
              <div class="row q-gutter-xs q-mt-xs">
                <q-chip
                  v-for="repo in repoSuggestions"
                  :key="repo"
                  clickable
                  dense
                  class="mono"
                  @click="applyRepoSuggestion(repo)"
                >
                  {{ repo }}
                </q-chip>
              </div>
            </div>

            <q-list v-if="repos.length > 0" bordered separator class="q-mt-sm">
              <!-- Keyed by POSITION: keyed by the URL, every keystroke in a row would remount it and
                   drop the focus. Each row is a field with its own rules, duplicates included, so a
                   remembered URL that no longer passes is marked where it sits. -->
              <q-item v-for="(repo, index) in repos" :key="index">
                <q-item-section>
                  <q-input
                    :model-value="repo"
                    class="mono"
                    dense
                    borderless
                    hide-bottom-space
                    :aria-label="index === 0 ? 'Primary repo URL' : `Repo ${index + 1} URL`"
                    :rules="repoUrlRules(repos, index)"
                    @update:model-value="setRepo(index, $event)"
                  />
                  <q-item-label caption>
                    {{ index === 0 ? 'Primary repo' : `Repo ${index + 1}` }}
                  </q-item-label>
                  <!-- Blank: the team owns this repository. Set: the URL above is its fork. -->
                  <q-input
                    :model-value="upstreams[index] ?? ''"
                    class="mono"
                    dense
                    hide-bottom-space
                    label="Upstream URL (if this is your fork)"
                    placeholder="https://github.com/project/repo.git"
                    :aria-label="`Upstream URL for ${repo}`"
                    :rules="upstreamRules(index)"
                    @update:model-value="setUpstream(index, $event)"
                  />
                </q-item-section>
                <q-item-section side>
                  <div class="row items-center no-wrap q-gutter-xs">
                    <q-btn
                      flat
                      dense
                      round
                      icon="arrow_upward"
                      :disable="index === 0"
                      :aria-label="`Move ${repo} up`"
                      @click="moveRepo(index, -1)"
                    />
                    <q-btn
                      flat
                      dense
                      round
                      icon="arrow_downward"
                      :disable="index === repos.length - 1"
                      :aria-label="`Move ${repo} down`"
                      @click="moveRepo(index, 1)"
                    />
                    <q-btn
                      flat
                      dense
                      round
                      color="negative"
                      icon="delete"
                      :aria-label="`Remove ${repo}`"
                      @click="removeRepo(index)"
                    />
                  </div>
                </q-item-section>
              </q-item>
            </q-list>

            <div v-else class="text-caption os-text-muted q-mt-xs">
              Optional. Add repositories in priority order; the first entry is the team's primary repo.
            </div>
          </div>

          <!-- Which CLI the Manager runs. What it is told is the built-in Manager prompt.

               The Concierge has no picker here — it belongs to the instance, not to a team, and
               lives on Admin → Concierge. -->
          <div>
            <q-select v-model="agent" :options="agents" outlined dense label="Manager agent" />
            <div v-if="agent && getAgentStatus(agent)" class="q-mt-xs">
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
          </div>

          <!-- Only when there is no default to fall back on - see `agent`'s own comment. -->
          <div v-if="agent === null" class="text-caption os-text-muted">
            A team must be created with a manager. Choose its Agent above.
          </div>


          <!-- DYNAMIC MEMBERS: the same heading and the same allowlist as Team Settings → General,
               deliberately - a person who has met one has met the other. Not preselected: a team
               created on whatever sorted first is a team hiring on a CLI nobody chose. -->
          <div class="text-subtitle2 q-mt-sm">Dynamic members</div>
          <div class="text-caption os-text-muted">
            What a member the manager hires gets when nobody is here to choose.
          </div>

          <div>
            <div class="row items-center no-wrap">
              <q-select
                v-model="memberAgentToAdd"
                :options="memberAgentOptions"
                class="col"
                outlined
                dense
                label="Allowlist: add Agent"
                hint="Headless presets only."
              />
              <q-btn
                class="col-auto q-ml-sm"
                outlined
                dense
                no-caps
                label="Add"
                :disable="!canAddMemberAgent"
                @click="addMemberAgent"
              />
            </div>

            <div v-if="memberAgentToAdd && getAgentStatus(memberAgentToAdd)" class="q-mt-xs">
              <q-icon
                :name="getAgentStatus(memberAgentToAdd)!.icon"
                size="14px"
                class="q-mr-xs"
                aria-hidden="true"
                :class="{ 'text-warning': getAgentStatus(memberAgentToAdd)!.tone === 'warn' }"
              />
              <span :class="{ 'text-warning': getAgentStatus(memberAgentToAdd)!.tone === 'warn', 'os-text-muted': getAgentStatus(memberAgentToAdd)!.tone !== 'warn' }">
                {{ getAgentStatus(memberAgentToAdd)!.text }}
              </span>
            </div>

            <q-list v-if="memberAgents.length > 0" bordered separator class="q-mt-sm">
              <q-item v-for="(entry, index) in memberAgents" :key="`${entry}-${index}`">
                <q-item-section>
                  <q-item-label class="mono">{{ entry }}</q-item-label>
                  <q-item-label caption>
                    {{ index === 0 ? 'Default when no tag is requested' : `Priority ${index + 1}` }}
                  </q-item-label>
                </q-item-section>
                <q-item-section side>
                  <div class="row items-center no-wrap q-gutter-xs">
                    <q-btn
                      flat
                      dense
                      round
                      icon="arrow_upward"
                      :disable="index === 0"
                      :aria-label="`Move ${entry} up`"
                      @click="moveMemberAgent(index, -1)"
                    />
                    <q-btn
                      flat
                      dense
                      round
                      icon="arrow_downward"
                      :disable="index === memberAgents.length - 1"
                      :aria-label="`Move ${entry} down`"
                      @click="moveMemberAgent(index, 1)"
                    />
                    <q-btn
                      flat
                      dense
                      round
                      color="negative"
                      icon="delete"
                      :aria-label="`Remove ${entry}`"
                      @click="removeMemberAgent(index)"
                    />
                  </div>
                </q-item-section>
              </q-item>
            </q-list>

            <div v-else class="os-body text-negative q-mt-xs">
              Choose at least one Agent. An empty allowlist cannot be saved.
            </div>
          </div>

          <!-- TEAM INSTRUCTIONS: their own section, after Dynamic members and not inside it. Every
               agent member reads them - the Manager too - so under Dynamic members they read as a
               setting for hired members only. Same heading and hint as Team Settings. -->
          <section data-section="team-instructions">
            <div class="text-subtitle2 q-mt-sm">{{ TeamInstructionsLabel }}</div>
            <q-input
              v-model="additionalInstructions"
              type="textarea"
              autogrow
              :input-style="{ minHeight: '9em' }"
              outlined
              dense
              class="q-mt-xs"
              :aria-label="TeamInstructionsLabel"
              :hint="TeamInstructionsHint"
            />
          </section>

          <!-- THE PER-WORKFLOW BUDGET. Last on the dialog and prefilled, which is the shape the
               argument needs: nobody has to answer it to make a team, and anybody who wants to can.

               THE HINT SAYS WHAT THE NUMBER BOUNDS, because the obvious misreading is that it is a
               total for the team. It is not: a team on this figure with three workflows open has
               three budgets of it, one each, and nothing is shared between them.

               AND IT SAYS WHAT EMPTY AND 0 MEAN, because they are different answers and neither is
               guessable. -->
          <q-input
            v-model.number="budgetTokens"
            type="number"
            outlined
            dense
            min="0"
            step="1"
            :rules="budgetRules"
            label="Budget for one workflow (tokens)"
            hint="What ONE workflow on this team may spend before it pauses — not a total across the team. Empty follows the instance figure; 0 is unlimited."
          />

          <q-banner v-if="serverError" dense class="os-bg-tint-error text-negative">
            <template #avatar><q-icon name="error" /></template>
            {{ serverError }}
          </q-banner>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn v-close-popup flat label="Cancel" :disable="busy" />
          <q-btn
            color="primary"
            label="Create team"
            type="submit"
            :loading="busy"
            :disable="!valid || busy"
          />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>

  <!-- The folder picker for `root` above, `HostPathPicker` rather than a browser of
       this dialog's own — and that picker is the shared `FileBrowser` with a
       host-filesystem source behind it, which this call site cannot tell apart. A separate
       top-level component instance rather than nested markup inside the dialog above: both teleport to <body> and Quasar stacks dialogs it opens in sequence
       itself, so there is no fixed-position ancestor here for a teleported dialog to lose to the
       way the Concierge shell forces `.concierge-settings`. `HostPathPicker`
       carries its own `.host-path-picker` z-index lift unconditionally for that other case, so
       opening it from in here needs nothing extra. Verified in the browser per the brief rather
       than assumed. -->
  <HostPathPicker v-model="pickerOpen" mode="folder" @chose="pathChosen" />
</template>
