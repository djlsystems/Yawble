<script setup lang="ts">
import type { TenantSetting } from '../api/types';
import { packageNames, sourceLine, ThemeChoices, validateDraft, type TenantSettingField } from '../lib/tenantSettings';
import ChipListInput from './ChipListInput.vue';

/**
 * ONE INSTANCE-WIDE SETTING: its label, its box, why it is there, and where its value came from.
 *
 * THE KEY IS NEVER RENDERED. `field.name` addresses the setting on the wire; a person reads
 * `field.label`. The source line under every field says whether the value is somebody's choice or
 * the deployment's default, and who changed it last.
 */
defineProps<{
  field: TenantSettingField;
  setting: TenantSetting | undefined;
  error?: string | null;
  disable?: boolean;
}>();

const draft = defineModel<string>({ required: true });

/** One package name's refusal before it joins the list, by the rule the whole box is held to. */
const packageRefusal = (value: string) => validateDraft('packages', value);
</script>

<template>
  <div class="tenant-setting" :data-setting="field.name">
    <q-select
      v-if="field.kind === 'theme'"
      v-model="draft"
      :options="[...ThemeChoices]"
      outlined
      dense
      :label="field.label"
      :disable="disable || !setting"
      :error="!!error"
      :error-message="error ?? undefined"
    />
    <!-- A LIST OF PACKAGES is the product's one list input. The draft stays the space-separated text
         the setting is read and checked as; the chips are its names. Lifted with this dialog, which
         sits above the Concierge panel, so its Add dialog opens in front of it. -->
    <ChipListInput
      v-else-if="field.kind === 'packages'"
      :model-value="packageNames(draft)"
      :label="field.label"
      item-name="package"
      mono
      layer-class="concierge-settings"
      :validate="packageRefusal"
      :disable="disable || !setting"
      :error="!!error"
      :error-message="error ?? undefined"
      @update:model-value="(value: string[]) => (draft = value.join(' '))"
    />
    <q-input
      v-else
      v-model="draft"
      outlined
      dense
      :label="field.label"
      :inputmode="field.kind === 'count' ? 'numeric' : 'text'"
      :disable="disable || !setting"
      :error="!!error"
      :error-message="error ?? undefined"
      no-error-icon
    />
    <div class="text-caption os-text-muted tenant-setting-hint">{{ field.hint }}</div>
    <!-- Not when it names the key: the key stays off screen. -->
    <div v-if="!field.hint && setting?.description && !setting.description.includes(field.name)" class="text-caption os-text-muted">{{ setting.description }}</div>
    <div class="text-caption tenant-setting-source">{{ sourceLine(setting) }}</div>
  </div>
</template>

<style scoped>
.tenant-setting-source {
  color: var(--os-ink-faint);
}
</style>
