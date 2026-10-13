<script setup lang="ts">
import { computed } from 'vue';
import FieldHelp from './FieldHelp.vue';
import type { TenantSetting } from '../api/types';
import { canReset, descriptionText, packageNames, sourceLine, ThemeOptions, validateDraft, type TenantSettingField } from '../lib/tenantSettings';
import ChipListInput from './ChipListInput.vue';

/**
 * ONE INSTANCE-WIDE SETTING: its label, its box, why it is there, and where its value came from.
 *
 * THE KEY IS NEVER RENDERED. `field.name` addresses the setting on the wire; a person reads
 * `field.label`. The source line under every field says whether the value is somebody's choice or
 * the deployment's default, and who changed it last.
 */
const props = defineProps<{
  field: TenantSettingField;
  setting: TenantSetting | undefined;
  error?: string | null;
  disable?: boolean;
}>();

const draft = defineModel<string>({ required: true });

/** "Reset to default", asked for. The dialog says what it would take and sends it. */
const emit = defineEmits<{ reset: [] }>();

/** The help's wrapper marks: its own hint, or the server's description, as the tests read them. */
const helpClass = computed(() => (props.field.hint ? 'tenant-setting-hint' : undefined));
const descriptionMark = computed(() => (props.field.hint ? undefined : ''));

/** One package name's refusal before it joins the list, by the rule the whole box is held to. */
const packageRefusal = (value: string) => validateDraft('packages', value);

/** What the setting does, behind its info icon: its own hint, else the server's description. */
const help = computed(() =>
  props.field.hint || (props.setting?.description ? descriptionText(props.setting.description) : null),
);
</script>

<template>
  <!-- ONE SETTING, ONE BLOCK: its box with its info icon inside it (or beside its toggle), then
       where its value comes from right under it, and space before the next setting, so an icon or
       a source line is never read as the next field's. -->
  <div class="tenant-setting" :data-setting="field.name">
    <q-select
      v-if="field.kind === 'theme'"
      v-model="draft"
      :options="ThemeOptions"
      emit-value
      map-options
      outlined
      dense
      :label="field.label"
      :disable="disable || !setting"
      :error="!!error"
      :error-message="error ?? undefined"
      hide-bottom-space
    >
      <template #append>
        <span :class="helpClass" :data-description="descriptionMark"><FieldHelp :text="help" /></span>
      </template>
    </q-select>
    <!-- ON OR OFF: the draft is the word `on` or `off`, as the server reads and takes it. -->
    <div v-else-if="field.kind === 'toggle'" class="row items-center no-wrap">
      <q-toggle
        :model-value="draft === 'on'"
        :label="field.label"
        :disable="disable || !setting"
        @update:model-value="(value: boolean) => (draft = value ? 'on' : 'off')"
      />
      <span :class="helpClass" :data-description="descriptionMark" class="q-ml-xs"><FieldHelp :text="help" /></span>
    </div>
    <!-- A LIST OF PACKAGES is the product's one list input. The draft stays the space-separated text
         the setting is read and checked as; the chips are its names. Lifted with this dialog, which
         sits above the Concierge panel, so its Add dialog opens in front of it. Its info icon is
         the list's own, inside its box. -->
    <div v-else-if="field.kind === 'packages'" :class="helpClass" :data-description="descriptionMark">
      <ChipListInput
        :model-value="packageNames(draft)"
        :label="field.label"
        :hint="help ?? undefined"
        item-name="package"
        mono
        layer-class="concierge-settings"
        :validate="packageRefusal"
        :disable="disable || !setting"
        :error="!!error"
        :error-message="error ?? undefined"
        @update:model-value="(value: string[]) => (draft = value.join(' '))"
      />
    </div>
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
      hide-bottom-space
    >
      <template #append>
        <span :class="helpClass" :data-description="descriptionMark"><FieldHelp :text="help" /></span>
      </template>
    </q-input>
    <div v-if="field.kind === 'toggle' && error" class="text-caption text-negative">{{ error }}</div>
    <!-- WHERE THE VALUE COMES FROM, under its own box and indented to its text. -->
    <div class="row items-center no-wrap tenant-setting-meta" :class="{ 'tenant-setting-meta-toggle': field.kind === 'toggle' }">
      <div class="text-caption tenant-setting-source">{{ sourceLine(setting) }}</div>
      <!-- ONLY FOR A VALUE A PERSON SET: anything else is already its default. -->
      <q-btn
        v-if="canReset(setting)"
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        class="q-ml-sm"
        label="Reset to default"
        data-reset
        :disable="disable"
        @click="emit('reset')"
      />
    </div>
  </div>
</template>

<style scoped>
.tenant-setting {
  margin-bottom: 1.25rem;
}

.tenant-setting:last-child {
  margin-bottom: 0;
}

/* Under its box, lined up with the text inside it (Quasar's dense outlined padding). */
.tenant-setting-meta {
  padding-left: 12px;
  margin-top: 2px;
  min-height: 1.5rem;
}

/* A toggle's line starts under its label, past the switch, and close under it: the toggle keeps
   room round its switch that a box does not. */
.tenant-setting-meta-toggle {
  padding-left: 3.25rem;
  margin-top: -6px;
}

.tenant-setting-source {
  color: var(--os-ink-faint);
}
</style>
