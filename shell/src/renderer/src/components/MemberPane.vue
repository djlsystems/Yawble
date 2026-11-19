<script setup lang="ts">
import { computed, onBeforeUnmount, provide, reactive, ref, useId, watch } from 'vue';
import { PaneHeaderKey } from '../terminalPane';
import { isStoppedRun, type AgentCard, type YawbleApi } from '../../../shared/api';
import type { ActivityRow } from '../monitor/model';
import { agentPillText, agentPillTitle } from '../memberModel';
import MemberAvatar from '../faces/MemberAvatar.vue';
import Icon from './Icon.vue';
import MemberActivity from './MemberActivity.vue';
import TerminalApp from '../TerminalApp.vue';
import { displayName, memberStateOf, MemberStateLabel } from '../faces/faces';
import { renderMarkdown } from '../markdown';
import { splitReport } from '../reportFormat';
import { leaseText } from '../heavyLease';
import { outputHeight } from '../agentOutputHeight';
import { errorText, resolveApi } from '../api';
import { getMemberView, MemberViews, setMemberView, type MemberView } from '../memberPaneView';
import { useNarrow } from '../narrowScreen';

// One member on the team page, in one frame: a title bar (face, name, role and CLI chips, state, "more waiting", and a
// ☰ menu with every action), a row of tabs - Report (what the member last said), Activity, Terminal - with the run's
// details beside them, and one view: the chosen tab's. The tab is per member and remembered (memberPaneView.ts); the
// terminal is mounted only while its tab shows, so only a shown terminal polls. The view's height is shared by every
// member (the gaps between members set it, TeamView).
// The terminal here is never interactive: it shows the member's runs (and mirrors a session opened elsewhere) with no
// input. Open interactive CLI opens the agent's own terminal window, where the session runs; the remote view has no
// such window, so it has no Open interactive CLI.
// A phone stacks it the same way; its terminal is the whole screen with one header row (B057).
/** pending: Stop or Remove was clicked and the core has not answered yet (that menu item is held). */
const props = withDefaults(defineProps<{
  card: AgentCard; pending?: 'stop' | 'remove' | null; api?: YawbleApi; pollMs?: number;
  /** The member's activity lines (the Monitor's), whether they have loaded, and the clock they are aged against. */
  rows?: readonly ActivityRow[]; loaded?: boolean; now?: number;
  /** A phone: how many activity lines show (the rest are in Logs). */
  activityLimit?: number;
}>(), { pending: null, rows: () => [], loaded: true, now: () => Date.now() });
const emit = defineEmits<{ code: []; settings: []; logs: []; stop: []; remove: [] }>();

