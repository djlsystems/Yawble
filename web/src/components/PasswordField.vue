<script setup lang="ts">
import { ref } from 'vue';
import type { Rule } from '../lib/rules';

/**
 * A password input with a reveal toggle.
 *
 * One component rather than the same six lines in five places: every password field in this app
 * wants the same behaviour, and a reveal that exists on some of them and not others is worse than
 * one that exists nowhere.
 */
const model = defineModel<string>({ required: true });

withDefaults(
  defineProps<{
    label: string;
    autocomplete?: string;
    hint?: string;
    disable?: boolean;
    // NULL, NOT FALSE, when unset: Quasar reads a null `dark` as "follow the Dark plugin", and an
    // explicit false pins the field light inside a dark dialog.
    dark?: boolean | null;
    // Checked after the field is first left, like every validated field in these dialogs.
    rules?: Rule[];
  }>(),
  { autocomplete: 'current-password', hint: '', disable: false, dark: null, rules: () => [] },
);

const revealed = ref(false);
</script>

<template>
  <q-input
    v-model="model"
    :type="revealed ? 'text' : 'password'"
    :label="label"
    :hint="hint"
    :autocomplete="autocomplete"
    :disable="disable"
    :dark="dark"
    :rules="rules"
    :lazy-rules="rules.length > 0"
    outlined
    dense
  >
    <template #append>
      <!-- A button, not a bare icon: it is a control, so it needs a name a screen reader can read
           and a tab stop a keyboard can reach. -->
      <q-btn
        flat
        dense
        round
        size="sm"
        :icon="revealed ? 'visibility_off' : 'visibility'"
        :aria-label="revealed ? 'Hide password' : 'Show password'"
        :aria-pressed="revealed"
        @click="revealed = !revealed"
      />
    </template>
  </q-input>
</template>
