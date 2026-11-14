<script setup lang="ts">
import { computed } from 'vue';
import type { BoardCard } from '../../../shared/api';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { displayName } from '../faces/faces';
import { cardFace, StatusColor, StatusLabel } from './cards';

// One card: the member's face in its state ring (none when unassigned), the item it comes from, the title, who and
// which team, the status in its colour, and what is waiting (the Manager, the person).
const props = defineProps<{ card: BoardCard; selected?: boolean }>();
const emit = defineEmits<{ open: [] }>();
const color = computed(() => StatusColor[props.card.status]);
// The heavy-work lease (one full test suite, build or image build at a time on this computer), when this card's member
// holds it or waits for it in the run working this card.
const leaseLabel = computed(() => {
  const l = props.card.lease;
  if (!l) return '';
  return l.state === 'holding' ? 'Heavy work' : `Waiting for heavy work${l.position ? ` (${l.position})` : ''}`;
});
const leaseTitle = computed(() => {
  const l = props.card.lease;
  if (!l) return '';
  const what = l.what ? `: ${l.what}` : '';
  return l.state === 'holding'
    ? `Holds the heavy-work lease${what}. Heavy work (a full test suite, build or image build) runs one at a time on this computer.`
    : `Waiting for the heavy-work lease${l.position ? `, position ${l.position}` : ''}${what}.`;
});
</script>

<template>
  <button class="card" :class="{ selected }" :style="{ '--status': color }" :data-test="`board-card-${card.id}`" data-card @click="emit('open')">
    <div class="top">
      <MemberAvatar v-if="card.member" :team="card.team" :member="card.member" :state="cardFace(card)" :size="26" />
      <span v-else class="nobody" title="Unassigned">?</span>
      <div class="what">
        <span v-if="card.item" class="item">{{ card.item }}</span>
        <span class="title">{{ card.title }}</span>
      </div>
    </div>
    <div class="meta">
      <span>{{ card.member ? displayName(card.member) : 'Unassigned' }}</span> · <span>{{ card.team }}</span>
      <span class="muted"> · card {{ card.id }}<template v-if="card.workflow"> · #{{ card.workflow }}</template></span>
    </div>
    <div class="foot">
      <span class="status" data-test="card-status">{{ StatusLabel[card.status] }}</span>
      <span v-if="card.lease" class="badge lease" :class="card.lease.state === 'waiting' ? 'queued' : 'held'" :title="leaseTitle" data-test="card-lease">{{ leaseLabel }}</span>
      <span v-if="card.awaitingManager" class="badge waiting" title="You changed this card; the Manager has not acted on it yet">awaiting Manager</span>
      <span v-if="card.trailCount" class="muted small">{{ card.trailCount }} update{{ card.trailCount === 1 ? '' : 's' }}</span>
    </div>
  </button>
</template>

<style scoped>
.card { display: flex; flex-direction: column; gap: 6px; width: 100%; text-align: left; padding: 9px 10px; border-radius: 10px;
  border: 1px solid var(--line); border-left: 4px solid var(--status); background: color-mix(in srgb, var(--status) 5%, var(--surface)); cursor: pointer; }
.card:hover { border-color: color-mix(in srgb, var(--status) 55%, var(--line)); border-left-color: var(--status); }
.card.selected { box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 45%, transparent); }
.top { display: flex; gap: 6px; align-items: flex-start; }
.nobody { width: 26px; height: 26px; margin: 4px; border-radius: 50%; border: 1.5px dashed var(--muted); color: var(--muted); display: grid; place-items: center; font-weight: 700; font-size: 12px; flex: none; }
.what { display: flex; flex-direction: column; gap: 2px; min-width: 0; padding-top: 3px; }
.item { font: 600 11px ui-monospace, Consolas, monospace; color: var(--accent); }
.title { font-weight: 600; font-size: 13.5px; line-height: 1.3; overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
.meta { font-size: 12px; color: var(--ink-soft); }
.foot { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.status { font-size: 11.5px; font-weight: 700; color: var(--status); }
.badge { font-size: 11px; padding: 1px 7px; border-radius: 10px; }
.waiting { background: var(--bad-bg-soft); color: var(--bad-ink); border: 1px solid var(--bad-line-soft); }
.lease { font-weight: 600; --k: var(--state-running); color: var(--k); background: color-mix(in srgb, var(--k) 12%, var(--surface)); border: 1px solid color-mix(in srgb, var(--k) 35%, transparent); }
.lease.queued { --k: var(--state-waiting); }
.muted { color: var(--muted); }
.small { font-size: 11px; }
</style>
