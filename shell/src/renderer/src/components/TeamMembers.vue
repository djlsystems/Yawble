<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { AgentCard, YawbleApi } from '../../../shared/api';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { displayName, memberStateOf, MemberStateLabel } from '../faces/faces';

// A team's members on its tile in the Teams list: each one's avatar with its state ring and animation, as on the
// team page. Hovering one shows its name, state and what it is doing. It sits inside the tile's open button (spans only).
const props = defineProps<{ teamId: number; api: YawbleApi; pollMs?: number; fallback: string }>();

const cards = ref<AgentCard[] | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

async function load() {
  try {
    const got = await props.api.call<AgentCard[]>('teams.agents', { teamId: props.teamId });
    cards.value = Array.isArray(got) ? got : [];
  } catch { /* keeps what it showed; the text fallback until the first answer */ }
}

function tip(c: AgentCard): string {
  const doing = c.statusLine?.split('\n').find((l) => l.trim() !== '')?.trim();
  const queued = c.state === 'queued' && c.queuePosition ? ` (#${c.queuePosition} in the queue)` : '';
  const line = doing ? `\n${doing.length > 160 ? `${doing.slice(0, 159)}…` : doing}` : '';
  return `${displayName(c.member.name)} · ${c.member.role}\n${MemberStateLabel[memberStateOf(c)]}${queued}${line}`;
}

onMounted(() => { void load(); timer = setInterval(load, Math.max(5000, props.pollMs ?? 5000)); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <span class="tm" data-test="team-members">
    <span v-if="!cards || !cards.length" class="muted small tm-text" data-test="team-members-text">{{ fallback }}</span>
    <MemberAvatar v-for="c in cards ?? []" :key="c.member.id" class="tm-face" :team="c.member.team" :member="c.member.name"
      :state="memberStateOf(c)" :size="24" :title="tip(c)" :data-state="memberStateOf(c)" data-test="team-member" />
  </span>
</template>

<style scoped>
.tm { display: flex; flex-wrap: wrap; align-items: center; gap: 2px; min-height: 30px; }
.tm-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
