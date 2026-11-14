<script setup lang="ts">
import TeamStateChip from './TeamStateChip.vue';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import TaskAskCard from './TaskAskCard.vue';
import type { AgentCard as Card, Decision, IntegrationAsk, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { type AgentCliId } from '../agentClis';
import { useAgentOptions } from '../agentOptions';
import AgentCard from './AgentCard.vue';
import MemberActivity from './MemberActivity.vue';
import { useTeamActivity } from '../monitor/teamActivity';
import LogsDialog from './LogsDialog.vue';
import TileGrip from './TileGrip.vue';
import { memberActivityStore, memberCardsStore } from '../gripStore';
import { activityWidth } from '../memberActivityWidth';
import Icon from './Icon.vue';
import TriggersDialog from './TriggersDialog.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import GitDialog from './GitDialog.vue';
import WorkflowsDialog from './WorkflowsDialog.vue';
import TeamManageDialog from './TeamManageDialog.vue';
import DocumentsDialog from './DocumentsDialog.vue';
import CodeDialog from './CodeDialog.vue';
import { closeCode, openCode } from '../codeDialog';
import TeamSyncBanner from './TeamSyncBanner.vue';
import { renderMarkdown } from '../markdown';
import { asksForApproval, questionMarkdown, questionOptions } from '../questionFormat';
import { useImageAttachments } from '../imageAttachments';
import { showMemberTerminal } from '../terminalPane';

// A team's agent list: the Tell Manager box, open needs_decision questions, the cards, and Add agent.
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ settings: [memberId: number]; guardrail: [memberId: number]; changed: []; deleted: []; discuss: [decision: Decision] }>();
const api = resolveApi(props.api);
/** The member whose activity the Logs dialog shows ("Team/Name"), opened from its card. */
const logsFor = ref<string | null>(null);
const agentOptions = useAgentOptions(api);
/** Each member's activity, beside its card: the Monitor's lines for that member. */
const activity = useTeamActivity(api, () => props.team.name, () => props.pollMs);

const cards = ref<Card[]>([]);
const decisions = ref<Decision[]>([]);
const asks = ref<IntegrationAsk[]>([]);
const declineNote = ref<Record<number, string>>({});
const answers = ref<Record<number, string>>({});
const tell = ref('');
const told = ref<string | null>(null);
const error = ref<string | null>(null);
/** The last refresh failed (cleared by the next one that works); action errors stay in `error`. */
const refreshError = ref<string | null>(null);
const showTriggers = ref(false);
const showGit = ref(false);
const showWorkflows = ref(false);
const showManage = ref(false);
const showDocs = ref(false);
const newAgent = ref({ name: '', cli: 'claude' as AgentCliId });
let timer: ReturnType<typeof setInterval> | null = null;

// The first answer for this team has arrived (later polls keep what is shown: no loading flash).
const loaded = ref(false);
async function refresh() {
  const id = props.team.id;
  try {
    const got = await Promise.all([
      api.call<Card[]>('teams.agents', { teamId: props.team.id }),
      api.call<Decision[]>('bus.decisions', { teamId: props.team.id }),
      api.call<IntegrationAsk[]>('integrations.asks', { teamId: props.team.id }).catch(() => [] as IntegrationAsk[]),
    ]);
    if (id !== props.team.id) return; // the person moved to another team meanwhile
    [cards.value, decisions.value, asks.value] = got;
    loaded.value = true;
    refreshError.value = null;
  } catch (e) {
    if (id !== props.team.id) return;
    refreshError.value = errorText(e);
  }
}

// The thing being worked on (e.g. "ask-12", "decision-204", "tell"): its buttons are held and the clicked one shows
// the shared spinner until the core answers (useBusy), since approving or answering can take a moment.
const busy = useBusy();
const busyFor = (key: string, work: () => Promise<unknown>, action = '') => () => busy.run(key, () => act(work), action);

async function act(work: () => Promise<unknown>) {
  error.value = null;
  try {
    await work();
    await refresh();
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
  }
}

