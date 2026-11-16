<script setup lang="ts">
import { computed, nextTick, provide, reactive, ref, useId, watch } from 'vue';
import { PaneHeaderKey } from '../terminalPane';
import type { AgentCard, YawbleApi } from '../../../shared/api';
import type { ActivityRow } from '../monitor/model';
import MemberAvatar from '../faces/MemberAvatar.vue';
import BusyLabel from './BusyLabel.vue';
import Icon from './Icon.vue';
import MemberActivity from './MemberActivity.vue';
import TileGrip from './TileGrip.vue';
import TerminalApp from '../TerminalApp.vue';
import { displayName, memberStateOf, MemberStateLabel } from '../faces/faces';
import { renderMarkdown } from '../markdown';
import { splitReport } from '../reportFormat';
import { leaseText } from '../heavyLease';
import { outputHeight } from '../agentOutputHeight';
import { activityWidth } from '../memberActivityWidth';
import { memberActivityStore } from '../gripStore';
import { errorText, resolveApi } from '../api';
import { getMemberView, setMemberView, type MemberView } from '../memberPaneView';
import { useNarrow } from '../narrowScreen';

// One member on the team page (B061), in one frame: a title bar (face, name, chips, state, "more waiting"), one toolbar
// (Open interactive CLI, Code, Settings, Logs, Stop, Remove, and the Activity | Terminal toggle), and a body: on the left
// what the member last said (formatted, the shared height, scrolling inside), on the right its Activity or its terminal.
// The toggle is per member and remembered (memberPaneView.ts); the terminal is only mounted while it shows, so only a
// shown terminal polls. The bar between the two areas sets the right area's width for every member (memberActivityWidth);
// the gaps between members set the body's height (TeamView). A phone stacks it: title, toolbar (the extras behind ⋯),
// what was said, then the Activity, full width; the terminal there is the whole screen with one header row (B057).
/** pending: Stop or Remove was clicked and the core has not answered yet (that icon shows the spinner, both are held). */
const props = withDefaults(defineProps<{
  card: AgentCard; pending?: 'stop' | 'remove' | null; api?: YawbleApi; pollMs?: number;
  /** The member's activity lines (the Monitor's), whether they have loaded, and the clock they are aged against. */
  rows?: readonly ActivityRow[]; loaded?: boolean; now?: number;
  /** A phone: how many activity lines show (the rest are in Logs). */
  activityLimit?: number;
}>(), { pending: null, rows: () => [], loaded: true, now: () => Date.now() });
const emit = defineEmits<{ code: []; settings: []; policy: []; logs: []; stop: []; remove: [] }>();

const m = computed(() => props.card.member);
const face = computed(() => memberStateOf(props.card));
const phone = useNarrow();
// The details after the state's own word (the label says Running, Needs you, Idle...); what the agent said is below.
const stateLine = computed(() => {
  const c = props.card;
  if (c.attention === 'decision') return 'a question is waiting for your answer';
  if (c.state === 'queued') return `position ${c.queuePosition} of ${c.queueLength} for a global run slot · ${c.queuedInstructions} instruction${c.queuedInstructions === 1 ? '' : 's'}`;
  if (c.state === 'running') return '';
  if (c.lastRunState === 'failed' || c.lastRunState === 'interrupted') return `last run ${c.lastRunState}: ${c.lastRunReason ?? ''}`;
  if (c.lastRunState === 'completed') return 'last run completed';
  return '';
});
const lease = computed(() => leaseText(props.card));
// Tells that arrived while the member runs wait behind this run (the core counts undelivered work for every state).
const moreWaiting = computed(() => (props.card.state === 'running' && props.card.queuedInstructions > 0 ? props.card.queuedInstructions : 0));
const moreWaitingTitle = computed(() =>
  `${moreWaiting.value} instruction${moreWaiting.value === 1 ? '' : 's'} waiting; they run after this one (oldest workflow first)`);
const said = computed(() => props.card.statusLine?.trim() || '');
const report = computed(() => splitReport(said.value));
const saidHtml = computed(() => (said.value ? renderMarkdown(report.value.body) : ''));
// The core keeps what a member said only while it runs (and seeds it from the log at start); a member that has run
// but shows nothing has not reported since then.
const emptyText = computed(() => (props.card.lastRunState ? 'No report since the app started.' : 'Nothing said yet.'));
const hasDetails = computed(() => !!(report.value.label || stateLine.value || lease.value || props.card.policyProblems?.length));

