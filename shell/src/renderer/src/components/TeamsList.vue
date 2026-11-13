<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText } from '../api';
import { useBusy } from '../busy';
import { teamState } from '../teamState';
import { availableWidth, renderedWidth, tileSize } from '../teamTileSize';
import BusyLabel from './BusyLabel.vue';
import Icon from './Icon.vue';
import TeamStateChip from './TeamStateChip.vue';
import TeamWorkflows from './TeamWorkflows.vue';
import TileGrip from './TileGrip.vue';

// The Teams page: one grid of equally sized tiles (every team, then "New team"), resized together by the grip on any
// of them (teamTileSize.ts). A tile is the header (name, state, counts), a row of icon buttons, then the recent
// workflows, which scroll inside the tile. The tile's buttons: settings, pause/resume, archive, delete (two clicks).
const props = defineProps<{ teams: TeamSummary[]; archived: TeamSummary[]; api: YawbleApi; pollMs?: number; refresh: () => Promise<void> | void }>();
const emit = defineEmits<{ open: [teamId: number, panel?: 'workflows', workflow?: number]; settings: [team: TeamSummary]; 'new-team': [] }>();

const cards = useBusy();
const cardBusy = computed(() => cards.any());
const cardDoing = (method: string, teamId: number) => cards.is('card', `${method}:${teamId}`);
const confirmDelete = ref<number | null>(null);
const showArchived = ref(false);
const failure = ref<string | null>(null);

function teamAction(method: string, teamId: number, params: Record<string, unknown> = {}) {
  return cards.run('card', async () => {
    failure.value = null;
    try {
      await props.api.call(method, { teamId, ...params });
      confirmDelete.value = null;
    } catch (e) { failure.value = errorText(e); }
    await props.refresh();
  }, `${method}:${teamId}`);
}
const deleteTeam = (t: { id: number; name: string }) => teamAction('teams.delete', t.id, { confirm: t.name });

// The tiles' real width is the shared width, or less when the content area is narrower (watched with a
// ResizeObserver on the list itself). The buttons show icon and label while every button row really fits; as soon as
// one row would overflow (its scrollWidth beyond its clientWidth) they all keep only their icons, the label becoming
// the tooltip. Measured with the labels on, so a label is never cut and never wraps.
const wrap = ref<HTMLElement | null>(null);
const iconsOnly = ref(false);
let fitting = 0;
async function fit() {
  const mine = ++fitting;
  iconsOnly.value = false;
  await nextTick();
  if (mine !== fitting) return;
  const rows = wrap.value?.querySelectorAll<HTMLElement>('.card-actions.normal') ?? [];
  iconsOnly.value = [...rows].some((r) => r.scrollWidth > r.clientWidth);
}
function measure() {
  const w = wrap.value?.clientWidth;
  availableWidth.value = w && w > 0 ? w : Infinity;
}
let observer: ResizeObserver | null = null;
onMounted(() => {
  measure();
  void fit();
  if (typeof ResizeObserver !== 'undefined' && wrap.value) { observer = new ResizeObserver(() => { measure(); void fit(); }); observer.observe(wrap.value); }
});
onBeforeUnmount(() => { observer?.disconnect(); availableWidth.value = Infinity; });
// What changes the buttons' widths: the size, which teams are paused (Resume is wider than Pause), the archived list
// shown, and an action in progress ("Pausing…").
watch([renderedWidth, () => props.teams.map((t) => `${t.id}:${t.paused}`).join(), showArchived, cardBusy, () => props.archived.length], () => void fit());
const gridStyle = computed(() => ({ '--tile-w': `${renderedWidth.value}px`, '--tile-h': `${tileSize.value.h}px` }));
const lab = (method: string, id: number, idle: string, busy: string) => (cardDoing(method, id) ? busy : idle);
</script>

