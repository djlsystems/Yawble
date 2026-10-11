<script setup lang="ts">
import { computed } from 'vue';
import type { TeamWorkflows } from '../api/types';
import type { TeamStatus } from '../lib/teamKpis';
import { teamStatusIcon } from '../lib/teamStatusIcon';

/**
 * A team's status as one icon, beside its Activity chart on the Teams table and on the team's own
 * strip. The words are the Status column's, in the tooltip and to a screen reader; nothing renders
 * for a team with nothing to say (no workflow run yet).
 */
const props = defineProps<{
  status: TeamStatus;
  workflows: TeamWorkflows | null;
  held?: readonly string[];
}>();

const shown = computed(() => teamStatusIcon(props.status, props.workflows, props.held ?? []));
</script>

<template>
  <span
    v-if="shown"
    class="team-status-icon"
    :class="{
      'team-status-icon--positive': shown.tone === 'positive',
      'team-status-icon--neutral': shown.tone === 'neutral',
      'team-status-icon--active': shown.tone === 'active',
      'team-status-icon--warning': shown.tone === 'warning',
      'team-status-icon--negative': shown.tone === 'negative',
    }"
    :data-team-status="status"
    :data-team-status-words="shown.words"
    role="img"
    :aria-label="shown.words"
  >
    <q-icon :name="shown.icon" size="22px" :class="{ 'team-status-icon--spin': shown.spin }" />
    <q-tooltip>{{ shown.words }}</q-tooltip>
  </span>
</template>

<style scoped>
.team-status-icon {
  display: inline-flex;
  align-items: center;
  flex: 0 0 auto;
}

.team-status-icon--positive { color: var(--os-ok); }
.team-status-icon--neutral { color: var(--os-ink-muted); }
.team-status-icon--active { color: var(--os-info); }
.team-status-icon--warning { color: var(--os-warn-ink); }
.team-status-icon--negative { color: var(--os-error); }

.team-status-icon--spin {
  animation: team-status-spin 2s linear infinite;
}

@keyframes team-status-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .team-status-icon--spin { animation: none; }
}
</style>
