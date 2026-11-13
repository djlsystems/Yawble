<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { AGENT_TEST_CHANGED } from '../../../shared/api';
import type { AgentProfileRow, AgentTestSession, AgentTestStarted, AgentVersionStatus, DetectedCli, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { cliLabel } from '../agentClis';
import { freeCopyName, type AgentDraft } from '../agentArgs';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import AgentTile from './AgentTile.vue';
import AgentCommandLineDialog from './AgentCommandLineDialog.vue';
import AgentModelDialog from './AgentModelDialog.vue';
import AgentEditDialog from './AgentEditDialog.vue';
import { showChannelTerminal } from '../terminalPane';

// Settings > Agents: ONE list of agent tiles. Each built-in CLI as detected live on this computer (agents.detect, no
// cache; not found: only the install instructions, the app installs nothing), each of the person's own agents right
// after the built-in it is based on. Every action opens a dialog: Command line, Model, Update (the CLI's own update
// command), Duplicate / Edit (the agent editor), Delete. Terminal opens the agent's TUI (built-in or custom, its own
// argv and model) in a sandbox (global policy) in the terminal pane with no prompt; Test does the same with the
// diagnostic prompt. One session per base CLI. A cloud viewer sees detection only (agentProfiles.* and agents.test are
// not offered over the Cloud Link).
const props = defineProps<{ api?: YawbleApi; inline?: boolean }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const clis = ref<DetectedCli[] | null>(null);
const profiles = ref<AgentProfileRow[]>([]);
const versions = ref<Record<string, AgentVersionStatus>>({});
/** The tiles' version status / default model is still being fetched (agentProfiles.list, then agents.versionStatus). */
const agentsLoading = ref(!api.remote);
const error = ref<string | null>(null);
const notice = ref<string | null>(null);
const detecting = ref(false);
const testing = ref<{ cliId: string; channel: string; runId?: string } | null>(null);
// The tile whose Terminal / Test is starting (stop of the old session, detection, sandbox): its button spins.
const starting = ref<{ key: string; kind: 'terminal' | 'test' } | null>(null);
// The CLI whose Update is opening its update terminal, and whether "Stop it" is waiting for the core.
const updating = ref<string | null>(null);
const stoppingTest = ref(false);

const names: Record<string, string> = { claude: 'Claude Code', grok: 'Grok Build (xAI)', codex: 'Codex (OpenAI)', copilot: 'GitHub Copilot CLI', antigravity: 'Antigravity (Google)' };
const titleOf = (cliId: string) => names[cliId] ?? cliLabel(cliId);

interface Tile { key: string; dataTest: string; title: string; cliId: string; custom: boolean; detected: DetectedCli | null; profile: AgentProfileRow }

const builtInRow = (cliId: string): AgentProfileRow =>
  profiles.value.find((p) => !p.custom && p.name === cliId)
  ?? { id: 0, name: cliId, baseCli: cliId, custom: false, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] };

const tiles = computed<Tile[]>(() => {
  const ids = (clis.value ?? []).map((c) => c.cliId);
  for (const p of profiles.value) if (!p.custom && !ids.includes(p.name)) ids.push(p.name);
  const customs = profiles.value.filter((p) => p.custom);
  for (const c of customs) if (!ids.includes(c.baseCli)) ids.push(c.baseCli);
  const list: Tile[] = [];
  for (const id of ids) {
    const detected = clis.value?.find((c) => c.cliId === id) ?? null;
    if (profiles.value.some((p) => !p.custom && p.name === id) || detected)
      list.push({ key: id, dataTest: `agent-cli-${id}`, title: titleOf(id), cliId: id, custom: false, detected, profile: builtInRow(id) });
    for (const c of customs.filter((x) => x.baseCli === id))
      list.push({ key: `custom:${c.id}`, dataTest: `agent-custom-${c.name}`, title: c.name, cliId: id, custom: true, detected, profile: c });
  }
  return list;
});

/** Detects the CLIs again. quiet: in the background (after Install, or back in the window), without the busy label. */
async function detect(quiet = false) {
  if (!quiet) detecting.value = true;
  if (!quiet) error.value = null;
  try {
    clis.value = await api.call<DetectedCli[]>('agents.detect');
  } catch (e) {
    if (!quiet) error.value = errorText(e);
  } finally {
    if (!quiet) detecting.value = false;
  }
}

