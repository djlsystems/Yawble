<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useConsoleStore } from '../stores/console';
import {
  deleteMember,
  listCatalog,
  setMemberAgents,
  setTeamAdditionalInstructions,
  setTeamRepos,
  setRepoDefaultBranch,
  setRepoContributor,
  setTeamWorkflowBudget,
  getTeamEnv,
  setTeamEnv,
} from '../api/client';
import {
  agentsForMode,
  isManagerContainer,
  type ContainerSnapshot,
  type TeamId,
  type UnresolvedAgent,
} from '../api/types';
import { normalizeAllowlist } from '../lib/memberAllowlist';
import {
  envNameRules,
  envValue,
  firstProblem,
  optional,
  parseEnvLines,
  repoFolderName,
  repoUrlRules,
  type Rule,
} from '../lib/rules';
import { budgetFieldIsLegal, budgetFieldRefusal, budgetFieldValue } from '../lib/teamBudget';
import { branchNameProblem, defaultBranchCaption } from '../lib/defaultBranch';
import {
  contributorChanged,
  contributorDraft,
  contributorDraftProblem,
  claNoteProblem,
  forkOwnerProblem,
  upstreamUrlProblem,
  type ContributorDraft,
} from '../lib/contributor';
import { installStatus, installationFor } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import {
  TeamInstructionsHint,
  TeamInstructionsLabel,
  TeamInstructionsTakesEffect,
} from '../lib/additionalInstructions';
import AddMemberDialog from './AddMemberDialog.vue';
import MemberSettingsDialog from './MemberSettingsDialog.vue';
import ForkItForMe from './ForkItForMe.vue';
import type { ForkResult } from '../api/types';

/**
 * Settings for the active team.
 *
 * General is PARTLY READ-ONLY. The name shown here is the label a person reads, but renaming a
 * team honestly would also have to move the identifier, the documents folder and half of every
 * member id in one write. No route does that today, so a team is named once, at
 * creation. The rest is real: Members - adding one goes through
 * POST /api/teams/{team}/containers, the per-member tune button opens MemberSettingsDialog - the
 * same dialog ContainerCard opens for a running member - and the bin removes one through DELETE on
 * the same route the tune button patches.
 *
 * The MANAGER has no bin, and the server refuses one with a 409 regardless. Both, deliberately: the
 * refusal is what makes the rule true, and the absent control is what stops anyone meeting it. A
 * team without its manager is a team nobody can address, with every other member still running.
 *
 * A team has two names and only one is shown. This dialog renders the LABEL and never shows the
 * identifier, here or anywhere else a person looks: the identifier keys the documents folder and
 * half of every container's identity, and a second name beside the first only
 * invites "which of these is it really called". Address with `team.id`, render `team.name`.
 *
 * General holds the team's own row (id, display name, member count); Members is the list of its
 * members, each with its own settings.
 */
const open = defineModel<boolean>({ required: true });

/** Which tab this opening lands on. The ribbon has an entry point per tab, and arriving on the
 *  wrong one would make the second icon a slower way to reach the first.
 *
 *  Spelt out rather than imported from a shared alias: `<script setup>` cannot export, so the type
 *  would have to live in a module of its own for the sake of one union that both ends already
 *  state. */
const props = withDefaults(
  defineProps<{ initialTab?: 'general' | 'members' | 'instructions' | 'repos' | 'env' }>(),
  { initialTab: 'general' },
);

const $q = useQuasar();
const board = useConsoleStore();

const { installations } = useAgentInstallations();

/** The tab's name: the heading without "(optional)", which a tab has no room for. */
const TeamInstructionsTab = 'Team instructions';

const tab = ref<'general' | 'members' | 'instructions' | 'repos' | 'env'>(props.initialTab);

const team = computed(() => board.activeTeam);
const containers = computed(() => team.value?.containers ?? []);

/**
 * The add-member dialog, nested inside this one.
 *
 * A refresh follows a successful add rather than trusting the push. `applySnapshot` does refresh on
 * a container it has never seen, so the new card would arrive on its own — but only once the hub
 * delivers a snapshot for it, and this list is on screen at the moment the person clicks. Waiting
 * for a broadcast to redraw what they just did is how a working action looks broken.
 */
const addingMember = ref(false);

/**
 * The member-settings dialog, opened per row rather than once per member - one instance, retargeted
 * at whichever row was clicked, the same way `addingMember` above is one instance reused for every
 * add. `settingsSnapshot` holds the row `MemberSettingsDialog` was opened for; `v-if` on it in the
 * template is what keeps the dialog from mounting against a null snapshot before anything has been
 * clicked.
 *
 * A SECOND instance of this dialog, distinct from the one `ContainerCard` nests for the board's own
 * cards - not a shared one. `ContainerCard`'s copy is scoped to the one container its card renders;
 * this one is scoped to whichever row in THIS list was clicked, and the two never show the same
 * team at once, so there is nothing to gain by threading one dialog through both and a great deal
 * to lose by coupling a board card to this settings dialog's lifecycle.
 */
const settingsSnapshot = ref<ContainerSnapshot | null>(null);


/** NOTHING to fall back to: there is no default preset. A team without a stored choice refuses at
 *  the hire rather than defaulting. */

/** The team's default Agent for new members, and the headless presets it may be. Loaded with the
 *  dialog rather than on demand: the select is on the General tab, which is the tab it opens on. */
const memberAgents = ref<string[]>([]);
const memberAgentToAdd = ref<string | null>(null);
const headlessAgents = ref<string[]>([]);
const agentsLoading = ref(false);