// Activity or Terminal, per member (shared with the entry points: the Monitor, the palette, the remote view).
// A phone (B064, card 310) has its own choice: one member's terminal at a time, none when the page opens (memberPaneView.ts).
const view = computed<MemberView>(() => getMemberView(m.value.team, m.value.name, phone.value));
const show = (v: MemberView) => setMemberView(m.value.team, m.value.name, v, phone.value);

// The shown terminal: its session is live (an interactive CLI, or a run attached), which holds Open and allows Stop.
const term = ref<InstanceType<typeof TerminalApp> | null>(null);
const termLive = ref(false);
watch(view, (v) => { if (v !== 'terminal') termLive.value = false; });
const canOpen = computed(() => props.card.state === 'idle' && !termLive.value);
const canStop = computed(() => props.card.state === 'running' || termLive.value);
const opening = ref(false);
const error = ref<string | null>(null);
/** Open interactive CLI: shows the terminal (so the session is seen) and starts the CLI at the terminal's fitted size. */
async function openInteractive() {
  if (opening.value) return;
  error.value = null;
  opening.value = true;
  try {
    show('terminal');
    await nextTick();
    await resolveApi(props.api).call('terminal.openInteractive', { memberId: m.value.id, ...(term.value?.size() ?? {}) });
  } catch (e) {
    error.value = errorText(e);
  } finally {
    opening.value = false;
  }
}

/** The phone's ⋯ menu (the toolbar's extras). */
const moreOpen = ref(false);

// A phone (B057, card 307): the member's terminal is the whole screen over the page with one 44 px header row - the
// title, the terminal's Formatted / Raw tabs, a ⋯ holding its bar (the state line, Open interactive CLI, Policy, Stop)
// and ✕ (back to Activity) - and the terminal fills the rest. The terminal puts its tabs and bar there (PaneHeaderKey).
const termFull = computed(() => phone.value && view.value === 'terminal');
const uid = useId();
const headerSlots = reactive({ get on() { return termFull.value; }, tabs: `member-terminal-tabs-${uid}`, more: `member-terminal-more-${uid}` });
provide(PaneHeaderKey, headerSlots);
const termMoreOpen = ref(false);
watch(termFull, () => { termMoreOpen.value = false; });
const height = outputHeight;
const root = ref<HTMLElement | null>(null);
/** The team page scrolls a pane into view (focusMemberTerminal). */
defineExpose({ el: () => root.value });
</script>