const m = computed(() => props.card.member);
const face = computed(() => memberStateOf(props.card));
const phone = useNarrow();
// The details after the state's own word (the label says Running, Needs you, Idle...); what the agent said is the Report.
const stateLine = computed(() => {
  const c = props.card;
  if (c.attention === 'decision') return 'a question is waiting for your answer';
  if (c.state === 'queued') return `position ${c.queuePosition} of ${c.queueLength} for a global run slot · ${c.queuedInstructions} instruction${c.queuedInstructions === 1 ? '' : 's'}`;
  if (c.state === 'running') return '';
  // B097: a run the person stopped (class "stopped", recorded failed) is no failure: it says who stopped it.
  if (c.lastRunState !== 'completed' && c.lastRunState !== 'interrupted' && isStoppedRun({ state: c.lastRunState, failureClass: c.lastRunFailureClass, attention: c.attention })) return 'last run stopped by you';
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

// Report, Activity or Terminal, per member (shared with the entry points: the Monitor, the palette, the remote view).
// A phone (B064, card 310) has its own choice: one member's terminal at a time, none when the page opens (memberPaneView.ts).
const view = computed<MemberView>(() => getMemberView(m.value.team, m.value.name, phone.value));
const show = (v: MemberView) => setMemberView(m.value.team, m.value.name, v, phone.value);
const TabLabel: Record<MemberView, string> = { report: 'Report', activity: 'Activity', terminal: 'Terminal' };

// A session opened in the agent's terminal window shows here too (read only): it holds Open and allows Stop.
const termLive = ref(false);
watch(view, (v) => { if (v !== 'terminal') termLive.value = false; });
const remote = computed(() => !!resolveApi(props.api)?.remote);
const canOpen = computed(() => props.card.state === 'idle' && !termLive.value);
const canStop = computed(() => props.card.state === 'running' || termLive.value);
const opening = ref(false);
const error = ref<string | null>(null);
/** Open interactive CLI: the agent's own terminal window, where the person starts and types into the session. */
async function openInteractive() {
  if (opening.value) return;
  error.value = null;
  opening.value = true;
  try {
    await resolveApi(props.api).openTerminal(m.value.id);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    opening.value = false;
  }
}

/** The ☰ menu: every action. Closes on a pick, a click outside it or Esc. */
const menuOpen = ref(false);
const menuWrap = ref<HTMLElement | null>(null);
function onDocDown(e: Event) { if (menuWrap.value && !menuWrap.value.contains(e.target as Node)) menuOpen.value = false; }
function onKey(e: KeyboardEvent) { if (e.key === 'Escape') menuOpen.value = false; }
watch(menuOpen, (open) => {
  if (open) { document.addEventListener('pointerdown', onDocDown, true); document.addEventListener('keydown', onKey); }
  else { document.removeEventListener('pointerdown', onDocDown, true); document.removeEventListener('keydown', onKey); }
});
onBeforeUnmount(() => { document.removeEventListener('pointerdown', onDocDown, true); document.removeEventListener('keydown', onKey); });
const menuBtn = ref<HTMLButtonElement | null>(null);
// A pick hands the focus back to the ⋯ button before the menu (v-if) goes: the item clicked is removed from the page,
// and a confirm it opens (Remove agent, card 355) returns the focus to where it was, the ⋯ button, not <body>.
function pick(f: () => void) { menuBtn.value?.focus(); menuOpen.value = false; f(); }

// A phone (B057, card 307): the member's terminal is the whole screen over the page with one 44 px header row - the
// title, a ⋯ holding the terminal's state line and Stop, and ✕ (back to the Report) - and the terminal fills the rest.
// The terminal puts its bar there (PaneHeaderKey).
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
      <span class="chip" :class="`cli-${m.cli}`" data-test="agent-cli" :title="agentPillTitle(m.cli, card.agentModel)">{{ agentPillText(m.cli, card.agentModel) }}</span>
      <span class="label" data-test="agent-face-state">{{ MemberStateLabel[face] }}</span>
      <span v-if="moreWaiting" class="more" data-test="agent-more-waiting" :title="moreWaitingTitle">· {{ moreWaiting }} more waiting</span>
      <span class="spacer" />
      <span ref="menuWrap" class="menu-wrap">
        <button ref="menuBtn" class="icon menu-button" :class="{ on: menuOpen }" title="Actions" :aria-label="`${displayName(m.name)}: actions`" aria-haspopup="menu" :aria-expanded="menuOpen"
          data-test="member-more" @click="menuOpen = !menuOpen"><Icon name="menu" :size="16" /></button>
        <span v-if="menuOpen" class="menu" role="menu" data-test="member-more-menu">
          <button v-if="!remote" role="menuitem" data-test="open-interactive-cli" :disabled="!canOpen || opening" :aria-busy="opening"
            title="Opens this agent's terminal window, where the interactive CLI runs" @click="pick(openInteractive)"><span class="mi">&gt;_</span> Open interactive CLI</button>
          <!-- The read-only Code browser of the member's clone: only when the member has one. -->
          <button v-if="card.hasClone" role="menuitem" data-test="open-code" @click="pick(() => emit('code'))"><span class="mi mono">&lt;/&gt;</span> Code</button>
          <button role="menuitem" data-test="open-settings" @click="pick(() => emit('settings'))"><span class="mi">⚙</span> Settings</button>
          <button role="menuitem" data-test="open-logs" @click="pick(() => emit('logs'))"><span class="mi"><Icon name="logs" :size="14" /></span> Logs: all of this agent's activity</button>
          <button role="menuitem" data-test="stop" :disabled="!canStop || !!pending" :aria-busy="pending === 'stop'" @click="pick(() => emit('stop'))">
            <span class="mi">■</span> {{ pending === 'stop' ? 'Stopping…' : 'Stop' }}</button>
          <button v-if="m.role === 'member'" role="menuitem" class="danger" data-test="remove-agent" :disabled="card.state === 'running' || !!pending" :aria-busy="pending === 'remove'"
            @click="pick(() => emit('remove'))"><span class="mi">🗑</span> {{ pending === 'remove' ? 'Removing…' : 'Remove agent' }}</button>
        </span>
      </span>
    </div>

    <div class="tabs" data-test="member-tabs">
      <span class="tablist" role="tablist" :aria-label="`${displayName(m.name)}: show`" data-test="member-view-toggle">
        <button v-for="v in MemberViews" :key="v" role="tab" :class="{ on: view === v }" :aria-selected="view === v" :data-test="`member-view-${v}`"
          @click="show(v)">{{ TabLabel[v] }}</button>
      </span>
      <div v-if="hasDetails" class="state" data-test="agent-state">
        <span v-if="report.label" class="kind" :class="report.kind" data-test="agent-report-kind">{{ report.label }}</span>
        <!-- The network / timeout summary is in the member's settings (Policy tab); only what needs action stays here. -->
        <template v-if="stateLine">{{ stateLine }}</template>
        <template v-if="stateLine && lease"> · </template>
        <span v-if="lease" class="lease" :class="card.lease" data-test="agent-lease">{{ lease }}</span>
        <span v-if="card.policyProblems?.length" class="error" data-test="agent-policy-problems"><template v-if="stateLine || lease"> · </template>policy: {{ card.policyProblems.join('; ') }}</span>
      </div>
      <span v-if="error" class="error small" data-test="member-pane-error">{{ error }}</span>
    </div>

    <div class="body" :class="`body-${view}`" :style="phone ? undefined : { height: `${height}px` }" data-test="member-pane-body">
      <template v-if="view === 'report'">
        <!-- Agent-written markdown: renderMarkdown never lets raw HTML through. -->
        <div v-if="said" class="output md scrolls" data-test="agent-output" :data-commit-team="m.team" v-html="saidHtml" />
        <div v-else class="output md scrolls" data-test="agent-output">
          <p class="muted" data-test="agent-output-empty">{{ emptyText }}</p>
        </div>
      </template>
      <MemberActivity v-else-if="view === 'activity'" class="member-activity" :rows="rows" :loaded="loaded" :now="now" :limit="activityLimit" />
      <!-- Mounted only while shown: a hidden terminal does not poll. -->
      <div v-else class="member-terminal" :class="{ full: termFull }" data-test="member-terminal">
        <header v-if="termFull" class="term-head" data-test="terminal-pane-head">
          <span class="term-title" data-test="terminal-pane-title">{{ displayName(m.name) }} · {{ m.team }}</span>
          <!-- The terminal's bar (state line, Stop) goes into the ⋯ menu (TerminalApp). -->
          <button class="term-mini" :class="{ on: termMoreOpen }" aria-label="More" :aria-expanded="termMoreOpen" data-test="terminal-pane-more"
            @click="termMoreOpen = !termMoreOpen">⋯</button>
          <div v-show="termMoreOpen" :id="headerSlots.more" class="term-menu" data-test="terminal-pane-menu" @click="termMoreOpen = false"></div>
          <button class="term-mini" title="Close the terminal (back to the Report)" data-test="terminal-pane-close" @click="show('report')">✕</button>
        </header>
        <TerminalApp :key="m.id" embedded in-pane :member-id="m.id" :api="api" :poll-ms="pollMs" @live="termLive = $event" />
      </div>
    </div>
  </section>
</template>

<style scoped>
/* A tile in the team page's grid (B076, card 366): the pane fills its cell (the grid sets the height) as a column - the
   title bar and the tabs at their own height, the view taking the rest and scrolling inside it - and never wider than
   the cell: what the title bar holds shrinks and ellipsizes. Outside a grid (no set height) it is as tall as its rows. */
.pane { width: 100%; box-sizing: border-box; background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 6px; padding: 4px 10px 4px;
  display: flex; flex-direction: column; height: 100%; min-width: 0; min-height: 0; }
.title { display: flex; align-items: center; gap: 8px; height: 30px; white-space: nowrap; flex: none; min-width: 0; }
.title > .avatar { flex: none; }
.title strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; }
.title strong, .title .chip, .title .label, .title .more { flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spacer { flex: 1; }
.label { font-weight: 600; font-size: 12px; color: var(--card-state); }
.more { font-size: 12px; color: var(--muted); }
.menu-wrap { position: relative; display: inline-flex; flex: none; }
.menu-button { height: 28px; min-width: 30px; padding: 0 6px; display: inline-flex; align-items: center; justify-content: center; }
.menu-button.on { background: var(--chip); }
.menu { position: absolute; right: 0; top: calc(100% + 2px); z-index: 5; display: flex; flex-direction: column; min-width: 240px; padding: 4px;
  background: var(--surface); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 6px 18px var(--shadow); }
.menu button { display: flex; align-items: center; gap: 8px; justify-content: flex-start; text-align: left; border: 0; background: transparent;
  padding: 0 10px; min-height: 32px; font-size: 13px; white-space: nowrap; }
.menu button:hover:not(:disabled) { background: var(--hover); }
.menu button.danger { color: var(--bad); }
.mi { display: inline-flex; justify-content: center; width: 18px; flex: none; }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 11px; letter-spacing: -.04em; }
.tabs { display: flex; align-items: center; gap: 10px; min-height: 26px; margin-bottom: 2px; border-top: 1px solid var(--line-soft); padding-top: 2px;
  flex: none; flex-wrap: nowrap; white-space: nowrap; min-width: 0; }
.tabs > .error { flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.tablist { display: inline-flex; flex: none; border: 1px solid var(--line); border-radius: 999px; overflow: hidden; }
.tablist button { border: 0; border-radius: 0; background: transparent; padding: 2px 12px; height: 24px; font-size: 12px; color: var(--ink-soft); }
.tablist button.on { background: var(--chip); color: var(--ink); font-weight: 600; }
.state { flex: 1 1 auto; min-width: 0; font-size: 12.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.lease { font-weight: 600; color: var(--state-running, inherit); }
.lease.waiting { color: var(--state-waiting, inherit); }
.body { display: flex; flex-direction: column; min-height: 0; flex: 1 1 auto; min-width: 0; overflow: auto; }
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
.body > .member-activity { flex: 1; min-width: 0; min-height: 0; }
.member-terminal { flex: 1; min-width: 0; min-height: 0; display: flex; flex-direction: column; border: 1px solid var(--term-line, var(--line)); border-radius: 6px; overflow: hidden; }
.member-terminal > .term-window { flex: 1; min-height: 0; height: auto; }
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
.is-stopped { --card-state: var(--state-stopped); border-left-color: var(--state-stopped); }
/* A phone (B061): stacked - the title (☰ a 44 px target), the tabs (44 px) with the details under them, then the
   chosen view at its own height (the page is the one scroller); the terminal is the whole screen over the page. */
@media (max-width: 759.98px) {
  /* Not a tile: a block as tall as its rows, the view at its own height (the page scrolls). */
  .pane { padding: 4px 8px 8px; display: block; height: auto; }
  .pane > .body { overflow: visible; }
  .title { height: 44px; }
  .menu-button { height: 44px; min-width: 44px; }
  .menu button { min-height: 44px; font-size: 14px; }
  .tabs { flex-wrap: wrap; gap: 6px; }
  .tablist button { height: 44px; min-width: 44px; padding: 0 14px; font-size: 14px; }
  .state { flex-basis: 100%; white-space: normal; }
  .body { height: auto; }
  .output { flex: none; height: auto; overflow-y: visible; }
  .body-terminal { height: auto; }
  /* The member's terminal (B057, card 307): the whole screen, one 44 px header row, the terminal filling the rest and
     never past the bottom (dvh: what the browser really shows). */
  .member-terminal.full { position: fixed; inset: 0; z-index: 9; width: auto; min-width: 0; max-width: none; height: 100dvh;
    border: 0; border-radius: 0; overflow: hidden; background: var(--term-bg); }
  .term-head { position: relative; display: flex; align-items: center; flex: none; height: 44px; box-sizing: content-box; gap: 2px;
    padding: env(safe-area-inset-top, 0px) calc(2px + env(safe-area-inset-right, 0px)) 0 calc(10px + env(safe-area-inset-left, 0px));
    background: var(--term-head); color: var(--term-fg); border-bottom: 1px solid var(--term-line); }
  .term-title { flex: 1; min-width: 0; font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .term-head > .term-mini { min-width: 44px; height: 44px; flex: none; padding: 0 8px; border: 1px solid transparent; border-radius: 6px;
    background: transparent; color: var(--term-btn-ink); font-size: 16px; }
  .term-head > .term-mini.on { background: var(--term-btn-on); }
  .term-menu { position: absolute; right: 4px; top: calc(100% + 2px); z-index: 3; display: flex; flex-direction: column; align-items: stretch; gap: 4px;
    max-width: min(320px, calc(100vw - 8px)); padding: 6px; background: var(--term-head); border: 1px solid var(--term-line); border-radius: 8px; box-shadow: 0 6px 20px var(--shadow); }
  .member-terminal.full > .term-window { flex: 1 1 0; min-height: 0; overflow: hidden; }
}
</style>
