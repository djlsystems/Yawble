<script setup lang="ts">
import { ref } from 'vue';
import BusyLabel from './BusyLabel.vue';
import type { IntegrationAsk } from '../../../shared/api';

/**
 * The approval ask for a host task: what will run, on which commit, why the agent says it needs it, and,
 * when a standing approval no longer matches, what changed since. Everything from the repository or the agent is shown as plain
 * (escaped) text. Run once approves one run at exactly these hashes; Always for this team stores the approval at them.
 */
/** busy: an answer to this ask is being sent (every button held); pending: which one, which shows the spinner. */
defineProps<{ ask: IntegrationAsk; busy?: boolean; pending?: 'once' | 'always' | 'decline' | null }>();
const emit = defineEmits<{ (e: 'once'): void; (e: 'always'): void; (e: 'decline', note: string): void }>();
const note = ref('');
const short = (s: string | null | undefined) => (s ?? '').slice(0, 8);
</script>

<template>
  <div class="decision ask task-ask" data-test="task-ask">
    <p>
      <strong>{{ ask.member.split('/')[1] }} asks to run the host task</strong> <code data-test="task-ask-name">{{ ask.taskName }}</code>
      <span class="muted small"> on {{ short(ask.detail?.commit ?? ask.commit) }}</span>
    </p>
    <p class="small"><span class="muted">The agent's words:</span> <span data-test="task-ask-reason">{{ ask.reason }}</span></p>
    <template v-if="ask.detail">
      <p class="small mono" data-test="task-ask-command">{{ ask.detail.program }} {{ ask.detail.args.join(' ') }}</p>
      <p class="small muted">
        in {{ ask.detail.workdir }} · limit {{ ask.detail.timeout_s }} s · pin mode {{ ask.detail.pin_mode }}
        <template v-if="ask.detail.env_names.length"> · passes environment names: {{ ask.detail.env_names.join(', ') }}</template>
      </p>
      <p class="small"><span class="muted">Pinned files:</span> <span class="mono">{{ ask.detail.pinned.join(', ') || 'none (only the command line)' }}</span></p>
      <p v-for="w in ask.detail.warnings" :key="w" class="small warn">{{ w }}</p>
      <p v-if="!ask.detail.standingApproval" class="small" data-test="task-ask-first">No standing approval yet.</p>
      <div v-if="ask.detail.changed" class="changed" data-test="task-ask-changed">
        <p class="small"><strong>What changed since you approved</strong> ({{ short(ask.detail.changed.approvedCommit) }} → {{ short(ask.detail.changed.currentCommit) }})</p>
        <ul v-if="ask.detail.changed.definition.length" class="small">
          <li v-for="d in ask.detail.changed.definition" :key="d.field"><b>{{ d.field }}</b>: <span class="mono">{{ d.old ?? '(none)' }}</span> → <span class="mono">{{ d.new ?? '(none)' }}</span></li>
        </ul>
        <div v-for="f in ask.detail.changed.files" :key="f.path" class="file" data-test="task-ask-file">
          <p class="small"><span class="mono">{{ f.path }}</span> {{ f.change }}
            <span class="muted mono">{{ short(f.oldBlob) }} → {{ short(f.newBlob) }}</span></p>
          <pre v-if="f.diff" class="diff">{{ f.diff }}</pre>
          <p v-if="f.diffMoreLines" class="small muted">{{ f.diffMoreLines }} more lines</p>
        </div>
        <p v-if="!ask.detail.changed.diffAvailable" class="small muted">The approved commit is no longer in your repository, so only the hashes can be shown.</p>
        <pre v-if="ask.detail.changed.commits" class="diff">{{ ask.detail.changed.commits }}</pre>
        <pre v-if="ask.detail.changed.stat" class="diff">{{ ask.detail.changed.stat }}</pre>
      </div>
    </template>
    <div class="row">
      <input v-model="note" class="grow" placeholder="A note if you decline (optional)" />
      <button class="primary" :disabled="busy" data-test="task-ask-once" @click="emit('once')"><BusyLabel :busy="pending === 'once'" text="Approving…">Run once</BusyLabel></button>
      <button :disabled="busy" data-test="task-ask-always" title="Run now, and let this team run this task without asking while its definition and pinned files stay as shown" @click="emit('always')"><BusyLabel :busy="pending === 'always'" text="Approving…">Always for this team</BusyLabel></button>
      <button :disabled="busy" data-test="task-ask-decline" @click="emit('decline', note)"><BusyLabel :busy="pending === 'decline'" text="Declining…">Decline</BusyLabel></button>
    </div>
  </div>
</template>

<style scoped>
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.small { font-size: 12px; margin: 3px 0; }
.muted { color: var(--muted); }
.warn { color: var(--warn); }
.changed { border-left: 3px solid var(--warn); padding-left: 8px; margin: 6px 0; }
.diff { max-height: 260px; overflow: auto; background: #f6f6f6; padding: 6px; font-size: 11.5px; margin: 3px 0; white-space: pre; }
</style>
