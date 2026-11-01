<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { clearConnectionProviderClient, listConnectionProviders } from '../api/client';
import type { Connection, ConnectionProvider, ConnectionProviderCleared } from '../api/types';
import { productTitle } from '../presentation/product';

/**
 * UNDO SET UP CLIENT for a built-in provider whose client a person saved: "Use <product>'s app" for
 * Microsoft while the instance has an app of its own (it signs in with that one again), "Remove this
 * app" otherwise (Google, or Microsoft on an instance with none - it then reads not set up). The Host
 * clears the client with its tenant row in one transaction; a refusal is said here and nothing changed.
 *
 * Before the click it names the connections that signed in through the saved app and so will need
 * Reconnect (from `connections`, when the opener has them); after it, the Host's own list. Shown on a
 * Providers tile, and beside the sentence a sign-in ends with when Microsoft does not know the saved
 * app's client ID - there the opener has no provider list, so it is read here (`provider` left out).
 */
const props = defineProps<{
  /** `microsoft` or `google`. */
  providerId: string;
  /** The provider as the opener read it; read here when left out. */
  provider?: ConnectionProvider | null | undefined;
  /** The connections the opener has, to name before the click which will need Reconnect. */
  connections?: Connection[] | undefined;
}>();

const emit = defineEmits<{
  /** The saved client is gone: the provider as it is now, and the connections to reconnect. */
  cleared: [answer: ConnectionProviderCleared];
}>();

const read = ref<ConnectionProvider | null>(null);
const busy = ref(false);
const problem = ref('');
const done = ref<ConnectionProviderCleared | null>(null);

const shown = computed(() => props.provider ?? read.value);

onMounted(async () => {
  if (props.provider !== undefined) return;
  try {
    read.value = (await listConnectionProviders()).find((candidate) => candidate.id === props.providerId) ?? null;
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  }
});

/** The instance's own app takes over: Microsoft, on an instance started with one. */
const usesInstance = computed(() => shown.value?.kind === 'microsoft' && shown.value.instanceClient === true);
const label = computed(() => (usesInstance.value ? `Use ${productTitle}'s app` : 'Remove this app'));
const offered = computed(() => shown.value?.clientSource === 'saved' && done.value === null);

/** Its sign-in connections: each signed in through the saved app. */
const affected = computed(() =>
  (props.connections ?? []).filter((connection) => connection.provider === props.providerId && (connection.kind ?? 'oauth') === 'oauth'),
);

const names = (list: { name: string }[]) => list.map((entry) => entry.name).join(', ');

const doneWords = computed(() => {
  const answer = done.value;
  if (!answer) return '';
  const now = answer.provider.clientSource === 'default'
    ? `${answer.provider.name} signs in with ${productTitle}'s app again.`
    : `${answer.provider.name}'s app was removed: it is not set up until a client is set up again.`;
  return answer.reconnect.length === 0
    ? now
    : `${now} ${answer.reconnect.length === 1 ? 'This connection' : 'These connections'} signed in through the saved app and need${answer.reconnect.length === 1 ? 's' : ''} Reconnect: ${names(answer.reconnect)}.`;
});

async function clear() {
  if (busy.value) return;
  busy.value = true;
  problem.value = '';
  try {
    const answer = await clearConnectionProviderClient(props.providerId);
    done.value = answer;
    emit('cleared', answer);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="clear-client-offer" data-clear-client>
    <template v-if="offered">
      <q-btn
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        :label="label"
        :loading="busy"
        data-clear-client-button
        @click="clear"
      />
      <span class="text-caption os-text-muted" data-clear-client-words>
        {{ usesInstance
          ? `Forgets the app you saved; ${shown?.name} signs in with ${productTitle}'s app again.`
          : `Forgets the app you saved; ${shown?.name} is not set up until you set one up again.` }}
        <template v-if="affected.length > 0">
          {{ affected.length === 1 ? 'This connection signed' : 'These connections signed' }} in through it and
          {{ affected.length === 1 ? 'needs' : 'need' }} Reconnect afterwards: {{ names(affected) }}.
        </template>
      </span>
    </template>
    <div v-if="done" class="text-caption text-positive" data-clear-client-done>{{ doneWords }}</div>
    <div v-if="problem" class="text-caption text-negative" data-clear-client-problem>{{ problem }}</div>
  </div>
</template>