const tellImages = useImageAttachments();
const tellPicker = ref<HTMLInputElement | null>(null);
const sendTell = busyFor('tell', async () => {
  const images = tellImages.payload();
  const r = await api.call<{ workflow: number }>('bus.tellManager', { teamId: props.team.id, text: tell.value, ...(images.length ? { images } : {}) });
  told.value = `Sent to the Manager · workflow ${r.workflow}${images.length ? ` · ${images.length} image${images.length === 1 ? '' : 's'}` : ''}`;
  tell.value = '';
  tellImages.clear();
});
const resume = busyFor('resume', () => api.call('teams.resume', { teamId: props.team.id }));
const answer = (d: Decision) => busyFor(`decision-${d.seq}`, async () => {
  await api.call('bus.answer', { teamId: props.team.id, seq: d.seq, text: answers.value[d.seq] ?? '' });
  delete answers.value[d.seq];
}, 'answer')();
// A question's quick answers: Approve, or Approve and remember it for the team (a standing approval the Manager is
// told in every run), or take it to the Concierge to talk it through first.
const remembering = ref<Record<number, string>>({});
const approveDecision = (d: Decision) => busyFor(`decision-${d.seq}`, () => api.call('bus.answer', { teamId: props.team.id, seq: d.seq, text: 'Approved.' }), 'approve')();
// A question that offers numbered options gets a button per option instead of Approve (an "Approved." to a choice
// says nothing about which one). The answer names the option and repeats it, so the agent cannot misread it.
const chooseOption = (d: Decision, o: { n: number; label: string; text: string }) => busyFor(`decision-${d.seq}`, () => api.call('bus.answer', {
  teamId: props.team.id, seq: d.seq,
  text: `Option ${o.n}: ${o.label}.${(answers.value[d.seq] ?? '').trim() ? ` ${answers.value[d.seq].trim()}` : ''}\n\n(You offered: ${o.text.replace(/\*\*/g, '')})`,
}).then(() => { delete answers.value[d.seq]; }), `option-${o.n}`)();
function startRemember(d: Decision) {
  const first = d.question.split(/(?<=[.?!])\s/)[0].replace(/^(do you |can you |may i |shall i )/i, '').replace(/\?$/, '');
  remembering.value[d.seq] = first.slice(0, 200);
}
const approveAlways = (d: Decision) => busyFor(`decision-${d.seq}`, async () => {
  const text = (remembering.value[d.seq] ?? '').trim();
  await api.call('teams.addApproval', { teamId: props.team.id, text });
  await api.call('bus.answer', {
    teamId: props.team.id, seq: d.seq,
    text: `Approved. I have also approved this for the team once and for all: "${text}". Do not ask me about it again; go ahead and tell me what you did.`,
  });
  delete remembering.value[d.seq];
}, 'remember')();
const rendered = (d: Decision) => renderMarkdown(questionMarkdown(d.question));
// An ask's remember choice: once, or always / ask (a choice saved on the row) scoped to this team or every team; declining may be "never".
type Scope = 'team' | 'all';
const askScope = ref<Record<number, Scope>>({});
const scopeOf = (a: IntegrationAsk): Scope => askScope.value[a.id] ?? 'team';
const approve = (a: IntegrationAsk, remember: 'once' | 'always' | 'ask', scope: Scope = 'team') => busyFor(`ask-${a.id}`, () => api.call('integrations.approve',
  remember === 'once' ? { id: a.id, remember } : { id: a.id, remember, scope }), remember)();
const decline = (a: IntegrationAsk, remember?: 'never', scope?: Scope) => busyFor(`ask-${a.id}`, () => api.call('integrations.decline',
  { id: a.id, note: declineNote.value[a.id] || undefined, ...(remember ? { remember, scope: scope ?? 'team' } : {}) }), remember ?? 'decline')();
