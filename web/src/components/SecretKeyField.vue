<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import type { SecretKeyState } from '../api/types';

/**
 * ONE SECRET KEY'S VALUE, WRITE-ONLY. A person types a key's value here; it is written when they
 * press the form's own button (Save, Add member, Install), as every other field on the form is - the
 * field has no button of its own (`lib/secretKeyValues`). The next run of a member bound to the key
 * gets it, with no restart.
 *
 * NOTHING IS EVER READ BACK. The Host never sends a value, and the parent empties the box once the
 * write is sent. What the field knows of a key is `state` - set, by whom and when, and from where -
 * which the parent says beside it. While `clearing`, the box is held: the key is cleared on save.
 */
defineProps<{
  state: SecretKeyState;
  /** The field's label; the key's name when absent. */
  label?: string | undefined;
  /** Its info icon's text. */
  help?: string | undefined;
  clearing?: boolean | undefined;
}>();

const value = defineModel<string>({ default: '' });
</script>

<template>
  <div v-if="!state.refusal" class="secret-key-field" :data-secret-value="state.key">
    <q-input
      v-model="value"
      outlined
      dense
      type="password"
      :label="label ?? `Value for ${state.key}`"
      :placeholder="state.set ? '•••••••• saved, never shown: type a new value to replace it' : undefined"
      :stack-label="state.set"
      autocomplete="new-password"
      spellcheck="false"
      :disable="clearing === true"
      data-secret-value-input
    >
      <template v-if="help" #append><FieldHelp :text="help" /></template>
    </q-input>
  </div>
</template>