<template>
  <div ref="wrap" class="teams-list">
    <h2>Teams</h2>
    <p class="muted">Each team has a Manager that plans the work and members that do it, each agent in its own sandbox.</p>
    <p v-if="failure" class="error small" data-test="teams-action-error">{{ failure }}</p>
    <div class="team-grid" :class="{ 'icons-only': iconsOnly }" :style="gridStyle" data-test="team-grid">
      <div v-for="t in teams" :key="t.id" class="tile-cell">
      <div class="team-card" :class="`is-${teamState(t)}`" :data-state="teamState(t)" data-test="team-card">
        <button class="card-open" :data-test="`team-open-${t.name}`" @click="emit('open', t.id)">
          <span class="card-title"><strong class="card-name" :title="t.name">{{ t.name }}</strong><TeamStateChip :team="t" data-test="team-state" /></span>
          <span class="muted small">{{ t.members }} agents · {{ t.running }} running · {{ t.queued }} queued<template v-if="t.paused"> · <b class="paused-tag">paused</b></template></span>
        </button>
        <div class="card-actions" :class="{ normal: confirmDelete !== t.id }" data-test="card-actions">
          <template v-if="confirmDelete !== t.id">
            <button class="tile-btn" data-test="card-settings" title="Settings: the team's repositories and path, pause and archive, environment, reset, delete" aria-label="Settings"
              @click.stop="emit('settings', t)"><Icon name="settings" :size="16" /><span class="lbl">Settings</span></button>
            <button v-if="!t.paused" class="tile-btn" data-test="card-pause" :title="lab('teams.pause', t.id, 'Pause', 'Pausing…')" :aria-label="lab('teams.pause', t.id, 'Pause', 'Pausing…')" :disabled="cardBusy" @click.stop="teamAction('teams.pause', t.id)"><BusyLabel :busy="cardDoing('teams.pause', t.id)" :text="iconsOnly ? undefined : 'Pausing…'"><Icon name="pause" :size="16" /><span class="lbl">Pause</span></BusyLabel></button>
            <button v-else class="tile-btn" data-test="card-resume" :title="lab('teams.resume', t.id, 'Resume', 'Resuming…')" :aria-label="lab('teams.resume', t.id, 'Resume', 'Resuming…')" :disabled="cardBusy" @click.stop="teamAction('teams.resume', t.id)"><BusyLabel :busy="cardDoing('teams.resume', t.id)" :text="iconsOnly ? undefined : 'Resuming…'"><Icon name="play" :size="16" /><span class="lbl">Resume</span></BusyLabel></button>
            <button class="tile-btn" data-test="card-archive" :title="lab('teams.archive', t.id, 'Archive', 'Archiving…')" :aria-label="lab('teams.archive', t.id, 'Archive', 'Archiving…')" :disabled="cardBusy" @click.stop="teamAction('teams.archive', t.id)"><BusyLabel :busy="cardDoing('teams.archive', t.id)" :text="iconsOnly ? undefined : 'Archiving…'"><Icon name="archive" :size="16" /><span class="lbl">Archive</span></BusyLabel></button>
            <button class="tile-btn danger-text" data-test="card-delete" title="Delete" aria-label="Delete" :disabled="cardBusy" @click.stop="confirmDelete = t.id"><Icon name="trash" :size="16" /><span class="lbl">Delete</span></button>
          </template>
          <template v-else>
            <span class="small confirm-q">Delete {{ t.name }}?</span>
            <button class="tile-btn danger" data-test="card-delete-yes" :disabled="cardBusy" @click.stop="deleteTeam(t)"><BusyLabel :busy="cardDoing('teams.delete', t.id)" text="Deleting…">Delete</BusyLabel></button>
            <button class="tile-btn" data-test="card-delete-no" :disabled="cardBusy" @click.stop="confirmDelete = null">Cancel</button>
          </template>
        </div>
        <TeamWorkflows :team-id="t.id" :api="api" :poll-ms="pollMs" @open="emit('open', t.id, 'workflows', $event)" />
        <TileGrip />
      </div>
      <TileGrip axis="w" /><TileGrip axis="h" />
      </div>
      <div class="tile-cell">
      <div class="team-card add-wrap" data-test="team-card-new-tile">
        <button class="add" data-test="team-card-new" @click="emit('new-team')"><Icon name="plus" />New team</button>
        <TileGrip />
      </div>
      <TileGrip axis="w" /><TileGrip axis="h" />
      </div>
    </div>
    <template v-if="archived.length">
      <button class="link" data-test="show-archived" @click="showArchived = !showArchived">{{ showArchived ? 'Hide' : 'Show' }} archived teams ({{ archived.length }})</button>
      <div v-if="showArchived" class="team-grid" :class="{ 'icons-only': iconsOnly }" :style="gridStyle" data-test="archived-grid">
        <div v-for="t in archived" :key="t.id" class="tile-cell">
        <div class="team-card archived" data-test="archived-team-card">
          <button class="card-open" @click="emit('open', t.id)">
            <strong class="card-name" :title="t.name">{{ t.name }}</strong>
            <span class="muted small">archived · {{ t.members }} agents</span>
          </button>
          <div class="card-actions" :class="{ normal: confirmDelete !== t.id }" data-test="card-actions">
            <template v-if="confirmDelete !== t.id">
              <button class="tile-btn" data-test="card-unarchive" :title="lab('teams.unarchive', t.id, 'Unarchive', 'Unarchiving…')" :aria-label="lab('teams.unarchive', t.id, 'Unarchive', 'Unarchiving…')" :disabled="cardBusy" @click.stop="teamAction('teams.unarchive', t.id)"><BusyLabel :busy="cardDoing('teams.unarchive', t.id)" :text="iconsOnly ? undefined : 'Unarchiving…'"><Icon name="unarchive" :size="16" /><span class="lbl">Unarchive</span></BusyLabel></button>
              <button class="tile-btn danger-text" data-test="card-delete" title="Delete" aria-label="Delete" :disabled="cardBusy" @click.stop="confirmDelete = t.id"><Icon name="trash" :size="16" /><span class="lbl">Delete</span></button>
            </template>
            <template v-else>
              <span class="small confirm-q">Delete {{ t.name }}?</span>
              <button class="tile-btn danger" data-test="card-delete-yes" :disabled="cardBusy" @click.stop="deleteTeam(t)"><BusyLabel :busy="cardDoing('teams.delete', t.id)" text="Deleting…">Delete</BusyLabel></button>
              <button class="tile-btn" data-test="card-delete-no" :disabled="cardBusy" @click.stop="confirmDelete = null">Cancel</button>
            </template>
          </div>
          <TileGrip />
        </div>
        <TileGrip axis="w" /><TileGrip axis="h" />
        </div>
      </div>
    </template>
    <p v-if="!teams.length" class="muted" data-test="no-teams">No teams yet. Install and sign in to your agent CLIs in your own terminal (Settings → Agents), then create a team with a Manager.</p>
  </div>
