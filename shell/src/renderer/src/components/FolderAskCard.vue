<script setup lang="ts">
import { readOnly, vChange } from '../remoteAccess';
import { computed, ref } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText, isRemoteRefusal } from '../api';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { usePhone } from '../phone';
import {
  accessWords, folderAnswerText, folderAskAskedBy, folderAskAskerWords, folderAskChoices, folderAskFromConcierge, folderAskMember, folderAskScopes,
  folderGrantText, folderScopeLabel, folderScopeWho, type FolderAsk, type FolderAskChoice, type FolderAskScope,
} from '../folderAsks';

/**
 * One folder ask in Needs you: an agent asks for a folder outside its clone, read-only or read-write, with its
 * reason and whether the folder exists; or the Concierge asks for the person. The person picks whom an approval covers:
 * this member (the default), the whole team (the team's folder list), or all teams (the global policy); the card says
 * exactly what that grants. Approve grants what was asked; a read-write ask also offers Approve read-only (never wider
 * than asked); Deny closes it. Each calls folder.answer; an approval applies from the next run, which the card then
 * says. Everything shown is agent-influenced text: always plain text ({{ }}), never HTML. A remote view whose changes
 * are refused says to answer it in the window.
 */
const props = defineProps<{ ask: FolderAsk; api: YawbleApi }>();
const emit = defineEmits<{ answered: [ask: FolderAsk, choice: FolderAskChoice, scope: FolderAskScope] }>();
// A phone: the card takes the full width and its buttons stack, each a full-width tap target.
const phone = usePhone();

/** Whom an approval covers; the member the ask is for until the person picks more. */
const scope = ref<FolderAskScope>('member');
const scopes = computed(() => folderAskScopes(props.ask).map((s) => ({ scope: s, label: folderScopeLabel(props.ask, s) })));
const fromConcierge = computed(() => folderAskFromConcierge(props.ask));

const choices = computed(() => folderAskChoices(props.ask).map((c) => c === 'deny'
  ? { choice: c, label: 'Deny', busy: 'Denying…', title: fromConcierge.value ? 'Closes it; nothing is granted' : 'Closes it; the agent is told' }
  : {
    choice: c, busy: 'Approving…', title: folderGrantText(props.ask, c, scope.value),
    // A read-write ask: "Approve" is read-write and "Approve read-only" is the narrower one. A wider scope is in the label.
    label: (c === 'read' && props.ask.access === 'readwrite' ? 'Approve read-only' : 'Approve')
      + (scope.value === 'member' ? '' : ` for ${scope.value === 'team' ? 'the team' : 'all teams'}`),
  }));
/** What Approve grants at the picked scope, as asked (the read-only button grants the same, read-only). */
const grants = computed(() => folderGrantText(props.ask, props.ask.access, scope.value));
const member = computed(() => folderAskMember(props.ask));
/** Who asked when it is not the member itself: its Manager's name, or "Concierge"; null for the member's own ask. */
const asker = computed(() => (fromConcierge.value ? props.ask.askedBy : folderAskAskedBy(props.ask)));
const askerText = computed(() => folderAskAskerWords(props.ask));
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
      // A deny has no scope; an approval says whom it covers.
      const covers: FolderAskScope = choice === 'deny' ? 'member' : scope.value;
      // The member alone is the core's default: only a wider scope is sent.
      await props.api.call('folder.answer', covers === 'member' ? { id: props.ask.id, choice } : { id: props.ask.id, choice, scope: covers });
      done.value = folderAnswerText(props.ask, choice, covers);
      emit('answered', props.ask, choice, covers);
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
      <template v-if="asker"><strong data-test="folder-ask-asked-by" :data-asked-by="asker">{{ askerText }}</strong> asks, for <strong data-test="folder-ask-member">{{ member }}</strong>,</template>
      <template v-else><strong data-test="folder-ask-member">{{ member }}</strong> asks</template> <b data-test="folder-ask-access">{{ access }}</b> on <code data-test="folder-ask-path">{{ ask.path }}</code><template v-if="ask.reason"> - <span data-test="folder-ask-reason">{{ ask.reason }}</span></template>
    </p>
    <p class="small" :class="ask.exists ? 'muted' : 'missing'" data-test="folder-ask-exists" :data-exists="ask.exists">
      {{ ask.exists ? 'The folder exists.' : 'The folder does not exist on this computer (yet).' }}
    </p>
    <p v-if="done || decided" class="small ok" role="status" data-test="folder-ask-done">{{ done || decided }}</p>
    <template v-else>
    <fieldset v-if="scopes.length > 1" class="scope" data-test="folder-ask-scope" :data-scope="scope">
      <legend class="small">An approval covers</legend>
      <label v-for="s in scopes" :key="s.scope" class="pick" :title="`Approving covers ${folderScopeWho(ask, s.scope)}`">
        <input v-model="scope" v-change type="radio" :name="`folder-ask-scope-${ask.id}`" :value="s.scope" :data-test="`folder-ask-scope-${s.scope}`" :disabled="busy.is('answer')" />
        {{ s.label }}
      </label>
    </fieldset>
    <p class="small" :class="scope === 'member' ? 'muted' : 'wide'" data-test="folder-ask-grants">{{ grants }}</p>
    <div class="buttons">
      <button v-for="(c, i) in choices" :key="c.choice" :class="{ primary: i === 0 }" :title="c.title" v-change :data-test="`folder-ask-${c.choice}`"
        :disabled="busy.is('answer')" :aria-busy="busy.is('answer', c.choice)" @click="answer(c.choice)">
        <BusyLabel :busy="busy.is('answer', c.choice)" :text="c.busy">{{ c.label }}</BusyLabel>
      </button>
    </div>
    </template>
    <p v-if="error" class="error small" data-test="folder-ask-error">{{ error }}</p>
  </article>
</template>

<style scoped>
.folder-ask { display: flex; flex-direction: column; gap: 4px; border: 1px solid var(--warn-edge-soft); border-left: 3px solid var(--warn-edge); border-radius: 10px; padding: 8px 10px; background: var(--surface); font-size: 13px; }
.folder-ask p { margin: 0; overflow-wrap: anywhere; }
.head code { font-weight: 600; }
.missing { color: var(--warn-ink); }
.ok { color: var(--ok); }
.scope { display: flex; flex-wrap: wrap; gap: 4px 14px; align-items: center; margin: 2px 0 0; padding: 0; border: 0; min-width: 0; }
.scope legend { float: left; padding: 0; margin-right: 10px; color: var(--muted); }
.pick { display: inline-flex; align-items: center; gap: 5px; font-size: 12.5px; }
.wide { color: var(--warn-ink); }
.buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
.buttons button { font-size: 12.5px; }
.folder-ask.phone { width: 100%; padding: 10px 12px; font-size: 14px; }
.phone .scope { flex-direction: column; align-items: stretch; gap: 0; }
.phone .scope legend { float: none; margin: 0 0 2px; }
.phone .pick { min-height: 44px; font-size: 14px; }
.phone .buttons { flex-direction: column; align-items: stretch; gap: 8px; }
.phone .buttons button { width: 100%; font-size: 14px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
