<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import SetupSteps from './SetupSteps.vue';
import { checkSolution, teamSolution } from '../api/client';
import type { SolutionMissing, SolutionPanelMember, SolutionPlan } from '../api/types';
import { finishLine, missingScreen } from '../lib/setup';

/**
 * COME BACK ANY TIME, on a solution's panel, for a package with a `setup` block:
 *
 * - **Change setup** reopens the guided wizard on the team's current answers; each change is saved
 *   as its screen is left, through the routes the panel's own controls use. No new write route.
 * - **"Finish setup: connect a mailbox"**, one line, while a required step was skipped - read from the
 *   same live `missing` the blocked banner reads (`GET /api/teams/{team}/solution`) - and it opens
 *   the wizard on that screen. It goes once what it names is provided.
 *
 * Whether the package has a `setup` block is read by checking its source folder, as the install
 * did. A package without one, or a folder that cannot be checked any more, shows nothing here.
 */
const props = defineProps<{
  team: string;
  /** The installed version's source folder. */
  folder: string;
  members: SolutionPanelMember[];
}>();

const emit = defineEmits<{ changed: [] }>();

const plan = ref<SolutionPlan | null>(null);
const missing = ref<SolutionMissing[]>([]);

/** The team's stored member name -> the package's, for a missing input that names the stored one. */
const packageNames = computed(() => Object.fromEntries(props.members.map((member) => [member.member, member.packageName])));

async function load() {
  const team = props.team;
  try {
    const check = await checkSolution(props.folder);
    if (team !== props.team) return;
    plan.value = check.ok && check.plan.setup ? check.plan : null;
  } catch {
    plan.value = null;
  }
  if (plan.value) await readMissing();
}

async function readMissing() {
  const team = props.team;
  try {
    const record = await teamSolution(team);
    if (team === props.team) missing.value = record?.missing ?? [];
  } catch {
    // Advisory, as the banner is: a failed read shows no line rather than a false one.
    missing.value = [];
  }
}

watch(() => [props.team, props.folder], () => void load(), { immediate: true });

const finish = computed(() => (plan.value?.setup ? finishLine(missing.value, plan.value.setup, packageNames.value) : null));
const finishAt = computed(() => {
  const setup = plan.value?.setup;
  if (!setup) return null;
  return missing.value.map((item) => missingScreen(item, setup, packageNames.value)).find(Boolean) ?? null;
});

const wizard = ref<{ open: boolean; start: string | null }>({ open: false, start: null });
let changed = false;
const markChanged = () => (changed = true);

function openWizard(start: string | null) {
  changed = false;
  wizard.value = { open: true, start };
}

/** Closed: what is missing is read again, and the panel is told when anything was saved. */
watch(
  () => wizard.value.open,
  (showing, was) => {
    if (!was || showing) return;
    void readMissing();
    if (changed) emit('changed');
  },
);
</script>

<template>
  <div v-if="plan" class="row items-center q-gutter-sm q-mt-xs" data-setup-entry>
    <q-btn
      v-if="finish"
      flat
      dense
      no-caps
      color="warning"
      :label="finish"
      data-finish-setup
      @click="openWizard(finishAt)"
    />
    <q-btn flat dense no-caps color="primary" icon="tune" label="Change setup" data-change-setup @click="openWizard(null)" />

    <q-dialog v-model="wizard.open">
      <q-card class="os-dialog-md">
        <SetupSteps
          v-if="wizard.open"
          :plan="plan"
          :folder="folder"
          :team="team"
          :start="wizard.start"
          @close="wizard.open = false"
          @changed="markChanged"
        />
      </q-card>
    </q-dialog>
  </div>
</template>
