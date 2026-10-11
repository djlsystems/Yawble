<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import {
  TenantSettingRejected,
  getAgentAuth,
  getTenantSettings,
  saveTenantSettings,
} from '../api/client';
import { getKanbanBoard, type KanbanLane } from '../api/kanban';
import type { AgentAuthReport, TenantSetting, TenantSettings } from '../api/types';
import { isNotInstalled } from '../lib/agentInstall';
import { useAgentInstallations } from '../lib/useAgentInstallations';
import {
  KanbanWipLimits,
  TenantSettingFields,
  WipMaxRunning,
  canReset,
  catalogHealth,
  changedSettings,
  draftErrors,
  draftOf,
  isRunningLane,
  laneLimitsOf,
  resetLine,
  settingErrorText,
  sourceLine,
  type TenantSettingTab,
} from '../lib/tenantSettings';
import { useKanbanStore } from '../stores/kanban';
import { useWipStore } from '../stores/wip';
import ConciergeAgentForm from './ConciergeAgentForm.vue';
import ConciergeSessions from './ConciergeSessions.vue';
import ConciergeDisplayForm from './ConciergeDisplayForm.vue';
import TenantSettingField from './TenantSettingField.vue';
import DialogTabs from './DialogTabs.vue';

/**
 * ADMIN > SETTINGS: every instance-wide setting in one place.
 *
 * One Save for the whole dialog, disabled while nothing moved or anything is invalid. It sends only
 * what changed, as the partial map `PUT /api/tenant/settings` takes; a 400 names the setting it
 * refused, and the message goes under that field on its own tab. Every field shows where its value
 * came from and who changed it last. The Concierge tab's Agent and Prompt are saved by the same
 * button, through the Concierge's own route.
 */
const open = defineModel<boolean>({ required: true });

/** The tab to show on opening. The Concierge panel's gear opens straight on `concierge`. */
const props = defineProps<{
  initialTab?: 'admission' | 'spend' | 'concierge' | 'sweeps' | 'kanban' | 'system' | 'health' | 'roots';
  /**
   * True while a dialog this one opened (Open Agents, Open Skills) is showing. This dialog is
   * lifted above the Concierge shell (`.concierge-settings`, 7100), and the Agents and Skills
   * dialogs are ordinary Quasar dialogs (6000), so they opened BEHIND it. While covered it drops
   * the lift and, being earlier in <body>, sits under the dialog it opened; its drafts are kept,
   * because it stays open rather than closing and reloading.
   */
  covered?: boolean;
}>();

const emit = defineEmits<{ 'open-agents': []; 'open-skills': [] }>();

type Tab = TenantSettingTab | 'health' | 'roots';

const Tabs: { name: Tab; label: string }[] = [
  { name: 'admission', label: 'Admission' },
  { name: 'spend', label: 'Spend' },
  { name: 'concierge', label: 'Concierge' },
  { name: 'sweeps', label: 'Sweeps' },
  { name: 'kanban', label: 'Kanban' },
  { name: 'system', label: 'System' },
  { name: 'health', label: 'Catalog health' },
  { name: 'roots', label: 'Roots' },
];

const $q = useQuasar();
const wip = useWipStore();
const kanban = useKanbanStore();
const { installations } = useAgentInstallations();

const tab = ref<Tab>('admission');
const loaded = ref<TenantSettings | null>(null);
const loading = ref(false);
const loadError = ref('');
const busy = ref(false);

const drafts = ref<Record<string, string>>({});
const laneDrafts = ref<Record<string, string>>({});
const lanes = ref<KanbanLane[]>([]);

/** What the server refused on the last Save, by setting name. Cleared as soon as that box moves. */
const serverErrors = ref<Record<string, string>>({});

const auth = ref<AgentAuthReport[] | null>(null);

const conciergeForm = ref<InstanceType<typeof ConciergeAgentForm> | null>(null);

const settings = computed(() => loaded.value?.settings ?? []);
const settingFor = (name: string) => settings.value.find((entry) => entry.name === name);
const fieldsOn = (on: TenantSettingTab) =>
  TenantSettingFields.filter((field) => field.tab === on && field.kind !== 'lanes');

const changes = computed(() => changedSettings(settings.value, drafts.value, laneDrafts.value));
const errors = computed(() => ({ ...draftErrors(settings.value, drafts.value, laneDrafts.value), ...serverErrors.value }));

