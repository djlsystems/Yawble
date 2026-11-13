<script setup lang="ts">
import type { YawbleApi, TeamSummary } from '../../../shared/api';
import DocumentsBrowser from './DocumentsBrowser.vue';
import Modal from './Modal.vue';

/** A team's documents in a dialog (the team page's Documents button); the Documents page shows every team's. */
defineProps<{ team: TeamSummary; api?: YawbleApi }>();
const emit = defineEmits<{ close: [] }>();
</script>

<template>
  <Modal :title="`${team.name} · documents`" wide @close="emit('close')">
    <DocumentsBrowser :team="team" :api="api" />
  </Modal>
</template>
