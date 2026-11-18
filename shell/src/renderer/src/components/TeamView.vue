<script setup lang="ts">
import { vChange } from '../remoteAccess';
import TeamStateChip from './TeamStateChip.vue';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { confirmDialog } from '../confirm';
import TaskAskCard from './TaskAskCard.vue';
import type { AgentCard as Card, Decision, IntegrationAsk, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { type AgentCliId } from '../agentClis';
import { useAgentOptions } from '../agentOptions';
import MemberPane from './MemberPane.vue';
import { useTeamActivity } from '../monitor/teamActivity';
import LogsDialog from './LogsDialog.vue';
import TileGrip from './TileGrip.vue';
import { memberAvailableWidth, memberRenderedWidth, memberTileSize, memberTilesStore } from '../memberTileSize';
import { closePhoneTerminals, memberFocus, takeMemberFocus } from '../memberPaneView';
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
import { useNarrow } from '../narrowScreen';

// A team's agent list: the Tell Manager box, open needs_decision questions, the cards, and Add agent.
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ settings: [memberId: number]; changed: []; deleted: []; discuss: [decision: Decision] }>();
const api = resolveApi(props.api);
/** The member whose activity the Logs dialog shows ("Team/Name"), opened from its card. */
const logsFor = ref<string | null>(null);
const agentOptions = useAgentOptions(api);
/** Each member's activity, beside its card: the Monitor's lines for that member. */
const activity = useTeamActivity(api, () => props.team.name, () => props.pollMs);
/** A phone (B037): the members stacked, each one's Activity under its card, no resize handles (nothing to drag). */
const phone = useNarrow();
/** On a phone (B054): the actions behind ⋯ (open or not), the Tell box's short placeholder (it fits at 375 px), and how
 * many of a member's latest activity lines show (the rest are in its Logs): the page is the one scroller. */
const moreOpen = ref(false);
const TellShort = 'Tell the Manager…';
const PhoneActivityRows = 5;

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
// The "New member" tile (B076), last in the grid: its + opens the add form in the tile; Add closes it once the member is
// there (a refused add keeps the form and what was typed, the error shows above), Cancel or Escape closes it.
const adding = ref(false);
const addName = ref<HTMLInputElement | null>(null);
async function openAdd() {
  adding.value = true;
  await nextTick();
  addName.value?.focus();
}
function cancelAdd() {
  adding.value = false;
  newAgent.value = { name: '', cli: 'claude' };
}
const addAgent = busyFor('add-agent', async () => {
  await api.call('members.add', { teamId: props.team.id, name: newAgent.value.name.trim(), cli: newAgent.value.cli });
  newAgent.value = { name: '', cli: 'claude' };
  adding.value = false;
});
const canAdd = computed(() => !!newAgent.value.name.trim() && !busy.is('add-agent'));

// The member grid (B076): equal tiles that wrap, the shared size (memberTileSize.ts) as --tile-w / --tile-h, never
// wider than the grid (measured, so a drag starts from the width the tiles really have).
const grid = ref<HTMLElement | null>(null);
const gridStyle = computed(() => ({ '--tile-w': `${memberRenderedWidth.value}px`, '--tile-h': `${memberTileSize.value.h}px` }));
function measureGrid() {
  const w = grid.value?.clientWidth;
  memberAvailableWidth.value = w && w > 0 ? w : Infinity;
}
let gridObserver: ResizeObserver | null = null;
watch(grid, (el) => {
  gridObserver?.disconnect();
  gridObserver = null;
  measureGrid();
  if (el && typeof ResizeObserver !== 'undefined') { gridObserver = new ResizeObserver(measureGrid); gridObserver.observe(el); }
});
const stop = (c: Card) => busyFor(`member-${c.member.id}`, () => api.call('terminal.stop', { memberId: c.member.id }), 'stop')();
const remove = async (c: Card) => {
  if (!(await confirmDialog({ message: `Remove ${c.member.name} from ${c.member.team}? Its queued instructions are dropped.`, okLabel: 'Remove', danger: true }))) return;
  return busyFor(`member-${c.member.id}`, () => api.call('members.remove', { memberId: c.member.id }), 'remove')();
};
// The members' panes by name, so a focus request (focusMemberTerminal: the Monitor, the palette, the remote view) can
// scroll one into view. The request waits until this team is shown and the member's pane is there.
const panes = new Map<string, InstanceType<typeof MemberPane>>();
function setPaneRef(name: string, p: unknown) {
  if (p) panes.set(name, p as InstanceType<typeof MemberPane>);
  else panes.delete(name);
}
watch([() => memberFocus(), () => props.team.name, () => cards.value.map((c) => c.member.name).join('\n')], async ([f]) => {
  if (!f || f.team !== props.team.name || !cards.value.some((c) => c.member.name === f.member)) return;
  takeMemberFocus(props.team.name);
  await nextTick();
  panes.get(f.member)?.el()?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
}, { immediate: true });

