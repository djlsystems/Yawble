<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useKanbanStore } from '../stores/kanban';
import { outcomeLabel } from '../api/outcomes';
import { useEndedOutcomeOption } from '../lib/currentOutcome';

/**
 * THE CARD MENU'S "CHANGE OUTCOME…": a person picks an active or proposed outcome and the
 * workflow the card's tag is read from gets a new link (`PUT .../workflows/{n}/outcome`, as the
 * person). The board refetches after, so the tag shown is the server's answer.
 *
 * `cardId` is the card, or '' for closed. The names are q-select labels: text, never HTML.
 */
const props = defineProps<{ cardId: string }>();
const emit = defineEmits<{ close: [] }>();

const kanban = useKanbanStore();
const $q = useQuasar();

const chosen = ref<string | null>(null);

const open = computed({
  get: () => props.cardId !== '',
  set: (value: boolean) => {
    if (!value) emit('close');
  },
});

const card = computed(() => kanban.board?.cards.find((entry) => entry.id === props.cardId) ?? null);

// A retired current outcome is shown by name with its status, and is not offered.
const ended = useEndedOutcomeOption(() => card.value?.outcome?.id, () => kanban.outcomes, { known: () => card.value?.outcome });

const options = computed(() => [
  ...kanban.outcomes.map((outcome) => ({ label: outcomeLabel(outcome), value: outcome.id })),
  ...ended.value,
]);

watch(
  () => props.cardId,
  (id) => {
    chosen.value = card.value?.outcome?.id ?? null;
    if (id) void kanban.loadOutcomes();
  },
  { immediate: true },
);

async function save() {
  if (!chosen.value || !props.cardId) return;

  try {
    await kanban.changeOutcome(props.cardId, chosen.value);
    $q.notify({ type: 'positive', timeout: 3000, message: 'Outcome changed' });
    emit('close');
  } catch (cause) {
    $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-sm k-change-outcome">
      <q-card-section>
        <div class="os-dialog-title">Change outcome</div>
        <div v-if="card" class="text-body2 os-text-muted">{{ card.title }}</div>
      </q-card-section>

      <q-card-section>
        <q-select
          v-model="chosen"
          :options="options"
          dense
          outlined
          emit-value
          map-options
          label="Outcome"
        />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" />
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Change"
          data-action="save-outcome"
          :disable="!chosen || chosen === card?.outcome?.id || kanban.busy"
          @click="save"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
