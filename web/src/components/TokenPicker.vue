<script setup lang="ts">
import { computed } from 'vue';

/**
 * Tokens a prompt may carry, inserted at the cursor.
 *
 * `{env:NAME}` is offered as a stub the person completes, because the set of variables is a
 * property of the machine rather than of Harness. Platform names are deliberately NOT listed -
 * a prompt may resolve one, and advertising it would invite a credential into a transcript.
 */
const emit = defineEmits<{ insert: [token: string] }>();

/**
 * Which tokens to offer. Defaults to every one a MEMBER's prompt can resolve.
 *
 * Overridable because not every prompt resolves the same set: a Concierge is not a member
 * and has no roster, so `{member}` and `{members}` would be left in its text verbatim. Offering a
 * token that does not resolve is worse than offering none - it looks like a feature and produces a
 * prompt with braces in it.
 */
const props = withDefaults(
  defineProps<{ tokens?: readonly string[] }>(),
  { tokens: () => ['{team}', '{teamId}', '{member}', '{members}', '{shared}', '{env:}'] },
);

const tokens = computed(() => props.tokens);
</script>

<template>
  <div class="token-picker row q-gutter-xs">
    <q-btn
      v-for="token in tokens"
      :key="token"
      dense
      no-caps
      outline
      size="sm"
      :label="token"
      @click="emit('insert', token)"
    />
  </div>
</template>

<style scoped>
.token-picker {
  flex-wrap: wrap;
}
</style>
