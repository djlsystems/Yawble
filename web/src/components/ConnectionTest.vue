<script setup lang="ts">
import { ref, watch } from 'vue';
import { testConnection } from '../api/client';
import type { Connection } from '../api/types';

/**
 * TEST CONNECTION: whether a connection works now, without hiring a member to find out. A worker
 * tries what the connection holds - a sign-in's refresh and a read of its profile, a mailbox's login,
 * the one harmless request an API key's preset declares, a username and password's URL - and answers
 * in one sentence. That answer is the connection's health, as the Connections list shows it: ok, or
 * Needs you with the sentence.
 *
 * Shown only where the Host says the connection has a test (`testable`): a website, a preset that
 * declares no request and a username and password with no URL get no button rather than a result
 * nobody measured. It tests what is SAVED; nothing typed and not yet saved is sent.
 */
const props = defineProps<{ connection: Connection }>();
const emit = defineEmits<{ tested: [connection: Connection] }>();

const busy = ref(false);
const result = ref<{ ok: boolean; sentence: string } | null>(null);
const problem = ref('');

watch(() => props.connection.id, () => {
  result.value = null;
  problem.value = '';
});

async function test() {
  if (busy.value) return;
  busy.value = true;
  result.value = null;
  problem.value = '';
  try {
    const answer = await testConnection(props.connection.id);
    result.value = { ok: answer.health === 'ok', sentence: answer.sentence };
    emit('tested', answer.connection);
  } catch (cause) {
    // Nothing was recorded: no worker ran it, or the connection is gone.
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div v-if="connection.testable" class="q-gutter-y-xs" data-connection-test>
    <q-btn
      outline
      no-caps
      icon="network_check"
      label="Test connection"
      data-connection-test-run
      :loading="busy"
      :disable="busy"
      @click="test"
    />
    <div v-if="result?.ok" class="text-positive" data-connection-test-result="ok">{{ result.sentence }}</div>
    <div v-else-if="result" class="text-negative" data-connection-test-result="needs-you">Needs you: {{ result.sentence }}</div>
    <div v-if="problem" class="text-negative" data-connection-test-problem>{{ problem }}</div>
  </div>
</template>