const conciergeDirty = computed(() => conciergeForm.value?.dirty === true);
const conciergeInvalid = computed(() => conciergeForm.value?.invalid === true);

const unchanged = computed(() => Object.keys(changes.value).length === 0 && !conciergeDirty.value);
const invalid = computed(() => Object.keys(errors.value).length > 0 || conciergeInvalid.value);
const canSave = computed(() => !busy.value && !loading.value && !unchanged.value && !invalid.value);

function errorFor(name: string): string | null {
  return errors.value[name] ?? null;
}

function setDraft(name: string, value: string) {
  drafts.value = { ...drafts.value, [name]: value };
  if (serverErrors.value[name]) {
    const { [name]: _dropped, ...rest } = serverErrors.value;
    serverErrors.value = rest;
  }
}

function setLaneDraft(lane: string, value: string | number | null) {
  laneDrafts.value = { ...laneDrafts.value, [lane]: value === null ? '' : String(value) };
  if (serverErrors.value[KanbanWipLimits]) {
    const { [KanbanWipLimits]: _dropped, ...rest } = serverErrors.value;
    serverErrors.value = rest;
  }
}

function fillDrafts(from: TenantSettings) {
  const next: Record<string, string> = {};
  for (const setting of from.settings) {
    if (setting.name !== KanbanWipLimits) next[setting.name] = draftOf(setting.value);
  }
  drafts.value = next;

  const limits = laneLimitsOf(from.settings.find((entry) => entry.name === KanbanWipLimits)?.value);
  const laneText: Record<string, string> = {};
  for (const lane of lanes.value) laneText[lane.id] = limits[lane.id] ? String(limits[lane.id]) : '';
  laneDrafts.value = laneText;
}

async function loadLanes() {
  // THE LANES THE BOARD ALREADY HOLDS when it has been fetched; otherwise one read of the board.
  const board = kanban.board ?? (await getKanbanBoard().catch(() => null));

  lanes.value = board?.lanes ?? [];
}

