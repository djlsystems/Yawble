<script setup lang="ts">
import { computed, ref } from 'vue';
import type { NetworkAnswer, NetworkAsk, NetworkAskChoice, YawbleApi } from '../../../shared/api';
import { errorText, isRemoteRefusal } from '../api';
import { displayName } from '../faces/faces';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * One network ask in Needs you: a host a member's run was refused (or asked for with request_host), with who asked, the
 * agent's reason or what was refused (method and a short path), how often, and what a package registry is for. The four
 * answers call network.answer; only the exact host is allowed, from the member's next run. Everything shown is
 * agent-influenced text: always plain text ({{ }}), never HTML. A remote view whose policy changes are refused says to
 * answer it in the window.
 */
const props = defineProps<{ ask: NetworkAsk; api: YawbleApi }>();
const emit = defineEmits<{ answered: [answer: NetworkAnswer] }>();

const CHOICES: { choice: NetworkAskChoice; label: string; busy: string; title: string }[] = [
  { choice: 'agent', label: 'Allow for this agent', busy: 'Allowing…', title: 'Adds the host to this member\'s allowed hosts, from its next run' },
  { choice: 'team', label: 'Allow for this team', busy: 'Allowing…', title: 'Adds the host to every member of this team, from their next runs' },
  { choice: 'everywhere', label: 'Allow everywhere', busy: 'Allowing…', title: 'Adds the host to the global allowed hosts (Settings > Guardrails), for every agent' },
  { choice: 'deny', label: 'Deny', busy: 'Denying…', title: 'Closes it; the agent is told, and this host is not asked again for this member for 24 hours' },
];
const choices = computed(() => CHOICES.filter((c) => !props.ask.choices?.length || props.ask.choices.includes(c.choice)));

/** What was refused when the agent gave no reason: "CONNECT", "GET /v3/index.json". */
const refused = computed(() => [props.ask.method, props.ask.pathSample].filter(Boolean).join(' '));
/**
 * A plain-http refusal: the gateway forwards only https (CONNECT), so allowing the host does not make it work.
 * Read from the ask's own flag when the core sends one (plainHttp); until then, a refused method other than CONNECT
 * is a plain-http request (the gateway records CONNECT for https; request_host asks have no method).
 */
const plainHttp = computed(() => {
  const flag = (props.ask as NetworkAsk & { plainHttp?: boolean | null }).plainHttp;
  if (typeof flag === 'boolean') return flag;
  return !!props.ask.method && props.ask.method.toUpperCase() !== 'CONNECT';
});
const times = computed(() => (props.ask.count === 1 ? 'once' : `${props.ask.count} times`));
const since = computed(() => {
  const d = new Date(props.ask.at);
  return Number.isNaN(d.getTime()) ? props.ask.at : d.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' });
});

const busy = useBusy();
const error = ref<string | null>(null);
const note = ref('');

async function answer(choice: NetworkAskChoice) {
  await busy.run('answer', async () => {
    error.value = null;
    try {
      const r = await props.api.call<NetworkAnswer>('network.answer', { id: props.ask.id, choice, ...(choice === 'deny' && note.value.trim() ? { note: note.value.trim() } : {}) });
      emit('answered', r);
    } catch (e) {
      // Only the Cloud Link's own refusal means "answer it in the window"; any other failure is shown as it is.
      error.value = isRemoteRefusal(props.api, e) ? `This view cannot change the network policy: answer it in the window. (${errorText(e)})` : errorText(e);
    }
  }, choice);
}
</script>

<template>
  <article class="network-ask" data-test="network-ask" :data-id="ask.id">
    <p class="head">
      <strong data-test="network-ask-member">{{ displayName(ask.memberName) }}</strong>
      <span class="muted"> · {{ ask.team }}</span> asks to reach
      <code data-test="network-ask-host">{{ ask.host }}</code>
    </p>
    <p v-if="ask.reason" class="small" data-test="network-ask-reason"><span class="muted">The agent's words:</span> {{ ask.reason }}</p>
    <p v-else class="small" data-test="network-ask-refused"><span class="muted">Refused:</span> <code>{{ refused || 'a request' }}</code></p>
    <p v-if="ask.hint" class="small hint" data-test="network-ask-hint">{{ ask.hint }}</p>
    <p v-if="plainHttp" class="small http" data-test="network-ask-http">This was plain http: only https is forwarded, even once the host is allowed. The agent needs the https:// address.</p>
    <p class="small muted" data-test="network-ask-count">Refused {{ times }} since {{ since }}</p>
    <input v-model="note" class="note" placeholder="A note for the agent if you deny (optional)" maxlength="500" data-test="network-ask-note" />
    <div class="buttons">
      <button v-for="(c, i) in choices" :key="c.choice" :class="{ primary: i === 0 }" :title="c.title" :data-test="`network-ask-${c.choice}`"
        :disabled="busy.is('answer')" :aria-busy="busy.is('answer', c.choice)" @click="answer(c.choice)">
        <BusyLabel :busy="busy.is('answer', c.choice)" :text="c.busy">{{ c.label }}</BusyLabel>
      </button>
    </div>
    <p v-if="error" class="error small" data-test="network-ask-error">{{ error }}</p>
  </article>
</template>

<style scoped>
.network-ask { display: flex; flex-direction: column; gap: 4px; border: 1px solid #f3c38b; border-left: 3px solid #b45309; border-radius: 10px; padding: 8px 10px; background: #fff; font-size: 13px; }
.network-ask p { margin: 0; overflow-wrap: anywhere; }
.head code { font-weight: 600; }
.hint { color: var(--ink-soft); }
.http { color: #92400e; }
.note { font: inherit; font-size: 12.5px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 8px; margin-top: 2px; }
.buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
.buttons button { font-size: 12.5px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
