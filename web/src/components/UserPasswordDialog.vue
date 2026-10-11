<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { AdminUser } from '../api/client';
import { MINIMUM_PASSWORD_LENGTH, password as passwordRule } from '../lib/rules';
import { passes } from '../lib/forms';
import PasswordField from './PasswordField.vue';

/**
 * One account's password, reset by an administrator.
 *
 * ITS OWN DIALOG rather than a field on the edit form beside it, and that separation predates the
 * move out of the list: they are different actions with different server calls, and editing the
 * tier while also typing a new password would make one Save button answer for two unrelated
 * mistakes.
 *
 * Presentational for the reason the edit dialog is - the list saves and keeps this open on a
 * refusal, so the server's words land where the person is looking.
 */
const open = defineModel<boolean>({ required: true });

defineProps<{
  /** Whose password this sets. Null only while the dialog is closed. */
  user: AdminUser | null;
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{ save: [string] }>();

const password = ref('');

// Cleared on every OPENING, never merely on close: a typed-but-abandoned password left in a
// component the parent reuses would be offered up on the NEXT account somebody opened.
watch(
  open,
  (showing) => {
    if (showing) password.value = '';
  },
  { immediate: true },
);

const passwordRules = [passwordRule];

const canSubmit = computed(() => passes(password.value, passwordRules));
</script>

<template>
  <q-dialog v-model="open" no-backdrop-dismiss>
    <q-card class="os-dialog-sm">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Change password</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="busy" />
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <div class="os-text-muted">
          Sets a new password for <strong>{{ user?.email }}</strong> immediately. They are not told,
          and any session they already have keeps working.
        </div>

        <PasswordField
          v-model="password"
          label="New password"
          autocomplete="new-password"
          :hint="`At least ${MINIMUM_PASSWORD_LENGTH} characters`"
          :rules="passwordRules"
        />

        <q-banner v-if="error" dense class="os-bg-tint-error text-negative">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" :disable="busy" />
        <q-btn
          unelevated
          color="primary"
          no-caps
          label="Change it"
          :loading="busy"
          :disable="!canSubmit"
          @click="emit('save', password)"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
