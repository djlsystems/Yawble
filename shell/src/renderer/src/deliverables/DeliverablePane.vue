<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { usePoll } from '../usePoll';
import { DELIVERABLE_EXTEND, DELIVERABLES_GET, type Goal, type LaneId, type DeliverableDetail, type DeliverableRow, type YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { ago } from '../board/cards';
import { displayName } from '../faces/faces';
import { renderMarkdown } from '../markdown';
import { confirmDialog, confirmState, type ConfirmState } from '../confirm';
import { extendErrorText } from '../deliverableExpiry';
import CommitText from '../components/CommitText.vue';
import LoadingNote from '../components/LoadingNote.vue';
import BusyLabel from '../components/BusyLabel.vue';
import Icon from '../components/Icon.vue';
import OverallTime from '../timeline/OverallTime.vue';
import { formatShort, hasTime, openTimeline } from '../timeline/time';
import ItemLink from './ItemLink.vue';
import DeliverableStatusIcon from './DeliverableStatusIcon.vue';
import GoalPicker from '../goals/GoalPicker.vue';
import { statusLook, statusTip, timeBar } from './page';

/**
 * The Deliverables page's inline pane: the deliverable's header, the item's spec, its cards by lane, its last messages,
 * its time, and Nudge, Extend, Stop and Close, each asked first in the app's confirm dialog (none for a Visitor). Read
 * with deliverables.get when opened and on every poll.
 */
const props = defineProps<{ row: DeliverableRow; api: YawbleApi; pollMs?: number; readOnly?: boolean; phone?: boolean; goals?: readonly Goal[] | null }>();
const emit = defineEmits<{ close: []; card: [id: number]; openTeam: [teamId: number]; board: [teamId: number]; changed: [] }>();

const detail = ref<DeliverableDetail | null>(null);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);
const specOpen = ref(false);

/** The freshest row: the pane's own read once it has one for this deliverable, else the list's. */
const w = computed<DeliverableRow>(() => (detail.value?.deliverable?.id === props.row.id ? detail.value.deliverable : props.row));
const look = computed(() => statusLook(w.value));
const backlog = computed(() => detail.value?.backlog ?? true);

async function load() {
  const id = props.row.id;
  try {
    const d = await props.api.call<DeliverableDetail>(DELIVERABLES_GET, { deliverable: id });
    if (id !== props.row.id) return;
    detail.value = d;
    error.value = null;
  } catch (e) { if (id === props.row.id) error.value = errorText(e); return false; }
}
watch(() => props.row.id, () => { detail.value = null; specOpen.value = false; error.value = null; void load(); });
usePoll(load, Math.max(2000, props.pollMs ?? 5000));

// ---- the spec ----
const spec = computed(() => (backlog.value && detail.value?.spec ? renderMarkdown(detail.value.spec) : null));
const specLong = computed(() => (detail.value?.spec ?? '').split('\n').length > 8 || (detail.value?.spec ?? '').length > 600);

// ---- the cards by lane ----
interface LaneDef { id: LaneId; label: string }
const Lanes: LaneDef[] = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
const lanes = computed(() => Lanes.map((l) => ({ ...l, cards: (detail.value?.cards ?? []).filter((c) => c.lane === l.id) })).filter((l) => l.cards.length));
const CardStatusWords: Record<string, string> = {
  queued: 'Queued', running: 'Running', blocked: 'Blocked', 'needs-decision': 'Needs a decision', failed: 'Failed', completed: 'Completed', handback: 'Handed back', done: 'Done', stopped: 'Stopped',
};
const cardStatus = (s: string) => CardStatusWords[s] ?? s;

// ---- the activity ----
const KindWords: Record<string, string> = {
  instruction: 'told', progress: 'progress', handback: 'handed back', blocked: 'blocked', decision: 'asks you', completed: 'run completed',
  failed: 'run failed', stopped: 'stopped', done: 'declared done', closed: 'closed', expired: 'expired',
};
const kindWord = (k: string) => KindWords[k] ?? k;
const who = (from: string | null) => (!from ? '' : from === 'person' ? 'You' : from === 'host' ? 'The app' : from.startsWith('trigger:') ? 'A trigger' : displayName(from));

// ---- the time ----
const breakdown = computed(() => timeBar(w.value.time));

