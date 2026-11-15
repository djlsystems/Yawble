<script setup lang="ts">
import { readOnly, vChange } from '../remoteAccess';
import { computed, ref } from 'vue';
import type { NetworkAnswer, NetworkAsk, NetworkAskChoice, YawbleApi } from '../../../shared/api';
import { errorText, isRemoteRefusal } from '../api';
import { hostsLabel } from '../networkAskGroups';
import { displayName } from '../faces/faces';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { discussWithConcierge, networkAskPrompt } from '../conciergeDiscuss';

/**
 * One network ask in Needs you: a host a member's run was refused (or asked for with request_host), with who asked, the
 * agent's reason or what was refused (method and a short path), how often, and what a package registry is for. The four
 * answers call network.answer, effective from the member's next run. `group`: one member's waiting asks with one
 * registrable domain, shown as one item ("crl3.digicert.com, ocsp.digicert.com (2 hosts)"); an answer answers every
 * listed host (ids). When the core offers a domain allow (domainPattern), "This host / All of *.digicert.com" picks what
 * an allow adds; the domain allow closes every waiting ask it covers at the chosen scope. Everything shown is
 * agent-influenced text: always plain text ({{ }}), never HTML. A remote view whose policy changes are refused says to
 * answer it in the window.
 */
const props = defineProps<{ ask: NetworkAsk; api: YawbleApi; group?: NetworkAsk[] }>();
const emit = defineEmits<{ answered: [answer: NetworkAnswer] }>();

const CHOICES: { choice: NetworkAskChoice; label: string; busy: string; title: string }[] = [
  { choice: 'agent', label: 'Allow for this agent', busy: 'Allowing…', title: 'Adds the host to this member\'s allowed hosts, from its next run' },
  { choice: 'team', label: 'Allow for this team', busy: 'Allowing…', title: 'Adds the host to every member of this team, from their next runs' },
  { choice: 'everywhere', label: 'Allow everywhere', busy: 'Allowing…', title: 'Adds the host to the global allowed hosts (Settings > Guardrails), for every agent' },
  { choice: 'deny', label: 'Deny', busy: 'Denying…', title: 'Closes it; the agent is told, and this host is not asked again for this member for 24 hours' },
];
const choices = computed(() => CHOICES.filter((c) => !props.ask.choices?.length || props.ask.choices.includes(c.choice)));

/** The asks this item answers: the group, or the one ask. */
const all = computed(() => (props.group?.length ? props.group : [props.ask]));
const hosts = computed(() => [...new Set(all.value.map((a) => a.host))]);
const hostLine = computed(() => hostsLabel(hosts.value));
/** The domain allow the core offers for the item ("*.digicert.com"), or null: then only the host is allowed. */
const domainPattern = computed(() => all.value.map((a) => a.domainPattern).find((p) => !!p) ?? null);
const target = ref<'host' | 'domain'>('host');
const reason = computed(() => all.value.find((a) => a.reason)?.reason ?? null);
const hint = computed(() => all.value.find((a) => a.hint)?.hint ?? null);
/** What was refused when the agent gave no reason: "CONNECT", "GET /v3/index.json". */
const refused = computed(() => [props.ask.method, props.ask.pathSample].filter(Boolean).join(' '));
/** A plain-http refusal (the core's plainHttp flag): the gateway forwards only https, so allowing the host does not make it work. */
const plainHttp = computed(() => all.value.some((a) => a.plainHttp === true));
const count = computed(() => all.value.reduce((n, a) => n + a.count, 0));
const times = computed(() => (count.value === 1 ? 'once' : `${count.value} times`));
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
      const r = await props.api.call<NetworkAnswer>('network.answer', {
        id: props.ask.id, choice,
        ...(all.value.length > 1 ? { ids: all.value.map((a) => a.id) } : {}),
        ...(choice !== 'deny' && target.value === 'domain' && domainPattern.value ? { domain: true } : {}),
        ...(choice === 'deny' && note.value.trim() ? { note: note.value.trim() } : {}),
      });
      emit('answered', r);
    } catch (e) {
      // Only the Cloud Link's own refusal means "answer it in the window"; any other failure is shown as it is. A
      // read-only viewer's refusal (Visitor) is shown as the core words it: answering in the window is not theirs to do.
      error.value = isRemoteRefusal(props.api, e) && !readOnly.value ?`This view cannot change the network policy: answer it in the window. (${errorText(e)})` : errorText(e);
    }
  }, choice);
}
</script>

