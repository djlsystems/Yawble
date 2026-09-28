<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useConsoleStore } from '../stores/console';
import { useCursorList } from '../lib/useCursorList';
import CursorSentinel from './CursorSentinel.vue';
import {
  archiveBacklogItem,
  backlogItem,
  backlogItems,
  createBacklogItem,
  deleteBacklogItem,
  dispatchBacklogItem,
  dispatchBacklogItemToNewTeam,
  fileSystemRoots,
  listCatalog,
  moveBacklogItem,
  repoCheckRefusal,
  restoreBacklogItem,
  setMemberAgents,
  setTeamRepos,
  updateBacklogItem,
  type BacklogItemView,
  type BacklogItemDetail,
} from '../api/client';
import {
  agentsForMode,
  asTeamId,
  type RepoCheckRefusal as RepoCheckRefused,
  type RepoChoice,
} from '../api/types';
import RepoCheckRefusal from './RepoCheckRefusal.vue';
import { afterRefusal, withChoice } from '../lib/repoChoices';
import { applyDefaults, readRemembered, remember } from '../lib/newTeamDefaults';
import {
  backlogTitle,
  firstProblem,
  identifier,
  repoUrlList,
  repoUrlListProblem,
  teamNameTaken,
  type Rule,
} from '../lib/rules';
import {
  BacklogStates,
  REVIEW_OPTIONS,
  canDispatch,
  canDrag,
  canMarkImplemented,
  canReopen,
  canReview,
  dispatchedTeamId,
  dispatchedTeamLabel,
  dispatchedTeamOptions,
  efficiencyLine,
  inFlightText,
  inFlightTitle,
  itemFromDocument,
  itemLabel,
  landedMark,
  linkTitle,
  neighboursFor,
  newTeamNameFor,
  numbered,
  reviewCaption,
  teamLabel,
  teamOptions,
  whyDispatchDisabled,
  whyDragDisabled,
  MAXIMUM_DOCUMENT_BYTES,
  type BacklogSort,
} from '../lib/backlog';

/**
 * The tenant backlog.
 *
 * TWO TABS, AND `archivedAt` IS WHAT DECIDES WHICH LIST AN ITEM IS IN - never its state. An
 * `implemented` item stays in Backlog until somebody archives it, which is what makes the two axes
 * visible rather than theoretical.
 *
 * DELETE APPEARS ON THE ARCHIVED TAB AND NOWHERE ELSE. One Delete control that behaved differently
 * depending on whether the item had ever been dispatched would be the same control doing two
 * different things on state the person cannot see, which is the failure this codebase refuses
 * everywhere else. The server enforces it too; this is not the boundary.
 *
 * THE LOGIC LIVES IN `lib/backlog.ts` and is tested there. What is here is the rendering and the
 * calls - which is the split every dialog in this app makes, and it is why the `#` computation, the
 * drag rule and the tokens line can be pinned without mounting anything.
 */
const open = defineModel<boolean>({ required: true });

/** Copied once because `QBtnToggle` declares its `options` mutable; the source of truth is `lib/`. */
const reviewOptions = [...REVIEW_OPTIONS];

const $q = useQuasar();
const board = useConsoleStore();

const tab = ref<'backlog' | 'archived'>('backlog');

/** The backlog tab: one ordered list a person reorders, so it is always read whole. */
const backlogRows = ref<BacklogItemView[]>([]);

/**
 * THE ARCHIVED TAB READS BY CURSOR: ids below the lowest held, as the sentinel at the bottom
 * comes into view. Only the archive - it is the list that grows without bound, and the one nobody
 * drags, so a partial list has no position to get wrong.
 */
const archived = useCursorList<BacklogItemView>(
  (before, take) => backlogItems(true, { before, take }),
  (item) => item.id,
);

const items = computed(() => (tab.value === 'archived' ? archived.rows.value : backlogRows.value));
const listLoading = ref(false);
const loading = computed(() => listLoading.value || (tab.value === 'archived' && archived.loading.value));
const errorText = ref('');

/** A write's failure, or the archive's first page failing - a later page's failure is on its sentinel. */
const shownError = computed(
  () => errorText.value || (tab.value === 'archived' && !items.value.length ? archived.error.value : ''),
);
const sortBy = ref<BacklogSort>('position');
const descending = ref(false);
const teamFilter = ref<string | null | undefined>(undefined);

const selected = ref<BacklogItemDetail | null>(null);
const busy = ref(false);

const draftTitle = ref('');
const draftBody = ref('');
const draftTeam = ref<string | null>(null);
const adding = ref(false);
const documentInput = ref<HTMLInputElement | null>(null);

const doomed = ref<BacklogItemView | null>(null);
const dispatchTarget = ref<BacklogItemView | null>(null);
const dispatchTeam = ref<string | null>(null);
const dispatchConfirm = ref(false);

/**
 * WHETHER THIS DISPATCH MAKES A TEAM OR USES ONE. Two shapes behind one button, because "start this
 * spec" is one intention and making somebody choose a destination before they have one is the
 * detour this removes.
 */
const dispatchToNew = ref(false);

/**
 * The new team's name, PREFILLED and editable. Derived here rather than behind the route: the field
 * has to show a person what they are about to create, and a second derivation on the server would
 * be two stores of one fact - see `newTeamNameFor`.
 */
const newTeamName = ref('');

/**
 * THE SETTINGS A NEW TEAM IS MADE FROM - the same remembered values the New Team dialog reads,
 * plus any choices or visible fallbacks this dialog applies on top.
 *
 * NULL UNTIL LOADED. Once loaded, the dialog can offer "new team" even on a browser with empty
 * localStorage because Team settings lets a person fill the same required Agent fields New Team
 * does, and the summary names whether the Manager's Agent is remembered, chosen here or a fallback.
 * No Prompt is chosen for either: what a team is told is the built-in role prompt.
 */
const newTeamSettings = ref<{
  agent: string | null
  memberAgents: string[]
  root: string | null
  repos: string[]
} | null>(null);

type NewTeamValueSource = 'remembered' | 'chosen-here' | 'fallback';

const newTeamSource = ref<{
  agent: NewTeamValueSource | null
}>({
  agent: null,
});

const canChooseNewTeam = computed(() => newTeamSettings.value !== null);

/**
 * "Create a local repository for this team", as on New Team: with no repository listed the Host
 * makes `local:<team>` unless this is unticked, which sends `localRepository: false`. Ticked by
 * default and shown only while the new team's list is empty - with a URL listed the Host ignores it.
 */
const newTeamLocalRepository = ref(true);

/**
 * A REFUSED REPOSITORY CHECK on dispatch-to-new: a listed URL `git ls-remote` could not read, so no
 * team was made and nothing was dispatched. Shown with the same component New Team uses; picking the
 * offered choices sends the same dispatch again with `repoChoices`.
 */
const newTeamRefusal = ref<RepoCheckRefused | null>(null);
const newTeamRepoChoices = ref<Record<string, RepoChoice>>({});

function clearNewTeamRefusal() {
  newTeamRefusal.value = null;
  newTeamRepoChoices.value = {};
}

/** A changed list is a different request: a refusal of the old one no longer answers it. */
watch(() => newTeamSettings.value?.repos, clearNewTeamRefusal, { deep: true });

function chooseForNewTeam(url: string, choice: RepoChoice) {
  if (!newTeamRefusal.value || busy.value) return;
  const next = withChoice(newTeamRepoChoices.value, newTeamRefusal.value, url, choice);
  newTeamRepoChoices.value = next.choices;
  if (next.ready) void dispatchIntoNewTeam();
}

/**
 * Every field's rules, from `lib/rules`, held here so the buttons read the SAME list the fields
 * render. The new team's repos are checked at Dispatch as well as in Team settings: remembered URLs
 * arrive from localStorage without passing through that dialog, and the server must not be the one
 * to find a URL that cannot be cloned.
 */