// ---- the actions ----
const canNudge = computed(() => w.value.state === 'open' && !w.value.working && !w.value.members.length);
const canStop = computed(() => w.value.state === 'open' && (w.value.working || w.value.members.some((m) => m.state === 'running' || m.state === 'lease')));
const canExtend = computed(() => w.value.state === 'open' && !!w.value.expiresAt);
const canClose = computed(() => w.value.state === 'open');
interface Action { id: 'nudge' | 'extend' | 'stop' | 'close'; method: string; ask: string; ok: string; danger?: boolean; doing: string }
const Actions: Record<Action['id'], Action> = {
  nudge: { id: 'nudge', method: 'bus.nudgeDeliverable', ask: 'Nudge #{id}? Its Manager is told to pick it up.', ok: 'Nudge', doing: 'Nudging…' },
  extend: { id: 'extend', method: DELIVERABLE_EXTEND, ask: 'Extend #{id}? Its expiry clock starts again.', ok: 'Extend', doing: 'Extending…' },
  stop: { id: 'stop', method: 'bus.stopDeliverable', ask: 'Stop what runs in #{id} now? The deliverable stays open.', ok: 'Stop', danger: true, doing: 'Stopping…' },
  close: { id: 'close', method: 'bus.closeDeliverable', ask: 'Close #{id} without declaring it complete?', ok: 'Close it', danger: true, doing: 'Closing…' },
};
/** The pane's own question while it is on screen: closed (as Cancel) when access turns read-only, the deliverable
 *  changes or the pane goes, so a Visitor's OK never acts. */
let asking: ConfirmState | null = null;
function cancelAsk() {
  const q = asking;
  asking = null;
  if (q && confirmState.value === q) q.resolve(false);
}
watch(() => props.readOnly, (ro) => { if (ro) cancelAsk(); });
watch(() => props.row.id, cancelAsk);
onBeforeUnmount(cancelAsk);
async function act(a: Action) {
  if (busy.value || props.readOnly) return;
  const id = w.value.id;
  const answer = confirmDialog({ message: a.ask.replace('{id}', String(id)), okLabel: a.ok, danger: a.danger });
  const q = confirmState.value;
  asking = q;
  const ok = await answer;
  if (asking === q) asking = null;
  if (!ok || props.readOnly || id !== props.row.id) return;
  busy.value = a.id;
  error.value = null;
  try {
    await props.api.call(a.method, { teamId: w.value.teamId, deliverable: id });
    emit('changed');
    await load();
  } catch (e) { error.value = a.method === DELIVERABLE_EXTEND ? extendErrorText(e, id) : errorText(e); } finally { busy.value = null; }
}
const timeline = () => openTimeline({ kind: 'deliverable', id: w.value.id, title: w.value.title, team: w.value.team });
</script>

