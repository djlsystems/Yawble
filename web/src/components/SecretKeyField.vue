<script setup lang="ts">
import { computed, ref } from 'vue';
import { ActionRefused, clearSecretKey, setSecretKey } from '../api/client';
import type { SecretKeyState } from '../api/types';

/**
 * ONE SECRET KEY'S VALUE, WRITE-ONLY. A person types a key's value here and presses Set (Replace
 * once it is set); the Host stores it and the next run of a member bound to the key gets it, with no
 * restart. Clear removes the key set here, and a key in the Host's environment then holds again.
 *
 * NOTHING IS EVER READ BACK. The Host never sends a value, and the box is emptied the moment a write
 * is sent, so a value typed here is held only until Set or Replace is pressed. What the field knows
 * of a key is `state` - set, by whom and when, and from where - which the parent shows; after a write
 * the Host's answer is emitted as `changed`, so the parent's state refreshes.
 */
const props = defineProps<{
  state: SecretKeyState;
}>();

const emit = defineEmits<{ changed: [state: SecretKeyState] }>();

const typed = ref('');
const busy = ref<'set' | 'clear' | null>(null);
const problem = ref('');

const replacing = computed(() => props.state.set);
/** Only a key set here is cleared here: one in the Host's environment is the operator's. */
const clearable = computed(() => props.state.source === 'stored');

function refusal(cause: unknown): string {
  if (cause instanceof ActionRefused && typeof cause.body.refusal === 'string') return cause.body.refusal;
  return cause instanceof Error ? cause.message : String(cause);
}

async function write(action: 'set' | 'clear') {
  const key = props.state.key;
  const value = typed.value;
  if (action === 'set' && value === '') return;

  typed.value = '';
  problem.value = '';
  busy.value = action;
  try {
    emit('changed', action === 'set' ? await setSecretKey(key, value) : await clearSecretKey(key));
  } catch (cause) {
    problem.value = `${key} was not ${action === 'set' ? 'set' : 'cleared'}: ${refusal(cause)}`;
  } finally {
    busy.value = null;
  }
}
</script>

<template>
  <div v-if="!state.refusal" class="secret-key-field row items-start no-wrap q-gutter-x-sm q-mt-xs" :data-secret-value="state.key">
    <q-input
      v-model="typed"
      class="col"
      outlined
      dense
      type="password"
      :label="`Value for ${state.key}`"
      autocomplete="new-password"
      spellcheck="false"
      :disable="busy !== null"
      data-secret-value-input
      @keydown.enter.prevent="write('set')"
    />
    <q-btn
      unelevated
      dense
      no-caps
      color="primary"
      :label="replacing ? 'Replace' : 'Set'"
      :aria-label="`${replacing ? 'Replace' : 'Set'} ${state.key}`"
      :disable="typed === '' || busy !== null"
      :loading="busy === 'set'"
      data-secret-value-set
      @click="write('set')"
    />
    <q-btn
      v-if="clearable"
      flat
      dense
      no-caps
      label="Clear"
      :aria-label="`Clear ${state.key}`"
      :disable="busy !== null"
      :loading="busy === 'clear'"
      data-secret-value-clear
      @click="write('clear')"
    />
  </div>
  <div v-if="problem" class="text-caption text-negative" data-secret-value-problem>{{ problem }}</div>
</template>
