<script setup lang="ts">
import { LOCAL_MARK } from '../localMark';

// The one "not pushed" mark (localMark.ts). long: the sentence (the Commit view's where list); else a small chip.
// title: a more precise hover (a branch's count of commits origin lacks).
defineProps<{ long?: boolean; title?: string }>();
</script>

<template>
  <span v-if="long" class="local-long" data-test="local-mark" :title="title ?? LOCAL_MARK.title">{{ LOCAL_MARK.long }}</span>
  <span v-else class="local-mark" data-test="local-mark" :title="title ?? LOCAL_MARK.title">{{ LOCAL_MARK.short }}</span>
</template>

<style scoped>
.local-mark { margin-left: 6px; font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--warn-chip); white-space: nowrap; }
</style>
