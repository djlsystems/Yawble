<script setup lang="ts">
import { computed, ref } from 'vue'
import { cronFromBuilder, previewCronOccurrences, type CronBuilderSpec } from '../lib/triggers'

/**
 * Writes a cron expression from a preset, so a person need not know the six-field seconds format.
 * It only WRITES the expression into the trigger's field (`use`); hand-editing it afterwards stays
 * possible. The preview is the same one the trigger dialog shows, in the trigger's timezone and from
 * its start, so what is seen here is what the schedule will do.
 */
const props = defineProps<{
  modelValue: boolean
  timezone: string
  startsAt: Date | null
}>()

const emit = defineEmits<{
  'update:modelValue': [open: boolean]
  use: [expression: string]
}>()

type Preset = CronBuilderSpec['preset']

const presets: { label: string, value: Preset }[] = [
  { label: 'Every N minutes', value: 'minutes' },
  { label: 'Every hour', value: 'hourly' },
  { label: 'Every day', value: 'daily' },
  { label: 'Every week, on chosen days', value: 'weekly' },
  { label: 'Every month, on a day', value: 'monthly' },
]

const weekDays = [
  { label: 'Mon', value: 1 },
  { label: 'Tue', value: 2 },
  { label: 'Wed', value: 3 },
  { label: 'Thu', value: 4 },
  { label: 'Fri', value: 5 },
  { label: 'Sat', value: 6 },
  { label: 'Sun', value: 0 },
]

const preset = ref<Preset>('daily')
const every = ref(15)
const time = ref('09:00')
const minute = ref(0)
const days = ref<number[]>([1, 2, 3, 4, 5])
const dayOfMonth = ref(1)

const hourOfTime = computed(() => Number(time.value.split(':')[0] ?? 0))
const minuteOfTime = computed(() => Number(time.value.split(':')[1] ?? 0))

const expression = computed<string | null>(() => {
  switch (preset.value) {
    case 'minutes':
      return cronFromBuilder({ preset: 'minutes', every: Number(every.value) })
    case 'hourly':
      return cronFromBuilder({ preset: 'hourly', minute: Number(minute.value) })
    case 'daily':
      return cronFromBuilder({ preset: 'daily', hour: hourOfTime.value, minute: minuteOfTime.value })
    case 'weekly':
      return cronFromBuilder({ preset: 'weekly', days: days.value, hour: hourOfTime.value, minute: minuteOfTime.value })
    case 'monthly':
      return cronFromBuilder({ preset: 'monthly', dayOfMonth: Number(dayOfMonth.value), hour: hourOfTime.value, minute: minuteOfTime.value })
  }
})

const preview = computed(() =>
  expression.value === null ? [] : previewCronOccurrences(expression.value, props.timezone, 5, new Date(), props.startsAt),
)

function close() {
  emit('update:modelValue', false)
}

function use() {
  if (expression.value === null) return
  emit('use', expression.value)
  close()
}
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="(open: boolean) => emit('update:modelValue', open)">
    <q-card class="os-dialog-sm">
      <q-card-section class="row items-center no-wrap">
        <div class="text-h6">Build a schedule</div>
        <q-space />
        <q-btn flat round dense icon="close" aria-label="Close" @click="close" />
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-select
          v-model="preset"
          outlined
          dense
          emit-value
          map-options
          :options="presets"
          label="Repeat"
          popup-content-class="triggers-popup"
        />

        <q-input
          v-if="preset === 'minutes'"
          v-model.number="every"
          outlined
          dense
          type="number"
          min="1"
          max="59"
          label="Every how many minutes"
        />

        <q-input
          v-if="preset === 'hourly'"
          v-model.number="minute"
          outlined
          dense
          type="number"
          min="0"
          max="59"
          label="At minute past the hour"
        />

        <div v-if="preset === 'weekly'">
          <div class="text-caption os-text-muted q-mb-xs">On</div>
          <div class="q-gutter-xs">
            <q-checkbox
              v-for="day in weekDays"
              :key="day.value"
              v-model="days"
              :val="day.value"
              :label="day.label"
              dense
            />
          </div>
        </div>

        <q-input
          v-if="preset === 'monthly'"
          v-model.number="dayOfMonth"
          outlined
          dense
          type="number"
          min="1"
          max="31"
          label="On day of the month"
          hint="A month without that day (the 31st in April) is skipped."
        />

        <q-input
          v-if="preset === 'daily' || preset === 'weekly' || preset === 'monthly'"
          v-model="time"
          outlined
          dense
          type="time"
          stack-label
          :label="`At (${timezone})`"
        />

        <div>
          <div class="text-caption os-text-muted q-mb-xs">Expression</div>
          <div class="mono">{{ expression ?? 'Choose at least one day.' }}</div>
        </div>

        <div>
          <div class="text-caption os-text-muted q-mb-xs">Next 5 occurrences</div>
          <q-list dense bordered class="rounded-borders">
            <q-item v-if="preview.length === 0">
              <q-item-section class="os-text-muted">No preview available.</q-item-section>
            </q-item>
            <q-item v-for="(line, index) in preview" v-else :key="index">
              <q-item-section>{{ line }}</q-item-section>
            </q-item>
          </q-list>
        </div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="close" />
        <q-btn unelevated no-caps color="primary" label="Use this schedule" :disable="expression === null" @click="use" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