const declineTask = (a: IntegrationAsk, note: string) => busyFor(`ask-${a.id}`, () => api.call('integrations.decline', { id: a.id, note: note || undefined }), 'decline')();
const addAgent = busyFor('add-agent', async () => {
  await api.call('members.add', { teamId: props.team.id, name: newAgent.value.name.trim(), cli: newAgent.value.cli });
  newAgent.value = { name: '', cli: 'claude' };
});
const stop = (c: Card) => busyFor(`member-${c.member.id}`, () => api.call('terminal.stop', { memberId: c.member.id }), 'stop')();
const remove = (c: Card) => {
  if (!window.confirm(`Remove ${c.member.name} from ${c.member.team}? Its queued instructions are dropped.`)) return;
  return busyFor(`member-${c.member.id}`, () => api.call('members.remove', { memberId: c.member.id }), 'remove')();
};
// The agent's terminal in the window's terminal pane (one at a time, replacing what it showed).
const openTerminal = (c: Card) => showMemberTerminal(c.member.id, `${c.member.name} · ${c.member.team}`);

onMounted(() => {
  void refresh();
  timer = setInterval(refresh, props.pollMs ?? 1000);
});
watch(() => props.team.id, () => {
  told.value = null; loaded.value = false; error.value = null; refreshError.value = null; cards.value = []; decisions.value = []; asks.value = [];
  void refresh();
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); closeCode(); });

/** Opens the Workflows dialog (the Monitor opens a team here when a workflow node is clicked). */
const workflowFocus = ref<number | null>(null);
/** Opens the team's workflows, with one of them selected when given (a workflow clicked on the Monitor). */
function openWorkflows(workflow?: number) { workflowFocus.value = workflow ?? null; showWorkflows.value = true; }

defineExpose({ refresh, openWorkflows });
</script>

