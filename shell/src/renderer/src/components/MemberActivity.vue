<script setup lang="ts">
import type { ActivityRow } from '../monitor/model';
import { Palette } from '../monitor/scene';
import { ago } from '../monitor/teamActivity';
import LoadingNote from './LoadingNote.vue';

// A member's activity beside its card on the team board: the lines the Monitor's activity list shows for that member,
// newest first, in the board's light look and without the face (the card beside it has it). It is as tall as the card
// and scrolls inside.
defineProps<{ rows: readonly ActivityRow[]; loaded: boolean; now: number }>();
</script>

<template>
  <aside class="activity" data-test="member-activity" aria-label="Activity">
    <header>Activity</header>
    <ol>
      <li v-for="r in rows" :key="r.key" :style="{ '--tone': Palette[r.tone] }" data-test="member-activity-row">
        <div><b>{{ r.verb }}</b> <template v-if="r.verb.endsWith('told')">{{ r.who }}</template><template v-else>· {{ r.who }}</template> <span class="dim">· {{ ago(r.at, now) }}</span></div>
        <div v-if="r.text" class="text">{{ r.text }}</div>
      </li>
      <li v-if="!loaded" class="none"><LoadingNote text="Loading activity…" /></li>
      <li v-else-if="!rows.length" class="none dim" data-test="member-activity-empty">Nothing yet.</li>
    </ol>
  </aside>
</template>

<style scoped>
.activity { position: relative; display: flex; flex-direction: column; min-height: 9rem; background: var(--surface); border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
header { flex: none; padding: 6px 10px; font-size: 12px; font-weight: 600; color: var(--ink-soft); border-bottom: 1px solid var(--line-soft); }
ol { list-style: none; margin: 0; padding: 2px 0; overflow-y: auto; overflow-x: hidden; position: absolute; top: 29px; left: 0; right: 0; bottom: 0; }
li { padding: 5px 10px 5px 8px; border-left: 3px solid var(--tone); font-size: 12px; line-height: 1.4; min-width: 0; }
li + li { border-top: 1px solid var(--line-soft); }
.text { color: var(--ink-soft); overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow-wrap: anywhere; }
.dim { color: var(--muted); }
.none { border-left-color: transparent; }
</style>