<template>
  <article class="network-ask" data-test="network-ask" :data-id="ask.id">
    <p class="head">
      <strong data-test="network-ask-member">{{ displayName(ask.memberName) }}</strong>
      <span class="muted"> · {{ ask.team }}</span> asks to reach
      <code data-test="network-ask-host">{{ hostLine }}</code>
    </p>
    <p v-if="reason" class="small" data-test="network-ask-reason"><span class="muted">The agent's words:</span> {{ reason }}</p>
    <p v-else class="small" data-test="network-ask-refused"><span class="muted">Refused:</span> <code>{{ refused || 'a request' }}</code></p>
    <p v-if="hint" class="small hint" data-test="network-ask-hint">{{ hint }}</p>
    <p v-if="plainHttp" class="small http" data-test="network-ask-http">This was plain http: only https is forwarded, even once the host is allowed. The agent needs the https:// address.</p>
    <p class="small muted" data-test="network-ask-count">Refused {{ times }} since {{ since }}</p>
    <div v-if="domainPattern" class="target" role="radiogroup" aria-label="What an allow adds" data-test="network-ask-target">
      <label><input v-model="target" type="radio" value="host" data-test="network-ask-target-host" />
        {{ hosts.length === 1 ? 'This host' : `These ${hosts.length} hosts` }}</label>
      <label :title="`Every subdomain of ${domainPattern.slice(2)} (not ${domainPattern.slice(2)} itself, which is allowed as its host when it is listed)`">
        <input v-model="target" type="radio" value="domain" data-test="network-ask-target-domain" />
        All of <code>{{ domainPattern }}</code></label>
    </div>
    <input v-model="note" class="note" placeholder="A note for the agent if you deny (optional)" maxlength="500" data-test="network-ask-note" />
    <div class="buttons">
      <button v-for="(c, i) in choices" :key="c.choice" :class="{ primary: i === 0 }" :title="c.title" v-change :data-test="`network-ask-${c.choice}`"
        :disabled="busy.is('answer')" :aria-busy="busy.is('answer', c.choice)" @click="answer(c.choice)">
        <BusyLabel :busy="busy.is('answer', c.choice)" :text="c.busy">{{ c.label }}</BusyLabel>
      </button>
      <button class="discuss" data-test="network-ask-discuss" title="Talk it through with the Concierge first; you still answer it here"
        @click="discussWithConcierge(ask.teamId, networkAskPrompt(ask))">Discuss with the Concierge</button>
    </div>
    <p v-if="error" class="error small" data-test="network-ask-error">{{ error }}</p>
  </article>
</template>

<style scoped>
.network-ask { display: flex; flex-direction: column; gap: 4px; border: 1px solid var(--warn-edge-soft); border-left: 3px solid var(--warn-edge); border-radius: 10px; padding: 8px 10px; background: var(--surface); font-size: 13px; }
.network-ask p { margin: 0; overflow-wrap: anywhere; }
.head code { font-weight: 600; }
.hint { color: var(--ink-soft); }
.http { color: var(--warn-ink); }
.note { font: inherit; font-size: 12.5px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 8px; margin-top: 2px; }
.target { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 12.5px; margin-top: 2px; }
.target label { display: inline-flex; align-items: center; gap: 4px; }
.buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
.buttons button { font-size: 12.5px; }
.buttons .discuss { margin-left: auto; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