/**
 * Words appended after the built-in role prompt for this team's Manager and members. There
 * is no Prompt picker: what they are told is chosen by role from the build, and this is
 * the only part a person writes. Empty adds nothing.
 */
const additionalInstructions = ref('');
const repoInput = ref('');
const repos = ref<string[]>([]);

/**
 * What ONE workflow on this team may spend, in tokens, input and output together.
 *
 * THREE ANSWERS, AND THIS DIALOG IS THE ONLY READER OF THE RAW ONE. `Team.budgetTokens` is the
 * team's stored CHOICE: `null` means it has chosen nothing and runs on the instance's own
 * `WorkflowSpendLimit`, `0` means it explicitly chose UNLIMITED, and anything above 0 is the
 * figure somebody typed. Everywhere else in the browser sees `effectiveWorkflowBudget`, one number
 * where `null` means unlimited - but a dialog that showed the resolved figure could not tell a
 * team that chose 100,000,000 from one that chose nothing while the instance said 100,000,000, and
 * a Save would then turn the second into the first without anybody meaning it.
 *
 * SO AN UNCHOSEN TEAM IS PREFILLED WITH THE INSTANCE FIGURE - what it is actually running under -
 * and a stored 0 renders as `0`, not as that figure.
 *
 * HELD AS WHAT THE BOX GIVES BACK, not as a number: a `q-input type="number"` answers the empty
 * string for a cleared box. `budgetFieldValue` turns that into the answer the server understands,
 * and `CreateTeamDialog` calls the same function rather than deciding separately.
 */
const budgetTokens = ref<number | string | null>(null);

/**
 * WHAT THE BOX WAS GIVEN WHEN THIS DIALOG OPENED, which is what "changed" is measured against.
 *
 * NOT `team.budgetTokens`. For a team that has chosen nothing those two differ by construction -
 * the stored value is `null` and the box shows the instance figure - so comparing against the
 * stored one would call an untouched dialog CHANGED, and pressing Save would convert "this team
 * follows the instance" into "this team chose this number", silently, for anybody who opened
 * Settings to edit something else.
 */
const budgetOpened = ref<number | null>(null);


// Rows rather than an object, because two rows may briefly share a name while somebody is typing
// and an object would silently drop one of them. The duplicate is marked on the row, by
// `envNameRules`, and Save stays off until it is resolved.
const envRows = ref<{ name: string; value: string }[]>([]);
const envStored = ref<Record<string, string>>({});
const envError = ref<string | null>(null);
const envSaving = ref(false);

const envNames = computed(() => envRows.value.map((row) => row.name));
const envValueRules: Rule[] = [envValue];

/**
 * The rows as the `KEY=value` block `parseEnvLines` reads, so this tab and AgentEditDialog refuse
 * exactly the same entries. A row with no name is REFUSED here rather than dropped: dropping it
 * would make it vanish from `envMap` on Save, value and all, with nothing on screen saying so.
 */
const envParsed = computed(() =>
  parseEnvLines(envRows.value.map((row) => `${row.name.trim()}=${row.value}`).join('\n')));

const envValid = computed(() =>
  envParsed.value.valid &&
  envRows.value.every((row, index) =>
    firstProblem(envNameRules(envNames.value, index), row.name) === null &&
    firstProblem(envValueRules, row.value) === null));

const envMap = computed(() => {
  const map: Record<string, string> = {};
  for (const row of envRows.value) if (row.name.trim() !== '') map[row.name.trim()] = row.value;
  return map;
});

const envChanged = computed(() => {
  const next = envMap.value;
  const stored = envStored.value;
  const keys = new Set([...Object.keys(next), ...Object.keys(stored)]);
  for (const key of keys) if (next[key] !== stored[key]) return true;
  return false;
});

const addEnvRow = () => {
  envRows.value = [...envRows.value, { name: '', value: '' }];
};

const removeEnvRow = (index: number) => {
  envRows.value = envRows.value.filter((_, row) => row !== index);
};

const loadEnv = async (team: TeamId) => {
  envStored.value = await getTeamEnv(team);
  envRows.value = Object.entries(envStored.value).map(([name, value]) => ({ name, value }));
  envError.value = null;
};

const saveEnv = async (team: TeamId) => {
  if (!envValid.value || envSaving.value) return;
  envError.value = null;
  envSaving.value = true;
  try {
    envStored.value = await setTeamEnv(team, envMap.value);
    envRows.value = Object.entries(envStored.value).map(([name, value]) => ({ name, value }));
  } catch (error) {
    envError.value = error instanceof Error ? error.message : String(error);
  } finally {
    envSaving.value = false;
  }
};

