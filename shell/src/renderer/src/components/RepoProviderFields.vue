<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { GitProvider, RepoProviderInfo } from '../../../shared/api';
import { GIT_PROVIDERS } from '../../../shared/api';
import { PROVIDER_LABELS } from '../gitWebLinks';
import BusyLabel from './BusyLabel.vue';

// B075 (card 447): Team settings > Repositories, one repository's provider. The detected provider (read-only), an
// override (blank: use the detected one) and an optional link pattern for a self-hosted or unknown host. Save hands
// both to the dialog, which saves them with teams.setRepo and reads teams.repos again.
const props = defineProps<{ info: RepoProviderInfo; testId: string; busy: boolean; saving: boolean }>();
const emit = defineEmits<{ save: [{ provider: GitProvider | null; linkPattern: string | null }] }>();

const provider = ref<GitProvider | ''>('');
const pattern = ref('');
function reset() {
  provider.value = props.info.provider ?? '';
  pattern.value = props.info.linkPattern ?? '';
}
reset();
watch(() => [props.info.provider, props.info.linkPattern], reset);

const detected = computed(() => {
  const d = props.info.providerDetected;
  return d ? PROVIDER_LABELS[d] : 'not detected (plain git)';
});
const changed = computed(() => (provider.value || null) !== (props.info.provider ?? null) || (pattern.value.trim() || null) !== (props.info.linkPattern ?? null));
const save = () => emit('save', { provider: provider.value || null, linkPattern: pattern.value.trim() || null });
</script>

<template>
  <div class="provider" :data-test="`${testId}-provider`">
    <p class="muted small">Provider from its origin: <b :data-test="`${testId}-provider-detected`">{{ detected }}</b></p>
    <label>Provider
      <select v-model="provider" :data-test="`${testId}-provider-select`" :disabled="busy">
        <option value="">As detected</option>
        <option v-for="p in GIT_PROVIDERS" :key="p" :value="p">{{ PROVIDER_LABELS[p] }}</option>
      </select>
    </label>
    <label>Link pattern (optional, for a self-hosted or unknown host)
      <input v-model="pattern" class="mono wide" :data-test="`${testId}-link-pattern`" :disabled="busy"
        placeholder="{base}/{path}/commit/{commit}" />
    </label>
    <p class="muted small">Placeholders: {base} {path} {commit} {branch} {file} {from} {to} {pr}. Blank: the provider's own links.</p>
    <div class="buttons"><span class="grow" />
      <button class="primary" :data-test="`${testId}-provider-save`" :disabled="busy || !changed" @click="save"><BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
    </div>
  </div>
</template>

<style scoped>
.provider { display: flex; flex-direction: column; gap: 6px; margin-top: 6px; padding-top: 6px; border-top: 1px solid var(--line-soft); }
.provider p { margin: 0; }
label { display: flex; flex-direction: column; gap: 4px; }
label select, label input { font: inherit; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; max-width: 320px; }
label input.wide { max-width: none; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.buttons { display: flex; gap: 8px; align-items: center; }
.grow { flex: 1; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.muted { color: var(--muted); }
.small { font-size: 12px; }
</style>