<template>
  <section class="team">
    <header class="teamhead">
      <!-- One line: the name and three counts. The "·" separators stay in the text (what the title reads as, and what
           tests read) but are hidden: the counts show as chips. -->
      <div class="titleline" data-test="team-title">
        <h2>Team {{ team.name }}</h2>
        <TeamStateChip :team="team" class="page-state" data-test="team-page-state" />
        <span class="kpi" data-test="kpi-agents"><span class="sep"> · </span><b>{{ loaded ? cards.length : '…' }}</b> agents</span>
        <span class="kpi" :class="{ live: team.running > 0 }" data-test="kpi-running"><span class="sep"> · </span><b>{{ team.running }}</b> running</span>
        <span class="kpi" data-test="kpi-queued"><span class="sep"> · </span><b>{{ team.queued }}</b> queued</span>
      </div>
      <div class="actions">
        <button data-test="open-documents" title="What the team writes for you, and what you add" @click="showDocs = true">Documents</button>
        <button data-test="open-workflows" title="The team's pieces of work: nudge, stop or close one" @click="showWorkflows = true">Workflows</button>
        <button data-test="open-triggers" title="Work on a schedule" @click="showTriggers = true">⏰ Triggers</button>
        <button data-test="open-git" title="The team's branches, merge to main" @click="showGit = true">⎇ Git</button>
        <button data-test="open-manage" title="The team's repositories, pause and archive, environment, reset, delete" @click="showManage = true">
          <Icon name="settings" :size="14" /> Settings</button>
      </div>
    </header>
    <p v-if="team.archived" class="paused" data-test="team-archived">This team is archived: it does no work and takes no instructions. Unarchive it from Settings.</p>
    <p v-else-if="team.paused" class="paused" data-test="team-paused">
      This team is paused: what it is told waits and runs when you resume it.
      <button class="primary" data-test="team-resume-banner" :disabled="busy.is('resume')" :aria-busy="busy.is('resume')" @click="resume"><BusyLabel :busy="busy.is('resume')" text="Resuming…">Resume</BusyLabel></button>
    </p>
    <TeamSyncBanner :key="team.id" :api="api" :team="team" />
    <TriggersDialog v-if="showTriggers" :api="api" :team="team" :agents="cards" @close="showTriggers = false" />
    <GitDialog v-if="showGit" :api="api" :team="team" @close="showGit = false" />
    <WorkflowsDialog v-if="showWorkflows" :key="workflowFocus ?? 0" :api="api" :team="team" :poll-ms="pollMs" :focus="workflowFocus" @close="showWorkflows = false" />
    <DocumentsDialog v-if="showDocs" :api="api" :team="team" @close="showDocs = false" />
    <CodeDialog :api="api" />
    <TeamManageDialog v-if="showManage" :api="api" :team="team" @close="showManage = false" @changed="emit('changed')"
      @deleted="showManage = false; emit('deleted')" />
    <p v-if="error || refreshError" class="error" data-test="team-error">{{ error ?? refreshError }}</p>

    <div class="tell row">
      <input v-model="tell" class="grow" placeholder="Tell the Manager… (paste an image to attach it)" data-test="tell-text"
        @keydown.enter="(tell.trim() || tellImages.images.value.length) && sendTell()" @paste="tellImages.onPaste" />
      <button title="Attach an image" data-test="tell-attach" @click="tellPicker?.click()">🖼</button>
      <input ref="tellPicker" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden data-test="tell-attach-input"
        @change="tellImages.add(($event.target as HTMLInputElement).files ?? []); ($event.target as HTMLInputElement).value = ''" />
      <button class="primary" data-test="tell-send" :disabled="(!tell.trim() && !tellImages.images.value.length) || busy.is('tell')" :aria-busy="busy.is('tell')" @click="sendTell"><BusyLabel :busy="busy.is('tell')" text="Sending…">Tell Manager ✉</BusyLabel></button>
    </div>
    <div v-if="tellImages.images.value.length" class="thumbs" data-test="tell-images">
      <span v-for="(im, i) in tellImages.images.value" :key="i" class="thumb">
        <img :src="im.url" :alt="im.name" :title="im.name" />
        <button class="x" title="Remove" @click="tellImages.remove(i)">✕</button>
      </span>
      <span class="muted small">Saved to the team's documents (attachments/) so every member can open them.</span>
    </div>
    <p v-if="tellImages.problem.value" class="error small">{{ tellImages.problem.value }}</p>
    <p v-if="told" class="muted small" data-test="tell-sent">{{ told }}</p>

    <LoadingNote v-if="!loaded && !error && !refreshError" text="Loading the team…" />
    <TaskAskCard v-for="a in asks.filter((x) => x.task)" :key="`task-ask-${a.id}`" :ask="a" :busy="busy.is(`ask-${a.id}`)"
      :pending="busy.is(`ask-${a.id}`, 'once') ? 'once' : busy.is(`ask-${a.id}`, 'always') ? 'always' : busy.is(`ask-${a.id}`, 'decline') ? 'decline' : null"
      @once="approve(a, 'once')" @always="approve(a, 'always')" @decline="(n: string) => declineTask(a, n)" />
    <div v-for="a in asks.filter((x) => !x.task)" :key="`ask-${a.id}`" class="decision ask" data-test="integration-ask">
      <p><strong>{{ a.member.split('/')[1] }} asks to run</strong> <code>{{ a.command }}</code> <span class="muted">in {{ a.repo ?? 'the main repository' }} — {{ a.reason }}</span></p>
      <p v-if="a.action" class="muted small" data-test="ask-action">remembers as <code>{{ a.action }}</code></p>
      <div class="row">
        <input v-model="declineNote[a.id]" class="grow" placeholder="A note if you decline (optional)" />
        <button class="primary" data-test="ask-approve" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'once')"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'once')" text="Approving…">Approve once</BusyLabel></button>
        <button v-if="a.canAlways !== false" data-test="ask-always" title="Approve, and let this run without asking from now on" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'always', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'always')" text="Approving…">Always allow</BusyLabel></button>
        <button data-test="ask-keep-asking" title="Approve this one and keep asking each time" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'ask', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'ask')" text="Approving…">Keep asking</BusyLabel></button>
        <button data-test="ask-decline" :disabled="busy.is(`ask-${a.id}`)" @click="decline(a)"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'decline')" text="Declining…">Decline</BusyLabel></button>
        <button data-test="ask-never" title="Decline, and never allow this" :disabled="busy.is(`ask-${a.id}`)" @click="decline(a, 'never', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'never')" text="Declining…">Never</BusyLabel></button>
        <select :value="scopeOf(a)" data-test="ask-scope" title="Applies to Always allow, Keep asking and Never" @change="askScope[a.id] = ($event.target as HTMLSelectElement).value as Scope">
          <option value="team">this team</option>
          <option value="all">every team</option>
        </select>
      </div>
    </div>
    <div v-for="d in decisions" :key="d.seq" class="decision" data-test="decision">
      <p><strong>{{ d.from }} needs a decision</strong></p>
      <div class="question md" data-test="decision-question" v-html="rendered(d)"></div>
      <div class="row quick">
        <template v-if="questionOptions(d.question).length">
          <button v-for="o in questionOptions(d.question)" :key="o.n" class="primary" :title="o.text" :data-test="`decision-option-${o.n}`"
            :disabled="busy.is(`decision-${d.seq}`)" @click="chooseOption(d, o)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, `option-${o.n}`)" text="Answering…">{{ o.n }}. {{ o.label }}</BusyLabel></button>
        </template>
        <template v-else-if="asksForApproval(d.question)">
          <button class="primary" data-test="decision-approve" :disabled="busy.is(`decision-${d.seq}`)" @click="approveDecision(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'approve')" text="Approving…">Approve</BusyLabel></button>
          <button data-test="decision-always" title="Approve, and let the team go ahead with this kind of thing from now on without asking" @click="startRemember(d)">Always approve for this team…</button>
        </template>
        <button data-test="decision-discuss" title="Talk it through with the Concierge first" @click="emit('discuss', d)">Discuss with the Concierge</button>
      </div>
      <div v-if="remembering[d.seq] !== undefined" class="row remember" data-test="decision-remember">
        <label class="grow">What the team may do from now on without asking
          <input v-model="remembering[d.seq]" maxlength="500" data-test="decision-remember-text" />
        </label>
        <button class="primary" data-test="decision-remember-save" :disabled="!(remembering[d.seq] ?? '').trim() || busy.is(`decision-${d.seq}`)" @click="approveAlways(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'remember')" text="Approving…">Approve and remember</BusyLabel></button>
        <button @click="delete remembering[d.seq]">Cancel</button>
      </div>
      <div class="row">
        <input v-model="answers[d.seq]" class="grow" placeholder="Or answer in your own words…" data-test="decision-answer" @keydown.enter="(answers[d.seq] ?? '').trim() && answer(d)" />
        <button data-test="decision-send" :disabled="!(answers[d.seq] ?? '').trim() || busy.is(`decision-${d.seq}`)" @click="answer(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'answer')" text="Answering…">Answer</BusyLabel></button>
      </div>
    </div>

    <!-- One column of members, each its card and its activity side by side: the 14px gap below each is a height handle for every member's output box (as on the Teams list). -->
    <div v-for="c in cards" :key="c.member.id" class="member-cell">
      <div class="member-row">
        <AgentCard class="member-main" :card="c"
          :pending="busy.is(`member-${c.member.id}`, 'stop') ? 'stop' : busy.is(`member-${c.member.id}`, 'remove') ? 'remove' : null"
          @terminal="openTerminal(c)" @code="openCode(c.member.team, c.member.name)" @settings="emit('settings', c.member.id)"
          @guardrail="emit('guardrail', c.member.id)" @logs="logsFor = `${c.member.team}/${c.member.name}`" @stop="stop(c)" @remove="remove(c)" />
        <!-- The bar in the gap to the left of the activity sets the width of every member's activity together. -->
        <div class="member-side" :style="{ flexBasis: `${activityWidth}px` }">
          <TileGrip class="activity-grip" axis="w" reverse :store="memberActivityStore" />
          <MemberActivity class="member-activity" :rows="activity.of(c.member.name)" :loaded="activity.loaded.value" :now="activity.now.value" />
        </div>
      </div>
      <TileGrip axis="h" :store="memberCardsStore" />
    </div>

    <div class="add row">
      <input v-model="newAgent.name" placeholder="Job and first name, e.g. Developer Priya" title="Named for the job, a space, then a first name. Its id is the name without the space (DeveloperPriya)." maxlength="60" data-test="add-agent-name" />
      <select v-model="newAgent.cli" data-test="add-agent-cli"><option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option></select>
      <button data-test="add-agent" :disabled="!newAgent.name.trim() || busy.is('add-agent')" :aria-busy="busy.is('add-agent')" @click="addAgent"><BusyLabel :busy="busy.is('add-agent')" text="Adding…">+ Add agent</BusyLabel></button>
    </div>
    <LogsDialog v-if="logsFor" :api="api" source="activity" :member="logsFor" @close="logsFor = null" />
  </section>