async function load() {
  loading.value = true;
  loadError.value = '';
  serverErrors.value = {};

  try {
    const [read] = await Promise.all([getTenantSettings(), loadLanes()]);
    loaded.value = read;
    fillDrafts(read);
  } catch (cause) {
    loaded.value = null;
    loadError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

async function loadAuth() {
  try {
    auth.value = await getAgentAuth();
  } catch {
    auth.value = null;
  }
}

watch(open, (showing) => {
  if (!showing) {
    wip.unwatch();
    return;
  }

  tab.value = props.initialTab ?? 'admission';
  wip.watch();
  void load();
  void loadAuth();
  void conciergeForm.value?.load();
});

/** The Concierge form mounts with its tab panel; load it the first time it appears. */
watch(conciergeForm, (form) => {
  if (form && open.value) void form.load();
});

const health = computed(() =>
  catalogHealth({
    notInstalled: installations.value.length === 0 ? null : installations.value.filter(isNotInstalled).map((entry) => entry.agent),
    auth: auth.value,
  }),
);

const advisoryLanes = computed(() => lanes.value.filter((lane) => !isRunningLane(lane.id)));
const inProgressLane = computed(() => lanes.value.find((lane) => isRunningLane(lane.id)) ?? null);

const lanesField = TenantSettingFields.find((field) => field.name === KanbanWipLimits)!;

/** Where a refused setting lives, so Save can take the person to it. */
function tabOf(name: string): Tab {
  return TenantSettingFields.find((field) => field.name === name)?.tab ?? 'admission';
}

async function save() {
  if (!canSave.value) return;

  busy.value = true;

  try {
    const sent = changes.value;

    if (Object.keys(sent).length > 0) await saveTenantSettings(sent);
    if (conciergeDirty.value) await conciergeForm.value?.save();

    open.value = false;

    $q.notify({
      type: 'positive',
      message: 'Settings saved. The tenant log records the change.',
      timeout: 4000,
    });

    // The ledger's limit may have moved; the board's lane limits certainly may have.
    void wip.refresh();
    void kanban.refreshIfActive();
  } catch (cause) {
    if (cause instanceof TenantSettingRejected && cause.field) {
      serverErrors.value = { ...serverErrors.value, [cause.field]: settingErrorText(cause.field, cause.message) };
      tab.value = tabOf(cause.field);
    } else {
      $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
    }
  } finally {
    busy.value = false;
  }
}

/** The setting "Reset to default" was asked for, while its confirmation is showing. */
const resetting = ref<TenantSetting | null>(null);

const resetLabel = computed(() =>
  resetting.value ? (TenantSettingFields.find((field) => field.name === resetting.value!.name)?.label ?? '') : '',
);

/** The lane titles the board holds, so a lane map's default reads as the board does. */
const laneTitle = (id: string) => lanes.value.find((lane) => lane.id === id)?.title ?? id;

const resetText = computed(() => (resetting.value ? resetLine(resetting.value, laneTitle) : ''));

function askReset(name: string) {
  const setting = settingFor(name);
  if (canReset(setting)) resetting.value = setting!;
}

/**
 * A RESET IS `null` FOR THAT ONE SETTING through the same PUT: the server removes its row, so the
 * value falls back to appsettings, then the built-in default. Other unsaved edits are kept.
 */
async function confirmReset() {
  const setting = resetting.value;
  if (!setting || busy.value) return;

  busy.value = true;

  try {
    await saveTenantSettings({ [setting.name]: null });
    resetting.value = null;

    const read = await getTenantSettings();
    loaded.value = read;

    const after = read.settings.find((entry) => entry.name === setting.name);
    if (setting.name === KanbanWipLimits) {
      const limits = laneLimitsOf(after?.value);
      const laneText: Record<string, string> = {};
      for (const lane of lanes.value) laneText[lane.id] = limits[lane.id] ? String(limits[lane.id]) : '';
      laneDrafts.value = laneText;
    } else {
      drafts.value = { ...drafts.value, [setting.name]: draftOf(after?.value) };
    }

    if (serverErrors.value[setting.name]) {
      const { [setting.name]: _dropped, ...rest } = serverErrors.value;
      serverErrors.value = rest;
    }

    $q.notify({ type: 'positive', message: 'Reset to its default. The tenant log records the change.', timeout: 4000 });

    void wip.refresh();
    void kanban.refreshIfActive();
  } catch (cause) {
    resetting.value = null;
    if (cause instanceof TenantSettingRejected && cause.field) {
      serverErrors.value = { ...serverErrors.value, [cause.field]: settingErrorText(cause.field, cause.message) };
      tab.value = tabOf(cause.field);
    } else {
      $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
    }
  } finally {
    busy.value = false;
  }
}

function openAgents() {
  emit('open-agents');
}

function openSkills() {
  emit('open-skills');
}

function holdText(hold: { team: string; member: string }) {
  return `${hold.team} / ${hold.member}`;
}
</script>

<template>
  <q-dialog v-model="open" :class="{ 'concierge-settings': !props.covered }" data-tenant-settings>
    <q-card class="os-dialog-lg tenant-settings-card">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Settings</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="busy" aria-label="Close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Instance-wide: every team and every person on this instance. Changes take effect without a
        restart and are recorded in the tenant log.
      </q-card-section>

      <q-banner v-if="loadError" dense class="os-bg-tint-error text-negative q-mx-md">
        <template #avatar><q-icon name="error" /></template>
        Could not read the settings: {{ loadError }}
      </q-banner>

      <DialogTabs v-model="tab">
        <q-tab v-for="entry in Tabs" :key="entry.name" :name="entry.name" :label="entry.label" />
      </DialogTabs>

      <q-separator />

      <q-tab-panels v-model="tab" animated keep-alive class="os-tab-panels tenant-settings-panels">
        <q-tab-panel name="admission">
          <div class="row q-col-gutter-lg">
            <div class="col-12 col-md-6">
              <TenantSettingField
                v-for="field in fieldsOn('admission')"
                :key="field.name"
                :field="field"
                :setting="settingFor(field.name)"
                :error="errorFor(field.name)"
                :disable="busy"
                :model-value="drafts[field.name] ?? ''"
                @update:model-value="(value: string) => setDraft(field.name, value)"
                @reset="askReset(field.name)"
              />
            </div>

            <!-- WHO HAS A SLOT AND WHO IS WAITING, LIVE, from the one copy of `/api/wip`. -->
            <div class="col-12 col-md-6 tenant-wip">
              <div class="text-subtitle2">Running now ({{ wip.runningCount }})</div>
              <ul class="tenant-wip-list" data-list="running">
                <li v-for="hold in wip.view?.running ?? []" :key="`r-${hold.team}-${hold.member}`">{{ holdText(hold) }}</li>
                <li v-if="wip.runningCount === 0" class="os-text-muted">Nobody.</li>
              </ul>
              <div class="text-subtitle2 q-mt-sm">Waiting for a slot ({{ wip.waitingCount }})</div>
              <ul class="tenant-wip-list" data-list="waiting">
                <li v-for="hold in wip.view?.waiting ?? []" :key="`w-${hold.team}-${hold.member}`">{{ holdText(hold) }}</li>
                <li v-if="wip.waitingCount === 0" class="os-text-muted">Nobody.</li>
              </ul>
            </div>
          </div>
        </q-tab-panel>

        <q-tab-panel name="spend">
          <TenantSettingField
            v-for="field in fieldsOn('spend')"
            :key="field.name"
            :field="field"
            :setting="settingFor(field.name)"
            :error="errorFor(field.name)"
            :disable="busy"
            :model-value="drafts[field.name] ?? ''"
            @update:model-value="(value: string) => setDraft(field.name, value)"
            @reset="askReset(field.name)"
          />
          <div class="text-caption os-text-muted q-mt-md">
            How spend is counted, and not settable: uncached input and output at full weight, cache
            reads at 1/10, cache writes at 5/4.
          </div>
        </q-tab-panel>

        <q-tab-panel name="concierge">
          <ConciergeAgentForm ref="conciergeForm" />
          <q-separator class="q-my-md" />
          <ConciergeSessions />
          <q-separator class="q-my-md" />
          <ConciergeDisplayForm />
          <q-separator class="q-my-md" />
          <TenantSettingField
            v-for="field in fieldsOn('concierge')"
            :key="field.name"
            :field="field"
            :setting="settingFor(field.name)"
            :error="errorFor(field.name)"
            :disable="busy"
            :model-value="drafts[field.name] ?? ''"
            @update:model-value="(value: string) => setDraft(field.name, value)"
            @reset="askReset(field.name)"
          />
        </q-tab-panel>

        <q-tab-panel name="sweeps">
          <div class="q-gutter-md">
            <TenantSettingField
              v-for="field in fieldsOn('sweeps')"
              :key="field.name"
              :field="field"
              :setting="settingFor(field.name)"
              :error="errorFor(field.name)"
              :disable="busy"
              :model-value="drafts[field.name] ?? ''"
              @update:model-value="(value: string) => setDraft(field.name, value)"
              @reset="askReset(field.name)"
            />
          </div>
        </q-tab-panel>

        <q-tab-panel name="kanban">
          <div class="text-subtitle2">{{ lanesField.label }}</div>
          <div class="text-caption os-text-muted q-mb-sm">{{ lanesField.hint }}</div>

          <div v-if="inProgressLane" class="tenant-lane-row">
            <span class="tenant-lane-title">{{ inProgressLane.title }}</span>
            <span class="os-text-muted">
              {{ drafts[WipMaxRunning] || '—' }} - the Agents running at once, set on Admission.
            </span>
          </div>

          <div v-for="lane in advisoryLanes" :key="lane.id" class="tenant-lane-row">
            <span class="tenant-lane-title">{{ lane.title }}</span>
            <q-input
              :model-value="laneDrafts[lane.id] ?? ''"
              outlined
              dense
              inputmode="numeric"
              placeholder="No limit"
              class="tenant-lane-input"
              :aria-label="`${lane.title} limit`"
              :disable="busy || !settingFor(KanbanWipLimits)"
              :error="!!errorFor(`lane:${lane.id}`)"
              :error-message="errorFor(`lane:${lane.id}`) ?? undefined"
              no-error-icon
              @update:model-value="(value) => setLaneDraft(lane.id, value)"
            />
          </div>
          <div v-if="lanes.length === 0" class="os-text-muted">The board has not answered, so there are no lanes to set.</div>

          <div v-if="errorFor(KanbanWipLimits)" class="text-negative text-caption">{{ errorFor(KanbanWipLimits) }}</div>
          <div class="row items-center no-wrap q-mt-sm">
            <div class="text-caption tenant-setting-source">{{ sourceLine(settingFor(KanbanWipLimits)) }}</div>
            <q-btn
              v-if="canReset(settingFor(KanbanWipLimits))"
              flat
              dense
              no-caps
              size="sm"
              color="primary"
              class="q-ml-sm"
              label="Reset to default"
              data-reset
              :disable="busy"
              @click="askReset(KanbanWipLimits)"
            />
          </div>
        </q-tab-panel>

        <q-tab-panel name="system">
          <TenantSettingField
            v-for="field in fieldsOn('system')"
            :key="field.name"
            :field="field"
            :setting="settingFor(field.name)"
            :error="errorFor(field.name)"
            :disable="busy"
            :model-value="drafts[field.name] ?? ''"
            @update:model-value="(value: string) => setDraft(field.name, value)"
            @reset="askReset(field.name)"
          />
        </q-tab-panel>

        <q-tab-panel name="health">
          <q-list dense>
            <q-item v-for="line in health" :key="line.label">
              <q-item-section avatar>
                <q-icon
                  :name="line.ok === null ? 'help' : line.ok ? 'check_circle' : 'warning'"
                  :color="line.ok === null ? 'grey-6' : line.ok ? 'positive' : 'warning'"
                />
              </q-item-section>
              <q-item-section>
                <q-item-label>{{ line.label }}</q-item-label>
                <q-item-label caption>{{ line.text }}</q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
          <div class="row q-gutter-sm q-mt-md">
            <q-btn flat no-caps icon="smart_toy" label="Open Agents" @click="openAgents" />
            <q-btn flat no-caps icon="psychology" label="Open Skills" @click="openSkills" />
          </div>
        </q-tab-panel>

        <q-tab-panel name="roots">
          <div class="text-caption os-text-muted q-mb-sm">
            The folders a team may be placed under. A deployment setting, read-only here: adding one
            means adding it to the Host's configuration AND mounting that path into the container.
          </div>
          <q-list dense bordered separator>
            <q-item v-for="root in loaded?.roots ?? []" :key="`${root.name}:${root.path}`">
              <q-item-section>
                <q-item-label v-if="root.name">{{ root.name }}</q-item-label>
                <q-item-label class="mono">{{ root.path }}</q-item-label>
                <q-item-label v-if="root.note" caption>{{ root.note }}</q-item-label>
              </q-item-section>
            </q-item>
            <q-item v-if="(loaded?.roots ?? []).length === 0">
              <q-item-section class="os-text-muted">No roots reported.</q-item-section>
            </q-item>
          </q-list>
        </q-tab-panel>
      </q-tab-panels>

      <q-card-actions align="right">
        <span v-if="!unchanged && invalid" class="text-caption text-negative q-mr-md">Fix the marked fields to save.</span>
        <q-btn v-close-popup flat label="Cancel" :disable="busy" />
        <q-btn color="primary" label="Save" :loading="busy" :disable="!canSave" @click="save" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- THE VALUE IT WOULD TAKE, AND FROM WHERE, BEFORE THE PERSON CONFIRMS. Lifted with this dialog. -->
  <q-dialog
    :model-value="resetting !== null"
    class="concierge-settings"
    data-reset-confirm
    @update:model-value="(showing: boolean) => { if (!showing && !busy) resetting = null }"
  >
    <q-card class="os-dialog-sm">
      <q-card-section class="os-dialog-title">Reset {{ resetLabel || 'this setting' }} to its default?</q-card-section>
      <q-card-section class="q-pt-none" data-reset-line>
        {{ resetText }} The value set by {{ resetting?.updatedBy ?? 'someone' }} is removed, and the tenant log
        records the reset.
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat label="Cancel" :disable="busy" @click="resetting = null" />
        <q-btn color="primary" label="Reset to default" data-reset-confirm-button :loading="busy" @click="confirmReset" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.tenant-settings-panels {
  --os-tab-panels-height: 28rem;
}

.tenant-wip-list {
  margin: 4px 0 0;
  padding-left: 1.2rem;
}

.tenant-lane-row {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 4px;
}

.tenant-lane-title {
  flex: 0 0 9rem;
}

.tenant-lane-input {
  max-width: 9rem;
}

.tenant-setting-source {
  color: var(--os-ink-faint);
}
</style>