<template>
  <section ref="root" class="pane" :class="[card.state, `is-${face}`, `view-${view}`, { phone }]" :data-test="`agent-${m.name}`" :aria-label="displayName(m.name)">
    <div class="title" data-test="agent-title">
      <MemberAvatar class="avatar" :team="m.team" :member="m.name" :state="face" :size="26" :title="MemberStateLabel[face]" />
      <strong data-test="agent-name" :title="m.name">{{ displayName(m.name) }}</strong>
      <span class="chip">{{ m.role }}</span>
      <span class="chip" :class="`cli-${m.cli}`" data-test="agent-cli">{{ m.cli }}</span>
      <!-- Only a marker: what is loosened is listed on the agent's settings, Policy tab (and its Guardrails tab), which it opens. -->
      <button v-if="card.loosened?.length" class="chip loosened" data-test="loosened-chip" :title="`Loosened beyond the global policy:\n${card.loosened.join('\n')}`"
        @click="emit('policy')">⚠ loosened</button>
      <span class="label" data-test="agent-face-state">{{ MemberStateLabel[face] }}</span>
      <span v-if="moreWaiting" class="more" data-test="agent-more-waiting" :title="moreWaitingTitle">· {{ moreWaiting }} more waiting</span>
    </div>

    <div class="toolbar" role="toolbar" :aria-label="`${displayName(m.name)}: actions`" data-test="member-toolbar">
      <template v-if="!phone">
        <button class="icon" title="Open interactive CLI" data-test="open-interactive-cli" :disabled="!canOpen || opening" :aria-busy="opening" @click="openInteractive"><BusyLabel :busy="opening">&gt;_</BusyLabel></button>
        <!-- The read-only Code browser of the member's clone: only when the member has one. -->
        <button v-if="card.hasClone" class="icon code" title="Code" data-test="open-code" @click="emit('code')">&lt;/&gt;</button>
        <button class="icon" title="Settings" data-test="open-settings" @click="emit('settings')">⚙</button>
        <button class="icon" title="Logs: all of this agent's activity" data-test="open-logs" @click="emit('logs')"><Icon name="logs" :size="14" /></button>
      </template>
      <button class="icon" :title="pending === 'stop' ? 'Stopping…' : 'Stop'" data-test="stop" :disabled="!canStop || !!pending" :aria-busy="pending === 'stop'" @click="emit('stop')"><BusyLabel :busy="pending === 'stop'">■</BusyLabel></button>
      <button v-if="!phone && m.role === 'member'" class="icon danger" :title="pending === 'remove' ? 'Removing…' : 'Remove agent'" data-test="remove-agent" :disabled="card.state === 'running' || !!pending" :aria-busy="pending === 'remove'" @click="emit('remove')"><BusyLabel :busy="pending === 'remove'">🗑</BusyLabel></button>
      <span class="spacer" />
      <span v-if="error" class="error small" data-test="member-pane-error">{{ error }}</span>
      <span class="seg" role="tablist" :aria-label="`${displayName(m.name)}: show`" data-test="member-view-toggle">
        <button role="tab" :class="{ on: view === 'activity' }" :aria-selected="view === 'activity'" data-test="member-view-activity" @click="show('activity')">Activity</button>
        <button role="tab" :class="{ on: view === 'terminal' }" :aria-selected="view === 'terminal'" data-test="member-view-terminal" @click="show('terminal')">Terminal</button>
      </span>
      <span v-if="phone" class="more-wrap">
        <button class="icon" title="More" aria-label="More member actions" aria-haspopup="menu" :aria-expanded="moreOpen" data-test="member-more" @click="moreOpen = !moreOpen"><Icon name="more" :size="18" /></button>
        <span v-if="moreOpen" class="menu" role="menu" data-test="member-more-menu" @click="moreOpen = false">
          <button role="menuitem" data-test="open-interactive-cli" :disabled="!canOpen || opening" @click="openInteractive">&gt;_ Open interactive CLI</button>
          <button v-if="card.hasClone" role="menuitem" data-test="open-code" @click="emit('code')">&lt;/&gt; Code</button>
          <button role="menuitem" data-test="open-settings" @click="emit('settings')">⚙ Settings</button>
          <button role="menuitem" data-test="open-logs" @click="emit('logs')"><Icon name="logs" :size="14" /> Logs</button>
          <button v-if="m.role === 'member'" role="menuitem" class="danger" data-test="remove-agent" :disabled="card.state === 'running' || !!pending" @click="emit('remove')">🗑 Remove agent</button>
        </span>
      </span>
    </div>

    <div class="body" :style="phone ? undefined : { height: `${height}px` }" data-test="member-pane-body">
      <div class="said" data-test="member-said">
        <div v-if="hasDetails" class="state" data-test="agent-state">
          <span v-if="report.label" class="kind" :class="report.kind" data-test="agent-report-kind">{{ report.label }}</span>
          <!-- The network / timeout summary is in the member's settings (Policy tab); only what needs action stays here. -->
          <template v-if="stateLine">{{ stateLine }}</template>
          <template v-if="stateLine && lease"> · </template>
          <span v-if="lease" class="lease" :class="card.lease" data-test="agent-lease">{{ lease }}</span>
          <span v-if="card.policyProblems?.length" class="error" data-test="agent-policy-problems"><template v-if="stateLine || lease"> · </template>policy: {{ card.policyProblems.join('; ') }}</span>
        </div>
        <!-- Agent-written markdown: renderMarkdown never lets raw HTML through. -->
        <div v-if="said" class="output md scrolls" data-test="agent-output" v-html="saidHtml" />
        <div v-else class="output md scrolls" data-test="agent-output">
          <p class="muted" data-test="agent-output-empty">{{ emptyText }}</p>
        </div>
      </div>
      <!-- The bar in the gap to the left of the right area sets its width for every member together. -->
      <div class="side" :class="`side-${view}`" :style="phone ? undefined : { flexBasis: `${activityWidth}px` }" data-test="member-side">
        <TileGrip v-if="!phone" class="activity-grip" axis="w" reverse :store="memberActivityStore" />
        <MemberActivity v-if="view === 'activity'" class="member-activity" :rows="rows" :loaded="loaded" :now="now" :limit="activityLimit" />
        <!-- Mounted only while shown: a hidden terminal does not poll. -->
        <div v-else class="member-terminal" :class="{ full: termFull }" data-test="member-terminal">
          <header v-if="termFull" class="term-head" data-test="terminal-pane-head">
            <span class="term-title" data-test="terminal-pane-title">{{ displayName(m.name) }} · {{ m.team }}</span>
            <!-- The terminal's Formatted / Raw tabs come here (TerminalScreen), its bar into the ⋯ menu (TerminalApp). -->
            <span :id="headerSlots.tabs" class="tabs-slot" data-test="terminal-pane-tabs"></span>
            <button class="term-mini" :class="{ on: termMoreOpen }" aria-label="More" :aria-expanded="termMoreOpen" data-test="terminal-pane-more"
              @click="termMoreOpen = !termMoreOpen">⋯</button>
            <div v-show="termMoreOpen" :id="headerSlots.more" class="term-menu" data-test="terminal-pane-menu" @click="termMoreOpen = false"></div>
            <button class="term-mini" title="Close the terminal (back to Activity)" data-test="terminal-pane-close" @click="show('activity')">✕</button>
          </header>
          <TerminalApp ref="term" :key="m.id" embedded in-pane :member-id="m.id" :api="api" :poll-ms="pollMs" @live="termLive = $event" />
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.pane { width: 100%; box-sizing: border-box; background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 6px; padding: 4px 10px 4px; }
.title { display: flex; align-items: center; gap: 8px; height: 30px; overflow: hidden; white-space: nowrap; }
.title strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; }
.toolbar { display: flex; align-items: center; gap: 4px; height: 30px; margin-bottom: 2px; border-top: 1px solid var(--line-soft); }
.toolbar .icon { height: 26px; min-width: 28px; padding: 0 6px; }
.spacer { flex: 1; }
.icon.code { font-family: ui-monospace, Consolas, monospace; font-size: 12px; letter-spacing: -.04em; }
.seg { display: inline-flex; flex: none; border: 1px solid var(--line); border-radius: 999px; overflow: hidden; }
.seg button { border: 0; border-radius: 0; background: transparent; padding: 2px 10px; height: 24px; font-size: 12px; color: var(--ink-soft); }
.seg button.on { background: var(--chip); color: var(--ink); font-weight: 600; }
.label { font-weight: 600; font-size: 12px; color: var(--card-state); }
.more { font-size: 12px; color: var(--muted); }
.body { display: flex; gap: 14px; align-items: stretch; min-height: 0; }
.said { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 2px; min-height: 0; }
.state { flex: none; font-size: 12.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.lease { font-weight: 600; color: var(--state-running, inherit); }
.lease.waiting { color: var(--state-waiting, inherit); }
.output { flex: 1 1 0; min-height: 0; padding: 8px 10px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface-2); overflow-y: auto; overflow-x: hidden;
  font-size: 13px; line-height: 1.55; overflow-wrap: anywhere; }