onMounted(() => {
  void refresh();
  timer = setInterval(refresh, props.pollMs ?? 1000);
});
watch(() => props.team.id, () => {
  cancelAdd();
  told.value = null; loaded.value = false; error.value = null; refreshError.value = null; cards.value = []; decisions.value = []; asks.value = [];
  void refresh();
});
// A phone (B064): the page opens with every member on Activity, so leaving it (or its team) closes that team's terminal.
watch(() => props.team.name, (_now, before) => closePhoneTerminals(before));
onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
  closeCode();
  closePhoneTerminals(props.team.name);
  gridObserver?.disconnect();
  memberAvailableWidth.value = Infinity;
});

/** Opens the Workflows dialog (the Monitor opens a team here when a workflow node is clicked). */
const workflowFocus = ref<number | null>(null);
/** Opens the team's workflows, with one of them selected when given (a workflow clicked on the Monitor). */
function openWorkflows(workflow?: number) { workflowFocus.value = workflow ?? null; showWorkflows.value = true; }

/** Opens the team's documents (on a phone the drawer's Documents item does, card 316: the header has no button for it). */
function openDocuments() { showDocs.value = true; }

defineExpose({ refresh, openWorkflows, openDocuments });
</script>

<template>
  <section class="team" :class="{ phone }">
    <header class="teamhead">
      <!-- One line: the name and three counts. The "·" separators stay in the text (what the title reads as, and what
           tests read) but are hidden: the counts show as chips. -->
      <div class="titleline" data-test="team-title">
        <h2><span class="title-prefix">Team </span>{{ team.name }}</h2>
        <TeamStateChip :team="team" class="page-state" data-test="team-page-state" />
        <span class="kpi" data-test="kpi-agents"><span class="sep"> · </span><b>{{ loaded ? cards.length : '…' }}</b> agents</span>
        <span class="kpi" :class="{ live: team.running > 0 }" data-test="kpi-running"><span class="sep"> · </span><b>{{ team.running }}</b> running</span>
        <span class="kpi" data-test="kpi-queued"><span class="sep"> · </span><b>{{ team.queued }}</b> queued</span>
      </div>
      <!-- A phone (B054): the title and the actions are one row, the actions behind ⋯. Documents and Workflows are in the
           ⋯ menu first (card 331) and in the drawer, under the team's name (App.vue, Sidebar.vue; card 316). Every item
           has an icon of the same size, as the drawer has. -->
      <div v-if="phone" class="actions phone-actions" data-test="team-phone-actions">
        <span class="more-wrap">
          <button class="hbtn" data-test="team-more" title="More" aria-label="More team actions" aria-haspopup="menu" :aria-expanded="moreOpen" @click="moreOpen = !moreOpen"><Icon name="more" :size="18" /></button>
          <span v-if="moreOpen" class="menu" role="menu" data-test="team-more-menu">
            <button role="menuitem" data-test="open-documents-menu" @click="moreOpen = false; openDocuments()"><Icon name="docs" :size="18" />Documents</button>
            <button role="menuitem" data-test="open-workflows-menu" @click="moreOpen = false; openWorkflows()"><Icon name="workflow" :size="18" />Workflows</button>
            <button role="menuitem" data-test="open-triggers" @click="moreOpen = false; showTriggers = true"><Icon name="clock" :size="18" />Triggers</button>
            <button role="menuitem" data-test="open-git" @click="moreOpen = false; showGit = true"><Icon name="branch" :size="18" />Git</button>
            <button role="menuitem" data-test="open-manage" @click="moreOpen = false; showManage = true"><Icon name="settings" :size="18" />Settings</button>
          </span>
        </span>
      </div>
      <div v-else class="actions">
        <button data-test="open-documents" title="What the team writes for you, and what you add" @click="openDocuments"><Icon name="docs" :size="14" /> Documents</button>
        <button data-test="open-workflows" title="The team's pieces of work: nudge, stop or close one" @click="openWorkflows()"><Icon name="workflow" :size="14" /> Workflows</button>
        <button data-test="open-triggers" title="Work on a schedule" @click="showTriggers = true">⏰ Triggers</button>
        <button data-test="open-git" title="The team's branches, merge to main" @click="showGit = true">⎇ Git</button>
        <button data-test="open-manage" title="The team's repositories, pause and archive, environment, reset, delete" @click="showManage = true">
          <Icon name="settings" :size="14" /> Settings</button>
      </div>
    </header>
    <p v-if="team.archived" class="paused" data-test="team-archived">This team is archived: it does no work and takes no instructions. Unarchive it from Settings.</p>
    <p v-else-if="team.paused" class="paused" data-test="team-paused">
      This team is paused: what it is told waits and runs when you resume it.
      <button class="primary" v-change data-test="team-resume-banner" :disabled="busy.is('resume')" :aria-busy="busy.is('resume')" @click="resume"><BusyLabel :busy="busy.is('resume')" text="Resuming…">Resume</BusyLabel></button>
    </p>
    <!-- A phone (card 282): the "commits behind" bar goes under Tell the Manager, so Tell stays the first row. -->
    <TeamSyncBanner v-if="!phone" :key="team.id" :api="api" :team="team" />
    <TriggersDialog v-if="showTriggers" :api="api" :team="team" :agents="cards" @close="showTriggers = false" />
    <GitDialog v-if="showGit" :api="api" :team="team" @close="showGit = false" />
    <WorkflowsDialog v-if="showWorkflows" :key="workflowFocus ?? 0" :api="api" :team="team" :poll-ms="pollMs" :focus="workflowFocus" @close="showWorkflows = false" />
    <DocumentsDialog v-if="showDocs" :api="api" :team="team" @close="showDocs = false" />
    <CodeDialog :api="api" />
    <TeamManageDialog v-if="showManage" :api="api" :team="team" @close="showManage = false" @changed="emit('changed')"
      @deleted="showManage = false; emit('deleted')" />
    <p v-if="error || refreshError" class="error" data-test="team-error">{{ error ?? refreshError }}</p>

    <div class="tell row">
      <input v-model="tell" class="grow" :placeholder="phone ? TellShort : 'Tell the Manager… (paste an image to attach it)'" data-test="tell-text"
        @keydown.enter="(tell.trim() || tellImages.images.value.length) && sendTell()" @paste="tellImages.onPaste" />
      <button title="Attach an image" data-test="tell-attach" @click="tellPicker?.click()"><svg class="ticon" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2.5" /><circle cx="8.5" cy="9.5" r="1.6" /><path d="M21 16l-5-5-9 9" /></svg></button>
      <input ref="tellPicker" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden data-test="tell-attach-input"
        @change="tellImages.add(($event.target as HTMLInputElement).files ?? []); ($event.target as HTMLInputElement).value = ''" />
      <button class="primary" v-change data-test="tell-send" :disabled="(!tell.trim() && !tellImages.images.value.length) || busy.is('tell')" :aria-busy="busy.is('tell')" :aria-label="phone ? 'Tell Manager' : undefined" @click="sendTell"><BusyLabel :busy="busy.is('tell')" text="Sending…"><template v-if="!phone">Tell Manager </template><svg class="ticon" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M22 2 11 13" /><path d="M22 2l-7 20-4-9-9-4Z" /></svg></BusyLabel></button>
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
    <TeamSyncBanner v-if="phone" :key="team.id" :api="api" :team="team" />

    <LoadingNote v-if="!loaded && !error && !refreshError" text="Loading the team…" />
    <TaskAskCard v-for="a in asks.filter((x) => x.task)" :key="`task-ask-${a.id}`" :ask="a" :busy="busy.is(`ask-${a.id}`)"
      :pending="busy.is(`ask-${a.id}`, 'once') ? 'once' : busy.is(`ask-${a.id}`, 'always') ? 'always' : busy.is(`ask-${a.id}`, 'decline') ? 'decline' : null"
      @once="approve(a, 'once')" @always="approve(a, 'always')" @decline="(n: string) => declineTask(a, n)" />
    <div v-for="a in asks.filter((x) => !x.task)" :key="`ask-${a.id}`" class="decision ask" data-test="integration-ask">
      <p><strong>{{ a.member.split('/')[1] }} asks to run</strong> <code>{{ a.command }}</code> <span class="muted">in {{ a.repo ?? 'the main repository' }} — {{ a.reason }}</span></p>
      <p v-if="a.action" class="muted small" data-test="ask-action">remembers as <code>{{ a.action }}</code></p>
      <div class="row">
        <input v-model="declineNote[a.id]" class="grow" placeholder="A note if you decline (optional)" />
        <button class="primary" v-change data-test="ask-approve" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'once')"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'once')" text="Approving…">Approve once</BusyLabel></button>
        <button v-if="a.canAlways !== false" v-change data-test="ask-always" title="Approve, and let this run without asking from now on" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'always', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'always')" text="Approving…">Always allow</BusyLabel></button>
        <button v-change data-test="ask-keep-asking" title="Approve this one and keep asking each time" :disabled="busy.is(`ask-${a.id}`)" @click="approve(a, 'ask', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'ask')" text="Approving…">Keep asking</BusyLabel></button>
        <button v-change data-test="ask-decline" :disabled="busy.is(`ask-${a.id}`)" @click="decline(a)"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'decline')" text="Declining…">Decline</BusyLabel></button>
        <button v-change data-test="ask-never" title="Decline, and never allow this" :disabled="busy.is(`ask-${a.id}`)" @click="decline(a, 'never', scopeOf(a))"><BusyLabel :busy="busy.is(`ask-${a.id}`, 'never')" text="Declining…">Never</BusyLabel></button>
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
          <button v-for="o in questionOptions(d.question)" :key="o.n" class="primary" :title="o.text" v-change :data-test="`decision-option-${o.n}`"
            :disabled="busy.is(`decision-${d.seq}`)" @click="chooseOption(d, o)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, `option-${o.n}`)" text="Answering…">{{ o.n }}. {{ o.label }}</BusyLabel></button>
        </template>
        <template v-else-if="asksForApproval(d.question)">
          <button class="primary" v-change data-test="decision-approve" :disabled="busy.is(`decision-${d.seq}`)" @click="approveDecision(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'approve')" text="Approving…">Approve</BusyLabel></button>
          <button v-change data-test="decision-always" title="Approve, and let the team go ahead with this kind of thing from now on without asking" @click="startRemember(d)">Always approve for this team…</button>
        </template>
        <button data-test="decision-discuss" title="Talk it through with the Concierge first" @click="emit('discuss', d)">Discuss with the Concierge</button>
      </div>
      <div v-if="remembering[d.seq] !== undefined" class="row remember" data-test="decision-remember">
        <label class="grow">What the team may do from now on without asking
          <input v-model="remembering[d.seq]" maxlength="500" data-test="decision-remember-text" />
        </label>
        <button class="primary" v-change data-test="decision-remember-save" :disabled="!(remembering[d.seq] ?? '').trim() || busy.is(`decision-${d.seq}`)" @click="approveAlways(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'remember')" text="Approving…">Approve and remember</BusyLabel></button>
        <button @click="delete remembering[d.seq]">Cancel</button>
      </div>
      <div class="row">
        <input v-model="answers[d.seq]" class="grow" placeholder="Or answer in your own words…" data-test="decision-answer" @keydown.enter="(answers[d.seq] ?? '').trim() && answer(d)" />
        <button v-change data-test="decision-send" :disabled="!(answers[d.seq] ?? '').trim() || busy.is(`decision-${d.seq}`)" @click="answer(d)"><BusyLabel :busy="busy.is(`decision-${d.seq}`, 'answer')" text="Answering…">Answer</BusyLabel></button>
      </div>
    </div>

    <!-- The members (B076): equal tiles in a wrapping grid, like the Teams list; the 14px gaps right of and below each
         tile are the width and height handles for every tile (memberTileSize.ts). The last tile is New member. A phone:
         one full-width tile per row, as tall as its content, no handles. -->
    <div ref="grid" class="member-grid" :style="phone ? undefined : gridStyle" data-test="member-grid">
      <div v-for="c in cards" :key="c.member.id" class="member-cell" data-test="member-cell">
        <MemberPane :ref="(p) => setPaneRef(c.member.name, p)" :card="c" :api="api" :poll-ms="pollMs"
          :pending="busy.is(`member-${c.member.id}`, 'stop') ? 'stop' : busy.is(`member-${c.member.id}`, 'remove') ? 'remove' : null"
          :rows="activity.of(c.member.name)" :loaded="activity.loaded.value" :now="activity.now.value" :activity-limit="phone ? PhoneActivityRows : undefined"
          @code="openCode(c.member.team, c.member.name)" @settings="emit('settings', c.member.id)"
          @logs="logsFor = `${c.member.team}/${c.member.name}`" @stop="stop(c)" @remove="remove(c)" />
        <template v-if="!phone"><TileGrip axis="w" :store="memberTilesStore" /><TileGrip axis="h" :store="memberTilesStore" /></template>
      </div>
      <div class="member-cell new-cell">
        <div class="new-tile" :class="{ open: adding }" data-test="member-card-new-tile">
          <button v-if="!adding" v-change class="new-open" data-test="member-card-new" title="Add a member to this team" @click="openAdd"><Icon name="plus" />New member</button>
          <form v-else class="add" data-test="add-agent-form" @submit.prevent="canAdd && addAgent()" @keydown.esc.stop="cancelAdd">
            <strong class="add-title">New member</strong>
            <label>Job and first name
              <input ref="addName" v-model="newAgent.name" placeholder="e.g. Developer Priya" title="Named for the job, a space, then a first name. Its id is the name without the space (DeveloperPriya)." maxlength="60" data-test="add-agent-name" />
            </label>
            <label>CLI
              <select v-model="newAgent.cli" data-test="add-agent-cli"><option v-for="o in agentOptions" :key="o.id" :value="o.id">{{ o.label }}</option></select>
            </label>
            <div class="add-buttons">
              <button type="submit" class="primary" v-change data-test="add-agent" :disabled="!canAdd" :aria-busy="busy.is('add-agent')"><BusyLabel :busy="busy.is('add-agent')" text="Adding…">Add</BusyLabel></button>
              <button type="button" data-test="add-agent-cancel" :disabled="busy.is('add-agent')" @click="cancelAdd">Cancel</button>
            </div>
          </form>
        </div>
        <template v-if="!phone"><TileGrip axis="w" :store="memberTilesStore" /><TileGrip axis="h" :store="memberTilesStore" /></template>
      </div>
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
/* B076: the members are equal tiles that wrap, like the Teams list's .team-grid: columns the shared width (or the whole
   row when narrower), rows the shared height; the handles live in the 14px gaps right of and below each cell. */