// After Install: detect again every few seconds until that CLI is found signed in (or 20 minutes pass), so its card
// changes by itself when the install and the sign-in in the terminal are done.
let watching: ReturnType<typeof setInterval> | undefined;
function watchFor(cliId: string) {
  clearInterval(watching);
  const until = Date.now() + 20 * 60_000;
  watching = setInterval(async () => {
    if (Date.now() > until) { clearInterval(watching); return; }
    await detect(true);
    const c = clis.value?.find((d) => d.cliId === cliId);
    if (c?.found && c.signIn?.status === 'signed-in') {
      clearInterval(watching);
      notice.value = `${titleOf(cliId)} is installed and signed in.`;
    } else if (c?.found && notice.value?.startsWith('A terminal opened')) {
      notice.value = `${titleOf(cliId)} is installed. Sign in to it in the terminal, as its card says.`;
    }
  }, 4000);
}

// Back in the window (from a terminal where a CLI was installed or signed in to): detect again.
function onFocus() { void detect(true); }

async function loadAgents() {
  if (api.remote) return;
  try {
    profiles.value = await api.call<AgentProfileRow[]>('agentProfiles.list');
  } catch (e) {
    error.value = errorText(e);
    agentsLoading.value = false;
    return;
  }
  const builtIns = profiles.value.filter((p) => !p.custom).map((p) => p.name);
  const found = await Promise.all(builtIns.map((cli) => api.call<AgentVersionStatus>('agents.versionStatus', { cli }).catch(() => null)));
  const next: Record<string, AgentVersionStatus> = {};
  builtIns.forEach((cli, i) => { if (found[i]) next[cli] = found[i]!; });
  versions.value = next;
  agentsLoading.value = false;
}

// The live Terminal / Test sessions (agents.testSessions, window only), refreshed on agents.testChanged: a session the
// person hid (the pane closed with Ctrl+`, or replaced by another terminal) stays visible on its tile, to Open or Stop.
const sessions = ref<AgentTestSession[]>([]);
let sessionsAsked = 0;
async function loadSessions() {
  if (api.remote) return;
  const asked = ++sessionsAsked; // a reply to an older ask (answered after a newer one) is dropped
  try {
    const list = await api.call<AgentTestSession[]>('agents.testSessions');
    if (asked === sessionsAsked) sessions.value = list;
  } catch { /* an older core: no chip */ }
}
/** agents.testChanged: refresh the chips; a session that ended on its own also clears the "runs in the pane" note. */
function onChanged(data: unknown) {
  const d = (data ?? {}) as { cli?: string; runId?: string; live?: boolean };
  const t = testing.value;
  if (d.live === false && t && d.cli === t.cliId && (!t.runId || !d.runId || d.runId === t.runId)) testing.value = null;
  void loadSessions();
}
let offChanged: (() => void) | null = null;

/** The tile's running session: any on its CLI for the built-in tile; on a custom tile, the one started as that agent. */
function runningFor(t: Tile): AgentTestSession | null {
  return sessions.value.find((s) => s.cli === t.cliId && (!t.custom || s.agent === t.profile.name)) ?? null;
}

/** Stops the CLI's session; with runId, only that run (an old pane cannot stop a newer session of the same CLI). */
const stopper = (cliId: string, runId?: string) => () =>
  api.call('agents.stopTest', runId ? { cli: cliId, runId } : { cli: cliId }).then(() => undefined).catch((e) => { error.value = errorText(e); });

const paneTitle = (diagnostic: boolean, title: string) => `${diagnostic ? 'Test' : 'Terminal'} · ${title} · global policy`;

/** Open on a tile's Running chip: the pane shows that session again, its Stop (and ✕) ending it. */
function openSession(s: AgentTestSession) {
  const title = s.agent === s.cli ? titleOf(s.cli) : s.agent;
  showChannelTerminal(`agent-test:${s.cli}`, paneTitle(s.kind === 'test', title), stopper(s.cli, s.runId));
}

async function stopSession(s: AgentTestSession) {
  error.value = null;
  await stopper(s.cli, s.runId)();
  if (testing.value?.cliId === s.cli) testing.value = null;
}

/** Terminal (diagnostic false: the CLI's own startup, no prompt) or Test (diagnostic true) for a tile's agent. */
async function session(t: Tile, diagnostic: boolean) {
  const cliId = t.cliId;
  if (starting.value) return; // one start at a time
  error.value = null;
  starting.value = { key: t.key, kind: diagnostic ? 'test' : 'terminal' };
  // One session per CLI: one already running for this CLI is stopped first. Another CLI's keeps running.
  if (testing.value?.cliId === cliId) await stop();
  else if (sessions.value.some((s) => s.cli === cliId)) await stopper(cliId)();
  try {
    const params: Record<string, unknown> = { cli: cliId, diagnostic };
    if (t.custom) params.agent = t.profile.name;
    const started = await api.call<AgentTestStarted>('agents.test', params);
    testing.value = { cliId, channel: started.channel, runId: started.runId };
    // Shown in the window's terminal pane, like every other terminal; its Stop (and ✕) ends this session.
    showChannelTerminal(started.channel, paneTitle(diagnostic, t.title), stopper(cliId, started.runId));
  } catch (e) {
    error.value = errorText(e);
  } finally {
    starting.value = null;
  }
}

