<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import type { GlobalPolicyDoc, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { CLIPBOARD } from '../policyFields';
import { fromSeconds, toSeconds, UNIT_SECONDS, type TimeUnit } from '../timeUnits';
import { addEntries, hasEntries } from '../listEntries';
import GuardrailPanel from './GuardrailPanel.vue';
import ListEditor from './ListEditor.vue';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The global policy editor, including the one global run limit (app.maxConcurrentAgents).
// One column of section cards (Limits, Network, Files and folders, Desktop, Sandbox, Guardrail check) and a footer
// that stays in view with Save. Every field is saved as before (policy.saveGlobal). The memory, CPU and process caps
// are not enforced, so they are not shown: the values already stored are sent back exactly as loaded.
const props = defineProps<{ api?: YawbleApi; pollMs?: number; inline?: boolean }>();
const emit = defineEmits<{ close: []; changed: [] }>();
const api = resolveApi(props.api);

const loadError = ref<string | null>(null);
const error = ref<string | null>(null);
const saved = ref(false);
const saving = ref(false);
const doc = ref<GlobalPolicyDoc | null>(null);
const f = reactive({
  maxConcurrentAgents: '', heavyWorkAtOnce: '1',
  allowHosts: [] as string[], grantableRoots: [] as string[], readonly: [] as string[], readwrite: [] as string[], denied: [] as string[],
  wallTimeout: '', wallUnit: 'minutes' as TimeUnit, idleTimeout: '', idleUnit: 'minutes' as TimeUnit,
  clipboard: 'none',
});
/** Seconds is offered as a unit only for a timeout that was saved as a number of seconds no minute count gives. */
const secondsUnit = reactive({ wall: false, idle: false });
/** Text typed in a list editor's Add field and not added yet: a change too, added by Save before it saves. */
type ListKey = 'allowHosts' | 'grantableRoots' | 'readonly' | 'readwrite' | 'denied';
const LIST_KEYS: ListKey[] = ['allowHosts', 'grantableRoots', 'readonly', 'readwrite', 'denied'];
const pending = reactive<Record<ListKey, string>>({ allowHosts: '', grantableRoots: '', readonly: '', readwrite: '', denied: '' });
/** A timeout as the duration it stands for (seconds), so a unit switch that keeps the duration is no change. */
const duration = (t: string, unit: TimeUnit) => {
  const s = t.trim() === '' ? NaN : toSeconds(Number(t), unit);
  return Number.isFinite(s) ? s : `${t}|${unit}`;
};
/** The form as last loaded or saved: Save is offered only when something differs from it. */
const pristine = ref('');
const snapshot = () => JSON.stringify({
  ...f, wallTimeout: duration(f.wallTimeout, f.wallUnit), wallUnit: null, idleTimeout: duration(f.idleTimeout, f.idleUnit), idleUnit: null,
});
const dirty = computed(() => doc.value !== null && (snapshot() !== pristine.value || LIST_KEYS.some((k) => hasEntries(pending[k]))));
watch(dirty, (d) => { if (d) saved.value = false; });

/** The hosts package managers download from: NuGet (api + its CDN), npm, and PyPI (index + files). */
const PackageRegistries = ['api.nuget.org', 'globalcdn.nuget.org', 'registry.npmjs.org', 'pypi.org', 'files.pythonhosted.org'];
function addHosts(hosts: string[]) {
  f.allowHosts = [...f.allowHosts, ...hosts.filter((h) => !f.allowHosts.includes(h))];
}
const num = (t: string, label: string, optional = false): number | undefined => {
  if (t.trim() === '' && optional) return undefined;
  const n = Number(t);
  if (!Number.isInteger(n)) throw new Error(`${label}: a whole number`);
  return n;
};
/** A unit switch keeps the duration: 2 hours → 120 minutes; 90 seconds → 1.5 minutes (saved back as 90 s). */
function changeUnit(which: 'wall' | 'idle', unit: TimeUnit) {
  const valueKey = which === 'wall' ? 'wallTimeout' : 'idleTimeout';
  const unitKey = which === 'wall' ? 'wallUnit' : 'idleUnit';
  const t = f[valueKey];
  const s = t.trim() === '' ? NaN : toSeconds(Number(t), f[unitKey]);
  if (Number.isFinite(s)) f[valueKey] = String(s / UNIT_SECONDS[unit]);
  f[unitKey] = unit;
}
const seconds = (t: string, unit: TimeUnit, label: string): number => {
  const s = t.trim() === '' ? NaN : toSeconds(Number(t), unit);
  if (!Number.isInteger(s)) throw new Error(`${label}: a whole number of seconds`);
  return s;
};

function fill(d: GlobalPolicyDoc) {
  doc.value = d;
  const wall = fromSeconds(d.compute.wallTimeoutSec);
  const idle = fromSeconds(d.compute.idleTimeoutSec);
  secondsUnit.wall = wall.unit === 'seconds';
  secondsUnit.idle = idle.unit === 'seconds';
  Object.assign(f, {
    maxConcurrentAgents: String(d.app.maxConcurrentAgents), heavyWorkAtOnce: String(d.app.heavyWorkAtOnce ?? 1),
    allowHosts: [...d.network.allowHosts], grantableRoots: [...d.filesystem.grantableRoots],
    readonly: [...d.filesystem.readonly], readwrite: [...d.filesystem.readwrite], denied: [...d.filesystem.denied],
    wallTimeout: String(wall.value), wallUnit: wall.unit, idleTimeout: String(idle.value), idleUnit: idle.unit,
    clipboard: d.ui.clipboard,
  });
  for (const k of LIST_KEYS) pending[k] = '';
  pristine.value = snapshot();
}

onMounted(async () => {
  try { fill(await api.call<GlobalPolicyDoc>('policy.getGlobal')); } catch (e) { loadError.value = errorText(e); }
});

// The caps that are not enforced (memory, CPU, processes): not shown or edited; a stored value goes back unchanged.
const CAPS = ['memoryMb', 'cpuPercent', 'maxProcesses'] as const;

async function save() {
  error.value = null;
  saved.value = false;
  saving.value = true;
  for (const k of LIST_KEYS) {
    f[k] = addEntries(f[k], pending[k]);
    pending[k] = '';
  }
  try {
    const compute: Record<string, number> = {
      wallTimeoutSec: seconds(f.wallTimeout, f.wallUnit, 'Wall-clock timeout'),
      idleTimeoutSec: seconds(f.idleTimeout, f.idleUnit, 'Idle timeout'),
    };
    for (const k of CAPS) {
      const v = doc.value?.compute[k];
      if (v != null) compute[k] = v;
    }
    const policy = {
      filesystem: { grantableRoots: [...f.grantableRoots], readonly: [...f.readonly], readwrite: [...f.readwrite], denied: [...f.denied] },
      network: { allowHosts: [...f.allowHosts] },
      compute,
      ui: { clipboard: f.clipboard },
      app: {
        maxConcurrentAgents: num(f.maxConcurrentAgents, 'Global run limit')!, backend: doc.value?.app.backend ?? 'default',
        heavyWorkAtOnce: num(f.heavyWorkAtOnce, 'Heavy work at once')!,
      },
    };
    fill(await api.call<GlobalPolicyDoc>('policy.saveGlobal', { policy }));
    saved.value = true;
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
  } finally {
    saving.value = false;
  }
}

const units = (offerSeconds: boolean): TimeUnit[] => (offerSeconds ? ['seconds', 'minutes', 'hours'] : ['minutes', 'hours']);
</script>

<template>
  <Modal :inline="inline" title="Global policy" wide @close="emit('close')">
    <p v-if="loadError" class="error" data-test="global-error">{{ loadError }}</p>
    <LoadingNote v-if="!doc && !loadError" text="Loading the global policy…" />
    <div v-if="doc" class="gp" data-test="global-form">
      <section class="card rows" data-test="global-section-limits">
        <h3>Limits</h3>
        <p class="lead">How many agents run at once, and how long a run may take.</p>
        <div class="prow">
          <label class="plabel" for="gp-max">Global run limit
            <span class="hint">most sandboxes at once across all teams: member runs, Manager runs and interactive sessions</span></label>
          <div class="pctl"><input id="gp-max" v-model="f.maxConcurrentAgents" class="num" inputmode="numeric" data-test="global-maxConcurrentAgents" /></div>
        </div>
        <div class="prow">
          <label class="plabel" for="gp-heavy">Heavy work at once
            <span class="hint">how many agents may run a full test suite, a full build or an image build at the same time (1-8); the others wait their turn</span></label>
          <div class="pctl"><input id="gp-heavy" v-model="f.heavyWorkAtOnce" class="num" inputmode="numeric" data-test="global-heavyWorkAtOnce" /></div>
        </div>
        <div class="prow">
          <label class="plabel" for="gp-wall">Wall-clock timeout
            <span class="hint">a run is stopped after this long</span></label>
          <div class="pctl">
            <input id="gp-wall" v-model="f.wallTimeout" class="num" inputmode="numeric" data-test="global-wallTimeoutSec" />
            <select :value="f.wallUnit" aria-label="Wall-clock timeout unit" data-test="global-wallTimeoutSec-unit"
              @change="changeUnit('wall', ($event.target as HTMLSelectElement).value as TimeUnit)">
              <option v-for="u in units(secondsUnit.wall)" :key="u" :value="u">{{ u }}</option>
            </select>
          </div>
        </div>
        <div class="prow">
          <label class="plabel" for="gp-idle">Idle timeout
            <span class="hint">a headless run with no progress for this long is stopped</span></label>
          <div class="pctl">
            <input id="gp-idle" v-model="f.idleTimeout" class="num" inputmode="numeric" data-test="global-idleTimeoutSec" />
            <select :value="f.idleUnit" aria-label="Idle timeout unit" data-test="global-idleTimeoutSec-unit"
              @change="changeUnit('idle', ($event.target as HTMLSelectElement).value as TimeUnit)">
              <option v-for="u in units(secondsUnit.idle)" :key="u" :value="u">{{ u }}</option>
            </select>
          </div>
        </div>
      </section>

      <section class="card rows" data-test="global-section-network">
        <h3>Network</h3>
        <p class="lead">The hosts every agent's sandbox may connect to (the CONNECT allowlist).</p>
        <div class="prow">
          <div class="plabel">Allowed hosts
            <span class="hint">beyond the model API and the team bus</span></div>
          <div class="pctl col">
            <ListEditor v-model="f.allowHosts" v-model:draft="pending.allowHosts" placeholder="Add host, e.g. api.github.com" data-test="global-allowHosts" />
            <div class="presets">
              <button type="button" data-test="global-add-registries" title="Lets agents restore .NET, npm and Python packages (dotnet restore, npm ci, pip install)" @click="addHosts(PackageRegistries)">
                + Package registries (NuGet, npm, PyPI)</button>
              <span class="hint">Teams that build code need these to restore packages.</span>
            </div>
          </div>
        </div>
      </section>

      <section class="card rows" data-test="global-section-files">
        <h3>Files and folders</h3>
        <p class="lead">What every agent may read and write on this computer, on top of its team's own folders.</p>
        <div class="prow">
          <div class="plabel">Folders agents may be granted
            <span class="hint">a team's settings can grant folders inside these</span></div>
          <div class="pctl col"><ListEditor v-model="f.grantableRoots" v-model:draft="pending.grantableRoots" mono placeholder="Add folder" data-test="global-grantableRoots" /></div>
        </div>
        <div class="prow">
          <div class="plabel">Read-only for every agent</div>
          <div class="pctl col"><ListEditor v-model="f.readonly" v-model:draft="pending.readonly" mono placeholder="Add folder" data-test="global-readonly" /></div>
        </div>
        <div class="prow">
          <div class="plabel">Read/write for every agent</div>
          <div class="pctl col"><ListEditor v-model="f.readwrite" v-model:draft="pending.readwrite" mono placeholder="Add folder" data-test="global-readwrite" /></div>
        </div>
        <div class="prow">
          <div class="plabel">Denied for every agent</div>
          <div class="pctl col"><ListEditor v-model="f.denied" v-model:draft="pending.denied" mono placeholder="Add folder" data-test="global-denied" /></div>
        </div>
      </section>

      <section class="card rows" data-test="global-section-desktop">
        <h3>Desktop</h3>
        <p class="lead">What an agent's sandbox may share with your desktop session.</p>
        <div class="prow">
          <label class="plabel" for="gp-clip">Clipboard
            <span class="hint">GUI access is always off in the MVP.</span></label>
          <div class="pctl">
            <select id="gp-clip" v-model="f.clipboard" data-test="global-clipboard"><option v-for="c in CLIPBOARD" :key="c">{{ c }}</option></select>
          </div>
        </div>
      </section>

      <section class="card rows" data-test="global-section-sandbox">
        <h3>Sandbox</h3>
        <p class="lead">How runs are isolated on this computer.</p>
        <div class="prow">
          <div class="plabel">Backend</div>
          <div class="pctl"><span data-test="global-backend">{{ doc.app.backend }}: this OS's default sandbox backend (ProcessContainer, bubblewrap or Seatbelt).</span></div>
        </div>
      </section>

      <section class="card" data-test="global-guardrail">
        <h3>Guardrail check</h3>
        <p class="lead">Runs the global policy alone as a synthetic agent.</p>
        <GuardrailPanel :api="api" :poll-ms="pollMs" run-label="Run check" />
      </section>

      <footer class="savebar" data-test="global-savebar">
        <span class="hint grow">Saving is refused if any rule breaks a floor (vendor hosts, IP literals, app folders) or the sandbox rejects the compiled request.</span>
        <span class="status">
          <span v-if="error" class="error" data-test="global-error">{{ error }}</span>
          <span v-else-if="dirty" class="hint" data-test="global-dirty">Unsaved changes</span>
          <span v-else-if="saved" class="ok" data-test="global-saved"
            title="The run limit applies to the next admission at once; other changes apply from each agent's next run.">Saved</span>
        </span>
        <button class="primary" data-test="save-global" :disabled="!dirty || saving" :aria-busy="saving" @click="save"><BusyLabel :busy="saving" text="Saving…">Save global policy</BusyLabel></button>
      </footer>
    </div>
  </Modal>
</template>

<style scoped>
.gp { display: flex; flex-direction: column; gap: 12px; }
/* Only the cards of rows are size containers (for the narrow rule below): none holds a Modal, whose position: fixed
   would otherwise be placed against the card instead of the window. */
.card.rows { container-type: inline-size; }
.card { border: 1px solid var(--line); border-radius: 8px; background: var(--surface); padding: 12px 16px 6px; }
.card h3 { margin: 0; font-size: 14px; }
.lead { margin: 2px 0 6px; font-size: 12.5px; color: var(--muted); }
.prow { display: grid; grid-template-columns: minmax(180px, 2fr) 3fr; gap: 6px 20px; align-items: start; padding: 10px 0; border-top: 1px solid var(--line-soft); }
.plabel { display: flex; flex-direction: column; gap: 2px; font-size: 13px; font-weight: 600; color: var(--ink); }
.hint { font-size: 12px; font-weight: 400; color: var(--muted); }
.pctl { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; min-width: 0; font-size: 13px; }
.pctl.col { flex-direction: column; align-items: stretch; }
.num { width: 90px; }
.presets { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.savebar { position: sticky; bottom: 0; z-index: 1; display: flex; flex-wrap: wrap; gap: 12px; align-items: center; padding: 10px 16px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); box-shadow: 0 -4px 12px color-mix(in srgb, var(--ink) 8%, transparent); }
.savebar .status:empty { display: none; }
.savebar .error { font-size: 12.5px; }
.savebar .ok { font-weight: 600; font-size: 12.5px; }
/* Narrow: the control drops below its label. */
@container (max-width: 560px) {
  .prow { grid-template-columns: 1fr; }
}
</style>
