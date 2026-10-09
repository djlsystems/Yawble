<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { ActionRefused, checkSolution, installPlugin } from '../api/client';
import type { PluginInstallResult, SolutionCheck } from '../api/types';
import { isNotAPackage } from '../lib/solutions';
import InstallFromFolderDialog from './InstallFromFolderDialog.vue';

/**
 * INSTALL A PLUGIN FROM A FOLDER: the install dialog Admin > Plugins opens, and Solutions > Get
 * started opens on a plugin it fetched (`folder` fills Folder; the person still presses Install). It
 * shows the Host's verdict, which refuses an existing version unless Replace is ticked.
 *
 * A folder holding solution.json is a solution package: `solution` hands it, with its check, to the
 * caller's wizard. `settled` follows every install, whatever the verdict.
 */
const props = withDefaults(defineProps<{ folder?: string }>(), { folder: '' });

const open = defineModel<boolean>({ required: true });

const emit = defineEmits<{ solution: [folder: string, check: SolutionCheck]; settled: [] }>();

const installing = ref(false);
const verdict = ref<PluginInstallResult | null>(null);

// Every open starts without a verdict: the last one is the last install's.
watch(open, (showing) => {
  if (showing) verdict.value = null;
});

async function install(path: string, replace: boolean) {
  if (installing.value) return;

  installing.value = true;
  verdict.value = null;

  // A FOLDER HOLDING solution.json IS A SOLUTION PACKAGE: the wizard installs it instead. Only the
  // check's "no solution.json" refusal (or a refused folder) goes on to the plain plugin install.
  const solution = await checkSolution(path).catch(() => null);
  if (solution && (solution.ok || !isNotAPackage(solution.refusals))) {
    installing.value = false;
    open.value = false;
    emit('solution', path, solution);
    return;
  }

  try {
    verdict.value = await installPlugin(path, replace);
  } catch (cause) {
    // A refusal is the Host's verdict too: its sentence names why, and nothing was written. A 409
    // is an existing version without Replace; the body names the id and version when it read them.
    const body = cause instanceof ActionRefused ? cause.body : {};
    verdict.value = {
      installed: false,
      id: typeof body.id === 'string' ? body.id : null,
      version: typeof body.version === 'string' ? body.version : null,
      replaced: false,
      reason: cause instanceof Error ? cause.message : String(cause),
    };
  } finally {
    installing.value = false;
  }

  // AFTER EVERY INSTALL, whatever the verdict: a 200 with `installed: false` copied the folder and
  // rescanned before the catalog refused it, so a list has a new row to show with its reason.
  emit('settled');
}

const verdictText = computed(() => {
  const result = verdict.value;
  if (!result) return '';

  const what = [result.id, result.version].filter(Boolean).join(' ');

  return result.installed
    ? `${result.replaced ? 'Replaced' : 'Installed'}${what ? ` ${what}` : ''}. It is the active version.`
    : `Not installed${what ? ` (${what})` : ''}: ${result.reason ?? 'the Host gave no reason.'}`;
});
</script>

<template>
  <!-- A built plugin already inside the instance, through the dialog Solutions shares. The Host's
       verdict is shown as it gave it; a refusal names why, and nothing was written. -->
  <InstallFromFolderDialog
    v-model="open"
    caption="A built plugin folder inside this instance's data root, holding its plugin.json, or a solution package. A plugin is installed as the active version of its plugin."
    picker-title="Choose the plugin folder"
    offer-replace
    :folder="props.folder"
    :installing="installing"
    @install="install"
  >
    <q-banner
      v-if="verdict"
      dense
      :class="verdict.installed ? 'os-bg-tint-ok text-positive' : 'os-bg-tint-error text-negative'"
      data-install-verdict
    >
      <template #avatar><q-icon :name="verdict.installed ? 'check_circle' : 'error'" /></template>
      {{ verdictText }}
    </q-banner>
  </InstallFromFolderDialog>
</template>