async function stop() {
  if (!testing.value || stoppingTest.value) return;
  stoppingTest.value = true;
  try {
    await api.call('agents.stopTest', { cli: testing.value.cliId });
    testing.value = null;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    stoppingTest.value = false;
  }
}

async function checkUpdates(cliId: string) {
  if (updating.value) return;
  notice.value = null;
  error.value = null;
  updating.value = cliId;
  try {
    await api.call('agents.checkForUpdates', { cli: cliId });
    notice.value = `A terminal opened with ${titleOf(cliId)}'s own update command.`;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    updating.value = null;
  }
}

/** Install: a terminal runs the maker's own install command; the person signs in, then presses Detect again. */
async function install(cliId: string) {
  if (updating.value) return;
  notice.value = null;
  error.value = null;
  updating.value = cliId;
  try {
    await api.call('agents.install', { cli: cliId });
    notice.value = `A terminal opened with ${titleOf(cliId)}'s install command. When it finishes, sign in as its card says; this list updates by itself.`;
    watchFor(cliId);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    updating.value = null;
  }
}

// ------------------------------------------------------------------ dialogs
type Dialog =
  | { kind: 'cmd'; tile: Tile }
  | { kind: 'model'; tile: Tile }
  | { kind: 'edit'; mode: 'new' | 'edit'; baseCli: string; initial: AgentDraft }
  | { kind: 'delete'; profile: AgentProfileRow; error: string | null; inUse: boolean; busy: boolean };
const dialog = ref<Dialog | null>(null);
// The editor made an agent (agentProfiles.duplicate) even if its save then failed: closing it reloads the list, so the
// tiles show it and the next Duplicate picks a name that is still free.
let created = false;

async function closeDialog() {
  dialog.value = null;
  if (created) { created = false; await loadAgents(); }
}

/** The core's refusal of a delete because members still run on the agent (AgentProfileStore.Delete: "<name> is in use by …").
 *  Only the message crosses the window's bridge, so its shape is what is checked. */
const isInUse = (message: string) => / is in use by /.test(message);

function duplicate(t: Tile) {
  const src = t.profile;
  const name = freeCopyName(t.custom ? src.name : t.title, profiles.value.map((p) => p.name));
  const initial: AgentDraft = t.custom
    ? { name, defaultModel: src.defaultModel, argsBefore: [...src.argsBefore], argsAfter: [...src.argsAfter], removedArgs: [...src.removedArgs] }
    : { name, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] };
  dialog.value = { kind: 'edit', mode: 'new', baseCli: t.cliId, initial };
}

function edit(t: Tile) {
  const p = t.profile;
  dialog.value = { kind: 'edit', mode: 'edit', baseCli: p.baseCli,
    initial: { id: p.id, name: p.name, defaultModel: p.defaultModel, argsBefore: [...p.argsBefore], argsAfter: [...p.argsAfter], removedArgs: [...p.removedArgs] } };
}

async function saved(message: string) {
  dialog.value = null;
  created = false;
  notice.value = message;
  error.value = null;
  await loadAgents();
}

// Which delete button was clicked (Delete, or Move … and delete): that one shows the spinner.
const deletingMove = ref(false);
async function remove(moveToBase: boolean) {
  const d = dialog.value;
  if (d?.kind !== 'delete' || d.busy) return;
  d.busy = true;
  deletingMove.value = moveToBase;
  try {
    await api.call('agentProfiles.delete', { id: d.profile.id, moveToBase });
    await saved(`${d.profile.name} deleted.`);
  } catch (e) {
    // In use: the core names the members; offer to move them to the base agent. Any other error is only shown.
    d.error = errorText(e);
    d.inUse = isInUse(d.error);
    d.busy = false;
  }
}

onMounted(() => {
  void detect();
  void loadAgents();
  window.addEventListener('focus', onFocus);
  if (!api.remote) {
    offChanged = api.onEvent?.((e) => { if (e.event === AGENT_TEST_CHANGED) onChanged(e.data); }) ?? null;
    void loadSessions();
  }
});
onBeforeUnmount(() => { offChanged?.(); offChanged = null; clearInterval(watching); window.removeEventListener('focus', onFocus); });
</script>