async function loadHeadlessAgents() {
  agentsLoading.value = true;

  try {
    const catalog = await listCatalog();

    headlessAgents.value = agentsForMode(catalog.agents, 'Headless').map((a) => a.name);
  } catch {
    // Left as it is: the select then offers only what the team already has, which is never blank
    // and never silently offers a change to nothing.
  } finally {
    agentsLoading.value = false;
  }
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

function removeRepo(index: number) {
  repos.value = repos.value.filter((_, entry) => entry !== index);
}

/**
 * FORKS MADE HERE AND NOT SAVED YET, keyed by the fork's URL. The fork joins the list like a
 * typed URL; its upstream is stored once Save has stored the list, because a repository's settings
 * are written against a saved repository.
 */
const pendingForks = ref<Record<string, ForkResult>>({});

function addFork(fork: ForkResult) {
  if (!repos.value.includes(fork.forkUrl)) repos.value = [...repos.value, fork.forkUrl];
  pendingForks.value = { ...pendingForks.value, [fork.forkUrl]: fork };
}

function moveRepo(index: number, direction: -1 | 1) {
  const target = index + direction;
  if (target < 0 || target >= repos.value.length) return;

  const next = [...repos.value];
  const [item] = next.splice(index, 1);
  if (item === undefined) return;
  next.splice(target, 0, item);
  repos.value = next;
}

/**
 * A person's choice of each saved repository's default branch, keyed by repository name.
 * Empty means no choice: the branch origin's HEAD names is used, or it is not known. Only
 * repositories already saved are listed - the server has a branch to show only for those.
 */
const branchDrafts = ref<Record<string, string>>({});
const storedBranches = computed(() => team.value?.defaultBranches ?? []);
const branchRules: Rule[] = [(value) => branchNameProblem(String(value ?? '')) ?? true];
const branchesAreLegal = computed(() =>
  Object.values(branchDrafts.value).every((draft) => branchNameProblem(draft) === null),
);
const changedBranches = computed(() =>
  storedBranches.value.filter(
    (entry) => (branchDrafts.value[entry.repo] ?? '').trim() !== (entry.setByPerson ?? ''),
  ),
);

/**
 * Each saved repository's contributor settings, keyed by repository name: an upstream URL
 * (contributor mode - the repository's own URL is then the fork), the fork's owner, DCO sign-off and
 * the CLA note. Like the default branch, only repositories already saved are listed.
 */
const contributorDrafts = ref<Record<string, ContributorDraft>>({});
const storedContributors = computed(() => team.value?.contributors ?? []);
const originUrlFor = (repo: string) =>
  (team.value?.repos ?? []).find((url) => repoFolderName(url)?.toLowerCase() === repo.toLowerCase());
const draftFor = (repo: string): ContributorDraft =>
  contributorDrafts.value[repo] ?? contributorDraft(storedContributors.value.find((c) => c.repo === repo));
/** The rows the template edits: each saved repository with its draft, once the dialog made one. */
const contributorRows = computed(() =>
  storedContributors.value.flatMap((entry) => {
    const draft = contributorDrafts.value[entry.repo];
    return draft ? [{ entry, draft }] : [];
  }),
);
const upstreamRules = (repo: string): Rule[] => [
  (value) => upstreamUrlProblem(String(value ?? ''), originUrlFor(repo)) ?? true,
];
const forkOwnerRules: Rule[] = [(value) => forkOwnerProblem(String(value ?? '')) ?? true];
const claNoteRules: Rule[] = [(value) => claNoteProblem(String(value ?? '')) ?? true];
const contributorsAreLegal = computed(() =>
  storedContributors.value.every(
    (entry) => contributorDraftProblem(draftFor(entry.repo), originUrlFor(entry.repo)) === null,
  ),
);
const changedContributors = computed(() =>
  storedContributors.value.filter((entry) => contributorChanged(draftFor(entry.repo), entry)),
);

const reposChanged = computed(() => {
  const stored = team.value?.repos ?? [];
  if (repos.value.length !== stored.length) return true;
  return repos.value.some((url, index) => url !== stored[index]);
});


const settingsOpen = ref(false);

function openSettings(container: ContainerSnapshot) {
  settingsSnapshot.value = container;
  settingsOpen.value = true;
}

/**
 * The member awaiting confirmation, and null for "no confirmation open".
 *
 * A q-dialog in this component's own template rather than `$q.dialog()`, for the reason the
 * Concierge panel's reload confirmation is one: the Dialog plugin is deliberately not
 * registered, and its `class` option lands on the inner card rather than on the dialog root, so a
 * confirmation opened that way cannot be lifted if it ever needs to be.
 */
const doomed = ref<ContainerSnapshot | null>(null);
const removing = ref(false);

/** A manager has no bin. `isManagerContainer` is the shared, tested predicate - it compares the
 *  IDENTIFIER against the seeded manager name, never the editable label, because a member somebody
 *  relabelled "Manager" is not one. */
const isManager = (container: ContainerSnapshot) =>
  isManagerContainer(container.id, board.managerName);

const getAgentStatus = (agentName: string | null) => {
  if (!agentName || !installations.value) return null;
  return installStatus(installationFor(installations.value, agentName));
};

function askRemove(container: ContainerSnapshot | null) {
  doomed.value = container;
}

async function remove() {
  if (!doomed.value || !team.value) return;

  const container = doomed.value;
  const label = container.name;

  removing.value = true;

  try {
    // The team's ID and the member's IDENTIFIER, never either of their labels. This is the mistake
    // the branded TeamId exists to make un-writable on the team half; the member half has no such
    // guard, and `container.name` beside `container.id` is exactly the pair that invites it.
    const removed = await deleteMember(team.value.id, container.id);

    // The member is gone whether or not its workspace went with it, so this is positive either way
    // - but a failure has to be SAID, or files are left on disk that nobody is ever told about.
    // Usually a child process that has not finished exiting.
    if (removed.failures.length > 0) {
      $q.notify({
        type: 'warning',
        timeout: 12000,
        multiLine: true,
        message:
          `${label} was deleted, but some of it could not be removed: `
          + `${removed.failures.join('; ')}`,
      });
    } else {
      $q.notify({ type: 'positive', timeout: 5000, message: `${label} was deleted.` });
    }

    askRemove(null);

    // Refreshed rather than spliced out locally, for the reason the add path refreshes: this list,
    // the board behind it and the manager's own card all read the same team object, and a
    // hand-assembled copy here is how two answers to "who is on this team" start.
    await board.refresh();
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });
  } finally {
    removing.value = false;
  }
}