</template>

<style scoped>
h2 { font-size: 16px; margin: 0; }
.teamhead { display: flex; flex-direction: column; gap: 8px; }
.titleline { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.titleline h2 { font-size: 18px; margin: 0 6px 0 0; }
.kpi { font-size: 12.5px; color: var(--ink-soft); background: var(--chip); border-radius: 12px; padding: 2px 10px; white-space: nowrap; }
.kpi b { color: var(--ink); font-weight: 600; margin-right: 3px; }
.kpi.live { background: var(--ok-bg); color: var(--ok); }
.kpi.live b { color: var(--ok); }
/* In the text, not on screen: the title reads "Team X · 3 agents · 1 running · 0 queued". */
.sep { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
.actions { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.actions button { white-space: nowrap; display: inline-flex; align-items: center; gap: 5px; }
.team > * { margin-bottom: 10px; }
.team > .member-cell { position: relative; margin-bottom: 14px; }
.member-row { display: flex; gap: 14px; align-items: stretch; }
.member-main { flex: 1 1 0; min-width: 0; }
.member-side { position: relative; display: flex; flex-grow: 0; flex-shrink: 1; min-width: 0; }
.member-activity { flex: 1; min-width: 0; }
/* The width bar fills the 14px gap between the card and the activity (TileGrip's gap handles sit outside what they size). */
.member-side > .activity-grip { left: -14px; right: auto; }
@media (max-width: 900px) {
  .member-row { flex-direction: column; }
  .member-side { flex-basis: auto !important; height: 12rem; }
  .member-side > .activity-grip { display: none; }
}
.decision { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 6px; padding: 6px 10px; }
.decision p { margin: 0 0 6px; }
.decision > .row { margin-top: 6px; }
.question { font-size: 13.5px; background: var(--surface); border: 1px solid var(--warn-line-soft); border-radius: 6px; padding: 4px 10px; overflow-x: auto; }
.question :deep(table) { border-collapse: collapse; width: 100%; margin: 6px 0; font-size: 13px; }
.question :deep(th), .question :deep(td) { border: 1px solid var(--line-soft); padding: 4px 8px; text-align: left; vertical-align: top; }
.question :deep(th) { background: var(--warn-bg-soft); font-weight: 600; }
.question :deep(td:first-child), .question :deep(th:first-child) { width: 28px; color: var(--muted); }
.quick { gap: 6px; flex-wrap: wrap; }
.thumbs { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
.thumb { position: relative; width: 56px; height: 56px; border-radius: 8px; overflow: hidden; border: 1px solid var(--line); }
.thumb img { width: 100%; height: 100%; object-fit: cover; }
.thumb .x { position: absolute; top: 2px; right: 2px; width: 18px; height: 18px; padding: 0; border: 0; border-radius: 50%; background: var(--scrim); color: var(--on-scrim); font-size: 10px; line-height: 18px; }
.error { color: var(--bad); }
.remember label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.remember input { font: inherit; font-size: 13px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 6px; }
.decision.ask code { background: var(--code-bg); padding: 1px 5px; border-radius: 4px; }
.muted { color: var(--muted); }
.add { margin-top: 14px; }
.paused { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 6px; padding: 6px 10px; display: flex; align-items: center; gap: 10px; justify-content: space-between; margin: 0 0 10px; }
.small { font-size: 12px; }
</style>