</template>

<style scoped>
.teams-list { margin: 12px auto; }
.teams-list h2 { margin: 0 0 4px; font-size: 22px; }
/* One size for every tile: columns are the shared width (or the whole row when narrower), rows the shared height. */
.team-grid { display: grid; grid-template-columns: repeat(auto-fill, min(var(--tile-w), 100%)); grid-auto-rows: var(--tile-h); justify-content: start; gap: 14px; margin: 18px 0; }
/* Each tile sits in a cell; the width and height handles live in the 14px gaps right of and below the cell, outside the tile. */
.tile-cell { position: relative; min-width: 0; min-height: 0; }
.tile-cell > .team-card { height: 100%; box-sizing: border-box; }
.team-card { position: relative; display: flex; flex-direction: column; align-items: stretch; gap: 4px; padding: 12px 12px 14px; min-width: 0; min-height: 0; overflow: hidden;
  border-radius: 14px; border: 1px solid var(--line); background: var(--surface); text-align: left; }
.team-card:hover { border-color: #b9c2d3; }
.card-open { flex: none; min-width: 0; display: flex; flex-direction: column; align-items: stretch; gap: 4px; border: 0; background: transparent; padding: 0; text-align: left; cursor: pointer; max-width: 100%; }
.card-open > .small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.card-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.card-open:hover strong { color: var(--accent); }
.card-actions { flex: none; display: flex; flex-wrap: nowrap; gap: 4px; align-items: center; margin-top: 6px; min-width: 0; }
.tile-btn { flex: none; display: inline-flex; align-items: center; justify-content: center; gap: 4px; height: 28px; padding: 0 7px; box-sizing: border-box;
  font-size: 12px; line-height: 1; white-space: nowrap; border-radius: 6px; border: 1px solid var(--line); background: var(--surface); }
.tile-btn:hover:not(:disabled) { background: var(--hover); }
.tile-btn:disabled { opacity: .5; }
.icons-only .tile-btn:not(.danger) { width: 32px; padding: 0; flex: none; }
.icons-only .tile-btn .lbl { display: none; }
.confirm-q { flex: 1 1 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
.danger-text { color: var(--bad); }
.tile-btn.danger { background: var(--bad); border-color: var(--bad); color: #fff; }
.team-card.archived { opacity: .75; }
/* A team's state: the outline says it at a glance, the chip says it in words; running breathes. */
.team-card { --team-state: var(--state-idle); }
.team-card.is-running { --team-state: var(--state-running); border-color: var(--team-state); animation: team-breathe 2.4s ease-in-out infinite; }
.team-card.is-queued { --team-state: var(--state-waiting); border-color: var(--team-state); border-style: dashed; }
.team-card.is-paused { --team-state: var(--state-blocked); border-color: var(--team-state); }
.team-card.is-running:hover, .team-card.is-queued:hover, .team-card.is-paused:hover { border-color: var(--team-state); }
.card-title { display: flex; align-items: center; gap: 8px; min-width: 0; }
.card-title > :not(.card-name) { flex: none; }
@keyframes team-breathe {
  0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--state-running) 0%, transparent); }
  50% { box-shadow: 0 0 0 4px color-mix(in srgb, var(--state-running) 22%, transparent); }
}
@media (prefers-reduced-motion: reduce) { .team-card.is-running { animation: none; box-shadow: 0 0 0 2px color-mix(in srgb, var(--state-running) 22%, transparent); } }
.paused-tag { color: var(--warn); font-weight: 600; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; }
.team-card.add-wrap { border-style: dashed; padding: 0; }
.add-wrap .add { flex: 1; display: flex; align-items: center; justify-content: center; gap: 6px; border: 0; background: transparent; color: var(--muted); }
.small { font-size: 12.5px; }
</style>
