<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { InstalledPlugin, MarketplacePackage, PluginInstallResult, PluginMemberRef } from '../api/types';
import { pluginNeedWords, pluginUsedByWords } from '../lib/marketplace';
import { UPGRADE_WARNING, secretMoveWords, upgradePlugin, upgradeTitle, upgradedWords } from '../lib/pluginUpgrade';

/**
 * UPGRADE AN INSTALLED PLUGIN TO THE MARKETPLACE'S NEWER VERSION (PLG 2), from Admin -> Plugins and
 * the Marketplace's Update alike. ONE CONFIRM, and it is the install press: its warning that settings
 * may not map, the members who will move, and each solution team's minimum. Cancel changes nothing.
 * Upgrade fetches and installs in one go (`upgradePlugin`); a refusal at either step closes this and
 * is handed to the opener (`refused`) to show on the plugin's row, the installed version still active.
 *
 * Once installed it says so, lists the secret moves the install made, and names each member whose
 * saved settings do not fit the new version - each with its fields and a link to its settings
 * (`member`). Nothing is removed or stopped: such a member fails its runs, naming the field, until a
 * person fixes it.
 */
const props = defineProps<{
  /** The catalog's newer version of the plugin; the dialog shows while it is set. */
  pkg: MarketplacePackage | null
  /** The installed plugin: who will move, and the solution teams' minimums. */
  plugin: Pick<InstalledPlugin, 'id' | 'name' | 'members' | 'solutions'> | null
}>();

const emit = defineEmits<{
  /** The dialog closed; the opener clears `pkg`. */
  close: []
  /** Fetch or install refused, with the Host's sentence. Nothing changed. */
  refused: [text: string]
  /** The install landed: the opener reads its lists again. */
  upgraded: [result: PluginInstallResult]
  /** A member's settings link. */
  member: [member: PluginMemberRef]
}>();

const busy = ref(false);
const result = ref<PluginInstallResult | null>(null);

watch(() => props.pkg, () => {
  busy.value = false;
  result.value = null;
});

const name = computed(() => props.plugin?.name ?? props.pkg?.name ?? props.pkg?.id ?? '');

const showing = computed(() => props.pkg !== null);

function close() {
  if (busy.value) return;
  emit('close');
}

async function upgrade() {
  const pkg = props.pkg;
  if (!pkg || busy.value) return;

  busy.value = true;
  try {
    const answer = await upgradePlugin(pkg);
    if (answer.ok) {
      result.value = answer.result;
      emit('upgraded', answer.result);
    } else {
      busy.value = false;
      emit('refused', answer.text);
      emit('close');
    }
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-dialog :model-value="showing" persistent @update:model-value="(open: boolean) => { if (!open) close(); }" @escape-key="close">
    <!-- THE CONFIRM: the install press. -->
    <q-card v-if="pkg && !result" class="os-dialog-sm" data-plugin-upgrade-confirm>
      <q-card-section class="os-dialog-title" data-upgrade-title>{{ upgradeTitle(name, pkg.version) }}</q-card-section>
      <q-card-section class="q-pt-none">
        <p data-upgrade-warning>{{ UPGRADE_WARNING }}</p>
        <p class="os-body os-text-muted q-mb-xs" data-upgrade-members>{{ pluginUsedByWords(plugin?.members ?? []) }}</p>
        <p
          v-for="need in plugin?.solutions ?? []"
          :key="need.team"
          class="os-body os-text-muted q-mb-xs"
          :data-upgrade-solution="need.team"
        >{{ pluginNeedWords(need) }}</p>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="busy" data-upgrade-cancel @click="close" />
        <q-btn
          color="primary"
          unelevated
          no-caps
          label="Upgrade"
          data-upgrade-confirm
          :loading="busy"
          :disable="busy"
          @click="upgrade"
        />
      </q-card-actions>
    </q-card>

    <!-- DONE: what moved, and who needs a person. -->
    <q-card v-else-if="pkg && result" class="os-dialog-md" data-plugin-upgrade-done>
      <q-card-section class="row items-center q-pb-none">
        <q-icon name="check_circle" color="positive" size="20px" class="q-mr-sm" aria-hidden="true" />
        <div class="os-dialog-title" data-upgrade-done>{{ upgradedWords(name, result.version ?? pkg.version) }}</div>
      </q-card-section>
      <q-card-section>
        <p class="os-body os-text-muted">
          {{ result.previous ? `${result.previous} is kept on disk, inactive; install it again with Replace to roll back.` : '' }}
        </p>

        <div v-if="(result.moves ?? []).length > 0" class="q-mb-md" data-upgrade-moves>
          <div class="text-weight-medium q-mb-xs">Secrets moved into connections</div>
          <div v-for="move in result.moves" :key="`${move.team}/${move.member}/${move.slot}`" class="os-body" :data-upgrade-move="`${move.team}/${move.member}`">
            {{ secretMoveWords(move) }}
          </div>
        </div>

        <div v-if="(result.attention ?? []).length > 0" data-upgrade-attention>
          <div class="text-weight-medium q-mb-xs">These members' settings need attention</div>
          <p class="os-body os-text-muted q-mb-sm">
            Each fails its runs, naming the field, until its settings are fixed. Nothing was removed or stopped.
          </p>
          <div
            v-for="entry in result.attention"
            :key="`${entry.team}/${entry.member}`"
            class="q-mb-sm"
            :data-attention-member="`${entry.team}/${entry.member}`"
          >
            <a
              href="#"
              class="plugin-member-link"
              :data-attention-link="`${entry.team}/${entry.member}`"
              @click.prevent="emit('member', { team: entry.team, member: entry.member })"
            >{{ entry.teamName }} / {{ entry.memberName }}</a>
            <ul class="q-my-xs q-pl-lg">
              <li v-for="field in entry.fields" :key="field.field" class="os-body" :data-attention-field="field.field">
                <span class="mono">{{ field.field }}</span>: {{ field.reason }}
              </li>
            </ul>
          </div>
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Close" data-upgrade-close @click="close" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