// Every write on this dialog is open to any signed-in person - every person is an administrator -
// and refused for a machine principal on the server, whatever this shows.

// A local copy for the read-only name field, reset from the board on each opening.
const displayName = ref('');
const saving = ref(false);
const saveError = ref('');

/** Whether every field Save writes passes its rules. Read by the button and by `save()` alike. */
const valid = computed(() => {
  if (memberAgents.value.length === 0) return false;

  // A NEGATIVE OR A FRACTION IS REFUSED RATHER THAN CORRECTED - see `budgetFieldIsLegal`. The route
  // answers 400 for a negative regardless, and a fraction never reaches its handler at all, so a
  // Save that sent either would only produce a worse-worded refusal later.
  if (!budgetFieldIsLegal(budgetTokens.value)) return false;
  if (!branchesAreLegal.value) return false;
  if (!contributorsAreLegal.value) return false;

  return reposAreLegal.value;
});

/** Whether anything Save writes differs from what is stored. */
const changed = computed(
  () =>
    memberAgentsChanged.value ||
    additionalInstructionsChanged.value ||
    reposChanged.value ||
    changedBranches.value.length > 0 ||
    changedContributors.value.length > 0 ||
    budgetChanged.value,
);

/** Whether anything on this tab differs from what is stored and can actually be saved from here. */
const canSave = computed(() => valid.value && changed.value && !saving.value);

/** Trimmed on both sides, because the server stores it trimmed: trailing whitespace is not a change. */
const additionalInstructionsChanged = computed(
  () => additionalInstructions.value.trim() !== (team.value?.additionalInstructions ?? '').trim(),
);

/** Whether the box holds something other than what it was opened with - see `budgetOpened`. */
const budgetChanged = computed(() => budgetFieldValue(budgetTokens.value) !== budgetOpened.value);

/** The field's own rule, so the refusal appears on the box rather than only in a dead button. The
 *  WORDING is `teamBudget`'s and never this dialog's - the New Team dialog renders the same
 *  sentence from the same place, which is the only way the two stay agreed about what it refuses. */
const budgetRules = [
  (value: number | string | null) => budgetFieldRefusal(value) ?? true,
];

const memberAgentsChanged = computed(() => {
  const stored = normalizeAllowlist(team.value?.memberAgents ?? null);
  const current = normalizeAllowlist(memberAgents.value);
  if (stored.length !== current.length) return true;
  return stored.some((entry, index) => entry !== current[index]);
});

