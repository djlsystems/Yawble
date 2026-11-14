<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { ToolAsk, ToolInstallCode, ToolInstallProgress, ToolInstallResult, ToolNotNowResult, YawbleApi } from '../../../shared/api';
import { checkToolAgain } from '../../../shared/api';
import { errorText } from '../api';
import { displayName } from '../faces/faces';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * One tool ask in Needs you: a developer tool of the app's catalog a member asked for with request_tool, with who asked,
 * the agent's reason, the tool's name, publisher and winget id, whether it installs per-user or machine-wide, and the
 * exact command. Install (Try again after a failure, with the installer's message) calls tools.install, which returns
 * when winget (Windows) or pkexec (Linux: "Your system asks for your password") has finished; tools.installProgress says
 * what it is doing meanwhile. A closed password window (cancelled) or a refusal (notAuthorized) is shown in the core's
 * plain words and the item stays open with Install. Off Windows without Install the item shows the command to run
 * yourself and Check again (tools.checkAgain), which closes the item when the program is found. Install runs only from
 * the window on this computer: a remote view shows Check again and Not now and says to install it in the Yawble window.
 * Everything shown is agent-influenced text: always plain text ({{ }}), never HTML.
 */
const props = defineProps<{ ask: ToolAsk; api: YawbleApi }>();
const emit = defineEmits<{ installed: [r: ToolInstallResult]; answered: [r: ToolInstallResult | ToolNotNowResult] }>();

const name = computed(() => props.ask.displayName || props.ask.tool);
const choices = computed(() => props.ask.choices ?? []);
/** Install is offered here: the core can run winget or pkexec, and this is the window on this computer. */
const canInstall = computed(() => props.ask.canInstall && !props.api.remote && (!choices.value.length || choices.value.includes('install')));
/** Check again: the core offers it (Linux and macOS); it installs nothing, so a remote view may use it too. */
const canCheckAgain = computed(() => choices.value.includes('checkAgain'));
const pkexec = computed(() => props.ask.installer === 'pkexec');
/** The command for the person to run themselves when the app cannot install it here. */
const manualCommand = computed(() => (props.ask.canInstall ? null : props.ask.manualCommand || props.ask.command));
const failed = computed(() => props.ask.state === 'pending' && !!props.ask.error);
const times = computed(() => (props.ask.count === 1 ? 'once' : `${props.ask.count} times`));
const since = computed(() => {
  const d = new Date(props.ask.at);
  return Number.isNaN(d.getTime()) ? props.ask.at : d.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' });
});

const busy = useBusy();
const error = ref<string | null>(null);
/** The last install's message when it did not finish (another install running, the manual command off Windows). */
const message = ref<string | null>(null);
/** What winget is doing now (tools.installProgress for this ask). */
const progress = ref<ToolInstallProgress | null>(null);
/** The last install's or Check again's result code here (older cores send none). */
const code = ref<ToolInstallCode | null>(null);
const note = ref('');
const installing = computed(() => busy.is('answer', 'install') || props.ask.state === 'installing');
/** The password window was closed or the person was not authorized: nothing ran, so the button says Install again. */
const declined = computed(() => code.value === 'cancelled' || code.value === 'notAuthorized');
const shownMessage = computed(() => message.value || (failed.value ? props.ask.error : null));
const installLabel = computed(() => (failed.value && !declined.value ? 'Try again' : 'Install'));
const progressText = computed(() => progress.value?.message
  || (pkexec.value ? `Installing ${name.value}… ${props.ask.passwordPrompt || 'Your system asks for your password'}` : `Installing ${name.value} with winget…`));
/** The home-folder note, unless the message above already says it. */
const homeOnly = computed(() => (props.ask.homeOnly && props.ask.homeOnly !== shownMessage.value ? props.ask.homeOnly : null));

let off: (() => void) | null = null;
onMounted(() => {
  off = props.api.onEvent?.((e) => {
    if (e.event !== 'tools.installProgress') return;
    const p = e.data as ToolInstallProgress | null;
    if (p && p.id === props.ask.id) progress.value = p;
  }) ?? null;
});
onBeforeUnmount(() => off?.());

async function install() {
  await busy.run('answer', async () => {
    error.value = null; message.value = null; progress.value = null; code.value = null;
    try {
      const r = await props.api.call<ToolInstallResult>('tools.install', { id: props.ask.id });
      code.value = r.result ?? null;
      if (r.done) emit('installed', r);
      else message.value = r.message; // the failure is also kept on the ask (error) for Try again
      emit('answered', r);
    } catch (e) {
      error.value = props.api.remote ? `Install it in the Yawble window on the computer. (${errorText(e)})` : errorText(e);
    } finally { progress.value = null; }
  }, 'install');
}

async function checkAgain() {
  await busy.run('answer', async () => {
    error.value = null; message.value = null; code.value = null;
    try {
      const r = await checkToolAgain(props.api, props.ask.id);
      code.value = r.result ?? null;
      if (r.done) emit('installed', r); // the asks closed: the list drops this item
      else message.value = r.message;
      emit('answered', r);
    } catch (e) { error.value = errorText(e); }
  }, 'checkAgain');
}

async function notNow() {
  await busy.run('answer', async () => {
    error.value = null;
    try {
      const r = await props.api.call<ToolNotNowResult>('tools.notNow', { id: props.ask.id, ...(note.value.trim() ? { note: note.value.trim() } : {}) });
      emit('answered', r);
    } catch (e) { error.value = errorText(e); }
  }, 'notNow');
}
</script>

<template>
  <article class="tool-ask" data-test="tool-ask" :data-id="ask.id" :data-state="installing ? 'installing' : failed ? 'failed' : 'asking'">
    <p class="head">
      <strong data-test="tool-ask-member">{{ displayName(ask.memberName) }}</strong>
      <span class="muted"> · {{ ask.team }}</span> asks for the <b data-test="tool-ask-tool">{{ name }}</b>
    </p>
    <p v-if="ask.reason" class="small" data-test="tool-ask-reason"><span class="muted">The agent's words:</span> {{ ask.reason }}</p>
    <p class="small" data-test="tool-ask-details">
      <span v-if="ask.publisher">{{ ask.publisher }}</span>
      <span v-if="ask.wingetId"> · <code>{{ ask.wingetId }}</code></span>
      <span v-if="ask.scope === 'user'" data-test="tool-ask-scope"> · installs for you only</span>
      <span v-else-if="ask.scope === 'machine'" data-test="tool-ask-scope"> · installs for the whole computer: Windows may ask for permission</span>
    </p>
    <p class="small muted" data-test="tool-ask-count">Asked {{ times }} since {{ since }}</p>
    <p v-if="ask.canInstall && ask.command" class="small" data-test="tool-ask-command">
      <span class="muted">Install runs:</span> <code>{{ ask.command }}</code>
    </p>
    <p v-if="ask.canInstall && pkexec && ask.passwordPrompt" class="small hint" data-test="tool-ask-password">{{ ask.passwordPrompt }}</p>
    <p v-if="manualCommand" class="small" data-test="tool-ask-command">
      <span class="muted">{{ canCheckAgain ? 'Run this on the computer, then choose Check again:' : 'Run this on the computer, then tell the team to retry:' }}</span> <code>{{ manualCommand }}</code>
    </p>
    <p v-if="api.remote && ask.canInstall" class="small hint" data-test="tool-ask-remote">
      {{ canCheckAgain ? 'Install it in the Yawble window on the computer; here you can choose Check again or Not now.' : 'Install it in the Yawble window on the computer; here you can only say Not now.' }}
    </p>
    <p v-if="homeOnly" class="small hint" data-test="tool-ask-homeOnly">{{ homeOnly }}</p>

    <p v-if="installing" class="small progress" data-test="tool-ask-progress" :data-phase="progress?.phase || 'installing'" role="status">
      {{ progressText }}
    </p>
    <p v-else-if="shownMessage" class="small" :class="declined ? 'hint' : 'error'" data-test="tool-ask-failed" :data-result="code || undefined">{{ shownMessage }}</p>

    <input v-if="!installing" v-model="note" class="note" placeholder="A note for the agent if you say Not now (optional)" maxlength="500" data-test="tool-ask-note" />
    <div class="buttons">
      <button v-if="canInstall" class="primary" data-test="tool-ask-install" :disabled="busy.is('answer') || installing" :aria-busy="installing"
        :title="`Runs the command above on this computer, then looks for ${ask.tool} again`" @click="install">
        <BusyLabel :busy="installing" text="Installing…">{{ installLabel }}</BusyLabel>
      </button>
      <button v-if="canCheckAgain" :class="{ primary: !canInstall }" data-test="tool-ask-checkAgain" :disabled="busy.is('answer') || installing" :aria-busy="busy.is('answer', 'checkAgain')"
        :title="`Looks for ${ask.tool} again where the team's runs look for it; installs nothing`" @click="checkAgain">
        <BusyLabel :busy="busy.is('answer', 'checkAgain')" text="Checking…">Check again</BusyLabel>
      </button>
      <button data-test="tool-ask-notNow" :disabled="busy.is('answer') || installing" :aria-busy="busy.is('answer', 'notNow')"
        title="Closes it; the agent is told, and the team is not asked about this tool again for 24 hours" @click="notNow">
        <BusyLabel :busy="busy.is('answer', 'notNow')" text="Closing…">Not now</BusyLabel>
      </button>
    </div>
    <p v-if="error" class="error small" data-test="tool-ask-error">{{ error }}</p>
  </article>
</template>

<style scoped>
.tool-ask { display: flex; flex-direction: column; gap: 4px; border: 1px solid #f3c38b; border-left: 3px solid #b45309; border-radius: 10px; padding: 8px 10px; background: #fff; font-size: 13px; }
.tool-ask p { margin: 0; overflow-wrap: anywhere; }
.hint, .progress { color: var(--ink-soft); }
.note { font: inherit; font-size: 12.5px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 8px; margin-top: 2px; }
.buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
.buttons button { font-size: 12.5px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