const titleRules: Rule[] = [backlogTitle];
const newTeamNameRules: Rule[] = [identifier];
const repoListRules: Rule[] = [repoUrlList];

const addValid = computed(() => firstProblem(titleRules, draftTitle.value) === null);

const newTeamReposProblem = computed(() =>
  newTeamSettings.value === null ? null : repoUrlListProblem(newTeamSettings.value.repos));

/** The server refused the new team's name (409, taken), so the field is marked as well. */
const newTeamNameTaken = computed(() => dispatchToNew.value && teamNameTaken(errorText.value));

const dispatchValid = computed(() =>
  dispatchToNew.value
    ? canMakeNewTeam.value
      && firstProblem(newTeamNameRules, newTeamName.value) === null
      && newTeamReposProblem.value === null
    : dispatchTeam.value !== null);

const canMakeNewTeam = computed(() =>
  newTeamSettings.value !== null
  && (newTeamSettings.value.agent ?? '').length > 0
  && newTeamSettings.value.memberAgents.length > 0);

/**
 * WHAT THE SETTINGS DIALOG OFFERS AS CHOICES, loaded BESIDE the defaults rather than when it opens.
 *
 * A picker that fetches on open renders its first frame empty, which reads as "the allowlist
 * filtered everything out" rather than "this has not loaded" - which is also why `mountDialog`
 * opens a dialog rather than mounting it open.
 */
const headlessAgents = ref<string[]>([]);
const rootOptions = ref<string[]>([]);

/**
 * Repository URLs this browser has used before. OFFERED, never imposed: they are the same
 * remembered values the New Team dialog suggests, and a person may type one this browser has never
 * seen.
 */
const repoSuggestions = ref<string[]>([]);

/** The team a dispatch to an EXISTING team is aimed at - the record, not just the id. */
const existingTeam = computed(() =>
  dispatchTeam.value === null
    ? null
    : board.teams.find((entry) => entry.id === dispatchTeam.value) ?? null);

const settingsOpen = ref(false);
const settingsBusy = ref(false);
const settingsError = ref('');

/**
 * THE VALUES THE SETTINGS DIALOG IS EDITING, held apart from what they came from so Cancel means
 * something. For a NEW team they are creation parameters and nothing is written until Dispatch; for
 * an EXISTING team they are that team's real settings and Save writes them immediately - there is
 * no such thing as a setting that applies to one dispatch.
 */
const settingsDraft = ref<{
  repos: string[]
  managerAgent: string | null
  memberAgents: string[]
  root: string | null
} | null>(null);

/** Says which of the two meanings above is in force, because they are genuinely different. */
const settingsCaption = computed(() =>
  dispatchToNew.value
    ? 'These make the new team. Nothing is written until you dispatch.'
    : `Saved to ${existingTeam.value?.name ?? 'the team'} straight away - a team's settings are its own.`);

const settingsReachable = computed(() =>
  dispatchToNew.value ? canChooseNewTeam.value : dispatchTeam.value !== null);

function newTeamSourceLabel(source: NewTeamValueSource | null): string {
  switch (source) {
    case 'remembered':
      return 'remembered';
    case 'chosen-here':
      return 'chosen here';
    case 'fallback':
      return 'fallback';
    default:
      return 'not chosen';
  }
}

