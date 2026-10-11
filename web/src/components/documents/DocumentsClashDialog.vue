<script setup lang="ts">
import { ref, watch } from 'vue';
import type { DocumentsClash, OnClash } from '../../api/types';
import type { ClashAnswer } from '../../lib/documentsExplorer';

/**
 * ONE NAME CLASH, put to the person: "{name} is already in {folder}." Keep both, Replace or Skip,
 * and "Do this for the rest" when more are waiting. Cancel sends nothing at all.
 */
const props = defineProps<{
  clash: DocumentsClash | null;
  folder: string;
  remaining: number;
}>();

const emit = defineEmits<{ answer: [answer: ClashAnswer | null] }>();

const rest = ref(false);
watch(() => props.clash, () => (rest.value = false));

const name = (path: string) => path.slice(path.lastIndexOf('/') + 1);

function choose(choice: OnClash) {
  emit('answer', { choice, rest: rest.value });
}

function onShow(showing: boolean) {
  if (!showing && props.clash) emit('answer', null);
}
</script>

<template>
  <q-dialog :model-value="clash !== null" @update:model-value="onShow">
    <q-card class="os-dialog-sm documents-clash">
      <q-card-section v-if="clash">
        <div class="os-dialog-title">Name clash</div>
        <p class="q-mt-sm q-mb-none">
          <strong>{{ name(clash.to) }}</strong> is already in {{ folder }}.
          <span v-if="clash.isFolder" class="os-text-muted">It is a folder.</span>
        </p>
        <q-checkbox v-if="remaining > 0" v-model="rest" dense class="q-mt-sm" :label="`Do this for the rest (${remaining})`" />
      </q-card-section>
      <q-card-actions align="right" class="q-gutter-xs">
        <q-btn flat no-caps label="Cancel" data-clash="cancel" @click="emit('answer', null)" />
        <q-btn flat no-caps label="Skip" data-clash="skip" @click="choose('skip')" />
        <q-btn flat no-caps color="negative" label="Replace" data-clash="replace" @click="choose('replace')" />
        <q-btn unelevated no-caps color="primary" label="Keep both" data-clash="keep-both" @click="choose('keep-both')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
