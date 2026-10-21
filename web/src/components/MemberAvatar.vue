<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { avatarSheet, loadAvatarSheet, memberAvatar, tileStyle } from '../lib/avatars';
import { initials } from '../lib/initials';
import { stateToken, type MemberState } from '../lib/memberState';
import { useConsoleStore } from '../stores/console';

/**
 * A MEMBER'S AVATAR AND ITS STATE RING, as the board's member card and the Kanban card draw it; the
 * Monitor draws the same tile and the same ring on its canvas (`lib/monitor/scene.ts`). The tile is
 * the member's own (`lib/avatars`), the ring is its state (`lib/memberState`) in the state's token:
 * three arcs turning while it runs, a dashed ring turning while it waits, a throb while it is
 * blocked, needs you or failed, and a thin line while it is idle. Under reduced motion the ring
 * holds still.
 *
 * A plugin member wears no avatar: a diamond with its initials, as on the Monitor, in the same
 * ring. Until the sheet has loaded, and if it never does, an agent shows its initials.
 *
 * Decorative: every view that draws it names the member and says its state in words beside it.
 *
 * A view that holds the member's state, kind or name hands them in; one that holds only the team and
 * the member (a dialog's title bar) leaves them out, and they are read from the member's snapshot on
 * the board (`memberRing`), so the ring there follows the member as its card's does.
 */
const props = withDefaults(
  defineProps<{
    team: string;
    member: string;
    name?: string;
    state?: MemberState;
    kind?: 'agent' | 'plugin';
    /** The face's side in px; the ring sits outside it. */
    size?: number;
    /** On the Monitor's dark wall: the ring in the state lifted toward white, as the canvas draws it. */
    wall?: boolean;
  }>(),
  { size: 32, wall: false },
);

onMounted(() => void loadAvatarSheet());

const board = useConsoleStore();
/** The member as the board holds it, read only for what the view did not hand in. */
const known = computed(() =>
  props.state !== undefined && props.kind !== undefined && props.name !== undefined ? null : board.memberRing(props.team, props.member),
);
const state = computed<MemberState>(() => props.state ?? known.value?.state ?? 'idle');
const plugin = computed(() => (props.kind ?? known.value?.kind ?? 'agent') === 'plugin');
const tile = computed(() => (plugin.value ? -1 : memberAvatar(props.team, props.member)));
const picture = computed(() =>
  tile.value < 0 ? undefined : tileStyle(tile.value, avatarSheet.cols, avatarSheet.rows),
);
/** The letters where there is no picture; a snapshot from an older Host may carry no name. */
const letters = computed(() => initials(props.name ?? known.value?.name ?? props.member ?? ''));
const style = computed(() => ({
  '--avatar-size': `${props.size}px`,
  '--avatar-ring': props.wall ? `var(--os-monitor-state-${state.value})` : `var(--os-state-${stateToken(state.value)})`,
}));
</script>

<template>
  <span
    class="member-avatar"
    :class="{
      'member-avatar--running': state === 'running',
      'member-avatar--waiting': state === 'waiting',
      'member-avatar--blocked': state === 'blocked',
      'member-avatar--decision': state === 'decision',
      'member-avatar--failed': state === 'failed',
      'member-avatar--idle': state === 'idle',
      'member-avatar--plugin': plugin,
    }"
    :style="style"
    :data-state="state"
    :data-avatar="tile"
    data-test="member-avatar"
    aria-hidden="true"
  >
    <span class="member-avatar-ring"></span>
    <span v-if="picture" class="member-avatar-face" :style="picture"></span>
    <span v-else class="member-avatar-face member-avatar-initials">
      <span class="member-avatar-letters">{{ letters }}</span>
    </span>
  </span>
</template>

<style scoped>
/* The ring stands `--ring-gap` outside the face, as the Monitor's stands 5px outside its disc. */
.member-avatar {
  --ring-gap: 4px;
  --ring-width: 2.5px;
  position: relative;
  display: inline-flex;
  flex: 0 0 auto;
  width: var(--avatar-size);
  height: var(--avatar-size);
  margin: var(--ring-gap);
}

.member-avatar-face {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  background-repeat: no-repeat;
  box-shadow: 0 0 8px color-mix(in srgb, var(--avatar-ring) 70%, transparent);
}

.member-avatar--idle .member-avatar-face {
  box-shadow: none;
}

.member-avatar-initials {
  display: flex;
  align-items: center;
  justify-content: center;
  background: color-mix(in srgb, var(--avatar-ring) 22%, var(--os-surface, #fff));
  color: var(--os-ink, inherit);
  font-size: calc(var(--avatar-size) * 0.38);
  font-weight: 700;
  line-height: 1;
}

/* A plugin is a diamond, as on the Monitor: the face turned a quarter, its letters turned back. */
.member-avatar--plugin .member-avatar-face {
  inset: 12%;
  background: color-mix(in srgb, var(--avatar-ring) 45%, var(--os-surface, #fff));
  border: 1px solid color-mix(in srgb, var(--avatar-ring) 80%, transparent);
  border-radius: 3px;
  transform: rotate(45deg);
}

.member-avatar--plugin .member-avatar-letters {
  transform: rotate(-45deg);
  font-size: calc(var(--avatar-size) * 0.32);
}

.member-avatar-ring {
  position: absolute;
  inset: calc(-1 * var(--ring-gap));
  border-radius: 50%;
  pointer-events: none;
}

/* RUNNING: three arcs of 75 degrees turning, a turn in 3.3 s (the Monitor's now / 520 radians). */
.member-avatar--running .member-avatar-ring {
  background: conic-gradient(
    var(--avatar-ring) 0deg 75deg, transparent 75deg 120deg,
    var(--avatar-ring) 120deg 195deg, transparent 195deg 240deg,
    var(--avatar-ring) 240deg 315deg, transparent 315deg 360deg
  );
  -webkit-mask: radial-gradient(farthest-side, transparent calc(100% - var(--ring-width)), #000 calc(100% - var(--ring-width)));
  mask: radial-gradient(farthest-side, transparent calc(100% - var(--ring-width)), #000 calc(100% - var(--ring-width)));
  animation: member-avatar-turn 3.27s linear infinite;
}

/* WAITING: a dashed ring creeping round. */
.member-avatar--waiting .member-avatar-ring {
  border: 2px dashed var(--avatar-ring);
  animation: member-avatar-turn 9s linear infinite;
}

/* BLOCKED, NEEDS YOU, FAILED: a ring that throbs, swelling and brightening (the Monitor's sin(now / 220)). */
.member-avatar--blocked .member-avatar-ring,
.member-avatar--decision .member-avatar-ring,
.member-avatar--failed .member-avatar-ring {
  border: var(--ring-width) solid var(--avatar-ring);
  animation: member-avatar-throb 1.38s ease-in-out infinite alternate;
}

/* IDLE: a thin line. */
.member-avatar--idle .member-avatar-ring {
  border: 1.2px solid color-mix(in srgb, var(--avatar-ring) 70%, transparent);
}

@keyframes member-avatar-turn {
  to {
    transform: rotate(360deg);
  }
}

@keyframes member-avatar-throb {
  from {
    opacity: 0.5;
    transform: scale(1);
  }
  to {
    opacity: 1;
    transform: scale(1.1);
  }
}

@media (prefers-reduced-motion: reduce) {
  .member-avatar-ring {
    animation: none !important;
  }
}
</style>