function sameList(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

/**
 * Opens on the CURRENT values of whichever shape is selected, copied rather than referenced: the
 * draft is discarded on Cancel, and a draft that aliased `newTeamSettings` would have edited it
 * anyway.
 */
function openSettings() {
  settingsError.value = '';

  if (dispatchToNew.value) {
    const current = newTeamSettings.value;

    settingsDraft.value = {
      repos: [...(current?.repos ?? [])],
      managerAgent: current?.agent ?? null,
      memberAgents: [...(current?.memberAgents ?? [])],
      root: current?.root ?? null,
    };
  } else {
    const team = existingTeam.value;

    // A LIVE TEAM'S MANAGER IS NOT EDITED HERE. Repointing a running Manager is a different act with
    // its own consequences - `Reprompt` reaches the live container - and Team Settings is where it
    // belongs. What this offers is what changes a DISPATCH: what gets cloned, and who may run.
    settingsDraft.value = {
      repos: [...(team?.repos ?? [])],
      managerAgent: null,
      memberAgents: [...(team?.memberAgents ?? [])],
      root: null,
    };
  }

  settingsOpen.value = true;
}

const settingsValid = computed(() =>
  settingsDraft.value !== null && firstProblem(repoListRules, settingsDraft.value.repos) === null);

async function saveSettings() {
  const draft = settingsDraft.value;
  if (draft === null || !settingsValid.value || settingsBusy.value) return;

  // NOTHING IS WRITTEN FOR A NEW TEAM. These values are handed to the create-and-dispatch route
  // when Dispatch is pressed, so Save here only replaces what that call will carry.
  if (dispatchToNew.value) {
    newTeamSettings.value = {
      agent: draft.managerAgent,
      memberAgents: [...draft.memberAgents],
      root: draft.root,
      repos: [...draft.repos],
    };
    newTeamSource.value = {
      agent: draft.managerAgent ? 'chosen-here' : null,
    };

    settingsOpen.value = false;
    return;
  }

  if (dispatchTeam.value === null) return;

  const team = existingTeam.value;
  const teamId = asTeamId(dispatchTeam.value);

  settingsBusy.value = true;
  settingsError.value = '';

  try {
    // ONLY WHAT MOVED, the same rule Team Settings follows.
    if (!sameList(draft.repos, team?.repos ?? [])) {
      await setTeamRepos(teamId, draft.repos);
    }

    if (draft.memberAgents.length > 0 && !sameList(draft.memberAgents, team?.memberAgents ?? [])) {
      await setMemberAgents(teamId, draft.memberAgents);
    }

    // Refetched rather than patched in place, like every other "a team changed" path in this app.
    await board.refresh();

    settingsOpen.value = false;
  } catch (cause) {
    settingsError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    settingsBusy.value = false;
  }
}

const sort = computed(() => ({ by: sortBy.value, descending: descending.value }));

/**
 * THE TEAMS THIS PERSON REACHES, which is what both the filter and the create picker are built
 * from. It NARROWS what the server already allows and can never widen it - the same property
 * `/api/kanban/board`'s filter has, and the reason that route is safe.
 */
const teams = computed(() =>
  board.teams.map((team) => ({ id: team.id as string, name: team.name })),
);

const createOptions = computed(() => teamOptions(teams.value));

/**
 * THE FILTER IS BUILT FROM THE ITEMS, NEVER FROM `createOptions`.
 *
 * `createOptions` is the ADD dialog's list - every team on the board - while `visible` narrows on
 * the dispatched team. Offering the board's teams here would make every option but `All` return an
 * empty table on an instance whose backlog is all tenant items. The filter follows the COLUMN, and
 * the column is the dispatched team.
 */
const filterOptions = computed(() => [
  { label: 'All', value: undefined as string | null | undefined },
  ...dispatchedTeamOptions(items.value),
]);

const visible = computed(() => {
  const narrowed =
    teamFilter.value === undefined
      ? items.value
      : items.value.filter((item) => dispatchedTeamId(item) === teamFilter.value);

  // THE LANDING MARK IS COMPUTED ONCE PER ROW, HERE. The template needs four things off it - the
  // marker, the words, the glyph and the long form - and calling the function four times in a
  // `v-for` is how a rule ends up half-evaluated in a binding. `lib/` decides; the row carries the
  // answer; every template expression below is a bare field read.
  return numbered(narrowed, sort.value).map((row) => ({ ...row, landing: landedMark(row.landed) }));
});

/**
 * A FILTER WHOSE OPTION HAS LEFT THE LIST IS CLEARED RATHER THAN LEFT SHOWING NOTHING. Archiving
 * the last item a team was given removes that team from the options above, and a selection nothing
 * offers is an empty table for a reason the person could not have predicted - the filter's own
 * rule, applied to the moment after a write rather than to the dropdown.
 */
watch(filterOptions, (options) => {
  if (teamFilter.value === undefined) return;
  if (options.some((option) => option.value === teamFilter.value)) return;

  teamFilter.value = undefined;
});

/** The open item's landing, beside the claim about it. Null when there is nothing to report. */
const selectedLanding = computed(() => landedMark(selected.value?.item.landed));

// THE BACKLOG TAB ONLY. The archive has no order to drag and holds only the pages read so far.
const draggable = computed(
  () => tab.value === 'backlog' && canDrag(sort.value) && teamFilter.value === undefined,
);

/**
 * A FILTERED LIST CANNOT BE DRAGGED EITHER, and this is the second half of the same rule. Order is
 * a property of the WHOLE backlog; a drop inside a filtered view lands between neighbours that are
 * not the item's real ones, which is the identical problem a non-position sort has.
 */
const dragReason = computed(() => {
  if (tab.value === 'archived') return '';
  if (!canDrag(sort.value)) return whyDragDisabled(sort.value);
  if (teamFilter.value !== undefined) return 'Clear the team filter to reorder. Order is over the whole backlog.';

  return '';
});

async function load() {
  errorText.value = '';

  if (tab.value === 'archived') {
    // From the top again: a restore or a delete changes which ids are below the cursor.
    await archived.reset();
    return;
  }

  listLoading.value = true;

  try {
    backlogRows.value = await backlogItems(false);
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    listLoading.value = false;
  }
}

// LOADS FROM A TRANSITION, so the dialog is mounted CLOSED and then opened. Mounting with
// `modelValue: true` skips a watcher that fires on a transition and not on a first render that
// happens to be true - and then an Agent picker comes up empty and reads as "the allowlist
// filtered everything out".
watch(open, (isOpen) => {
  if (isOpen) void load();
  else selected.value = null;
});

watch(tab, () => {
  selected.value = null;
  void load();
});

/**
 * A ROW TOGGLES ITS OWN PANEL, and the panel has a close control of its own besides.
 *
 * The panel is capped at 40vh, so on a laptop it takes nearly half the dialog and the list above it
 * shrinks to a handful of rows - a person who opened an item to glance at it needs a way back to
 * the list they were reading.
 *
 * CLICKING THE SAME ROW CLOSES IT, which is the affordance somebody tries first, and the ✕ is for
 * the case where the row that opened it has been scrolled away or filtered out. Both are needed:
 * with the list re-sorted under it the row is no longer where it was, and a panel you can only
 * close by finding its row again is the same trap wearing a different shape.
 */
async function openItem(item: BacklogItemView) {
  if (selected.value?.item.id === item.id) {
    selected.value = null;
    return;
  }

  try {
    selected.value = await backlogItem(item.id);
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function add() {
  if (!addValid.value || busy.value) return;

  errorText.value = '';
  busy.value = true;

  try {
    await createBacklogItem({
      title: draftTitle.value.trim(),
      body: draftBody.value,
      team: draftTeam.value,
    });

    draftTitle.value = '';
    draftBody.value = '';
    adding.value = false;

    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/**
 * A NEW ITEM FROM A DOCUMENT: read in the browser, drafted by `itemFromDocument`, and opened in the
 * ordinary Add form so the person checks the title and the spec before anything is saved. Nothing is
 * uploaded to the server except the item itself, through the same route Add uses.
 */
async function uploadDocument(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];

  // Cleared at once so choosing the same file again still fires a change event.
  input.value = '';
  if (!file) return;

  errorText.value = '';
  if (file.size > MAXIMUM_DOCUMENT_BYTES) {
    errorText.value = `${file.name} is larger than 1 MB, which is more than a spec a team reads. Upload the spec itself.`;
    return;
  }

  const draft = itemFromDocument(file.name, await file.text());
  if ('error' in draft) {
    errorText.value = draft.error;
    return;
  }

  draftTitle.value = draft.title;
  draftBody.value = draft.body;
  adding.value = true;
}

async function saveBody() {
  if (selected.value === null) return;

  busy.value = true;

  try {
    await updateBacklogItem(selected.value.item.id, { body: selected.value.item.body });
    await load();
    $q.notify({ type: 'positive', message: `${itemLabel(selected.value.item.id)} saved.`, timeout: 2000 });
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/**
 * MOVES AN ITEM BETWEEN `pending` AND `ready`, from the screen a person is actually in.
 *
 * THE DETAIL PANEL IS WHERE IT LIVES, deliberately: `ready` means somebody has READ THE SPEC, and
 * the spec is on screen exactly there. A toggle in the row would let it be set from a list of
 * titles, which is the claim the state exists to make true.
 */
async function setState(item: BacklogItemView, state: string) {
  busy.value = true;

  try {
    const updated = await updateBacklogItem(item.id, { state });

    // THE OPEN PANEL FOLLOWS THE WRITE. `load()` re-reads the LIST; the detail is a separate copy,
    // so without this the toggle would snap back to the previous value while the row behind it changed.
    // ONLY THE STATE is taken from the response - a PATCH answers `inFlight: null` without asking,
    // and merging the whole record would drop the in-flight mark the detail was showing.
    if (selected.value?.item.id === item.id) selected.value.item.state = updated.state;

    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

async function archive(item: BacklogItemView) {
  busy.value = true;

  try {
    await archiveBacklogItem(item.id);
    selected.value = null;
    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

async function restore(item: BacklogItemView) {
  busy.value = true;

  try {
    await restoreBacklogItem(item.id);
    selected.value = null;
    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

async function remove() {
  if (doomed.value === null) return;

  busy.value = true;

  try {
    await deleteBacklogItem(doomed.value.id);
    doomed.value = null;
    selected.value = null;
    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/**
 * PREFILLED AT OPEN, NOT AT MOUNT. The name depends on WHICH item is being dispatched, so it can
 * only be derived once that is known - and a dialog that derived at mount would show the previous
 * item's name.
 */
function openDispatch(row: BacklogItemView) {
  // THE DOOR, NOT ONLY THE BUTTON. The control is disabled with its reason on screen, but a
  // disabled button is a rendering and this is the function - the same reason the server refuses
  // at BOTH dispatch routes rather than trusting whichever one the screen calls.
  if (!canDispatch(row)) return;

  dispatchTarget.value = row;
  errorText.value = '';
  dispatchConfirm.value = false;
  dispatchToNew.value = false;
  newTeamLocalRepository.value = true;
  clearNewTeamRefusal();
  newTeamName.value = newTeamNameFor(row);
  void loadNewTeamSettings();
}

async function dispatch() {
  if (dispatchTarget.value === null || !dispatchValid.value || busy.value) return;
  if (dispatchTarget.value.inFlight !== null && !dispatchConfirm.value) {
    dispatchConfirm.value = true;
    return;
  }

  dispatchConfirm.value = false;

  if (dispatchToNew.value) {
    await dispatchIntoNewTeam();
    return;
  }

  if (dispatchTeam.value === null) return;

  busy.value = true;

  try {
    const { correlation } = await dispatchBacklogItem(
      asTeamId(dispatchTeam.value), dispatchTarget.value.id);

    $q.notify({
      type: 'positive',
      message: `${itemLabel(dispatchTarget.value.id)} dispatched. Workflow #${correlation}.`,
      caption: 'Its Manager will cut it into cards.',
      timeout: 5000,
    });

    closeDispatch();
    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/**
 * CLONE, THEN DISPATCH INTO THE CLONE - in ONE request, deliberately.
 *
 * Doing it as two calls from here would leave a window where the team exists and the item never
 * reached it: a person would see a team they did not ask for, holding nothing, with no message
 * saying why. The route owns both writes so there is one failure surface.
 *
 * THE SETTINGS COME FROM THIS BROWSER, not from another team: the values remembered from the last
 * team made here. A team assembled for one job should not arrive staffed for another, which is what
 * cloning an existing team would do.
 */
/**
 * READ THE SAME REMEMBERED VALUES THE NEW TEAM DIALOG READS, through the same `applyDefaults` - so a
 * person who set up their last team the way they like gets that again here without setting it twice.
 *
 * `applyDefaults` needs the catalog and the roots to resolve a remembered name that no longer
 * exists: an Agent can be relabelled or removed, and a default pointing at nothing would be refused at
 * the route with a message about a choice the person never made here.
 */
async function loadNewTeamSettings() {
  newTeamSettings.value = null;
  newTeamSource.value = { agent: null };

  const [catalog, roots] = await Promise.allSettled([listCatalog(), fileSystemRoots()]);

  if (catalog.status !== 'fulfilled') return;

  const defaults = applyDefaults(
    readRemembered(),
    catalog.value,
    roots.status === 'fulfilled' ? roots.value.roots : [],
    teams.value.map((team) => team.id),
  );

  headlessAgents.value = agentsForMode(catalog.value.agents, 'Headless').map((agent) => agent.name);
  rootOptions.value = roots.status === 'fulfilled' ? roots.value.roots.map((root) => root.path) : [];
  repoSuggestions.value = defaults.repoSuggestions;
  const fallbackAgent = headlessAgents.value[0] ?? null;
  const managerAgent = defaults.managerAgent ?? fallbackAgent;

  newTeamSource.value = {
    agent: defaults.managerAgent ? 'remembered' : managerAgent ? 'fallback' : null,
  };

  newTeamSettings.value = {
    agent: managerAgent,
    memberAgents: defaults.memberAgents ?? [],
    root: defaults.root,

    // THE REMEMBERED REPOS, NOT `defaults.repos`.
    //
    // `applyDefaults` returns `repos: []` and puts what this browser remembers in
    // `repoSuggestions`, because the New Team dialog RENDERS a repo field and offers them as
    // suggestions to pick from deliberately. Taking `repos: []` here would create every dispatched
    // team with NO REPOSITORY: its Manager would plan cards, hire members, and then block on
    // `no repository attached` - after the spend.
    //
    // This dialog has a repo field, behind Team settings, prefilled from the same suggestions. So
    // the remembered value is the starting point like every other field on this dialog, and it is
    // visible and editable before anything is created rather than silently absent.
    repos: [...defaults.repoSuggestions],
  };
}

async function dispatchIntoNewTeam() {
  if (dispatchTarget.value === null || newTeamSettings.value === null) return;

  errorText.value = '';
  busy.value = true;

  const settings = newTeamSettings.value;

  try {
    const result = await dispatchBacklogItemToNewTeam(
      dispatchTarget.value.id, newTeamName.value.trim(), {
        ...settings,

        // Only with no repository listed: the Host ignores it otherwise.
        localRepository: settings.repos.length === 0 ? newTeamLocalRepository.value : undefined,

        // The answer to a refused check, when there was one.
        repoChoices: newTeamRepoChoices.value,
      });

    $q.notify({
      type: 'positive',
      message: `${itemLabel(dispatchTarget.value.id)} dispatched to a new team, ${result.teamName}.`,
      // A PARTIAL CLONE IS NOT SILENT. The route reports what it could not carry rather than
      // throwing, so a caption that ignored it would hand somebody a team quietly missing its
      // repos or its env.
      caption: `Its Manager will cut the spec into cards. Workflow #${result.correlation}.`,
      timeout: 8000,
    });

    // WHAT THIS DISPATCH USED BECOMES THE NEXT ONE'S DEFAULT, exactly as creating a team through
    // New Team does. Without this the remembering would be one-directional: a person who only ever
    // dispatched would re-choose the same repositories every time, and this browser's remembered
    // `repos` would stay `[]`, leaving the prefill above nothing to offer.
    //
    // AFTER the await, so a refused dispatch does not teach this browser a setting that did not work.

    // WHAT THIS DISPATCH USED BECOMES THE NEXT ONE'S DEFAULT, exactly as creating a team through
    // New Team does. Without this the remembering would be one-directional: a person who only ever
    // dispatched would re-choose the same repositories every time, and this browser's remembered
    // `repos` would stay `[]`, leaving the prefill above nothing to offer.
    //
    // AFTER the await, so a refused dispatch does not teach this browser a setting that did not work.
    remember({
      managerAgent: newTeamSettings.value.agent ?? '',
      memberAgents: newTeamSettings.value.memberAgents,
      root: newTeamSettings.value.root,
      // Not a URL that was dropped for a local repository: it does not exist, so it is no suggestion.
      repos: newTeamSettings.value.repos.filter((url) => newTeamRepoChoices.value[url.trim()] !== 'use-local'),
    });

    clearNewTeamRefusal();
    closeDispatch();

    // THE TEAM LIST IS THIS BROWSER'S TO REFRESH, AND NOTHING WILL DO IT FOR IT. `teamChanged` is
    // pushed to `Clients.Group(team.Id)`, and a browser cannot be in the group of a team that did
    // not exist a moment ago - so the one client that most needs to hear about this team is the
    // one client the push structurally cannot reach. `load()` re-reads the BACKLOG; the teams come
    // from `/api/overview`, which only `refresh()` re-reads. Without this the team would be real,
    // running, and invisible until a hard reload.
    //
    // THE SAME TWO LINES AS `MainLayout.teamCreated`, deliberately: both paths create a team from
    // this browser, and two answers to "what happens after I make a team" is how one of them ends
    // up missing a step. Landing on it is the point - the item was dispatched to be watched.
    board.setActiveTeam(asTeamId(result.team));
    await board.refreshForTeamCreated(result.team);

    await load();
  } catch (cause) {
    const refused = repoCheckRefusal(cause);
    if (refused) {
      newTeamRepoChoices.value = afterRefusal(newTeamRepoChoices.value, refused);
      newTeamRefusal.value = refused;
      return;
    }

    clearNewTeamRefusal();
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

function closeDispatch() {
  dispatchConfirm.value = false;
  dispatchTarget.value = null;
}

/**
 * A REORDER WRITES ONE ROW, and the neighbours are computed in `lib/` so the off-by-one can be
 * tested without a DOM.
 */
async function reorder(from: number, to: number) {
  if (from === to) return;

  const moved = items.value[from];
  if (moved === undefined) return;

  const { after, before } = neighboursFor(items.value, from, to);

  busy.value = true;

  try {
    await moveBacklogItem(moved.id, after, before);
    await load();
  } catch (cause) {
    errorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

function up(index: number) {
  void reorder(index, Math.max(0, index - 1));
}

function down(index: number) {
  void reorder(index, Math.min(items.value.length - 1, index + 1));
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="backlog-card os-dialog-xl">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Backlog</div>
        <div class="text-caption os-text-muted">
          An item is the spec. Dispatch it to a team and its Manager cuts it into cards.
        </div>
      </q-card-section>

      <q-tabs v-model="tab" dense align="left" class="text-primary">
        <q-tab name="backlog" label="Backlog" />
        <q-tab name="archived" label="Archived" />
      </q-tabs>

      <q-separator />

      <q-card-section v-if="shownError" class="q-pb-none">
        <q-banner dense class="bg-negative text-white">{{ shownError }}</q-banner>
      </q-card-section>

      <q-card-section class="backlog-toolbar row items-center q-gutter-sm">
        <q-select
          v-model="teamFilter"
          :options="filterOptions"
          dense
          outlined
          emit-value
          map-options
          label="Team"
          class="backlog-filter"
          popup-content-class="backlog-popup"
        />

        <q-space />

        <div v-if="dragReason" class="text-caption os-text-muted backlog-drag-reason">
          {{ dragReason }}
        </div>

        <q-btn
          v-if="tab === 'backlog'"
          dense
          no-caps
          color="primary"
          icon="add"
          label="Add item"
          @click="adding = true"
        />

        <q-btn
          v-if="tab === 'backlog'"
          dense
          no-caps
          outline
          color="primary"
          icon="upload_file"
          label="Upload document"
          title="Make a new item from a Markdown or text file: its first # heading is the title, the whole file the spec. You check both before it is added."
          @click="documentInput?.click()"
        />
        <input
          ref="documentInput"
          type="file"
          accept=".md,.markdown,.txt,text/markdown,text/plain"
          class="hidden"
          data-test="backlog-document-input"
          @change="uploadDocument"
        />
      </q-card-section>

      <q-card-section class="backlog-list">
        <q-markup-table dense flat class="backlog-table">
          <thead>
            <tr>
              <th class="text-left backlog-index" @click="sortBy = 'position'">#</th>
              <th class="text-left" @click="sortBy = 'id'">Id</th>
              <th class="text-left" @click="sortBy = 'title'">Title</th>
              <!-- WHICH TEAM THIS IS, said in the header, because the row shows two. -->
              <th
                class="text-left"
                title="The team this item was dispatched to. The small link marker under a title is
                       the other fact - who may SEE the item - and the two can differ."
                @click="sortBy = 'team'"
              >Team</th>
              <th class="text-left" @click="sortBy = 'state'">Status</th>
              <th class="text-left" @click="sortBy = 'created'">Added</th>
              <th class="text-left" @click="sortBy = 'updated'">Modified</th>
              <th v-if="tab === 'archived'" class="text-left" @click="sortBy = 'archived'">Archived</th>
              <th class="text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(row, i) in visible"
              :key="row.id"
              class="backlog-row"
              :data-in-flight="row.inFlight ? 'yes' : null"
              @click="openItem(row)"
            >
              <td class="backlog-index">{{ row.index }}</td>
              <td class="backlog-id">{{ itemLabel(row.id) }}</td>
              <td>
                <div>{{ row.title }}</div>
                <!-- WHETHER THIS ITEM IS BEING WORKED, under the title rather than in a column.
                     The screen is used on a PHONE and the table is already four columns wide
                     there, so the mark takes height the row already has and no width. It is NOT
                     in the Team column: `team` is who may SEE the
                     item and is never rewritten on dispatch; this is who is WORKING it, which can
                     be a different team. A tooltip alone is not enough because a phone has no hover -
                     the words are on the row and the long form is the title for those who can. -->
                <span
                  v-if="row.inFlight"
                  class="backlog-inflight"
                  :data-running="row.inFlight.running ? 'yes' : null"
                  :title="inFlightTitle(row.inFlight)"
                  :aria-label="inFlightTitle(row.inFlight)"
                >
                  <q-icon :name="row.inFlight.running ? 'play_arrow' : 'hourglass_empty'" size="12px" />
                  {{ inFlightText(row.inFlight) }}
                </span>
                <!-- WHERE THE WORK ACTUALLY IS, which is not what the Status cell says. A person
                     reading the Backlog has to be able to
                     tell "a Manager says this is done" from "this is on main", and one word cannot
                     carry both - an item can read `implemented` while the only copy of its work
                     sits unpushed in one team's clone.

                     FOUR READINGS AND `unknown` IS ONE OF THEM. `data-landed` carries the word, so
                     the four are four different things to a reader's eye and to a test rather than
                     one string with a different adjective. IT IS NOT HIDDEN WHEN NOBODY CAN TELL
                     and it is NOT DRAWN AS A NEGATIVE: a missing answer rendered as "not landed"
                     is the product asserting something nobody measured, which is the substitution
                     this codebase already refuses for spend.

                     ABSENT ONLY WHEN THERE IS NO LANDING TO REPORT - an item no dispatch has ever
                     touched has no question to answer, which is a different thing again. -->
                <span
                  v-if="row.landing"
                  class="backlog-landed"
                  :data-landed="row.landing.state"
                  :title="row.landing.title"
                  :aria-label="row.landing.title"
                >
                  <q-icon :name="row.landing.icon" size="12px" />
                  {{ row.landing.text }}
                </span>
                <!-- THE LINK, AS A SMALL MARKER AND ONLY WHEN THERE IS ONE. It is who may SEE the
                     item, set by `--team` at `add` and never rewritten by a dispatch - a different
                     fact from the Team column beside it, which names who was GIVEN the work. The
                     two can differ and are deliberately not merged. Most items carry no link at
                     all, so this is absent from most rows rather than reading `(no team)` on
                     them. -->
                <span
                  v-if="row.team"
                  class="backlog-link"
                  :class="row.teamGone ? 'text-negative' : ''"
                  :title="linkTitle(row)"
                  :aria-label="linkTitle(row)"
                >
                  <q-icon name="link" size="12px" />
                  {{ teamLabel(row) }}
                </span>
              </td>
              <!-- WHO IT WAS DISPATCHED TO. Empty on an item nobody has been given - not
                   `(no team)`, which is an answer about the link and meant something else. -->
              <td>{{ dispatchedTeamLabel(row) }}</td>
              <td>
                <div>{{ row.state }}</div>
                <!-- WHY DISPATCH IS DEAD, IN WORDS ON THE ROW. A disabled `<button>` fires no
                     mouse events, so a tooltip inside one never opens - the hover explanation for
                     a disabled control is the one explanation that cannot be read. And a phone has
                     no hover. Same choice as the in-flight mark above, same reason. -->
                <!-- THE BACKLOG TAB ONLY. The archived tab offers no Dispatch control at all, so
                     a reason it is disabled there would explain a button that is not on screen. -->
                <span
                  v-if="tab === 'backlog' && whyDispatchDisabled(row)"
                  class="backlog-blocked"
                  :title="whyDispatchDisabled(row)"
                >
                  {{ whyDispatchDisabled(row) }}
                </span>
              </td>
              <td class="backlog-when">{{ row.createdAt.slice(0, 10) }}</td>
              <td class="backlog-when">{{ row.updatedAt.slice(0, 10) }}</td>
              <td v-if="tab === 'archived'" class="backlog-when">
                {{ (row.archivedAt ?? '').slice(0, 10) }}
              </td>
              <td class="text-right" @click.stop>
                <template v-if="tab === 'backlog'">
                  <q-btn
                    flat dense round size="sm" icon="arrow_upward"
                    :disable="!draggable || busy || i === 0"
                    @click="up(i)"
                  />
                  <q-btn
                    flat dense round size="sm" icon="arrow_downward"
                    :disable="!draggable || busy || i === visible.length - 1"
                    @click="down(i)"
                  />
                  <!-- DISABLED ON ANYTHING THAT IS NOT `ready`, with the reason rendered in the
                       Status cell rather than hidden behind this tooltip - see there. The tooltip
                       stays for the ENABLED case, which is the case it can actually open in. -->
                  <q-btn
                    flat dense round size="sm" icon="send" color="primary"
                    :disable="busy || !canDispatch(row)"
                    @click="openDispatch(row)"
                  >
                    <q-tooltip>Dispatch to a team</q-tooltip>
                  </q-btn>
                  <q-btn
                    flat dense round size="sm" icon="inventory_2"
                    :disable="busy"
                    @click="archive(row)"
                  >
                    <q-tooltip>Archive</q-tooltip>
                  </q-btn>
                </template>
                <template v-else>
                  <q-btn
                    flat dense round size="sm" icon="unarchive"
                    :disable="busy"
                    @click="restore(row)"
                  >
                    <q-tooltip>Restore</q-tooltip>
                  </q-btn>
                  <!-- DELETE APPEARS HERE AND NOWHERE ELSE. See this component's own summary. -->
                  <q-btn
                    flat dense round size="sm" icon="delete" color="negative"
                    :disable="busy"
                    @click="doomed = row"
                  >
                    <q-tooltip>Delete permanently</q-tooltip>
                  </q-btn>
                </template>
              </td>
            </tr>
            <tr v-if="visible.length === 0 && !loading">
              <td colspan="9" class="os-text-muted">
                {{ tab === 'archived' ? 'Nothing archived.' : 'Nothing in the backlog yet.' }}
              </td>
            </tr>
          </tbody>
        </q-markup-table>

        <CursorSentinel
          v-if="tab === 'archived' && items.length"
          :loading="archived.loading.value"
          :exhausted="archived.exhausted.value"
          :error="archived.error.value"
          done-label="The oldest archived item."
          @more="archived.loadMore"
        />
      </q-card-section>

      <q-separator v-if="selected" />

      <q-card-section v-if="selected" class="backlog-detail">
        <div class="row items-center q-gutter-sm">
          <div class="text-subtitle1">
            {{ itemLabel(selected.item.id) }}: {{ selected.item.title }}
          </div>
          <q-space />
          <!-- WHERE THE OPEN ITEM'S WORK IS, BESIDE THE CLAIM ABOUT IT. The two facts sit next to
               each other because neither one answers the other's question: the state is what a
               person or a Manager SAID, this is what the repository SHOWS. Same rendering as the
               row, same rule about `unknown`. -->
          <span
            v-if="selectedLanding"
            class="backlog-landed"
            :data-landed="selectedLanding.state"
            :title="selectedLanding.title"
            :aria-label="selectedLanding.title"
          >
            <q-icon :name="selectedLanding.icon" size="12px" />
            {{ selectedLanding.text }}
          </span>

          <!-- CLOSE IS THE LAST CONTROL IN THIS ROW RATHER THAN THE FIRST, and it is further down
               past the state controls: a close beside "Mark implemented" is a misclick that
               changes an item's state. The landing chip above is a FACT and sits nearest the
               title; everything after it acts. -->
          <!-- THE REVIEW TOGGLE. `pending` means nobody has reviewed this and `ready`
               means somebody has.

               IT SETS TWO STATES AND NOT FOUR. `implemented` is the other axis and `declared` is
               the platform's own word; one control for unrelated decisions is how a person marks
               their own draft done by reaching for the nearest toggle.

               ABSENT, NOT DISABLED, ON A DECLARED OR IMPLEMENTED ITEM: neither of its two values
               is the item's state, so it would render with nothing selected and read as broken.
               The caption below says so and names the way back, which is the button beside it. -->
          <q-btn-toggle
            v-if="canReview(selected.item)"
            :model-value="selected.item.state"
            :options="reviewOptions"
            class="backlog-review"
            dense no-caps unelevated
            toggle-color="primary"
            color="grey-3"
            text-color="grey-9"
            :disable="busy"
            @update:model-value="(value: string) => setState(selected!.item, value)"
          />
          <!-- A PERSON MAY STILL SAY `implemented` BY HAND, AND ON A `declared` ITEM THAT IS THE
               WHOLE POINT: a Manager's declaration stops at `declared`, so the last
               step is a person's and nothing derived ever overrides them. Two independent
               controls rather than one whose label flips, because on a declared item BOTH moves
               are available - confirm the landing, or put it back. -->
          <q-btn
            v-if="canMarkImplemented(selected.item)"
            flat dense no-caps size="sm"
            label="Mark implemented"
            :disable="busy"
            @click="setState(selected.item, BacklogStates.Implemented)"
          />
          <q-btn
            v-if="canReopen(selected.item)"
            flat dense no-caps size="sm"
            label="Reopen as pending"
            :disable="busy"
            @click="setState(selected.item, BacklogStates.Pending)"
          />
          <q-btn
            flat dense round
            icon="close"
            size="sm"
            aria-label="Close this item"
            class="backlog-detail-close"
            @click="selected = null"
          >
            <q-tooltip>Close this item</q-tooltip>
          </q-btn>
        </div>

        <div class="text-caption os-text-muted">{{ reviewCaption(selected.item) }}</div>

        <q-input
          v-model="selected.item.body"
          type="textarea"
          outlined
          dense
          autogrow
          label="The spec"
          class="q-mt-sm"
        />

        <div class="row q-mt-sm">
          <q-space />
          <q-btn dense no-caps color="primary" label="Save" :disable="busy" @click="saveBody" />
        </div>

        <div v-if="selected.dispatches.length > 0" class="q-mt-md">
          <div class="text-caption os-text-muted">Dispatched</div>
          <div v-for="(d, i) in selected.dispatches" :key="d.id" class="backlog-dispatch">
            <span :class="d.teamGone ? 'text-negative' : ''">{{ d.teamName }}</span>
            <span class="os-text-muted"> · workflow #{{ d.correlation }}</span>
            <span class="os-text-muted"> · {{ selected.stats[i]?.outcome ?? 'unknown' }}</span>
            <span class="os-text-muted">
              ·
              {{ efficiencyLine(selected.stats[i] ?? { tokens: 0, runsWithUsage: 0, runsWithoutUsage: 0 }) }}
            </span>
            <span v-if="d.frozenAt" class="os-text-muted"> · frozen</span>
            <div v-if="(selected.stats[i]?.members.length ?? 0) > 0" class="text-caption os-text-muted">
              {{ selected.stats[i]!.members.join(', ') }}
            </div>
          </div>
        </div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Close" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- Add. A `q-dialog` in this component's own template, never `$q.dialog()`. -->
  <q-dialog v-model="adding">
    <q-card class="backlog-add-card os-dialog-md">
      <q-card-section class="os-dialog-title">Add a backlog item</q-card-section>
      <q-form lazy-rules="ondemand" @submit="add">
        <q-card-section class="q-gutter-sm">
          <q-input v-model="draftTitle" dense outlined autofocus label="Title" :rules="titleRules" />
          <q-input v-model="draftBody" type="textarea" dense outlined autogrow label="The spec" />
          <q-select
            v-model="draftTeam"
            :options="createOptions"
            dense
            outlined
            emit-value
            map-options
            label="Team"
            popup-content-class="backlog-popup"
          />
          <div class="text-caption os-text-muted">
            The team decides who SEES this item. Dispatching it later shows it to whichever team runs
            it, whatever this says.
          </div>
          <q-banner v-if="errorText" dense class="os-bg-tint-error text-negative">{{ errorText }}</q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" :disable="busy" @click="adding = false" />
          <q-btn
            no-caps color="primary" label="Add" type="submit"
            :disable="!addValid || busy"
            :loading="busy"
          />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>

  <!-- Dispatch. -->
  <q-dialog :model-value="dispatchTarget !== null" @update:model-value="closeDispatch">
    <q-card class="backlog-add-card os-dialog-md">
      <q-card-section class="os-dialog-title">
        Dispatch {{ dispatchTarget ? itemLabel(dispatchTarget.id) : '' }}
      </q-card-section>
      <q-form lazy-rules="ondemand" @submit="dispatch">
        <q-card-section>
          <!-- TWO SHAPES BEHIND ONE BUTTON. "Start this spec" is one intention; making somebody go
               and create a team first would be a detour. -->
          <!-- THE SECOND OPTION WAITS ONLY FOR ITS CHOICES TO LOAD. A browser with nothing remembered
               can still make a new team from this screen, because Team settings lets a person choose
               the required values here instead of being sent to New Team first.

               AND IT IS NOT A CLONE. The settings are this browser's remembered ones, not another
               team's, so the label must not say "cloned from one" - that would also tell a person
               with no teams that the option needs one, when what it needs is a previous team MADE
               IN THIS BROWSER. -->
          <q-option-group
            v-model="dispatchToNew"
            :options="[
              { label: 'To a team that exists', value: false },
              { label: 'To a new team', value: true, disable: !canChooseNewTeam },
            ]"
            :disable="busy"
            dense
            inline
            type="radio"
          />

          <q-select
            v-if="!dispatchToNew"
            v-model="dispatchTeam"
            :options="teams.map((t) => ({ label: t.name, value: t.id }))"
            dense
            outlined
            emit-value
            map-options
            label="Team"
            class="q-mt-md"
            popup-content-class="backlog-popup"
          />

          <template v-else>
            <q-input
              v-model="newTeamName"
              dense
              outlined
              label="New team name"
              class="q-mt-md"
              :disable="busy"
              :rules="newTeamNameRules"
              :error="newTeamNameTaken"
              :error-message="newTeamNameTaken ? errorText : undefined"
              hint="Derived from the item's title. Letters, digits, '-' and '_', at most 32 characters."
            />
            <div v-if="newTeamReposProblem" class="os-body text-negative q-mt-sm">
              {{ newTeamReposProblem }} Fix it in Team settings.
            </div>
            <q-checkbox
              v-if="newTeamSettings !== null && newTeamSettings.repos.length === 0"
              v-model="newTeamLocalRepository"
              dense
              class="q-mt-sm"
              label="Create a local repository for this team"
              :disable="busy"
              data-local-repository-checkbox
            />
            <!-- WHAT IT WILL ACTUALLY MAKE, named rather than implied. A screen about to create a
                 team has to say what kind. The source matters too: remembered from this browser,
                 chosen on this screen, or a visible fallback when this browser remembered nothing. -->
            <div v-if="canChooseNewTeam" class="text-caption os-text-muted q-mt-md">
              Manager runs <strong>{{ newTeamSettings?.agent }}</strong>
              ({{ newTeamSourceLabel(newTeamSource.agent) }}).
              <span v-if="(newTeamSettings?.memberAgents.length ?? 0) > 0">
                Members may run {{ newTeamSettings?.memberAgents.join(', ') }}.
              </span>
              <span v-if="canMakeNewTeam">
                Ready to dispatch.
              </span>
              <span v-else>
                Finish Team settings before Dispatch so members have an allowed Agent.
              </span>
            </div>
          </template>

          <!-- THE SETTINGS THAT DECIDE WHAT A DISPATCH CAN DO, reachable from the screen that starts
               it. Repos are the reason this exists: a team with none is one whose Manager plans
               cards, hires members and then blocks, having already spent. Offered for BOTH shapes,
               because the question "what will this team be able to touch" is the same question
               whether the team is about to be made or already exists - only the answer's permanence
               differs, which is what the caption says. -->
          <div class="row items-center q-mt-md">
            <q-btn
              flat dense no-caps size="sm" icon="tune" label="Team settings"
              :disable="busy || !settingsReachable"
              @click="openSettings"
            />
            <span class="text-caption os-text-muted q-ml-sm">{{ settingsCaption }}</span>
          </div>

          <div class="text-caption os-text-muted q-mt-sm">
            Its Manager is told, reads the item, and cuts it into cards. This makes the item visible to
            that team.
          </div>

          <RepoCheckRefusal
            v-if="dispatchToNew && newTeamRefusal"
            class="q-mt-md"
            :refusal="newTeamRefusal"
            :chosen="newTeamRepoChoices"
            :busy="busy"
            @choose="chooseForNewTeam"
          />

          <q-banner v-if="errorText" dense class="os-bg-tint-error text-negative q-mt-md">
            {{ errorText }}
          </q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" :disable="busy" @click="closeDispatch" />
          <q-btn
            no-caps color="primary" label="Dispatch" type="submit"
            :disable="!dispatchValid || busy"
            :loading="busy"
          />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>

  <q-dialog :model-value="dispatchConfirm" no-backdrop-dismiss @update:model-value="dispatchConfirm = false">
    <q-card class="backlog-add-card os-dialog-md">
      <q-card-section class="os-dialog-title">
        Dispatch {{ dispatchTarget ? itemLabel(dispatchTarget.id) : '' }} again?
      </q-card-section>
      <q-card-section>
        {{
          dispatchTarget?.inFlight
            ? `${itemLabel(dispatchTarget.id)} is already in flight on ${dispatchTarget.inFlight.teamName}. Dispatch anyway?`
            : ''
        }}
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="busy" @click="dispatchConfirm = false" />
        <q-btn no-caps color="primary" label="Dispatch anyway" :disable="busy" @click="dispatch" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- Team settings for the dispatch. A SIBLING of the dispatch dialog rather than nested inside
       it, which is the pattern this template already uses for Add and Delete: a QDialog teleports
       to `<body>`, so nesting buys nothing and costs a stacking context. -->
  <q-dialog v-model="settingsOpen">
    <q-card class="backlog-add-card os-dialog-md">
      <q-card-section class="os-dialog-title">
        {{ dispatchToNew ? 'New team settings' : `Settings for ${existingTeam?.name ?? ''}` }}
      </q-card-section>
      <q-form lazy-rules="ondemand" @submit="saveSettings">
        <q-card-section v-if="settingsDraft">
          <div class="text-caption os-text-muted">{{ settingsCaption }}</div>

          <!-- REPOS FIRST, because it is the field that decides whether the team can do the work at
               all. `new-value-mode` lets a URL this browser has never seen be typed; the options are
               the remembered ones. Order is kept - the first entry is the primary repo. -->
          <q-select
            v-model="settingsDraft.repos"
            :options="repoSuggestions"
            label="Repositories the platform clones"
            hint="The first is the primary. Type a URL to add one this browser has not used before."
            class="q-mt-md"
            dense outlined multiple use-chips use-input
            new-value-mode="add-unique"
            popup-content-class="backlog-popup"
            :disable="settingsBusy"
            :rules="repoListRules"
          />

          <q-select
            v-model="settingsDraft.memberAgents"
            :options="headlessAgents"
            label="Agents members may run"
            hint="An ordered allowlist. A hire naming none of these is refused."
            class="q-mt-md"
            dense outlined multiple use-chips
            popup-content-class="backlog-popup"
            :disable="settingsBusy"
          />

          <!-- MANAGER AND ROOT ARE NEW-TEAM ONLY. Repointing a LIVE Manager reaches the running
               container through `Reprompt`, and a team's root has no setter at all - neither is this
               dialog's to change, and offering them here would be a control that cannot work. -->
          <template v-if="dispatchToNew">
            <q-select
              v-model="settingsDraft.managerAgent"
              :options="headlessAgents"
              label="Agent the Manager runs"
              class="q-mt-md"
              dense outlined
              popup-content-class="backlog-popup"
              :disable="settingsBusy"
            />

            <q-select
              v-model="settingsDraft.root"
              :options="rootOptions"
              label="Where the team's files go"
              hint="Left empty, it goes under the instance root."
              class="q-mt-md"
              dense outlined clearable
              popup-content-class="backlog-popup"
              :disable="settingsBusy"
            />
          </template>

          <q-banner v-if="settingsError" dense class="os-bg-tint-error text-negative q-mt-md">
            {{ settingsError }}
          </q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" :disable="settingsBusy" @click="settingsOpen = false" />
          <q-btn
            no-caps color="primary" :label="dispatchToNew ? 'Use these' : 'Save'" type="submit"
            :disable="!settingsValid || settingsBusy"
            :loading="settingsBusy"
          />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>

  <!-- Delete. A destructive confirmation lives in this template for the reason `$q.dialog()`
       cannot be lifted at all: its class lands on the inner card, and a confirmation that renders
       behind the shell reads as the click doing nothing. -->
  <q-dialog :model-value="doomed !== null" no-backdrop-dismiss @update:model-value="doomed = null">
    <q-card class="backlog-add-card os-dialog-md">
      <q-card-section class="os-dialog-title">
        Delete {{ doomed ? itemLabel(doomed.id) : '' }}?
      </q-card-section>
      <q-card-section>
        <div>This cannot be undone. It permanently removes:</div>
        <ul>
          <li>the item and its spec</li>
          <li>its record of which teams ran it, and what they spent</li>
        </ul>
        <div class="text-caption os-text-muted">
          Cards already planned from it stay on their boards - they are on the message log, which
          nothing removes. They will read "{{ doomed ? itemLabel(doomed.id) : '' }} (deleted)".
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="busy" @click="doomed = null" />
        <q-btn no-caps color="negative" label="Delete" :disable="busy" @click="remove" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* THE WIDEST STEP OF THE DIALOG SCALE (`os-dialog-xl`, 72rem). Nine columns of dates, ids, states
   and a free-text title do not fit in 1000px - the Team column would be half off the right
   edge - which is why this is xl and not lg. The width and the phone gutter come from
   `css/dialog-scale.scss`; this rule only makes the card a column its list can scroll inside. */
.backlog-card {

  /* A FLEX CHILD DEFAULTS TO `min-height: auto` AND REFUSES TO SHRINK BELOW ITS CONTENT, so
     `overflow-y: auto` on the list below is not what makes it scroll - this is what lets it. */
  display: flex;
  flex-direction: column;
  max-height: 90vh;
}

/* `auto` ON BOTH AXES, AND THE HORIZONTAL HALF IS LOAD-BEARING RATHER THAN A TIDY-UP. With
   `overflow-y: auto` alone the x axis stays `visible`, so a table wider than the card is not
   clipped by THIS box at all - it overflows into the card, whose own scrollbar sits at the bottom
   of the whole DIALOG. Reaching it would mean scrolling the list to its end first, so the
   controls to the right of a row could not be got at without scrolling down to rows nobody is
   looking at.
   Setting x explicitly makes the scrollbar belong to the list, where the content that overflows
   is. It is unlikely to appear at all at the width above - which is the point: it is there for
   the narrow window, not for the ordinary one. */
.backlog-list {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  overflow-x: auto;
}

.backlog-detail {
  flex: 0 0 auto;
  max-height: 40vh;
  overflow-y: auto;

  /* A hairline where the panel meets the list, because the two scroll independently and without it
     a body that opens reads as more rows. */
  border-top: 1px solid var(--os-rule);
}

/* Pushed away from the state controls beside it - see the comment in the template. */
.backlog-detail-close {
  margin-left: 4px;
}

.backlog-filter {
  min-width: 180px;
}

.backlog-row {
  cursor: pointer;
}

/* A ROW MARKER AS WELL AS THE WORDS: a thin accent down the row's left edge, so a person scanning
   a long list on a phone finds the items being worked without reading every title. Selected on an
   attribute the template spells, for the reason KanbanCard.vue gives - a class built in a binding
   is a class `styles-match-templates.spec.ts` cannot see. */
.backlog-row[data-in-flight] td:first-child {
  box-shadow: inset 3px 0 0 var(--q-info);
}

.backlog-row[data-in-flight] .backlog-inflight[data-running] {
  background: var(--os-tint-ok);
  color: var(--os-ok);
  border-color: color-mix(in srgb, var(--os-ok) 30%, transparent);
}

/* The mark itself. Blue while the workflow is open and idle, green (above) while a member is
   Running - the same two colours the board's cards use for queued and running, so a person who
   reads one screen reads the other. */
.backlog-inflight {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-top: 2px;
  padding: 0 6px;
  border: 1px solid color-mix(in srgb, var(--q-info) 30%, transparent);
  border-radius: 10px;
  background: var(--os-tint-info);
  color: var(--q-info);
  font-size: 11px;
  line-height: 18px;
  white-space: nowrap;
}

/* THE LINK MARKER. Deliberately quieter than the in-flight mark beside it: it answers "who may
   see this", which is a permission rather than an event, and an item that carries one is rare. */
.backlog-link {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-top: 2px;
  margin-left: 6px;
  color: var(--os-muted);
  font-size: 11px;
  line-height: 18px;
  white-space: nowrap;
}

/* WHERE THE WORK IS. Shaped like the in-flight mark beside it because it is the same kind of
   thing - a derived fact about this item, on the row - and coloured differently per reading,
   because four answers that look alike are one answer with extra words.

   THE MODIFIERS ARE ATTRIBUTE SELECTORS, NOT CLASSES. `data-landed` already carries the word for
   the template and for the tests, and a second copy of the vocabulary as four class names would be
   four more strings free to drift from it. */
.backlog-landed {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-top: 2px;
  margin-left: 6px;
  padding: 0 6px;
  border: 1px solid transparent;
  border-radius: 10px;
  font-size: 11px;
  line-height: 18px;
  white-space: nowrap;
}

/* IN THE PRODUCT. The only one that gets to look settled. */
.backlog-landed[data-landed='landed'] {
  border-color: color-mix(in srgb, var(--os-ok) 30%, transparent);
  background: var(--os-tint-ok);
  color: var(--os-ok);
}

/* OFF THE MACHINE BUT NOT IN THE PRODUCT. Safe from a disk failure, not finished. */
.backlog-landed[data-landed='pushed'] {
  border-color: color-mix(in srgb, var(--os-warn) 30%, transparent);
  background: var(--os-tint-warn);
  color: var(--os-warn);
}

/* ONE DISK. This is the near-miss the landing mark exists to expose, and it is allowed to look like one. */
.backlog-landed[data-landed='local'] {
  border-color: color-mix(in srgb, var(--q-negative) 30%, transparent);
  background: var(--os-tint-error);
  color: var(--q-negative);
}

/* IN REVIEW UPSTREAM: off the machine, waiting on somebody else. */
.backlog-landed[data-landed='in-review'] {
  border-color: color-mix(in srgb, var(--os-warn) 30%, transparent);
  background: var(--os-tint-warn);
  color: var(--os-warn);
}

/* DECLINED UPSTREAM: an answer, and a negative one - never landed. */
.backlog-landed[data-landed='declined'] {
  border-color: color-mix(in srgb, var(--q-negative) 30%, transparent);
  background: var(--os-tint-error);
  color: var(--q-negative);
}

/* NOBODY HAS SAID - AND THIS MUST NOT LOOK LIKE THE ONE ABOVE IT. A missing answer is not a
   negative one, so it is drawn quiet and DASHED rather than red: the dashes say "nothing here to
   read yet", where a colour would say "we checked, and it is bad". Same rule as `(unknown)` for
   spend, which renders rather than showing a zero nobody measured. */
.backlog-landed[data-landed='unknown'] {
  border-style: dashed;
  border-color: var(--os-rule-strong);
  background: transparent;
  color: var(--os-muted);
}

/* WHY DISPATCH IS DEAD, under the state in the same cell. Wraps rather than truncating - the
   sentence names the way out of the refusal and a clipped one would name only the refusal. */
.backlog-blocked {
  display: block;
  max-width: 11rem;
  color: var(--os-muted);
  font-size: 11px;
  line-height: 14px;
}

/* The review toggle sits in a row of dense controls and must not grow it. */
.backlog-review {
  font-size: 12px;
}

.backlog-index {
  width: 3rem;
  color: var(--os-muted);
}

.backlog-id {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.backlog-when {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.backlog-dispatch {
  padding: 2px 0;
}

.backlog-drag-reason {
  max-width: 28rem;
  text-align: right;
}

.backlog-toolbar {
  flex: 0 0 auto;
}

.backlog-table th {
  cursor: pointer;
  user-select: none;
}
</style>