.output :deep(p) { margin: 0 0 6px; }
/* The report's first paragraph is its summary. */
.output > :deep(p:first-child) { font-weight: 600; }
.output :deep(p:last-child) { margin-bottom: 0; }
.output :deep(ul), .output :deep(ol) { margin: 0 0 6px; padding-left: 20px; }
.output :deep(li) { margin: 1px 0; }
.output :deep(code) { background: var(--chip); padding: 0 4px; border-radius: 4px; font-size: 12px; }
.output :deep(pre) { background: var(--code-block-bg); color: var(--code-block-ink); padding: 8px 10px; border-radius: 6px; overflow: auto; margin: 0 0 6px; }
.output :deep(pre code) { background: transparent; padding: 0; }
.output :deep(h1), .output :deep(h2), .output :deep(h3) { font-size: 13px; margin: 6px 0 4px; }
.kind { display: inline-block; font-size: 11px; font-weight: 600; line-height: 1; padding: 3px 7px; margin-right: 6px; border-radius: 999px;
  --k: var(--state-idle); color: var(--k); background: color-mix(in srgb, var(--k) 12%, transparent); }
.kind.handback { --k: var(--state-waiting); }
.kind.complete { --k: var(--ok); }
.kind.blocked { --k: var(--state-blocked); }
.kind.decision { --k: var(--state-decision); }
.side { position: relative; display: flex; flex-grow: 0; flex-shrink: 1; min-width: 0; min-height: 0; }
.side > .member-activity { flex: 1; min-width: 0; min-height: 0; }
.member-terminal { flex: 1; min-width: 0; min-height: 0; display: flex; flex-direction: column; border: 1px solid var(--term-line, var(--line)); border-radius: 6px; overflow: hidden; }
.member-terminal > .term-window { flex: 1; min-height: 0; height: auto; }
/* The width bar fills the 14px gap between what was said and the right area (TileGrip's gap handles sit outside what they size). */
.side > .activity-grip { left: -14px; right: auto; }
.error { color: var(--bad); }
.small { font-size: 12px; }
.muted { color: var(--muted); }
.pane { --card-state: var(--state-idle); }
.is-running { --card-state: var(--state-running); border-color: color-mix(in srgb, var(--state-running) 55%, var(--line)); border-left-color: var(--state-running);
  animation: member-breathe 2.4s ease-in-out infinite; }
