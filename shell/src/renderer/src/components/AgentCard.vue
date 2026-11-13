<script setup lang="ts">
import { computed } from 'vue';
import type { AgentCard } from '../../../shared/api';
import MemberAvatar from '../faces/MemberAvatar.vue';
import BusyLabel from './BusyLabel.vue';
import Icon from './Icon.vue';
import { displayName, memberStateOf, MemberStateLabel } from '../faces/faces';
import { renderMarkdown } from '../markdown';
import { splitReport } from '../reportFormat';
import { leaseText } from '../heavyLease';
import { outputHeight } from '../agentOutputHeight';

// One agent in the list. A title bar (face, name, chips, state,
// and the actions: >_ terminal, ⚙ settings, 🛡 guardrail check, ■ Stop, 🗑 remove), a details line, and what the agent
// last said as formatted text in a box of fixed height that scrolls inside. The gaps between cards drag the height
// of every member's box together (the gap handles under each card, see TeamView).
/** pending: Stop or Remove was clicked and the core has not answered yet (that icon shows the spinner, both are held). */
const props = defineProps<{ card: AgentCard; pending?: 'stop' | 'remove' | null }>();
const emit = defineEmits<{ terminal: []; code: []; settings: []; guardrail: []; logs: []; stop: []; remove: [] }>();

const m = computed(() => props.card.member);
const face = computed(() => memberStateOf(props.card));
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

// The output box's height is the shared agentOutputHeight, resized by the gap handles between member cards (TeamView).
const height = outputHeight;
</script>

<template>
  <div class="card" :class="[card.state, `is-${face}`]" :data-test="`agent-${m.name}`">
    <div class="title" data-test="agent-title">
      <MemberAvatar class="avatar" :team="m.team" :member="m.name" :state="face" :size="28" :title="MemberStateLabel[face]" />
      <strong data-test="agent-name" :title="m.name">{{ displayName(m.name) }}</strong>
      <span class="chip">{{ m.role }}</span>
      <span class="chip" :class="`cli-${m.cli}`" data-test="agent-cli">{{ m.cli }}</span>
      <span v-if="card.loosened?.length" class="chip loosened" data-test="loosened-chip" :title="card.loosened.join('\n')">
        loosened: {{ card.loosened.map((l) => l.replace(/^.*\+ /, '')).join(', ') }}</span>
      <span class="label" data-test="agent-face-state">{{ MemberStateLabel[face] }}</span>
      <span v-if="moreWaiting" class="more" data-test="agent-more-waiting" :title="moreWaitingTitle">· {{ moreWaiting }} more waiting</span>
      <span class="spacer" />
      <div class="actions">
        <button class="icon" title="Terminal" data-test="open-terminal" @click="emit('terminal')">&gt;_</button>
        <!-- The read-only Code browser of the member's clone: only when the member has one. -->
        <button v-if="card.hasClone" class="icon code" title="Code" data-test="open-code" @click="emit('code')">&lt;/&gt;</button>
        <button class="icon" title="Settings" data-test="open-settings" @click="emit('settings')">⚙</button>
        <button class="icon" title="Guardrail check" data-test="open-guardrail" @click="emit('guardrail')">🛡</button>
        <button class="icon" title="Logs: all of this agent's activity" data-test="open-logs" @click="emit('logs')"><Icon name="logs" :size="14" /></button>
        <button class="icon" :title="pending === 'stop' ? 'Stopping…' : 'Stop'" data-test="stop" :disabled="card.state !== 'running' || !!pending" :aria-busy="pending === 'stop'" @click="emit('stop')"><BusyLabel :busy="pending === 'stop'">■</BusyLabel></button>
        <button v-if="m.role === 'member'" class="icon danger" :title="pending === 'remove' ? 'Removing…' : 'Remove agent'" data-test="remove-agent" :disabled="card.state === 'running' || !!pending" :aria-busy="pending === 'remove'" @click="emit('remove')"><BusyLabel :busy="pending === 'remove'">🗑</BusyLabel></button>
      </div>
    </div>
    <div class="state" data-test="agent-state">
      <span v-if="report.label" class="kind" :class="report.kind" data-test="agent-report-kind">{{ report.label }}</span>
      <!-- The network / timeout summary is in the member's settings (Policy tab); only what needs action stays here. -->
      <template v-if="stateLine">{{ stateLine }}</template>
      <template v-if="stateLine && lease"> · </template>
      <span v-if="lease" class="lease" :class="card.lease" data-test="agent-lease">{{ lease }}</span>
      <span v-if="card.policyProblems?.length" class="error" data-test="agent-policy-problems"><template v-if="stateLine || lease"> · </template>policy: {{ card.policyProblems.join('; ') }}</span>
    </div>
    <!-- Agent-written markdown: renderMarkdown never lets raw HTML through. -->
    <div v-if="said" class="output md scrolls" :style="{ height: `${height}px` }" data-test="agent-output" v-html="saidHtml" />
    <!-- Nothing said: the same box at the shared height, so every member lines up. -->
    <div v-else class="output md scrolls" :style="{ height: `${height}px` }" data-test="agent-output">
      <p class="muted" data-test="agent-output-empty">{{ emptyText }}</p>
    </div>
  </div>
</template>

<style scoped>
.card { width: 100%; background: #fff; border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 6px; padding: 6px 12px 8px; }
.title { display: flex; align-items: center; gap: 8px; min-height: 32px; flex-wrap: wrap; }
.title strong { font-size: 14px; }
.spacer { flex: 1; }
.actions { display: flex; gap: 4px; align-items: center; }
.icon.code { font-family: ui-monospace, Consolas, monospace; font-size: 12px; letter-spacing: -.04em; }
.label { font-weight: 600; font-size: 12px; color: var(--card-state); }
.state { font-size: 12.5px; padding: 0 0 4px 36px; }
.more { font-size: 12px; color: var(--muted, #6b7280); }
.lease { font-weight: 600; color: var(--state-running, inherit); }
.lease.waiting { color: var(--state-waiting, inherit); }
.output { margin: 2px 0 0 36px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 6px; background: #fafbfc; overflow-y: auto; overflow-x: hidden;
  font-size: 13px; line-height: 1.5; overflow-wrap: anywhere; }
.output :deep(p) { margin: 0 0 6px; }
/* The report's first paragraph is its summary. */
.output > :deep(p:first-child) { font-weight: 600; }
.output { line-height: 1.55; }
.kind { display: inline-block; font-size: 11px; font-weight: 600; line-height: 1; padding: 3px 7px; margin-right: 6px; border-radius: 999px;
  --k: var(--state-idle); color: var(--k); background: color-mix(in srgb, var(--k) 12%, transparent); }
.kind.handback { --k: var(--state-waiting); }
.kind.complete { --k: var(--ok, #1d6b2f); }
.kind.blocked { --k: var(--state-blocked); }
.kind.decision { --k: var(--state-decision); }
.output :deep(p:last-child) { margin-bottom: 0; }
.output :deep(ul), .output :deep(ol) { margin: 0 0 6px; padding-left: 20px; }
.output :deep(li) { margin: 1px 0; }
.output :deep(code) { background: var(--chip); padding: 0 4px; border-radius: 4px; font-size: 12px; }
.output :deep(pre) { background: #1f2430; color: #e7e9ee; padding: 8px 10px; border-radius: 6px; overflow: auto; margin: 0 0 6px; }
.output :deep(pre code) { background: transparent; padding: 0; }
.output :deep(h1), .output :deep(h2), .output :deep(h3) { font-size: 13px; margin: 6px 0 4px; }
.card { --card-state: var(--state-idle); }
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
</style>
