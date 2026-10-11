<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { computed } from 'vue';
import ChipListInput from './ChipListInput.vue';
import type { SolutionSetupSetting } from '../api/types';
import { asPersonSetting, questionText } from '../lib/setup';
import { settingInputKind, settingKey } from '../lib/solutions';
import { boundsHint } from '../lib/numberBounds';

/**
 * ONE QUESTION OF A PACKAGE'S `setup` BLOCK about a setting, as the guided wizard asks it: the
 * package's question as the label (its description when it asks none), its hint under it, and the
 * input its type takes - a toggle, a choice, a list, a number or text - the default filled in by
 * the caller. Under Advanced… (`bounds`) a number also says its bounds. Package text is text.
 */
const props = defineProps<{
  setting: SolutionSetupSetting;
  modelValue: unknown;
  /** The sentence for a number outside its bounds, or null. */
  problem?: string | null | undefined;
  /** Say a number's bounds under it: Advanced… shows every setting with its bounds. */
  bounds?: boolean;
}>();

const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>();

const kind = computed(() => settingInputKind(asPersonSetting(props.setting)));
const label = computed(() => questionText(props.setting));
const boundsWords = computed(() => (props.bounds && kind.value === 'number' ? boundsHint(props.setting) : null));
const hint = computed(() => [props.setting.hint, boundsWords.value].filter(Boolean).join(' ') || undefined);
const list = computed(() => (Array.isArray(props.modelValue) ? props.modelValue.map(String) : []));

function setNumber(value: string | number | null) {
  emit('update:modelValue', value === '' || value === null ? null : Number(value));
}
</script>

<template>
  <div class="q-mb-md" :data-setup-question="settingKey(setting)">
    <div v-if="kind === 'toggle'" class="row items-center no-wrap q-gutter-x-xs">
      <q-toggle
        :model-value="modelValue === true"
        :label="label"
        @update:model-value="(value: boolean) => emit('update:modelValue', value)"
      />
      <FieldHelp :text="hint" data-setup-hint />
    </div>
    <q-select
      v-else-if="kind === 'choice'"
      :model-value="modelValue"
      :options="setting.choices ?? []"
      outlined
      :label="label"
      @update:model-value="(value: unknown) => emit('update:modelValue', value ?? '')"
    >
      <template #append><FieldHelp :text="hint" /></template>
    </q-select>
    <ChipListInput
      v-else-if="kind === 'choices' || kind === 'list'"
      :model-value="list"
      :options="kind === 'choices' ? setting.choices ?? [] : null"
      :label="label"
      :hint="hint"
      @update:model-value="(value: string[]) => emit('update:modelValue', value)"
    />
    <q-input
      v-else-if="kind === 'number'"
      :model-value="modelValue as number | null"
      type="number"
      outlined
      :label="label"
      :error="problem ? true : undefined"
      :error-message="problem ?? ''"
      @update:model-value="setNumber"
    >
      <template #append><FieldHelp :text="hint" /></template>
    </q-input>
    <q-input
      v-else
      :model-value="modelValue as string | null"
      outlined
      :label="label"
      @update:model-value="(value: string | number | null) => emit('update:modelValue', value ?? '')"
    >
      <template #append><FieldHelp :text="hint" /></template>
    </q-input>
  </div>
</template>