const memberAgentOptions = computed(() =>
  headlessAgents.value.filter(
    (name) =>
      !normalizeAllowlist(memberAgents.value).some(
        (entry) => entry.toLowerCase() === name.toLowerCase(),
      ),
  ),
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

/**
 * The reason the button is off, or undefined when it is on. One string rather than a chain of
 * `v-if`s in the template: the tooltip has to live on the WRAPPER, because a disabled control
 * swallows pointer events and a tooltip inside one never fires.
 */
const saveTooltip = computed(() => {
  if (memberAgents.value.length === 0) return 'A team must allow at least one Agent for new members.';
  if (!reposAreLegal.value) return 'A repository URL on the GitHub Repos tab needs fixing.';
  if (!branchesAreLegal.value) return 'A default branch on the GitHub Repos tab needs fixing.';
  if (!contributorsAreLegal.value) return 'An upstream setting on the GitHub Repos tab needs fixing.';
  if (!valid.value) return 'A field needs fixing.';
  if (!canSave.value) return 'Nothing has changed.';

  return undefined;
});

async function save() {
  if (!canSave.value || !team.value) return;

  saving.value = true;
  saveError.value = '';

  try {

    // SAVED HERE, not on change. This dialog has a Save button, so Save is what saves - an
    // auto-saving control beside one is two answers to "is my change committed", and the toast it
    // fired made a change look stored that a Cancel would then appear to undo.
    if (memberAgents.value.length === 0) {
      saveError.value = 'A team must allow at least one Agent for new members.';
      return;
    }

    let unresolvedAgents: UnresolvedAgent[] = [];

    if (memberAgentsChanged.value) {
      const response = await setMemberAgents(team.value.id, normalizeAllowlist(memberAgents.value));
      if (response.unresolvedAgents?.length) {
        unresolvedAgents = unresolvedAgents.concat(response.unresolvedAgents);
      }
    }

    // ONLY WHAT MOVED. Emptied, it is sent as null and nothing is appended to the role prompt.
    if (additionalInstructionsChanged.value) {
      await setTeamAdditionalInstructions(team.value.id, additionalInstructions.value);
    }

    // ONLY WHAT MOVED, like everything else on this dialog. Branches BEFORE the list: a repository
    // removed in the same Save would otherwise have no row left to write its branch to.
    // Contributor settings go before the list too, for the same reason as the branches.
    for (const entry of changedContributors.value) {
      const draft = draftFor(entry.repo);
      const upstreamUrl = draft.upstreamUrl.trim();
      await setRepoContributor(team.value.id, entry.repo, {
        upstreamUrl: upstreamUrl === '' ? null : upstreamUrl,
        forkOwner: upstreamUrl === '' || draft.forkOwner.trim() === '' ? null : draft.forkOwner.trim(),
        dcoSignOff: draft.dcoSignOff,
        claSignedNote: draft.claSignedNote.trim() === '' ? null : draft.claSignedNote.trim(),
      });
    }

    for (const entry of changedBranches.value) {
      const draft = (branchDrafts.value[entry.repo] ?? '').trim();
      await setRepoDefaultBranch(team.value.id, entry.repo, draft === '' ? null : draft);
    }

    if (reposChanged.value) {
      await setTeamRepos(team.value.id, repos.value);
    }

    // After the list: each fork made here gets its upstream and owner.
    for (const fork of Object.values(pendingForks.value)) {
      const name = repoFolderName(fork.forkUrl);
      if (!name || !repos.value.includes(fork.forkUrl)) continue;
      await setRepoContributor(team.value.id, name, {
        upstreamUrl: fork.upstreamUrl,
        forkOwner: fork.forkOwner,
        dcoSignOff: false,
        claSignedNote: null,
      });
    }
    pendingForks.value = {};

    // EXACTLY WHAT WAS TYPED, including a figure ABOVE the instance's own `WorkflowSpendLimit`.
    // Nothing is clamped, lowered or rounded here: a lower-only field was offered and rejected,
    // because one that silently refuses what a person typed is a worse lie than a number they can
    // change. `budgetFieldValue` normalises only the ABSENCE of a number - an empty box to `null`,
    // meaning this team chooses nothing - and never the value of one. A typed 0 survives as 0,
    // which is this team choosing unlimited.
    if (budgetChanged.value) {
      await setTeamWorkflowBudget(team.value.id, budgetFieldValue(budgetTokens.value));
    }

    // Refetched rather than patched in place. The ribbon heading, the switcher and this dialog all
    // read the same team object, and the response is the team - but `refresh()` is what every other
    // "a team changed" path in this app does, and one of them holding a hand-assembled copy is how
    // two answers to "what is this team" start.
    await board.refresh();

    // Handle unresolvedAgents if present
    if (unresolvedAgents.length > 0) {
      const messages = unresolvedAgents
        .map((ua) => `${ua.agent}: ${ua.message}`)
        .join('\n');
      $q.notify({
        type: 'warning',
        message: `Some agents could not be resolved:\n${messages}`,
        caption: 'Settings saved, but some agents failed',
      });
    }

    // Re-read from the board, not left as typed: the server trims, and leaving the raw text would
    // make the field disagree with what was actually stored until the next opening.
    displayName.value = team.value?.name ?? displayName.value.trim();
  } catch (cause) {
    saveError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    saving.value = false;
  }
}

// There is no Access tab. Every signed-in person reaches every team, so a per-team list of who
// reaches this one would be the whole user list under a different heading.

watch(open, (showing) => {
  if (!showing) return;

  // Read on every opening rather than only at mount: the dialog is created once and reused, so a
  // tab set at construction would be whichever icon was pressed first, for the rest of the session.
  tab.value = props.initialTab;
  displayName.value = team.value?.name ?? '';
  saveError.value = '';

  // The stored choice or NOTHING - there is no platform default to show in its place, so an empty
  // box is the honest rendering of a team that has never chosen.
  memberAgents.value = normalizeAllowlist(team.value?.memberAgents ?? null);
  memberAgentToAdd.value = null;

  additionalInstructions.value = team.value?.additionalInstructions ?? '';
  repos.value = [...(team.value?.repos ?? [])];
  repoInput.value = '';
  pendingForks.value = {};
  contributorDrafts.value = Object.fromEntries(
    (team.value?.contributors ?? []).map((entry) => [entry.repo, contributorDraft(entry)]),
  );
  branchDrafts.value = Object.fromEntries(
    (team.value?.defaultBranches ?? []).map((entry) => [entry.repo, entry.setByPerson ?? '']),
  );

  // THE TEAM'S OWN CHOICE WHEN IT HAS MADE ONE, AND THE INSTANCE FIGURE WHEN IT HAS NOT - which is
  // what such a team is actually running under, so the box is never a blank demanding a number.
  //
  // `?? null` FIRST AND THE `=== null` TEST SECOND, not a single `??` chain: a stored `0` must
  // survive as `0`. `0 ?? instanceFigure` is `0` in JavaScript, but writing it as one expression
  // is exactly the line somebody later "simplifies" into `|| instanceFigure`, which turns this
  // team's deliberate UNLIMITED into a bound. Spelt out so there is nothing to simplify.
  const stored = team.value?.budgetTokens ?? null;

  budgetOpened.value = stored === null ? board.workflowSpendLimit : stored;
  budgetTokens.value = budgetOpened.value;

  void loadHeadlessAgents();

  // Fetched rather than read off the Team record, deliberately: the environment is NOT on
  // TeamSummary, because that record is returned by GET /api/teams to every signed-in person and
  // would put a team's credentials on a list route nobody asked for them from.
  envRows.value = [];
  envStored.value = {};
  envError.value = null;
  if (team.value) void loadEnv(team.value.id);
});
</script>

<template>
  <!-- Nested inside this dialog rather than hoisted to the layout. The layout owns the dialogs that
       are ALTERNATIVES to each other — Settings, Users — because one replaces the other on screen.
       This one is a step WITHIN the Members tab, and the tab is still there behind it. -->
  <AddMemberDialog
    v-if="team"
    v-model="addingMember"
    :team="team.id"
    :team-name="team.name"
    :member-agents="team.memberAgents"
    @added="board.refresh()"
  />


  <!-- A SECOND instance of MemberSettingsDialog, not the one ContainerCard nests for its own card -
       see settingsSnapshot's own comment above for why sharing one across both is not the move. -->
  <MemberSettingsDialog
    v-if="settingsSnapshot"
    v-model="settingsOpen"
    :snapshot="settingsSnapshot"
  />

  <q-dialog v-model="open">
    <q-card class="team-card os-dialog-md">
      <q-form lazy-rules="ondemand" @submit="save">
        <q-card-section class="q-pb-none">
          <div class="os-dialog-title">Team settings</div>
          <div class="text-caption os-text-muted">{{ team?.name ?? 'No team selected' }}</div>
        </q-card-section>

        <q-tabs v-model="tab" dense no-caps align="left" active-color="primary" class="os-text-muted">
          <q-tab name="general" label="General" />
          <q-tab name="members" :label="`Members (${containers.length})`" />
          <q-tab name="instructions" :label="TeamInstructionsTab" />
          <q-tab name="repos" label="GitHub Repos" />
          <q-tab name="env" label="Environment" />
        </q-tabs>

        <q-separator />

        <q-tab-panels v-model="tab" animated class="os-tab-panels team-panels">
          <q-tab-panel name="general">
            <!-- The identifier is NOT shown, here or anywhere else a person looks. It is machinery -
                 a directory name, a route value, half of a container id - and a second name beside
                 the first only invites the question "which one is the real one". See the team-naming
                 note in AGENTS.md: render the label, address with the id. -->
            <q-input
              v-model="displayName"
              label="Team name"
              outlined
              dense
              class="q-mb-md"
              readonly
              hint="Chosen when the team was created and fixed after that. A full rename would also have to move the identifier, folders and member ids."
            />

            <q-input
              :model-value="board.managerName"
              label="Manager"
              outlined
              dense
              readonly
              hint="Every team is created with its Manager. There is no team without a door into it."
            />

            <!-- WHERE THIS TEAM'S FILES ARE, and READ-ONLY on purpose rather than for want of an
                 endpoint: moving an existing team's root means relocating the whole tree AND
                 repairing git worktrees, which store absolute paths, and that is not offered.

                 Rendered only when it is there. The server fills it for a PERSON and withholds it
                 from a machine principal - an absolute path on the Host's own filesystem is not an
                 agent's business - so an absent value means "you may not see this", never "this team
                 has no folder". A blank readonly box saying "Team folder" would say the second
                 thing. -->
            <q-input
              v-if="team?.root"
              :model-value="team.root"
              label="Team folder"
              outlined
              dense
              readonly
              class="q-mt-md"
              hint="Where everything this team owns lives. Chosen when the team was created and fixed after that."
            />

            <!-- DYNAMIC MEMBERS: which Agents a member a manager hires while nobody is watching may
                 run. The same heading and the same allowlist as New Team, deliberately - a person who
                 has met one has met the other.

                 This is a setting rather than a literal inside the manager's own prompt, because a
                 manager copies what it is shown: a literal there would have a team deliberately put
                 on one CLI hire workers on another. What a member is told is not
                 chosen here: it is the built-in Member prompt. -->
            <div class="text-subtitle2 q-mt-lg">Dynamic members</div>
            <div class="os-body os-text-muted q-mb-sm">
              What a member a manager hires gets when nobody is here to choose.
            </div>

            <div>
              <div class="row items-center no-wrap">
                <q-select
                  v-model="memberAgentToAdd"
                  :options="memberAgentOptions"
                  :loading="agentsLoading"
                  class="col"
                  outlined
                  dense
                  label="Allowlist: add Agent"
                  hint="Headless presets only. Ordered: earlier entries win ties."
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

            <!-- THE PER-WORKFLOW BUDGET, the same field and the same words as New Team - a person
                 who has met one has met the other, which is the rule the two pickers above already
                 follow.

                 IT BOUNDS ONE WORKFLOW AND NOT THE TEAM, which the hint has to say because the
                 obvious misreading is the other one: this team with three workflows open has three
                 budgets of this figure, one each, and nothing is shared between them. -->
            <div class="text-subtitle2 q-mt-lg">Spending</div>
            <div class="os-body os-text-muted q-mb-sm">
              A workflow that reaches its budget pauses, and a person can resume it. Its siblings on
              this team keep running.
            </div>

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

            <q-banner v-if="saveError" dense class="os-bg-tint-error text-negative q-mt-md">
              <template #avatar><q-icon name="error" /></template>
              {{ saveError }}
            </q-banner>
          </q-tab-panel>

          <q-tab-panel name="members">
            <q-list v-if="containers.length > 0" bordered separator>
              <q-item v-for="container in containers" :key="container.id">
                <q-item-section avatar>
                  <q-icon name="smart_toy" />
                </q-item-section>

                <q-item-section>
                  <q-item-label>{{ container.name }}</q-item-label>
                  <q-item-label caption>
                    agent <span class="mono">{{ container.agent }}</span> · queue
                    <span class="mono">{{ container.queueDepth }}/{{ container.ceiling }}</span> ·
                    {{ container.state }}
                  </q-item-label>
                </q-item-section>

                <q-item-section side>
                  <!-- Enabled, so no tooltip-wrap: that wrapper exists only because a DISABLED button
                       swallows pointer events and a tooltip inside one never fires. A live button
                       needs none of it, matching the Add-member and Manage-in-Users buttons below. -->
                  <div class="row items-center no-wrap">
                    <q-btn
                      flat
                      dense
                      round
                      icon="tune"
                      aria-label="Member settings"
                      @click="openSettings(container)"
                    >
                      <q-tooltip>Member settings</q-tooltip>
                    </q-btn>

                    <!-- ABSENT for a manager rather than disabled. A disabled control invites the
                         question "what would let me press it", and nothing would: the server answers
                         409 whoever asks. The empty span keeps the two rows' controls aligned. -->
                    <q-btn
                      v-if="!isManager(container)"
                      flat
                      dense
                      round
                      color="negative"
                      icon="delete"
                      :aria-label="`Delete ${container.name}`"
                      @click="askRemove(container)"
                    >
                      <q-tooltip>Delete this member</q-tooltip>
                    </q-btn>
                    <span v-else class="member-bin-spacer" />
                  </div>
                </q-item-section>
              </q-item>
            </q-list>

            <div v-else class="os-text-muted">This team has no members.</div>

            <!-- THE CONCIERGE IS NOT LISTED HERE, and its absence is deliberate.

                 It shares the members' QUESTION - which Agent runs, and what is it told - but not
                 their SCOPE: a session is keyed on the USER alone, one Concierge per person serving
                 every team they reach. A row under a team's Members tab would report an
                 instance-wide setting as though it belonged to the team being looked at, and
                 changing it here would change every other team's Concierge too, which is exactly
                 what a team-scoped screen must not do.

                 It lives on Admin → Concierge, beside the other things that are true of the whole
                 instance. -->

            <!-- Enabled, so no tooltip-wrap: that wrapper exists because a DISABLED button swallows
                 pointer events and the tooltip inside it never fires. A live button needs none of it. -->
            <q-btn
              class="q-mt-md"
              flat
              no-caps
              icon="person_add"
              label="Add member"
              :disable="!team"
              @click="addingMember = true"
            />
          </q-tab-panel>

          <!-- TEAM INSTRUCTIONS, a tab of their own and not a box on General: every agent member
               reads them, the Manager too, so they are not a setting of Dynamic members. The same
               heading and hint as New Team. Saved by Save, like everything else here. -->
          <q-tab-panel name="instructions">
            <div class="text-subtitle2">{{ TeamInstructionsLabel }}</div>
            <q-input
              v-model="additionalInstructions"
              type="textarea"
              autogrow
              outlined
              dense
              class="q-mt-xs"
              :aria-label="TeamInstructionsLabel"
              :hint="TeamInstructionsHint"
            />
            <div class="os-body os-text-muted q-mt-lg">{{ TeamInstructionsTakesEffect }}</div>

            <q-banner v-if="saveError" dense class="os-bg-tint-error text-negative q-mt-md">
              <template #avatar><q-icon name="error" /></template>
              {{ saveError }}
            </q-banner>
          </q-tab-panel>

          <q-tab-panel name="repos">
            <div class="os-body os-text-muted q-mb-sm">
              Ordered repository URLs for this team. The first entry is the primary repo.
            </div>

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

            <q-list v-if="repos.length > 0" bordered separator class="q-mt-sm">
              <!-- Keyed by POSITION: keyed by the URL, every keystroke in a row would remount it and
                   drop the focus. Each row is a field with its own rules, duplicates included, so a
                   stored URL that no longer passes is marked where it sits. -->
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
              No repos configured. Save with an empty list to clear team repos.
            </div>

            <!-- The branch each Git action treats as main. Empty uses what origin's HEAD names;
                 nothing here, or anywhere, assumes `main`. -->
            <template v-if="storedBranches.length > 0">
              <div class="os-body os-text-muted q-mt-md q-mb-sm">
                Default branch — what the Git dialog merges into and measures against.
              </div>
              <q-list bordered separator>
                <q-item v-for="entry in storedBranches" :key="entry.repo">
                  <q-item-section>
                    <q-input
                      v-model="branchDrafts[entry.repo]"
                      class="mono"
                      dense
                      borderless
                      hide-bottom-space
                      :label="entry.repo"
                      :placeholder="entry.fromRemote ?? 'not known'"
                      :aria-label="`Default branch for ${entry.repo}`"
                      :rules="branchRules"
                    />
                    <q-item-label caption>{{ defaultBranchCaption(entry) }}</q-item-label>
                  </q-item-section>
                  <q-item-section side>
                    <q-btn
                      flat
                      dense
                      no-caps
                      label="Clear"
                      :disable="!(branchDrafts[entry.repo] ?? '').trim()"
                      :aria-label="`Clear the default branch for ${entry.repo}`"
                      @click="branchDrafts[entry.repo] = ''"
                    />
                  </q-item-section>
                </q-item>
              </q-list>
            </template>

            <!-- Contributor mode, per repository. An upstream makes the repository's own URL the
                 fork: the clone gets an `upstream` remote, and Bring current syncs the fork from it.
                 Opening a pull request is not here; a person does that on GitHub for now. -->
            <template v-if="contributorRows.length > 0">
              <div class="os-body os-text-muted q-mt-md q-mb-sm">
                Contributing — give the original project's URL when this repository is your fork of it.
              </div>
              <q-list bordered separator>
                <q-item v-for="{ entry, draft } in contributorRows" :key="entry.repo">
                  <q-item-section>
                    <q-item-label class="mono">{{ entry.repo }}</q-item-label>
                    <q-input
                      v-model="draft.upstreamUrl"
                      class="mono"
                      dense
                      hide-bottom-space
                      label="Upstream URL"
                      placeholder="https://github.com/project/repo.git"
                      :aria-label="`Upstream URL for ${entry.repo}`"
                      :rules="upstreamRules(entry.repo)"
                    />
                    <q-input
                      v-if="draft.upstreamUrl.trim()"
                      v-model="draft.forkOwner"
                      class="mono"
                      dense
                      hide-bottom-space
                      label="Fork owner"
                      placeholder="read from the repository URL"
                      :aria-label="`Fork owner for ${entry.repo}`"
                      :rules="forkOwnerRules"
                    />
                    <q-toggle
                      v-model="draft.dcoSignOff"
                      label="Sign off commits (DCO)"
                      :aria-label="`Sign off commits (DCO) for ${entry.repo}`"
                    />
                    <q-item-label caption>
                      Adds Signed-off-by with this instance's git identity to every commit made in the clone.
                    </q-item-label>
                    <q-input
                      v-model="draft.claSignedNote"
                      dense
                      hide-bottom-space
                      label="CLA signed (note)"
                      placeholder="e.g. signed on the project's site, 2026-09-20"
                      :aria-label="`CLA note for ${entry.repo}`"
                      :rules="claNoteRules"
                    />
                    <q-item-label caption>A record only. The platform never signs a CLA.</q-item-label>
                  </q-item-section>
                </q-item>
              </q-list>
            </template>

            <q-banner v-if="saveError" dense class="os-bg-tint-error text-negative q-mt-md">
              <template #avatar><q-icon name="error" /></template>
              {{ saveError }}
            </q-banner>
          </q-tab-panel>

          <q-tab-panel name="env">
            <div class="os-body os-text-muted q-mb-sm">
              Named values every member of this team is launched with. Reference them in a prompt as
              <span class="mono">{env:NAME}</span> rather than writing the value anywhere.
            </div>

            <q-banner dense class="os-bg-tint-warn text-warning q-mb-sm">
              Coordination, not confinement. These are stored in plain text and every agent can read
              them. Use test credentials only &mdash; never a production key.
            </q-banner>

            <div
              v-for="(row, index) in envRows"
              :key="index"
              class="row items-center no-wrap q-mb-sm"
            >
              <q-input
                v-model="row.name"
                class="col-4"
                outlined
                dense
                hide-bottom-space
                label="Name"
                placeholder="TEST_ADMIN_EMAIL"
                :rules="envNameRules(envNames, index)"
                @keydown.enter.prevent
              />
              <q-input
                v-model="row.value"
                class="col q-ml-sm"
                outlined
                dense
                hide-bottom-space
                label="Value"
                :rules="envValueRules"
                @keydown.enter.prevent
              />
              <q-btn
                class="col-auto q-ml-sm"
                flat
                dense
                round
                icon="delete"
                :aria-label="`Remove ${row.name || 'entry'}`"
                @click="removeEnvRow(index)"
              />
            </div>

            <div class="row items-center no-wrap q-mt-sm">
              <q-btn outlined dense no-caps label="Add" @click="addEnvRow" />
              <q-btn
                class="q-ml-sm"
                color="primary"
                dense
                no-caps
                label="Save"
                :disable="!envChanged || !envValid || envSaving"
                :loading="envSaving"
                @click="team && saveEnv(team.id)"
              />
            </div>

            <div v-if="envParsed.error" class="text-negative q-mt-sm">{{ envParsed.error }}</div>

            <div v-if="envError" class="text-negative q-mt-sm">{{ envError }}</div>
          </q-tab-panel>

        </q-tab-panels>

        <q-separator />

        <q-card-actions align="right">
          <q-btn flat no-caps label="Close" @click="open = false" />

          <!-- Tooltip on the wrapper, for the same reason as every other disabled control here. -->
          <span class="tooltip-wrap">
            <q-btn
              unelevated
              no-caps
              color="primary"
              label="Save changes"
              type="submit"
              :disable="!valid || saving || !changed"
              :loading="saving"
            />
            <q-tooltip v-if="saveTooltip">{{ saveTooltip }}</q-tooltip>
          </span>
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>

  <!-- Nested inside this dialog, like AddMemberDialog and MemberSettingsDialog above it.
       `no-backdrop-dismiss`, NOT `persistent`: a stray click outside must not dismiss a destructive
       confirmation, but Escape must. Escape CANCELS - it is the safe direction, and a confirmation
       that cannot be backed out of with the key everyone reaches for teaches people to click. -->
  <q-dialog :model-value="doomed !== null" no-backdrop-dismiss @update:model-value="askRemove(null)">
    <q-card class="member-confirm-card os-dialog-sm">
      <q-card-section class="os-dialog-title">Delete {{ doomed?.name }}?</q-card-section>

      <q-card-section class="q-pt-none">
        <p>This cannot be undone. It permanently removes:</p>
        <ul class="q-pl-md">
          <li>the member, and any agent it is running right now</li>
          <li>its working folder, and every file in it</li>
          <li>work it had accepted and not yet finished</li>
        </ul>
        <p class="os-body os-text-muted">
          Any uncommitted work in this member worktree will be lost. Committed branches are kept.
        </p>
        <p class="os-body os-text-muted">
          Its transcripts are kept, and so is the message log — those are a record of what happened,
          and the rest of the team's messages are linked to them.
        </p>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat label="Cancel" :disable="removing" @click="askRemove(null)" />
        <q-btn
          color="negative"
          unelevated
          no-caps
          label="Delete member"
          :loading="removing"
          :disable="removing"
          @click="remove"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* SIX TABS AND NOTHING BOUNDED THEM, which made this the worst of the four: General is a short
   form and Members is a roster, so switching between them re-laid the card out and moved the tab
   strip under the pointer that had just clicked it. Tallest of the four heights because Members and
   Access are lists. See `.os-tab-panels` in `css/app.scss`. */
.team-panels {
  --os-tab-panels-height: 30rem;
}

/* Stands in for the bin a manager does not get, so its tune button lines up with everyone else's
   rather than sitting one control-width to the right. */
.member-bin-spacer {
  display: inline-block;
  width: 2.5rem;
}

/* Holds the tooltip for a control that may be disabled. Inline-flex so it does not disturb the
   layout it sits in - see the template comments for why the tooltip cannot live on the button. */
.tooltip-wrap {
  display: inline-flex;
}

</style>
