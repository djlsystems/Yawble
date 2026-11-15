<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { avatarSheet, initials, loadAvatarSheet, memberAvatar, tileStyle, type MemberState } from './faces';

/**
 * A member's face and the ring that says its state: three arcs turning while it runs, a dashed ring creeping while
 * it waits for a run slot, a throb while it is blocked, needs you or its last run failed, a thin line while idle.
 * The ring holds still under reduced motion. Until the face sheet has loaded (or if it never does) the member shows
 * its initials. Decorative: every view that draws it names the member and says its state in words beside it.
 */
const props = withDefaults(defineProps<{
  team: string;
  member: string;
  state?: MemberState;
  /** The face's side in px; the ring sits outside it. */
  size?: number;
  /** On the Monitor's dark wall: the ring's colour lifted toward white. */
  wall?: boolean;
}>(), { state: 'idle', size: 32, wall: false });

onMounted(() => void loadAvatarSheet());

const tile = computed(() => (avatarSheet.cols ? memberAvatar(props.team, props.member) : -1));
const picture = computed(() => (tile.value < 0 ? undefined : tileStyle(tile.value, avatarSheet.cols, avatarSheet.rows)));
const letters = computed(() => initials(props.member));
const style = computed(() => ({
  '--avatar-size': `${props.size}px`,
  '--avatar-ring': props.wall ? `color-mix(in srgb, var(--state-${props.state}) 60%, white)` : `var(--state-${props.state})`,
}));
</script>

<template>
  <span class="member-avatar" :class="`is-${state}`" :style="style" :data-state="state" :data-avatar="tile" data-test="member-avatar" aria-hidden="true">
    <span class="ring"></span>
    <span v-if="picture" class="face" :style="picture"></span>
    <span v-else class="face initials"><span>{{ letters }}</span></span>
  </span>
</template>

<style scoped>
/* The ring stands --ring-gap outside the face. */
.member-avatar {
  --ring-gap: 4px; --ring-width: 2.5px;
  position: relative; display: inline-flex; flex: 0 0 auto;
  width: var(--avatar-size); height: var(--avatar-size); margin: var(--ring-gap);
}
.face {
  position: absolute; inset: 0; border-radius: 50%; background-repeat: no-repeat;
  box-shadow: 0 0 8px color-mix(in srgb, var(--avatar-ring) 70%, transparent);
}
.is-idle .face { box-shadow: none; }
.initials {
  display: flex; align-items: center; justify-content: center;
  background: color-mix(in srgb, var(--avatar-ring) 22%, var(--surface)); color: var(--ink, inherit);
  font-size: calc(var(--avatar-size) * 0.38); font-weight: 700; line-height: 1;
}
.ring { position: absolute; inset: calc(-1 * var(--ring-gap)); border-radius: 50%; pointer-events: none; }

/* RUNNING: three arcs of 75 degrees turning, a turn in 3.3 s. */
.is-running .ring {
  background: conic-gradient(
    var(--avatar-ring) 0deg 75deg, transparent 75deg 120deg,
    var(--avatar-ring) 120deg 195deg, transparent 195deg 240deg,
    var(--avatar-ring) 240deg 315deg, transparent 315deg 360deg);
  -webkit-mask: radial-gradient(farthest-side, transparent calc(100% - var(--ring-width)), #000 calc(100% - var(--ring-width)));
  mask: radial-gradient(farthest-side, transparent calc(100% - var(--ring-width)), #000 calc(100% - var(--ring-width)));
  animation: avatar-turn 3.27s linear infinite;
}
/* WAITING: a dashed ring creeping round. */
.is-waiting .ring { border: 2px dashed var(--avatar-ring); animation: avatar-turn 9s linear infinite; }
/* BLOCKED, NEEDS YOU, FAILED: a ring that throbs, swelling and brightening, once every 1.4 s. */
.is-blocked .ring, .is-decision .ring, .is-failed .ring {
  border: var(--ring-width) solid var(--avatar-ring);
  animation: avatar-throb 0.69s ease-in-out infinite alternate;
}
/* IDLE: a thin line. */
.is-idle .ring { border: 1.2px solid color-mix(in srgb, var(--avatar-ring) 70%, transparent); }

@keyframes avatar-turn { to { transform: rotate(360deg); } }
@keyframes avatar-throb { from { opacity: .5; transform: scale(1); } to { opacity: 1; transform: scale(1.1); } }
@media (prefers-reduced-motion: reduce) { .ring { animation: none !important; } }
</style>