.member-grid { display: grid; grid-template-columns: repeat(auto-fill, min(var(--tile-w), 100%)); grid-auto-rows: var(--tile-h); justify-content: start; gap: 14px; margin-bottom: 14px; }
.member-cell { position: relative; min-width: 0; min-height: 0; }
/* The pane fills its tile; its view takes what the title and tabs leave (MemberPane scrolls it inside, card 366), not the
   old shared output height (the body's inline height, which only the removed gap handles set). The desktop only: on a
   phone the pane keeps its own height.
   Card 368: the pane clips sideways only, so its ☰ menu (absolute, under the title) draws in full over the tile's
   bottom edge at a short tile; the view never spills down because it scrolls inside (flex 1 1 0, min-height 0). The
   cell being used (its ☰ button was clicked, so it holds focus) sits above the tiles after it (next row, handles). */
@media (min-width: 760px) {
  .member-cell > :deep(.pane) { height: 100%; min-width: 0; display: flex; flex-direction: column; overflow-x: clip; overflow-y: visible; }
  .member-cell > :deep(.pane > .body) { flex: 1 1 0; min-height: 0; height: auto !important; }
  .member-cell:focus-within { z-index: 6; }
}
.new-tile { height: 100%; box-sizing: border-box; display: flex; flex-direction: column; border: 1px dashed var(--line); border-radius: 6px; background: var(--surface); overflow: auto; }
.new-tile:hover { border-color: var(--line-strong); }
.new-open { flex: 1; display: flex; align-items: center; justify-content: center; gap: 6px; border: 0; background: transparent; color: var(--muted); }
.new-open:hover:not(:disabled) { color: var(--accent); }
.add { display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; }
.add label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.add input, .add select { font: inherit; font-size: 13px; }
.add-buttons { display: flex; gap: 6px; }
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
.paused { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 6px; padding: 6px 10px; display: flex; align-items: center; gap: 10px; justify-content: space-between; margin: 0 0 10px; }
.small { font-size: 12px; }
/* Card 342: the Tell buttons draw monochrome line icons (currentColor), not emoji iOS paints in colour. */
.tell .ticon { display: inline-block; vertical-align: middle; flex: none; }
/* Card 342 (card 344): on the desktop the 20 px icons take the room the 14 px emoji took, so the 29 px buttons, their widths,
   the box and everything below stay put (phone-remote's 1280 px guard). -6 px top / -2 px bottom keeps them out of the line
   height and lifts them the 2 px vertical-align: middle leaves them below the button's centre; 6 px of width overlaps the
   10 px padding (both sides on Attach, the right on Send, so the plane stays after "Tell Manager "). The phone's 44 px
   buttons are unchanged. */