/* A running member breathes, like a running team's card; idle and the other states stay still. */
@keyframes member-breathe {
  0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--state-running) 0%, transparent); }
  50% { box-shadow: 0 0 0 4px color-mix(in srgb, var(--state-running) 22%, transparent); }
}
@media (prefers-reduced-motion: reduce) { .is-running { animation: none; box-shadow: 0 0 0 2px color-mix(in srgb, var(--state-running) 22%, transparent); } }
.is-waiting { --card-state: var(--state-waiting); border-left-color: var(--state-waiting); }
.is-decision { --card-state: var(--state-decision); border-left-color: var(--state-decision); }
.is-blocked { --card-state: var(--state-blocked); border-left-color: var(--state-blocked); }
.is-failed { --card-state: var(--state-failed); border-left-color: var(--state-failed); }
/* A narrow window: what was said over the right area, each at its own height. */
@media (max-width: 900px) {
  .body { flex-direction: column; height: auto !important; }
  .said { flex: none; height: 9rem; }
  .side { flex-basis: auto !important; height: 12rem; }
  .side.side-terminal { height: 22rem; }
  .side > .activity-grip { display: none; }
}
/* A phone (B061): stacked - the title, the toolbar (Stop, the toggle and ⋯ as 44 px targets), what was said at its own
   height, then the Activity (its newest lines) or the terminal, full width. The terminal's transcript gets at least 60%
   of the visible height: the area is 60dvh plus its two 44 px rows (the terminal's bar and its Formatted / Raw tabs). */
@media (max-width: 759.98px) {
  .pane { padding: 4px 8px 8px; }
  .title { height: 36px; }
  .toolbar { height: 48px; gap: 6px; }
  .toolbar .icon { height: 44px; min-width: 44px; font-size: 16px; }
  .seg button { height: 44px; min-width: 44px; padding: 0 12px; font-size: 14px; }
  .body { flex-direction: column; gap: 8px; height: auto; }
  .said { flex: none; height: auto; }
  .state { white-space: normal; }
  /* What the agent said at its own height: the page is the one scroller. */
  .output { flex: none; height: auto; overflow-y: visible; }
  .side { flex-basis: auto; height: auto; }
  /* Card 307 replaces the 60dvh terminal area said above: the terminal is the whole screen over the page (below), so
     its area in the stack takes no height. (The comment above stays as it was: it is part of the desktop CSS hash.) */
  .side.side-terminal { height: auto; }
  .side > .activity-grip { display: none; }
  /* The member's terminal (B057, card 307): the whole screen, one 44 px header row, the terminal filling the rest and
     never past the bottom (dvh: what the browser really shows). */
  .member-terminal.full { position: fixed; inset: 0; z-index: 9; width: auto; min-width: 0; max-width: none; height: 100dvh;
    border: 0; border-radius: 0; overflow: hidden; background: var(--term-bg); }
  .term-head { position: relative; display: flex; align-items: center; flex: none; height: 44px; box-sizing: content-box; gap: 2px;
    padding: env(safe-area-inset-top, 0px) calc(2px + env(safe-area-inset-right, 0px)) 0 calc(10px + env(safe-area-inset-left, 0px));
    background: var(--term-head); color: var(--term-fg); border-bottom: 1px solid var(--term-line); }
  .term-title { flex: 1; min-width: 0; font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tabs-slot { display: flex; flex: none; }
  .term-head > .term-mini { min-width: 44px; height: 44px; flex: none; padding: 0 8px; border: 1px solid transparent; border-radius: 6px;
    background: transparent; color: var(--term-btn-ink); font-size: 16px; }
  .term-head > .term-mini.on { background: var(--term-btn-on); }
  .term-menu { position: absolute; right: 4px; top: calc(100% + 2px); z-index: 3; display: flex; flex-direction: column; align-items: stretch; gap: 4px;
    max-width: min(320px, calc(100vw - 8px)); padding: 6px; background: var(--term-head); border: 1px solid var(--term-line); border-radius: 8px; box-shadow: 0 6px 20px var(--shadow); }
  .member-terminal.full > .term-window { flex: 1 1 0; min-height: 0; overflow: hidden; }
  .more-wrap { position: relative; display: inline-flex; }
  .menu { position: absolute; right: 0; top: 100%; z-index: 5; display: flex; flex-direction: column; min-width: 200px; padding: 4px;
    background: var(--surface); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 6px 18px var(--shadow); }
  .menu button { display: flex; align-items: center; gap: 6px; justify-content: flex-start; text-align: left; border: 0; background: transparent; padding: 0 10px; min-height: 44px; font-size: 14px; }
  .menu button:hover { background: var(--hover); }
}
</style>