<template>
  <Modal :inline="inline" title="Agents" wide @close="emit('close')">
    <p class="muted small">
      The app runs the agent CLIs installed on this computer, from where they are installed, inside the sandbox. Install and
      Update open a terminal that runs the CLI's own install or update command; you sign in to each CLI there, your own way.
    </p>
    <div class="row bar">
      <button data-test="agents-redetect" :disabled="detecting" :aria-busy="detecting" @click="detect()"><BusyLabel :busy="detecting" text="Detecting…">Detect again</BusyLabel></button>
    </div>
    <p v-if="error" class="error" data-test="agents-error">{{ error }}</p>
    <p v-if="notice" class="ok small" data-test="agents-notice">{{ notice }}</p>
    <p v-if="testing" class="muted small" data-test="agents-test-note">
      The session runs in the terminal pane on the right (View → Terminal).
      <button class="link" data-test="agents-test-stop" :disabled="stoppingTest" :aria-busy="stoppingTest" @click="stop"><BusyLabel :busy="stoppingTest" text="Stopping…">Stop it</BusyLabel></button>
    </p>

    <!-- Driven by detect alone: another call failing (e.g. agentProfiles.list) does not hide it while detect is still running. -->
    <LoadingNote v-if="!clis && detecting" text="Loading agents (looking for the agent CLIs on this computer)…" />
    <div class="tiles" data-test="agent-tiles">
      <AgentTile v-for="t in tiles" :key="t.key" :data-test="t.dataTest" :title="t.title" :cli-id="t.cliId" :custom="t.custom"
        :base-title="titleOf(t.cliId)" :detected="t.detected" :version="versions[t.cliId] ?? null" :default-model="t.profile.defaultModel"
        :remote="!!api.remote" :loading="agentsLoading" :running="runningFor(t)" :starting="starting?.key === t.key ? starting.kind : null" :updating="updating === t.cliId"
        @open="openSession(runningFor(t)!)" @stop="stopSession(runningFor(t)!)"
        @terminal="session(t, false)" @test="session(t, true)" @command-line="dialog = { kind: 'cmd', tile: t }" @model="dialog = { kind: 'model', tile: t }"
        @update="checkUpdates(t.cliId)" @install="install(t.cliId)" @duplicate="duplicate(t)" @edit="edit(t)"
        @remove="dialog = { kind: 'delete', profile: t.profile, error: null, inUse: false, busy: false }" />
    </div>
    <p v-if="api.remote" class="muted small" data-test="agents-remote-note">The command lines, models and your own agents are set on the computer itself.</p>

    <AgentCommandLineDialog v-if="dialog?.kind === 'cmd'" :api="api" :cli="dialog.tile.custom ? dialog.tile.profile.name : dialog.tile.cliId"
      :title="dialog.tile.title" :detected="dialog.tile.detected" @close="dialog = null" />
    <AgentModelDialog v-if="dialog?.kind === 'model'" :api="api" :agent="dialog.tile.profile" :title="dialog.tile.title"
      @close="dialog = null" @saved="saved" />
    <AgentEditDialog v-if="dialog?.kind === 'edit'" :api="api" :mode="dialog.mode" :base-cli="dialog.baseCli" :base-title="titleOf(dialog.baseCli)"
      :initial="dialog.initial" @close="closeDialog" @saved="saved" @created="created = true" />
    <Modal v-if="dialog?.kind === 'delete'" :title="`Delete ${dialog.profile.name}`" @close="dialog = null">
      <div data-test="delete-dialog">
        <template v-if="!dialog.inUse">
          <p>Delete your agent <b>{{ dialog.profile.name }}</b>? This cannot be undone.</p>
          <p v-if="dialog.error" class="error" data-test="delete-error">{{ dialog.error }}</p>
          <div class="row"><span class="grow"></span>
            <button data-test="delete-cancel" :disabled="dialog.busy" @click="dialog = null">Cancel</button>
            <button class="primary" data-test="delete-confirm" :disabled="dialog.busy" :aria-busy="dialog.busy" @click="remove(false)"><BusyLabel :busy="dialog.busy && !deletingMove" text="Deleting…">Delete</BusyLabel></button>
          </div>
        </template>
        <div v-else data-test="delete-in-use">
          <p class="error">{{ dialog.error }}</p>
          <div class="row"><span class="grow"></span>
            <button data-test="delete-cancel" :disabled="dialog.busy" @click="dialog = null">Keep it</button>
            <button class="primary" data-test="delete-move" :disabled="dialog.busy" :aria-busy="dialog.busy" @click="remove(true)"><BusyLabel :busy="dialog.busy && deletingMove" text="Moving and deleting…">Move those members to {{ titleOf(dialog.profile.baseCli) }} and delete</BusyLabel></button>
          </div>
        </div>
      </div>
    </Modal>
  </Modal>
</template>

<style scoped>
.bar { gap: 12px; margin-bottom: 8px; }
.tiles { display: flex; flex-direction: column; gap: 6px; }
.small { font-size: 12px; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; }
</style>