@media (min-width: 760px) {
  .tell .ticon { margin: -6px -3px -2px; }
  .tell [data-test=tell-send] .ticon { margin: -6px -6px -2px 0; }
}
/* A phone (B037): one column of member panes (each stacked, MemberPane); the actions, Tell and the asks wrap; tap targets 44 px. */
@media (max-width: 759.98px) {
  /* B076: one full-width tile per row, as tall as its content, the New member tile last; no handles (the template). */
  .member-grid { grid-template-columns: minmax(0, 1fr); grid-auto-rows: auto; gap: 16px; }
  .new-tile { min-height: 64px; overflow: visible; }
  .new-open { min-height: 64px; }
  .add-buttons button { flex: 1 1 0; }
  .team > * { margin-bottom: 6px; }
  /* The header (B054): one 44 px row, the title (cut with …) and the icon actions; it scrolls away with the page. */
  .teamhead { flex-direction: row; align-items: center; gap: 4px; height: 44px; }
  .titleline { flex: 1 1 auto; min-width: 0; flex-wrap: nowrap; overflow: hidden; gap: 6px; }
  .titleline h2 { flex: 0 1 auto; min-width: 0; font-size: 16px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  /* Card 282: the name gets the room. "Team " is left out on screen (the top bar names the team too; the title still reads
     "Team X"), every count chip is hidden (the state chip already says Running), and the state chip gives way first. */
  .titleline .kpi { display: none; }
  .title-prefix { display: none; }
  .titleline .page-state { flex: 0 1000 auto; min-width: 0; overflow: hidden; }
  .phone-actions { flex: none; flex-wrap: nowrap; gap: 2px; }
  .hbtn { display: inline-flex; align-items: center; justify-content: center; width: 44px; height: 44px; padding: 0; box-sizing: border-box; }
  .more-wrap { position: relative; display: inline-flex; }
  /* Card 331: the menu opens from the ⋯ at the right edge, leftward, and stays on screen (never wider than the screen,
     scrolls if taller than it); it sits above the member panes (their grips and menus are z-index 1-5). */
  .menu { position: absolute; right: 0; top: 100%; z-index: 20; display: flex; flex-direction: column; min-width: 190px; padding: 4px;
    max-width: calc(100vw - 16px); max-height: calc(100dvh - 120px); overflow-y: auto; box-sizing: border-box;
    background: var(--surface); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 6px 18px var(--shadow); }
  .menu button { display: flex; align-items: center; gap: 10px; justify-content: flex-start; text-align: left; border: 0; background: transparent;
    padding: 0 12px; height: 44px; min-height: 44px; flex: none; font-size: 14px; white-space: nowrap; }
  .menu button:hover { background: var(--hover); }
  .actions button, .row button, .row input, .row select, .paused button { min-height: 44px; box-sizing: border-box; }
  .row { flex-wrap: wrap; }
  /* Tell the Manager (B054): one row, the box beside the two 44 px icon buttons (its short placeholder fits at 375 px). */
  .tell { flex-wrap: nowrap; gap: 6px; min-height: 44px; }
  .tell input.grow { flex: 1 1 0; min-width: 0; }
  .tell button { flex: none; min-width: 44px; padding: 0 10px; }
  .add input, .add select, .add button { min-height: 44px; box-sizing: border-box; }
  .paused { flex-wrap: wrap; }
  .thumb .x { width: 32px; height: 32px; line-height: 32px; font-size: 13px; }
}
</style>
