<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { CompanionClientInfo, CompanionLevel, CompanionList, CompanionStatus, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { COMPANION_LEVELS, COMPANION_LEVEL_TEXT } from '../companionLevels';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * Settings > Companion: whether a program can use Yawble right now (one plain sentence), the steps that connect Claude Code
 * (level, pair, add), and the paired programs (revoke asks first). Only at this computer, never remote.
 */
const props = defineProps<{ api: YawbleApi }>();

const clients = ref<CompanionClientInfo[]>([]);
const status = ref<CompanionStatus | null>(null);
const error = ref<string | null>(null);
const revoking = ref<CompanionClientInfo | null>(null);
const copied = ref<string | null>(null); // which Copy button was last used
const level = ref<CompanionLevel>('act');
watch(level, () => { copied.value = null; }); // the command changed: nothing copied is current any more
const showSteps = ref(false); // the steps stay folded away once claude-code is paired, until asked for
const LEVEL_LABEL: Record<CompanionLevel, string> = { read: 'Read only', act: 'Read and act', destructive: 'Everything, with approval' };
let off: (() => void) | null = null;

const when = (t: string | null) => (t ? new Date(t).toLocaleString() : '—');

/** The companion's folder (control.json and clients\), named when something is wrong with it. */
const folder = computed(() => {
  const f = status.value?.controlFile;
  if (!f) return null;
  const i = Math.max(f.lastIndexOf('\\'), f.lastIndexOf('/'));
  return i > 0 ? f.slice(0, i) : f;
});

const active = computed(() => clients.value.filter(c => !c.revokedAt));
const claudePaired = computed(() => active.value.some(c => c.name === 'claude-code'));
const lastUsed = computed(() => {
  const times = active.value.map(c => c.lastUsedAt).filter((t): t is string => !!t);
  return times.length ? times.reduce((a, b) => (Date.parse(b) > Date.parse(a) ? b : a)) : null;
});
const state = computed<'off' | 'ready' | 'connected'>(() =>
  !status.value?.endpoint ? 'off' : active.value.length ? 'connected' : 'ready');
const isWindowsPath = (p: string) => /^[A-Za-z]:[\\/]|^\\\\/.test(p);
/** The pair command for a program name, as typed in PowerShell (& '…', single quotes keep $ and ` literal) and in cmd or a POSIX shell ("…"). */
function pairLines(name: string): { powershell: string | null; plain: string } | null {
  const bin = status.value?.binaryPath;
  if (!bin) return null;
  const tail = `pair --name ${name} --level ${level.value}`;
  const plain = `"${bin}" ${tail}`;
  return isWindowsPath(bin) ? { powershell: `& '${bin.replace(/'/g, "''")}' ${tail}`, plain } : { powershell: null, plain };
}
const pairCommand = computed(() => pairLines('claude-code'));
const pairOtherCommand = computed(() => pairLines('my-tool'));
const stepsShown = computed(() => !claudePaired.value || showSteps.value);

const loaded = ref(false); // the first answer arrived (or failed)
async function load() {
  try {
    clients.value = (await props.api.call<CompanionList>('companion.list', {})).clients ?? [];
    status.value = await props.api.call<CompanionStatus>('companion.status', {});
    error.value = null;
  } catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}

// The confirmation stays up (its Revoke shows Revoking…) until the core answers. One revoke at a time; when it ends it
// closes only its own confirmation (one escaped meanwhile and another opened stays open).
const revokingName = ref<string | null>(null);
async function revoke(c: CompanionClientInfo) {
  if (revokingName.value !== null) return;
  revokingName.value = c.name;
  try {
    try { await props.api.call('companion.revoke', { name: c.name }); } catch (e) { error.value = errorText(e); }
    await load();
  } finally {
    revokingName.value = null;
    if (revoking.value?.name === c.name) revoking.value = null;
  }
}

async function copy(key: string, text: string | null | undefined) {
  if (!text) return;
  try { await navigator.clipboard.writeText(text); copied.value = key; } catch { /* the text is on screen */ }
}

onMounted(() => {
  void load();
  off = props.api.onEvent((e) => { if (e.event === 'companion.changed') void load(); });
});
onBeforeUnmount(() => off?.());
</script>

<template>
  <section class="companion" data-test="settings-companion">
    <h3>Yawble companion</h3>
    <p class="muted">The <code>yawble-companion</code> program lets tools on this computer, such as Claude Code, see and work with your teams.
      Each program pairs once, with a code you check here; anything that can't be undone waits for your approval in this window.</p>
    <p v-if="error" class="error" data-test="companion-panel-error">{{ error }}</p>
    <LoadingNote v-if="!loaded" text="Loading the companion…" />

    <div v-if="status" class="endpoint" data-test="companion-endpoint" :data-state="status.endpoint ? 'on' : 'off'" :data-status="state">
      <div class="sentence" data-test="companion-status-text">
        <span class="dot" :class="state" aria-hidden="true"></span>
        <span v-if="state === 'off'">Not available: {{ status.problem || 'the companion is turned off for this run' }}</span>
        <span v-else-if="state === 'ready'">Ready — no program is connected yet. Follow the steps below to connect Claude Code.</span>
        <span v-else>Connected — {{ active.length }} {{ active.length === 1 ? 'program' : 'programs' }} can use Yawble: {{ active.map(c => c.name).join(', ') }}.<template v-if="lastUsed"> Last used {{ when(lastUsed) }}.</template></span>
      </div>
      <div v-if="folder" class="muted small">Companion folder: <code>{{ folder }}</code></div>
    </div>

    <div class="connect">
      <h4>Connect Claude Code</h4>
      <p v-if="!status && !error" class="muted small">The steps appear once the companion has answered.</p>
      <p v-else-if="status && (!status.binaryPath || !status.claudeCommand)" class="muted" data-test="companion-no-binary">This build of Yawble does not include yawble-companion (an installed copy does).</p>
      <template v-else-if="status && pairCommand">
        <p v-if="claudePaired" class="muted" data-test="companion-steps-done">
          Claude Code is paired.
          <button class="linklike" data-test="companion-steps-toggle" @click="showSteps = !showSteps">{{ showSteps ? 'Hide the steps' : 'Show the steps again' }}</button>
        </p>
        <ol v-if="stepsShown" class="steps" data-test="companion-steps">
          <li data-test="companion-step-level">
            <div class="head"><b>Choose what it may do</b></div>
            <select v-model="level" data-test="companion-level">
              <option v-for="l in COMPANION_LEVELS" :key="l" :value="l">{{ LEVEL_LABEL[l] }}</option>
            </select>
            <p class="muted small" data-test="companion-level-text">{{ COMPANION_LEVEL_TEXT[level] }}</p>
          </li>
          <li data-test="companion-step-pair" :data-done="claudePaired ? 'true' : 'false'">
            <div class="head"><b>Pair it</b> <span v-if="claudePaired" class="muted" data-test="companion-step-pair-done">✓ done</span></div>
            <div v-if="pairCommand.powershell" class="muted small">PowerShell</div>
            <div class="line">
              <code data-test="companion-pair-command">{{ pairCommand.powershell ?? pairCommand.plain }}</code>
              <button data-test="companion-copy-pair" @click="copy('pair', pairCommand!.powershell ?? pairCommand!.plain)">{{ copied === 'pair' ? 'Copied' : 'Copy' }}</button>
            </div>
            <template v-if="pairCommand.powershell">
              <div class="muted small">cmd</div>
              <div class="line">
                <code data-test="companion-pair-command-cmd">{{ pairCommand.plain }}</code>
                <button data-test="companion-copy-pair-cmd" @click="copy('pair-cmd', pairCommand!.plain)">{{ copied === 'pair-cmd' ? 'Copied' : 'Copy' }}</button>
              </div>
            </template>
            <p class="muted small">Run it in a terminal. A card with a code appears in this window; check the code matches the terminal and click Approve (within 5 minutes).</p>
          </li>
          <li data-test="companion-step-add">
            <div class="head"><b>Add it to Claude Code</b> <span class="muted">Run once</span></div>
            <div class="line">
              <code data-test="companion-claude-command">{{ status.claudeCommand }}</code>
              <button data-test="companion-copy" @click="copy('claude', status.claudeCommand)">{{ copied === 'claude' ? 'Copied' : 'Copy' }}</button>
            </div>
            <p class="muted small">Run it once in a terminal; then restart Claude Code.</p>
          </li>
        </ol>
      </template>
    </div>

    <h4>Paired programs</h4>
    <table v-if="clients.length" class="list" data-test="companion-clients">
      <thead><tr><th>Name</th><th>Level</th><th>Paired</th><th>By</th><th>Last used</th><th></th></tr></thead>
      <tbody>
        <tr v-for="c in clients" :key="c.name" data-test="companion-client" :data-revoked="c.revokedAt ? 'true' : 'false'" :class="{ revoked: c.revokedAt }">
          <td>{{ c.name }}</td>
          <td :title="COMPANION_LEVEL_TEXT[c.level]">{{ c.level }}</td>
          <td>{{ when(c.pairedAt) }}</td>
          <td>{{ c.pairedBy }}</td>
          <td>{{ when(c.lastUsedAt) }}</td>
          <td>
            <span v-if="c.revokedAt" class="muted">Revoked {{ when(c.revokedAt) }}</span>
            <button v-else class="danger" data-test="companion-revoke" @click="revoking = c">Revoke…</button>
          </td>
        </tr>
      </tbody>
    </table>
    <p v-else-if="loaded" class="muted" data-test="companion-no-clients">None yet.</p>
    <div v-if="pairOtherCommand" class="muted small" data-test="companion-pair-other">
      Pair another program: change <code>my-tool</code> to the program's name.
      <div v-if="pairOtherCommand.powershell">PowerShell: <code data-test="companion-pair-other-ps">{{ pairOtherCommand.powershell }}</code></div>
      <div><span v-if="pairOtherCommand.powershell">cmd: </span><code data-test="companion-pair-other-plain">{{ pairOtherCommand.plain }}</code></div>
    </div>

    <Modal v-if="revoking" title="Revoke access" @close="revoking = null">
      <div data-test="companion-revoke-confirm">
        <p><b>Revoke {{ revoking.name }}?</b> It can no longer reach Yawble, and its requests waiting for you are dropped.
          To use it again, pair it again.</p>
        <div class="buttons">
          <button class="danger" data-test="companion-revoke-yes" :disabled="revokingName !== null" :aria-busy="revokingName === revoking.name" @click="revoke(revoking)"><BusyLabel :busy="revokingName === revoking.name" text="Revoking…">Revoke</BusyLabel></button>
          <button data-test="companion-revoke-no" @click="revoking = null">Cancel</button>
        </div>
      </div>
    </Modal>
  </section>
</template>

<style scoped>
.companion h3 { margin: 0 0 8px; font-size: 15px; }
.companion h4 { margin: 18px 0 6px; font-size: 14px; }
.endpoint { padding: 8px 12px; border: 1px solid var(--line); border-radius: 6px; font-size: 13.5px; }
.sentence { display: flex; gap: 8px; align-items: baseline; }
.dot { width: 10px; height: 10px; border-radius: 50%; flex: none; background: var(--muted); }
.dot.off { background: var(--bad); }
.dot.ready { background: var(--warn); }
.dot.connected { background: var(--ok); }
.steps { padding-left: 20px; margin: 8px 0 0; }
.steps li { margin-bottom: 12px; }
.steps .head { margin-bottom: 4px; }
.linklike { background: none; border: none; padding: 0; color: var(--accent); cursor: pointer; text-decoration: underline; }
.line { display: flex; gap: 8px; align-items: flex-start; }
.line code { flex: 1; white-space: pre-wrap; word-break: break-all; background: var(--bg); padding: 6px 8px; border-radius: 4px; font-size: 12.5px; }
.list { border-collapse: collapse; width: 100%; font-size: 13px; }
.list th, .list td { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--line-soft); }
.revoked td { color: var(--muted); }
.buttons { display: flex; gap: 8px; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12.5px; margin-top: 4px; }
.error { color: var(--bad); }
</style>