<template>
  <section class="pane" data-test="deliverable-pane" :data-id="w.id" aria-label="Deliverable">
    <div class="phead">
      <DeliverableStatusIcon :deliverable="w" :size="18" />
      <span class="word" :style="{ color: look.tone }" :title="statusTip(w)" data-test="deliverable-pane-status">{{ look.label }}</span>
      <span class="mono muted">#{{ w.id }}</span>
      <span class="grow" />
      <button v-if="phone" class="tool back" title="Back to the deliverables" data-test="deliverable-pane-back" @click="emit('close')">← Deliverables</button>
      <button v-else class="tool" title="Close (Esc)" aria-label="Close" data-test="deliverable-pane-close" @click="emit('close')"><Icon name="close" :size="16" /></button>
    </div>
    <h3 class="title" data-test="deliverable-pane-title">{{ w.title }}</h3>
    <div class="meta">
      <ItemLink v-if="w.item" :item="w.item" :backlog="backlog" />
      <span>{{ w.team }}</span>
      <button class="link" data-test="deliverable-pane-team" @click="emit('openTeam', w.teamId)">Open team</button>
      <button class="link" data-test="deliverable-pane-board" @click="emit('board', w.teamId)">Its board</button>
    </div>
    <div v-if="w.goal !== undefined" class="goal-line" data-test="deliverable-pane-goal">
      <span class="muted small">Goal</span>
      <GoalPicker :api="api" :deliverable="w.id" :goal="w.goal ?? null" :goals="goals ?? null" @changed="emit('changed'); load()" @goals-changed="emit('changed')" />
    </div>
    <p v-if="error" class="error small" data-test="deliverable-pane-error">{{ error }}</p>

    <div v-if="!readOnly && (canNudge || canExtend || canStop || canClose)" class="actions" role="toolbar" aria-label="Deliverable actions" data-test="deliverable-pane-actions">
      <button v-if="canNudge" :disabled="!!busy" data-test="deliverable-pane-nudge" title="Tell its Manager to pick it up" @click="act(Actions.nudge)"><BusyLabel :busy="busy === 'nudge'" :text="Actions.nudge.doing">Nudge</BusyLabel></button>
      <button v-if="canExtend" :disabled="!!busy" data-test="deliverable-pane-extend" title="Reset its expiry clock" @click="act(Actions.extend)"><BusyLabel :busy="busy === 'extend'" :text="Actions.extend.doing">Extend</BusyLabel></button>
      <button v-if="canStop" :disabled="!!busy" data-test="deliverable-pane-stop" title="Stop the runs working on it now" @click="act(Actions.stop)"><BusyLabel :busy="busy === 'stop'" :text="Actions.stop.doing">Stop</BusyLabel></button>
      <button v-if="canClose" :disabled="!!busy" data-test="deliverable-pane-close-wf" title="End it without declaring it complete" @click="act(Actions.close)"><BusyLabel :busy="busy === 'close'" :text="Actions.close.doing">Close</BusyLabel></button>
    </div>

    <LoadingNote v-if="!detail && !error" text="Loading the deliverable…" />
    <template v-if="detail">
      <section v-if="spec" class="block" data-test="deliverable-pane-spec">
        <h4>Spec <span class="muted small">{{ w.item?.label }}</span></h4>
        <div class="md spec" :class="{ collapsed: specLong && !specOpen }" v-html="spec"></div>
        <button v-if="specLong" class="link small" data-test="deliverable-pane-spec-toggle" :aria-expanded="specOpen" @click="specOpen = !specOpen">{{ specOpen ? 'Show less' : 'Show all' }}</button>
      </section>

      <section class="block" data-test="deliverable-pane-cards">
        <h4>Cards <span class="muted small">{{ detail.cards.length }}</span></h4>
        <p v-if="!detail.cards.length" class="muted small">No cards yet.</p>
        <div v-for="l in lanes" :key="l.id" class="lane" :data-lane="l.id">
          <div class="lane-name">{{ l.label }}</div>
          <button v-for="c in l.cards" :key="c.id" type="button" class="card-row" data-test="deliverable-pane-card" :data-id="c.id" @click="emit('card', c.id)">
            <span class="cid mono muted">{{ c.id }}</span>
            <span class="ctitle">{{ c.title }}</span>
            <span class="cwho muted">{{ c.member ? displayName(c.member) : 'unassigned' }} · {{ cardStatus(c.status) }}</span>
            <OverallTime v-if="hasTime(c.time)" :time="c.time" :target="{ kind: 'card', id: c.id, title: c.title, team: w.team }" />
          </button>
        </div>
      </section>

      <section class="block" data-test="deliverable-pane-time">
        <h4>Time</h4>
        <div class="time-head">
          <OverallTime v-if="hasTime(w.time)" :time="w.time" :target="{ kind: 'deliverable', id: w.id, title: w.title, team: w.team }" />
          <span class="grow" />
          <button class="small" data-test="deliverable-pane-timeline" @click="timeline"><Icon name="clock" :size="13" /> Timeline</button>
        </div>
        <table v-if="breakdown.length" class="mini-table" data-test="deliverable-pane-breakdown">
          <tr v-for="b in breakdown" :key="b.category" data-test="deliverable-pane-breakdown-row">
            <td><span class="sw" :style="{ background: b.color }" />{{ b.label }}</td>
            <td class="num">{{ formatShort(b.ms) }}</td>
            <td class="num muted">{{ b.pct }}%</td>
          </tr>
        </table>
      </section>

      <section class="block" data-test="deliverable-pane-activity">
        <h4>Activity</h4>
        <p v-if="!detail.messages.length" class="muted small">Nothing yet.</p>
        <ol class="acts">
          <li v-for="m in detail.messages" :key="m.seq" class="act" :class="m.kind" data-test="deliverable-pane-message" :data-kind="m.kind">
            <div class="act-head"><b>{{ who(m.from) }}</b> <span class="kind">{{ kindWord(m.kind) }}</span><template v-if="m.kind === 'instruction' && m.member && m.member !== m.from"> {{ displayName(m.member) }}</template>
              <span class="grow" /><span class="muted small" :title="m.at">{{ ago(m.at) }}</span></div>
            <div v-if="m.text" class="act-text"><CommitText :text="m.text" :team="w.team" /></div>
          </li>
        </ol>
      </section>
    </template>
  </section>
