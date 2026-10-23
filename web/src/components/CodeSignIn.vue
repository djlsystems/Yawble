<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { getConnectionFlow } from '../api/client';
import type { ConnectionDeviceStart, ConnectionFlow } from '../api/types';
import { copyText } from '../lib/clipboard';
import { countdown, refusalWords } from '../lib/connections';

/**
 * A SIGN-IN WITH A CODE, ONCE THE HOST HAS GIVEN THE CODE: the code (large and copyable), the link
 * to enter it at (in a new tab), a countdown to its expiry, and the flow read until it ends. Add
 * connection shows it for Microsoft, and so does a slot's Reconnect on a Microsoft connection
 * (ConnectionPicker): one component, so both say the same thing in the same words.
 *
 * It reads `GET /api/connections/flows/{flowId}` every few seconds while the flow waits; the Host does
 * the waiting at the provider. A SECOND CODE - Microsoft finishing a sign-in that names more than one
 * API - is the same flow waiting on a new code: it is shown in place of the first, numbered, with the
 * first said done. When the flow ends, `ended` carries the read; a refused or expired one is said
 * here in the Host's sentence (`refusalWords`). A read that fails is no ending: `problem` carries
 * its sentence, and an empty one once a read succeeds again.
 *
 * A new `code` (Try again) starts over; `reading` false stops the reads (its opener closed). The
 * provider's device code never reaches the browser: only the code the person types does.
 */
const props = withDefaults(defineProps<{
  code: ConnectionDeviceStart;
  /** The provider's name: "Microsoft approved the permissions". */
  providerName: string;
  /** The sign-in takes two codes, said before they were asked for: the first is numbered. */
  twoCodes?: boolean | undefined;
  /** A waiting sign-in picked back up rather than just started: said so, and read at once. */
  resumed?: boolean | undefined;
  /** False once the opener stops showing it: the reads stop. */
  reading?: boolean | undefined;
}>(), { reading: true });

const emit = defineEmits<{
  /** The flow read done, refused or expired. */
  ended: [read: ConnectionFlow];
  /** A read or a copy failed, in a sentence; empty once a read succeeds again. */
  problem: [text: string];
}>();

/** How often the flow is read while it waits. The Host does the waiting at the provider. */
const flowReadEvery = 3000;

const shown = ref<ConnectionDeviceStart>(props.code);
const read = ref<ConnectionFlow | null>(null);
/** The Host's sentence when the code shown was replaced by a second one while waiting. */
const notice = ref('');
/** Which of the two codes is shown: 1 or 2, or null for a single code or a sign-in picked back up. */
const codeNumber = ref<1 | 2 | null>(null);
const now = ref(Date.now());
let readTimer: ReturnType<typeof setTimeout> | null = null;
let clock: ReturnType<typeof setInterval> | null = null;

const state = computed(() => read.value?.state ?? 'waiting');
const sentence = computed(() => refusalWords(read.value?.sentence));
const left = computed(() => countdown(shown.value.expiresAt, now.value));
const waitingWords = computed(() =>
  codeNumber.value === 1 ? 'Waiting for the first code…' : codeNumber.value === 2 ? 'Waiting for the second code…' : 'Waiting for you to sign in…');

defineExpose({ state });

function stop() {
  if (readTimer !== null) clearTimeout(readTimer);
  if (clock !== null) clearInterval(clock);
  readTimer = null;
  clock = null;
}

onBeforeUnmount(stop);

function start() {
  stop();
  shown.value = props.code;
  read.value = null;
  notice.value = '';
  codeNumber.value = props.twoCodes && !props.resumed ? 1 : null;
  now.value = Date.now();
  clock = setInterval(() => (now.value = Date.now()), 1000);
  // A sign-in picked back up is read at once: the flow may have moved on while it was not shown.
  readTimer = setTimeout(readFlow, props.resumed ? 0 : flowReadEvery);
}

watch(() => props.code, start, { immediate: true });
watch(
  () => props.reading,
  (reading) => {
    if (!reading) stop();
  },
);

/** Still the code shown, and still shown: a Try again, a second code or a close since makes it stale. */
const active = (flow: ConnectionDeviceStart) => shown.value === flow && props.reading;

async function readFlow() {
  const flow = shown.value;
  readTimer = null;
  if (!active(flow)) return;

  let answer: ConnectionFlow;
  try {
    answer = await getConnectionFlow(flow.flowId);
  } catch (cause) {
    // A read that failed is not an ending: say so and read again.
    emit('problem', cause instanceof Error ? cause.message : String(cause));
    if (active(flow)) readTimer = setTimeout(readFlow, flowReadEvery);
    return;
  }
  // Try again or a close while the read was out: this answer is for a flow no longer shown.
  if (!active(flow)) return;

  emit('problem', '');
  read.value = answer;
  if (answer.state === 'waiting') {
    // A SECOND CODE: the same flow, waiting on the code the Host now holds, with its sentence.
    if (answer.userCode && answer.verificationUri && answer.expiresAt && answer.userCode !== flow.userCode) {
      shown.value = { flowId: flow.flowId, userCode: answer.userCode, verificationUri: answer.verificationUri, expiresAt: answer.expiresAt };
      notice.value = answer.sentence;
      codeNumber.value = 2;
    }
    readTimer = setTimeout(readFlow, flowReadEvery);
    return;
  }

  stop();
  notice.value = '';
  emit('ended', answer);
}

async function copy(value: string) {
  try {
    await copyText(value);
  } catch {
    emit('problem', 'Could not copy to the clipboard. Select the value and copy it by hand.');
  }
}
</script>

<template>
  <div class="code-sign-in" :data-device-state="state">
    <template v-if="state === 'waiting'">
      <div v-if="resumed" class="q-mb-sm" data-device-resumed>
        This is the {{ providerName }} sign-in you started earlier, still waiting for you.
        Back lets it go and starts again.
      </div>
      <div v-if="codeNumber === 1" class="text-weight-medium" data-code-number="1">Code 1 of 2</div>
      <template v-else-if="codeNumber === 2">
        <div class="row items-center q-gutter-xs text-positive" data-first-code-done>
          <q-icon name="check_circle" size="18px" />
          <span>First code done: {{ providerName }} approved the permissions.</span>
        </div>
        <div class="text-weight-medium" data-code-number="2">Code 2 of 2: one more code to finish</div>
      </template>
      <div v-else-if="notice" class="q-mb-sm" data-device-notice>{{ notice }}</div>
      <div>
        Open
        <a :href="shown.verificationUri" target="_blank" rel="noopener noreferrer" data-device-link>{{ shown.verificationUri }}</a>
        and enter this code:
      </div>
      <div class="row items-center q-gutter-sm q-my-sm">
        <span class="mono connect-device-code" data-device-code>{{ shown.userCode }}</span>
        <q-btn flat round dense icon="content_copy" aria-label="Copy the code" @click="copy(shown.userCode)" />
      </div>
      <div class="text-caption os-text-muted" data-device-countdown>
        The code expires in {{ left }}. Sign in there and agree; this step moves on by itself.
      </div>
      <div class="row items-center q-gutter-sm q-mt-sm os-text-muted">
        <q-spinner size="16px" />
        <span>{{ waitingWords }}</span>
      </div>
    </template>
    <div v-else-if="state === 'done'" data-device-sentence>{{ sentence }}</div>
    <div v-else class="text-negative" data-device-sentence>{{ sentence }}</div>
  </div>
</template>

<style scoped>
.connect-device-code {
  font-size: 2rem;
  font-weight: 600;
  letter-spacing: 0.1em;
  user-select: all;
}
</style>
