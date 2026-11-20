<script setup lang="ts">
import { readOnly, vChange } from '../remoteAccess';
import { computed, ref } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText, isRemoteRefusal } from '../api';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { usePhone } from '../phone';
import { accessWords, askerWords, folderAnswerText, folderAskAskedBy, folderAskChoices, folderAskMember, type FolderAsk, type FolderAskChoice } from '../folderAsks';

/**
 * One folder ask in Needs you (B111): an agent asks for a folder outside its clone, read-only or read-write, with its
 * reason and whether the folder exists. Approve grants what was asked; a read-write ask also offers Approve read-only
 * (never wider than asked); Deny closes it. Each calls folder.answer; an approval applies from the member's next run,
 * which the card then says. Everything shown is agent-influenced text: always plain text ({{ }}), never HTML. A remote
 * view whose changes are refused says to answer it in the window.
 */
const props = defineProps<{ ask: FolderAsk; api: YawbleApi }>();
const emit = defineEmits<{ answered: [ask: FolderAsk, choice: FolderAskChoice] }>();
// A phone (B037): the card takes the full width and its buttons stack, each a full-width tap target.
const phone = usePhone();

const LABELS: Record<FolderAskChoice, { label: string; busy: string; title: string }> = {
  readwrite: { label: 'Approve', busy: 'Approving…', title: 'Grants read-write on this folder to this member, from its next run' },
  read: { label: 'Approve', busy: 'Approving…', title: 'Grants read-only on this folder to this member, from its next run' },
  deny: { label: 'Deny', busy: 'Denying…', title: 'Closes it; the agent is told' },
};
const choices = computed(() => folderAskChoices(props.ask).map((c) => ({
  choice: c, ...LABELS[c],
  // A read-write ask: "Approve" is read-write and "Approve read-only" is the narrower one.
  label: c === 'read' && props.ask.access === 'readwrite' ? 'Approve read-only' : LABELS[c].label,
})));
const member = computed(() => folderAskMember(props.ask));
/** Card 507: the Manager asked for this member (askedBy is not member); null for the member's own ask. */
const asker = computed(() => folderAskAskedBy(props.ask));
const access = computed(() => accessWords(props.ask.access));

const busy = useBusy();
const error = ref<string | null>(null);
/** The answer given here, for the line after it (the list reloads and the card goes). */
const done = ref<string | null>(null);
/** An ask already decided (a list with all): what it got. */
const decided = computed(() => {
  const a = props.ask;
  if (a.state === 'approved') return `Approved ${accessWords(a.grantedAccess ?? a.access)}: applies from its next run.`;
  if (a.state === 'denied') return 'Denied.';
  if (a.state === 'closed') return 'Closed.';
  return null;
});

async function answer(choice: FolderAskChoice) {
  await busy.run('answer', async () => {
    error.value = null;
    try {
      await props.api.call('folder.answer', { id: props.ask.id, choice });
      done.value = folderAnswerText(props.ask, choice);
      emit('answered', props.ask, choice);
    } catch (e) {
      error.value = isRemoteRefusal(props.api, e) && !readOnly.value ? `This view cannot change folder access: answer it in the window. (${errorText(e)})` : errorText(e);
    }
  }, choice);
}
</script>

<template>
  <article class="folder-ask" :class="{ phone }" data-test="folder-ask" :data-id="ask.id">
    <p class="head" data-test="folder-ask-head">
      <span data-test="folder-ask-team">{{ ask.team }}</span> /
      <template v-if="asker"><strong data-test="folder-ask-asked-by" :data-asked-by="asker">{{ askerWords(asker) }}</strong> asks, for <strong data-test="folder-ask-member">{{ member }}</strong>,</template>
      <template v-else><strong data-test="folder-ask-member">{{ member }}</strong> asks</template> <b data-test="folder-ask-access">{{ access }}</b> on <code data-test="folder-ask-path">{{ ask.path }}</code><template v-if="ask.reason"> - <span data-test="folder-ask-reason">{{ ask.reason }}</span></template>
    </p>
    <p class="small" :class="ask.exists ? 'muted' : 'missing'" data-test="folder-ask-exists" :data-exists="ask.exists">
      {{ ask.exists ? 'The folder exists.' : 'The folder does not exist on this computer (yet).' }}
    </p>
    <p v-if="done || decided" class="small ok" role="status" data-test="folder-ask-done">{{ done || decided }}</p>
    <div v-else class="buttons">
      <button v-for="(c, i) in choices" :key="c.choice" :class="{ primary: i === 0 }" :title="c.title" v-change :data-test="`folder-ask-${c.choice}`"
        :disabled="busy.is('answer')" :aria-busy="busy.is('answer', c.choice)" @click="answer(c.choice)">
        <BusyLabel :busy="busy.is('answer', c.choice)" :text="c.busy">{{ c.label }}</BusyLabel>
      </button>
    </div>
    <p v-if="error" class="error small" data-test="folder-ask-error">{{ error }}</p>
  </article>
</template>

<style scoped>
.folder-ask { display: flex; flex-direction: column; gap: 4px; border: 1px solid var(--warn-edge-soft); border-left: 3px solid var(--warn-edge); border-radius: 10px; padding: 8px 10px; background: var(--surface); font-size: 13px; }
.folder-ask p { margin: 0; overflow-wrap: anywhere; }
.head code { font-weight: 600; }
.missing { color: var(--warn-ink); }
.ok { color: var(--ok); }
.buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
.buttons button { font-size: 12.5px; }
.folder-ask.phone { width: 100%; padding: 10px 12px; font-size: 14px; }
.phone .buttons { flex-direction: column; align-items: stretch; gap: 8px; }
.phone .buttons button { width: 100%; font-size: 14px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