</template>

<style scoped>
.pane { display: flex; flex-direction: column; gap: 8px; border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; background: var(--surface); min-height: 0; overflow: auto;
  animation: pane-in .18s ease-out; }
@keyframes pane-in { from { opacity: 0; transform: translateX(6px); } }
@media (prefers-reduced-motion: reduce) { .pane { animation: none; } }
.pane > * { flex: none; }
.phead { display: flex; align-items: center; gap: 8px; }
.word { font-weight: 600; font-size: 13px; }
.title { margin: 0; font-size: 16px; line-height: 1.3; overflow-wrap: anywhere; }
.meta { display: flex; flex-wrap: wrap; gap: 4px 12px; align-items: baseline; font-size: 12.5px; color: var(--ink-soft); }
.grow { flex: 1; }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); margin: 0; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; font-size: 12px; cursor: pointer; }
.link:hover { text-decoration: underline; }
.tool { display: inline-flex; align-items: center; justify-content: center; width: 26px; height: 24px; padding: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--ink-soft); cursor: pointer; }
.tool:hover { background: color-mix(in srgb, var(--ink) 7%, transparent); color: var(--ink); }
.actions { display: flex; flex-wrap: wrap; gap: 6px; padding: 6px 0; border-top: 1px solid var(--line-soft); border-bottom: 1px solid var(--line-soft); }
.actions button { font-size: 12.5px; }
.block { border-top: 1px solid var(--line-soft); padding-top: 6px; }
h4 { margin: 0 0 6px; font-size: 12px; text-transform: uppercase; letter-spacing: .05em; color: var(--ink-soft); font-weight: 600; }
.md { font-size: 13px; border: 1px solid var(--line-soft); border-radius: 6px; padding: 4px 10px; overflow: auto; }
.spec.collapsed { max-height: 9.5em; overflow: hidden; -webkit-mask-image: linear-gradient(to bottom, var(--ink) 60%, transparent); mask-image: linear-gradient(to bottom, var(--ink) 60%, transparent); }
.lane { margin-bottom: 6px; }
.lane-name { font-size: 11px; color: var(--muted); margin: 2px 0; }
.card-row { display: grid; grid-template-columns: auto 1fr auto; gap: 0 8px; align-items: baseline; width: 100%; text-align: left; border: 1px solid var(--line-soft); background: transparent;
  border-radius: 7px; padding: 5px 8px; margin-bottom: 4px; font-size: 12.5px; cursor: pointer; color: var(--ink); }
.card-row:hover { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 5%, transparent); }
.ctitle { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cwho { grid-column: 2; font-size: 11.5px; }
.card-row :deep(.ot) { grid-column: 3; grid-row: 1 / span 2; align-self: center; }
.time-head { display: flex; align-items: center; gap: 8px; }
.time-head :deep(.ot) { font-size: 13px; color: var(--ink); }
.time-head button { display: inline-flex; align-items: center; gap: 4px; font-size: 12px; }
.mini-table { border-collapse: collapse; margin-top: 6px; font-size: 12px; width: 100%; }
.mini-table td { padding: 2px 4px; }
.mini-table .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
.sw { display: inline-block; width: 9px; height: 9px; border-radius: 2px; margin-right: 6px; vertical-align: -1px; }
.acts { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.act { border-left: 3px solid var(--line); padding: 2px 0 2px 8px; font-size: 12.5px; }
.act.handback, .act.completed, .act.done { border-left-color: var(--ok); }
.act.decision { border-left-color: var(--state-decision); }
.act.blocked, .act.failed { border-left-color: var(--state-blocked); }
.act.progress { border-left-color: var(--state-running); }
.act-head { display: flex; gap: 4px; align-items: baseline; }
.kind { color: var(--ink-soft); }
.act-text { color: var(--ink-soft); white-space: pre-wrap; overflow-wrap: anywhere; margin-top: 1px; }
@media (max-width: 759.98px) {
  .pane { overflow: visible; }
  .tool { width: 44px; height: 44px; }
  .tool.back { width: auto; padding: 0 10px; font-size: 13px; color: var(--accent); }
  .actions button, .link, .card-row { min-height: 44px; }
}
.goal-line { display: flex; align-items: center; gap: 8px; margin: 4px 0 8px; flex-wrap: wrap; }
</style>
